using System.Globalization;

namespace Tec.Driver.Abi;

/// <summary>
/// 指令描述文案里用的数值格式。对应原型的 fx = v => Math.round(v*100)/100：
/// 保留两位有效小数，末尾的零不显示（60 而不是 60.00，0.5 而不是 0.50）。
/// 摘要文案要和原型逐字一致，格式化就只能有这一份。
/// </summary>
public static class Txt
{
    public static string Fx(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Fx(object? v)
        => v is null ? "" : v is double d ? Fx(d) : Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
}
