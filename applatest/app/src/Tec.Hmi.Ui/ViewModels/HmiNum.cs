namespace Tec.Hmi.Ui.ViewModels;

/// <summary>
/// 面板上的数怎么念。负号用「−」（U+2212），四舍五入以后是零就不带符号——
/// 从前 −0.03 印成「−0.0」（2026-11-03 现场 0 ℃ 那次在屏上看到）。
/// </summary>
public static class HmiNum
{
    /// <summary>没读到（null / NaN）念「—」。</summary>
    public static string Fixed(double? v, int decimals)
        => v is { } x && !double.IsNaN(x) ? Body(x, decimals, plus: false) : "—";

    /// <summary>带正负号（差值用）：+1.5 / −1.5，零念「0.0」。</summary>
    public static string Signed(double v, int decimals)
        => double.IsNaN(v) ? "—" : Body(v, decimals, plus: true);

    private static string Body(double x, int decimals, bool plus)
    {
        var s = Math.Abs(x).ToString(decimals <= 0 ? "0" : "0." + new string('0', decimals));
        if (s.All(c => c is '0' or '.' or ',')) return s;
        return (x < 0 ? "−" : plus ? "+" : "") + s;
    }
}
