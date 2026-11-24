using System.Diagnostics;
using System.Net.Sockets;

namespace Tec.Drivers.TouchPanel;

/// <summary>
/// 找到并启动跟着工作站一起装的那两个 C++ 程序（hmi-cpp 编出来的）：
///   tec_core  控制核心：连串口、控温、把着安全。不带触摸屏的机型由 PC 自己跑它（用户 2026-11-24 定的）；
///   tec_hmi   新界面：工作站里点「手动控制面板」弹出来的就是它，带不带触摸屏都是同一个界面。
///
/// 两个程序放在工作站程序旁边的 hmi 文件夹里（连同 assets 和 gain-table.csv）；
/// 开发时可以用环境变量 TEC_HMI_DIR 指到 hmi-cpp 的编译输出目录。
///
/// 控制核心是工作站启动的，但**工作站关掉时不去停它**：它正在控温的话，界面退出、崩掉都不该让加热失控或者突然断掉——
/// 这和带触摸屏的机型是同一个规矩（屏上的核心一直开着）。下次打开工作站发现它还在，直接连上接着用。
/// </summary>
public static class LocalPrograms
{
    /// <summary>测试时把目录指到别处；平时是空的。</summary>
    public static string? DirOverride { get; set; }

    public static string Dir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(DirOverride)) return DirOverride!;
            var env = Environment.GetEnvironmentVariable("TEC_HMI_DIR");
            return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(AppContext.BaseDirectory, "hmi");
        }
    }

    private static string Exe(string name) => Path.Combine(Dir, OperatingSystem.IsWindows() ? name + ".exe" : name);
    public static string CorePath => Exe("tec_core");
    public static string HmiPath => Exe("tec_hmi");

    /// <summary>本机控制核心的数据放哪：每一炉的记录、配方、串口设置、导出文件都在这下面。可用环境变量 TEC_CORE_DATA 改。</summary>
    public static string CoreDataDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TEC_CORE_DATA");
            return !string.IsNullOrWhiteSpace(env)
                ? env
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TecCore");
        }
    }

    /// <summary>这个端口上有没有程序在听（连得上就算）。</summary>
    public static async Task<bool> IsListeningAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromMilliseconds(600));
            await tcp.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException)
        {
            ct.ThrowIfCancellationRequested();
            return false;
        }
    }

    /// <summary>
    /// 保证本机的控制核心在跑：端口上已经有了就什么都不做；没有就启动一个，等它开始听端口（最多 8 秒）。
    /// 返回空串是好了；不是空串就是没起来的原因，原话给操作人看。
    /// </summary>
    public static async Task<string> EnsureCoreAsync(int port, CancellationToken ct)
    {
        if (await IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false)) return "";
        if (!File.Exists(CorePath))
            return $"没找到控制核心程序 {CorePath}：把 hmi-cpp 编出来的 tec_core、tec_hmi 和 assets 文件夹放进工作站旁边的 hmi 文件夹";
        try
        {
            var data = CoreDataDir;
            Directory.CreateDirectory(data);
            // 增益表：界面「PID 整定」页改了会写回这个文件，所以用数据目录里自己的那一份；第一次从程序旁边拷一份出厂的过来
            var gains = Path.Combine(data, "gain-table.csv");
            var shipped = Path.Combine(Dir, "gain-table.csv");
            if (!File.Exists(gains))
            {
                if (!File.Exists(shipped)) return $"没找到出厂增益表 {shipped}：它应该和 tec_core 放在一起";
                File.Copy(shipped, gains);
            }
            var psi = new ProcessStartInfo(CorePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,          // 它没有界面，不要弹一个黑窗口出来
                WorkingDirectory = Dir
            };
            foreach (var a in new[] { "--data", data, "--gains", gains, "--port", port.ToString() }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return $"控制核心 {CorePath} 没启动起来";
            for (var i = 0; i < 40; i++)
            {
                await Task.Delay(200, ct).ConfigureAwait(false);
                if (await IsListeningAsync("127.0.0.1", port, ct).ConfigureAwait(false)) return "";
                if (p.HasExited) return $"控制核心一启动就退出了（退出码 {p.ExitCode}）：端口 {port} 可能被别的程序占着";
            }
            return $"控制核心启动了但 8 秒内没有开始听端口 {port}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return $"控制核心启动不了：{ex.Message}";
        }
    }

    private static readonly Dictionary<string, Process> Hmis = new();

    /// <summary>
    /// 打开新界面，让它连 host:port 上的控制核心。同一个地址只开一个：已经开着就不再开第二个。
    /// 返回空串是开了（或者本来就开着）；不是空串就是没开成的原因。
    /// </summary>
    public static string StartHmi(string host, int port)
    {
        var key = $"{host}:{port}";
        lock (Hmis)
        {
            if (Hmis.TryGetValue(key, out var had))
            {
                if (!had.HasExited) return "";
                had.Dispose();
                Hmis.Remove(key);
            }
            if (!File.Exists(HmiPath))
                return $"没找到界面程序 {HmiPath}：把 hmi-cpp 编出来的 tec_hmi 和 assets 文件夹放进工作站旁边的 hmi 文件夹";
            try
            {
                var psi = new ProcessStartInfo(HmiPath) { UseShellExecute = false, WorkingDirectory = Dir };
                foreach (var a in new[] { "--host", host, "--port", port.ToString() }) psi.ArgumentList.Add(a);
                var p = Process.Start(psi);
                if (p is null) return $"界面程序 {HmiPath} 没启动起来";
                Hmis[key] = p;
                return "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                return $"界面程序启动不了：{ex.Message}";
            }
        }
    }

    /// <summary>这台设备（按驱动和它的连接参数）的新界面该连哪里；不是这两种机型就返回 null（走老面板）。</summary>
    public static (string Host, int Port)? HmiEndpointOf(string driverId, Tec.Driver.Abi.ParameterSet connection) => driverId switch
    {
        TouchPanelDriver.DriverId => (connection.Str(TouchPanelDriver.Fields.Host, "127.0.0.1").Trim(), connection.Int(TouchPanelDriver.Fields.Port, 5020)),
        LocalCoreDriver.DriverId => ("127.0.0.1", connection.Int(LocalCoreDriver.Fields.Port, 5020)),
        _ => null
    };
}
