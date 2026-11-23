namespace Tec.Core;

/// <summary>
/// 【本地改动】演示模式开关（2026-11-23，用户要在没接真机的电脑上截软件截图写招标文档）。
///
/// WHY：程序里的铁规矩是「界面上每个数都有出处、绝不摆演示数据」，仿真驱动也早已从程序里拿掉。
/// 这条规矩**只在这里例外**，而且例外必须是显式的：启动参数 <c>--demo</c>（或环境变量
/// <c>TEC_DEMO=1</c>）才进演示模式；不带开关时这个类的每个判断都是 false，所有演示代码路径一行不走，
/// 程序行为与从前一模一样。进了演示模式，界面常驻「演示数据」标记——截图上看得见，谁都不会把它当实测。
///
/// 放在 Tec.Core 是因为工作站（Workspace）、设备端（HmiRuntime）、两边的界面都要问它；
/// 这里只有「开没开 / 时标 / 数据目录」三件事，不认识任何驱动名——演示工程的内容在 Tec.Demo。
/// </summary>
public static class DemoMode
{
    /// <summary>启动参数。</summary>
    public const string Switch = "--demo";

    /// <summary>环境变量（Windows 快捷方式不方便带参数时用它）。</summary>
    public const string EnvVar = "TEC_DEMO";

    /// <summary>界面上那枚签的字。</summary>
    public const string Badge = "演示数据";

    /// <summary>开没开。缺省 false；由各可执行的 Main 按参数 / 环境变量调 <see cref="Arm"/> 打开。</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// 演示的仿真时钟倍率。10×：启动几分钟后趋势 / 甘特 / 执行记录就有看头，
    /// 又不至于快到曲线失真。真机恒为 1，这个数只在 Enabled 时被读。
    /// </summary>
    public static double TimeScale { get; private set; } = 10;

    /// <summary>
    /// 按启动参数与环境变量决定开不开。返回是否打开了。
    /// 可重复调用；只认 <c>--demo</c>（不区分大小写）与 <c>TEC_DEMO</c> 为 1 / true / yes。
    /// <c>--demo=5</c> 这种带数的写法改时标倍率（1～60）。
    /// </summary>
    public static bool Arm(IEnumerable<string>? args)
    {
        var on = Parse(args, out var scale);
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (env is not null && env.Trim() is "1" or "true" or "TRUE" or "True" or "yes" or "YES") on = true;

        Enabled = on;
        TimeScale = scale;
        return on;
    }

    /// <summary>只看启动参数、不动全局开关（测试用它）。scale 是解析出的时标，没写就是 10。</summary>
    public static bool Parse(IEnumerable<string>? args, out double scale)
    {
        var on = false;
        scale = 10;
        foreach (var a in args ?? Array.Empty<string>())
        {
            if (a.Equals(Switch, StringComparison.OrdinalIgnoreCase)) on = true;
            else if (a.StartsWith(Switch + "=", StringComparison.OrdinalIgnoreCase))
            {
                on = true;
                if (double.TryParse(a[(Switch.Length + 1)..], System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var s))
                    scale = s;
            }
        }
        scale = Math.Clamp(scale, 1, 60);
        return on;
    }

    /// <summary>测试用：直接设开关，不看参数。</summary>
    public static void Set(bool enabled, double timeScale = 10)
    {
        Enabled = enabled;
        TimeScale = Math.Clamp(timeScale, 1, 60);
    }

    /// <summary>
    /// 演示数据的根目录：系统临时目录下 <c>TecStudio-Demo</c>。
    /// **不在用户的 AppData 里**——演示工程是一次性的、每次启动重建，不能跟操作人自己的台面、配方库、归档混在一起。
    /// </summary>
    public static string RootDir => Path.Combine(Path.GetTempPath(), "TecStudio-Demo");

    /// <summary>工作站用的演示数据目录。</summary>
    public static string StudioDataDir => Path.Combine(RootDir, "Studio");

    /// <summary>设备端 HMI 用的演示数据目录。</summary>
    public static string HmiDataDir => Path.Combine(RootDir, "Hmi");

    /// <summary>标记文件：目录里有它才允许整目录清空——绝不清一个不是我们建的目录。</summary>
    public const string Marker = "demo-pack.marker";

    /// <summary>
    /// 把一个演示数据目录重置成空的（有标记文件才删旧内容），再写上标记。
    /// 每次演示启动都从同一份生成结果出发，截出来的图才一致。
    /// </summary>
    public static string ResetDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            if (File.Exists(Path.Combine(dir, Marker)))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* 上一个演示进程还没退干净：留着，下面照常覆盖写 */ }
            }
            else
            {
                // 没有标记 = 不是我们建的；换一个带时间戳的旁路目录，不碰它
                dir = dir + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            }
        }
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, Marker),
            "TecStudio 演示模式（--demo）生成的一次性数据目录，可整目录删除。\n");
        return dir;
    }
}
