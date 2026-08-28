using Tec.Core.Data;
using Tec.Core.Safety;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// HMI 面板「反应釜与安全」页收的操作人限值层（0252）。规矩与配方层同宗：
/// 只能在底线之内收紧、同量只留最新一条；但生命期不同——不跟批次走，
/// 设回底线值才撤。这里盯登记语义与钳位，不盯求值（求值路径老测试盯着）。
/// </summary>
public class OperatorLimitTests
{
    private static SafetyMonitor Rig(out SafetyLimit baseline)
    {
        var m = new SafetyMonitor(new DataPipeline());
        baseline = new SafetyLimit(1, "Tr", -41, 181, 20, TimeSpan.FromSeconds(3),
                                   SafetyAction.AbortChannel)
        { FromDeviceLimits = true };
        m.Add(baseline);
        return m;
    }

    [Fact]
    public void 在底线之内收紧_登记为操作人层()
    {
        var m = Rig(out _);

        var lim = m.SetOperatorLimit(1, "Tr", -20, 120, 10);

        Assert.NotNull(lim);
        Assert.True(lim!.FromOperator);
        Assert.Equal(-20, lim.Min);
        Assert.Equal(120, lim.Max);
        Assert.Equal(10, lim.MaxRatePerMin);
        // 动作沿用底线：操作人收的是「多早动手」，不是「动什么手」
        Assert.Equal(SafetyAction.AbortChannel, lim.Action);
        // 与底线并存、身份分层——不然两层共用一份去抖状态
        Assert.Equal(2, m.Limits.Count);
        var keys = m.Limits.Select(SafetyMonitor.KeyOf).ToList();
        Assert.Equal(2, keys.Distinct().Count());
        Assert.Contains(keys, k => k.EndsWith("|op"));
    }

    [Fact]
    public void 想放宽只会被按回底线_全松了等于撤层()
    {
        var m = Rig(out _);

        // 越过底线往外放：三个数都被钳回底线 → 这一层根本不该存在
        var lim = m.SetOperatorLimit(1, "Tr", -60, 200, 30);

        Assert.Null(lim);
        Assert.Single(m.Limits);            // 只剩底线
        Assert.DoesNotContain(m.Limits, l => l.FromOperator);
    }

    [Fact]
    public void 部分出界的按到底线_没出界的照收()
    {
        var m = Rig(out _);

        var lim = m.SetOperatorLimit(1, "Tr", -60, 100, null);

        Assert.NotNull(lim);
        Assert.Equal(-41, lim!.Min);        // 出界的那头按回底线
        Assert.Equal(100, lim.Max);         // 收紧的那头照收
    }

    [Fact]
    public void 同量重设只留最新一条_设回底线即撤()
    {
        var m = Rig(out _);

        m.SetOperatorLimit(1, "Tr", null, 120, null);
        m.SetOperatorLimit(1, "Tr", null, 90, null);
        Assert.Single(m.Limits.Where(l => l.FromOperator));
        Assert.Equal(90, m.Limits.Single(l => l.FromOperator).Max);

        // 设回底线值 = 回到底线，这一层撤掉
        var back = m.SetOperatorLimit(1, "Tr", -41, 181, 20);
        Assert.Null(back);
        Assert.DoesNotContain(m.Limits, l => l.FromOperator);
    }
}
