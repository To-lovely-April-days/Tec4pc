namespace Tec.Core.Recipes;

/// <summary>
/// 配方级变量（对照 GBG Process 视图左缘的 Variables 栏）。
///
/// 只有数值一种类型：条件（Cond）比较的是数，开关就是 0 / 1，
/// 多一套类型系统换不来表达力，只多一批「类型不匹配」的报错。
/// 启动时按 Init 装进执行器；运行中由「设定变量」改，循环条件、
/// 条件等待随时读。单位、说明只给人看，机器不碰。
/// </summary>
public sealed class RecipeVariable
{
    /// <summary>界面认行的凭据（选中、撤销合并），不参与求值。</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>在条件里引用的名字。合法性见 Cond.ValidName，校验器把关。</summary>
    public string Name { get; set; } = "";

    /// <summary>启动那一刻的取值。</summary>
    public double Init { get; set; }

    /// <summary>只用于显示。</summary>
    public string Unit { get; set; } = "";

    public string? Note { get; set; }

    public RecipeVariable Clone() => new()
    {
        Id = Id,
        Name = Name,
        Init = Init,
        Unit = Unit,
        Note = Note
    };
}
