using Tec.Core.Recipes;

namespace Tec.Core.Execution;

/// <summary>
/// 启动被校验器拦下（CH-5 的「阻断下发」）。
///
/// 校验器早就会报 Error，但从前只用来把配方页的提示条染红——启动路径看都不看，
/// 「错误」和「警告」的差别只是颜色。这条异常让 Error 真的挡事：
/// 参数超范围、指令没安装、通道缺能力、泵没标定，带着这些毛病的配方不许下发。
///
/// 消息给操作人念第一条，其余给个数——启动按钮旁边那一行地方就那么宽，
/// 逐条看去配方页的校验面板。完整清单在 <see cref="Errors"/> 里，界面要列全随时有。
/// </summary>
public sealed class RecipeRejectedException : InvalidOperationException
{
    public int Channel { get; }
    public IReadOnlyList<ValidationIssue> Errors { get; }

    public RecipeRejectedException(int channel, IReadOnlyList<ValidationIssue> errors)
        : base(Compose(channel, errors))
    {
        Channel = channel;
        Errors = errors;
    }

    private static string Compose(int channel, IReadOnlyList<ValidationIssue> errors)
    {
        var head = $"CH{channel} 不能启动：{errors[0].Message}";
        return errors.Count == 1 ? head + "。"
            : head + $"（共 {errors.Count} 条错误，其余见配方页校验面板）";
    }
}
