using Tec.DriverHost;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示模式下把仿真驱动包当插件装进驱动目录（2026-11-23）。
///
/// WHY：仿真驱动 2026-11 起已从程序里拿掉（只留在 tests/Tec.Drivers.Simulator 当回归替身），
/// 程序不引用它。演示模式要用它的热模型 / 搅拌 / 加料 / pH 让曲线自己长出来，
/// 又不能把它引回程序——所以走第三方驱动包那条路：Tec.App / Tec.Hmi 的 csproj 在 Build 后把
/// Tec.Drivers.Simulator.dll 拷到输出目录的 <c>demo/drivers/sim/</c>（不引用，只拷），
/// manifest.json 列出程序集里全部驱动类型（siblingTypes，同一个 AssemblyLoadContext）。
/// 这个目录**只在 DemoMode.Enabled 时才被扫**；缺省启动程序根本不知道它在。
/// </summary>
public static class DemoDrivers
{
    /// <summary>仿真驱动的驱动号（与 tests/Tec.Drivers.Simulator 里的常量一致；这里不能引用那个程序集）。</summary>
    public const string Reactor = "sim.reactor.rd105";
    public const string TrProbe = "sim.probe.tr";
    public const string PhProbe = "sim.probe.ph";
    public const string Pump = "sim.dosing.pump";
    public const string Stirrer = "sim.stir.aqmd";
    public const string Feed = "sim.feed.htlab";

    public static readonly string[] All = { Reactor, TrProbe, PhProbe, Pump, Stirrer, Feed };

    /// <summary>缺省的插件目录：可执行旁边的 demo/drivers/。测试可以指到别处。</summary>
    public static string Dir { get; set; } = Path.Combine(AppContext.BaseDirectory, "demo", "drivers");

    /// <summary>
    /// 发现并加载。返回装上了几个仿真驱动；0 = 目录不在或程序集没拷过来（界面照实说，不假装有仿真）。
    /// </summary>
    public static int Load(DriverCatalog drivers, string? dir = null)
    {
        dir ??= Dir;
        if (!Directory.Exists(dir)) return 0;
        drivers.Discover(dir);
        drivers.LoadAll();
        return All.Count(id => drivers.Driver(id) is not null);
    }

    /// <summary>目录里缺了什么（给日志 / 测试一句人话）。</summary>
    public static string Missing(DriverCatalog drivers)
        => string.Join("、", All.Where(id => drivers.Driver(id) is null));

    /// <summary>
    /// 测试 / 脚本用：把一份 manifest 和仿真程序集摆成一个插件目录。
    /// manifest 内容与 src/Tec.Demo/drivers/sim/manifest.json 同源。
    /// </summary>
    public static void Stage(string pluginDir, string simulatorDllPath)
    {
        Directory.CreateDirectory(pluginDir);
        File.Copy(simulatorDllPath, Path.Combine(pluginDir, "Tec.Drivers.Simulator.dll"), overwrite: true);
        File.WriteAllText(Path.Combine(pluginDir, "manifest.json"), ManifestJson);
    }

    public const string ManifestJson = """
        {
          "id": "sim.reactor.rd105",
          "name": "双工位反应器 RD105（仿真）",
          "vendor": "Tec",
          "version": "1.0.0",
          "abi": "1.0",
          "entry": "Tec.Drivers.Simulator.dll",
          "assemblyQualifiedType": "Tec.Drivers.Simulator.Rd105ReactorDriver",
          "channelsPerDevice": 2,
          "simulatorIncluded": true,
          "icon": "rd105",
          "siblingTypes": [
            "Tec.Drivers.Simulator.TrProbeDriver",
            "Tec.Drivers.Simulator.PhProbeDriver",
            "Tec.Drivers.Simulator.DosingPumpDriver",
            "Tec.Drivers.Simulator.StirrerSimDriver",
            "Tec.Drivers.Simulator.FeedSystemSimDriver",
            "Tec.Drivers.Simulator.TurbidityProbeDriver"
          ]
        }
        """;
}
