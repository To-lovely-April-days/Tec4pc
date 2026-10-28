using Tec.Driver.Abi;
using Tec.Hmi.Ui.ViewModels;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// HMI 中途「开不开冷却水」弹窗的状态机（纯状态，不碰 Avalonia）：同一个问题只弹一次、取消了不再弹、别的窗开着先等、
/// 问题收回就关窗、换了问题（另一路接着问）再弹。
/// </summary>
public sealed class WaterAskPrompterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);
    private static WaterAsk Ask(int id, int ch = 1) => new(id, ch, "工位 A 要切 TEC 制冷", T0, T0.AddSeconds(60));

    [Fact]
    public void 挂上就开窗_同一个问题之后只刷副标题_收回就关窗()
    {
        var p = new WaterAskPrompter();
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(null, false, false));
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.RefreshSub, p.Step(Ask(1), true, true));
        Assert.Equal(WaterAskPrompter.Action.RefreshSub, p.Step(Ask(1), true, true));
        Assert.Equal(WaterAskPrompter.Action.Close, p.Step(null, true, true));
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(null, false, false));
    }

    [Fact]
    public void 人答了窗已关_问题收回时不再关一次()
    {
        var p = new WaterAskPrompter();
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(null, false, false));   // 答复后窗已经关了
    }

    [Fact]
    public void 取消了这个问题不再弹_换了问题再弹()
    {
        var p = new WaterAskPrompter();
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));
        p.Dismiss(1);
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(2, ch: 2), false, false));
        Assert.Equal(WaterAskPrompter.Action.Close, p.Step(null, true, true));
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(3), false, false));   // 收回之后取消记录也清了
    }

    [Fact]
    public void 本窗开着问题换了_用新问题换掉旧窗()
    {
        var p = new WaterAskPrompter();
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(2, ch: 2), true, true));   // 旧窗还开着：换成新问题
        Assert.Equal(WaterAskPrompter.Action.RefreshSub, p.Step(Ask(2, ch: 2), true, true));
    }

    [Fact]
    public void 别的窗开着先不弹_关了再弹_本窗被盖掉当没开过()
    {
        var p = new WaterAskPrompter();
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(Ask(1), true, false));    // 换模式确认那张开着
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));
        Assert.Equal(WaterAskPrompter.Action.None, p.Step(Ask(1), true, false));    // 被别的窗盖掉了
        Assert.Equal(WaterAskPrompter.Action.Open, p.Step(Ask(1), false, false));   // 那张关了：再弹
    }
}
