using System.Collections.Concurrent;
using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 【本地改动】仿真世界的「同一只釜」：几台仿真设备之间物理上共享的量（2026-11-23，演示模式）。
///
/// WHY：真机上泵往釜里加的酸，釜里那支 pH 电极自然量得到——两台设备靠**釜里的料**联着。
/// 仿真里泵会话和 pH 会话是两个对象、互不认识，没有这层「釜」的话 pH 反馈加料永远停不下来：
/// 泵一直加、pH 曲线照自己的时间函数走。这里按**系统通道号**记每一路累计加进去的滴定剂体积，
/// 泵会话每拍往里加、pH 探头每拍来读。只在同一个程序集 / 同一个加载上下文里有效——
/// 所以演示模式把仿真驱动包整包装进一个 AssemblyLoadContext（见 DriverManifest.SiblingTypes）。
/// </summary>
public static class SimWorld
{
    private static readonly ConcurrentDictionary<int, double> _dosed = new();

    /// <summary>这一路累计加进釜里的滴定剂（mL）。没加过是 0。</summary>
    public static double Dosed(int channel) => _dosed.TryGetValue(channel, out var v) ? v : 0;

    /// <summary>泵送进去了一点。</summary>
    public static void AddDosed(int channel, double mL)
    {
        if (mL <= 0 || channel <= 0) return;
        _dosed.AddOrUpdate(channel, mL, (_, old) => old + mL);
    }

    /// <summary>换一釜料（测试 / 重开会话）。</summary>
    public static void Reset(int channel) => _dosed.TryRemove(channel, out _);

    public static void ResetAll() => _dosed.Clear();

    /// <summary>
    /// 仿真料液的滴定曲线：往一釜弱碱液里滴酸，pH 从 10.3 左右经中和点落到 3.7 左右。
    /// 中和点在 15 mL（tanh 的拐点），4.5 mL 的宽度让 7.0 附近有一段可控的斜坡——
    /// 「pH 反馈加料到 7.0 ± 0.2」能在十几分钟（仿真时间）里真的停在死区里。
    /// </summary>
    public static double TitrationPh(double dosedMl) => 7.0 - 3.3 * Math.Tanh((dosedMl - 15.0) / 4.5);
}
