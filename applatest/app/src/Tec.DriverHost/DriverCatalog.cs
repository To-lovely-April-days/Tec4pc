using System.Reflection;
using System.Runtime.Loader;
using Tec.Driver.Abi;

namespace Tec.DriverHost;

public enum PackageStatus { Ok, AbiMismatch, Broken, NotLoaded }

public sealed class DriverPackage
{
    public required DriverManifest Manifest { get; init; }
    public required string Directory { get; init; }
    public PackageStatus Status { get; set; } = PackageStatus.NotLoaded;
    public string? Problem { get; set; }
    public IDeviceDriver? Driver { get; set; }
    public bool Builtin { get; init; }

    public string Id => Manifest.Id;
    public string Display => string.IsNullOrWhiteSpace(Manifest.Name) ? Manifest.Id : Manifest.Name;
    /// <summary>灰掉时界面要说明原因，不能只是不显示。</summary>
    public bool Usable => Status == PackageStatus.Ok && Driver is not null;
}

/// <summary>
/// 每个驱动一个 AssemblyLoadContext，避免第三方 SDK 的依赖互相打架（§3.5）。
/// </summary>
internal sealed class DriverLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly HashSet<string> _shared;

    public DriverLoadContext(string entryPath)
        : base($"tec-driver:{Path.GetFileNameWithoutExtension(entryPath)}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(entryPath);
        // 契约包必须由宿主提供，否则驱动里的 ITemperatureControl 和主程序的不是同一个类型
        _shared = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tec.Driver.Abi" };
    }

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is not null && _shared.Contains(name.Name)) return null;   // 交给默认上下文
        var path = _resolver.ResolveAssemblyToPath(name);
        return path is null ? null : LoadFromAssemblyPath(path);
    }

    protected override IntPtr LoadUnmanagedDll(string name)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(name);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}

/// <summary>
/// 发现 · 加载 · 隔离 · 版本校验 · 故障隔离。
/// 驱动抛异常只让该包进 Broken，绝不拖垮进程。
/// </summary>
public sealed class DriverCatalog
{
    private readonly List<DriverPackage> _packages = new();
    private readonly Dictionary<string, DriverLoadContext> _contexts = new(StringComparer.Ordinal);

    public IReadOnlyList<DriverPackage> Packages => _packages;

    public event EventHandler? Changed;

    /// <summary>内置驱动（仿真、自研设备）不走文件发现，直接注册。</summary>
    public DriverPackage RegisterBuiltin(IDeviceDriver driver)
    {
        var info = driver.Info;
        var pkg = new DriverPackage
        {
            Manifest = new DriverManifest
            {
                Id = info.Id,
                Name = info.Name,
                Vendor = info.Vendor,
                Version = info.Version,
                Abi = info.Abi,
                ChannelsPerDevice = info.ChannelsPerDevice,
                SimulatorIncluded = info.SimulatorIncluded,
                Capabilities = info.Capabilities.ToList(),
                Description = info.Description,
                Icon = info.IconKey
            },
            Directory = "<builtin>",
            Builtin = true,
            Driver = driver,
            Status = AbiVersion.IsCompatible(info.Abi) ? PackageStatus.Ok : PackageStatus.AbiMismatch
        };
        if (pkg.Status == PackageStatus.AbiMismatch)
            pkg.Problem = $"驱动 ABI {info.Abi} 与宿主 {AbiVersion.Text} 不兼容";
        Replace(pkg);
        return pkg;
    }

    /// <summary>扫描 drivers/ 目录，一个子目录一个驱动包。</summary>
    public void Discover(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            var manifest = DriverManifest.Read(manifestPath);
            if (manifest is null)
            {
                Replace(new DriverPackage
                {
                    Manifest = new DriverManifest { Id = Path.GetFileName(dir), Name = Path.GetFileName(dir) },
                    Directory = dir,
                    Status = PackageStatus.Broken,
                    Problem = "manifest.json 无法解析"
                });
                continue;
            }

            var problem = manifest.Validate();
            var pkg = new DriverPackage { Manifest = manifest, Directory = dir };

            if (problem.Length > 0) { pkg.Status = PackageStatus.Broken; pkg.Problem = problem; }
            else if (!AbiVersion.IsCompatible(manifest.Abi))
            {
                pkg.Status = PackageStatus.AbiMismatch;
                pkg.Problem = $"驱动 ABI {manifest.Abi} 与宿主 {AbiVersion.Text} 不兼容";
            }

            Replace(pkg);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>真正加载程序集。失败只影响这一个包。</summary>
    public bool Load(DriverPackage pkg)
    {
        if (pkg.Builtin || pkg.Driver is not null) return pkg.Usable;
        if (pkg.Status is PackageStatus.AbiMismatch or PackageStatus.Broken) return false;

        try
        {
            var entry = Path.Combine(pkg.Directory, pkg.Manifest.Entry);
            if (!File.Exists(entry))
            {
                pkg.Status = PackageStatus.Broken;
                pkg.Problem = $"找不到程序集 {pkg.Manifest.Entry}";
                return false;
            }

            if (!_contexts.TryGetValue(pkg.Id, out var alc))
            {
                alc = new DriverLoadContext(entry);
                _contexts[pkg.Id] = alc;
            }

            var asm = alc.LoadFromAssemblyPath(entry);
            var type = asm.GetType(pkg.Manifest.TypeName, throwOnError: false)
                       ?? asm.GetTypes().FirstOrDefault(t => typeof(IDeviceDriver).IsAssignableFrom(t) && !t.IsAbstract);
            if (type is null)
            {
                pkg.Status = PackageStatus.Broken;
                pkg.Problem = $"程序集里找不到 {pkg.Manifest.TypeName}";
                return false;
            }

            if (Activator.CreateInstance(type) is not IDeviceDriver driver)
            {
                pkg.Status = PackageStatus.Broken;
                pkg.Problem = $"{type.FullName} 不是 IDeviceDriver";
                return false;
            }

            pkg.Driver = driver;
            pkg.Status = PackageStatus.Ok;
            pkg.Problem = null;
            Changed?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            pkg.Status = PackageStatus.Broken;
            pkg.Problem = ex.Message;
            return false;
        }
    }

    public void LoadAll()
    {
        foreach (var p in _packages.ToList()) Load(p);
    }

    public IDeviceDriver? Driver(string driverId)
    {
        var pkg = _packages.FirstOrDefault(p => p.Id == driverId);
        if (pkg is null) return null;
        if (pkg.Driver is null) Load(pkg);
        return pkg.Driver;
    }

    public DriverPackage? Package(string driverId)
        => _packages.FirstOrDefault(p => p.Id == driverId);

    /// <summary>设备库列表：不可用的也要列出来并说明原因，不能悄悄消失。</summary>
    public IReadOnlyList<DriverPackage> ForLibrary()
        => _packages.OrderByDescending(p => p.Usable).ThenBy(p => p.Display, StringComparer.Ordinal).ToList();

    private void Replace(DriverPackage pkg)
    {
        _packages.RemoveAll(p => p.Id == pkg.Id);
        _packages.Add(pkg);
    }
}
