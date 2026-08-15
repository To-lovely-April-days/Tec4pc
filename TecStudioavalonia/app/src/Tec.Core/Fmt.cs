using System.Globalization;

namespace Tec.Core;

/// <summary>时间与数值的统一格式。两个视图对不上就是错的——格式化也只留一份（§13.1）。</summary>
public static class Fmt
{
    /// <summary>0:05:30 这种。负号提到最前面。</summary>
    public static string Hms(TimeSpan t)
    {
        var neg = t < TimeSpan.Zero;
        if (neg) t = t.Negate();
        return (neg ? "-" : "") +
               $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
    }

    /// <summary>+2:30 / −1:05，给偏差列用。零显示为 0:00 且不带符号。</summary>
    public static string Signed(TimeSpan t)
    {
        if (t == TimeSpan.Zero) return "0:00";
        var neg = t < TimeSpan.Zero;
        var a = neg ? t.Negate() : t;
        var body = a.TotalHours >= 1
            ? $"{(int)a.TotalHours}:{a.Minutes:D2}:{a.Seconds:D2}"
            : $"{(int)a.TotalMinutes}:{a.Seconds:D2}";
        return (neg ? "−" : "+") + body;
    }

    public static string Clock(DateTimeOffset t) => t.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Stamp(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Num(double v, int decimals = 1)
        => v.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
}
