using Tec.Core.Data;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// 台面只重连一路的设备时，安全层只撤那一路由设备 Limits 推出来的底线限值，别的通道和配方限值不动
/// （从前台面一动全清再推，操作人收紧的也跟着丢）。
/// </summary>
public sealed class SafetyDeviceLimitRemovalTests
{
    [Fact]
    public void 只撤那一路的设备底线限值_别的通道和配方限值不动()
    {
        var m = new SafetyMonitor(new DataPipeline());
        m.Add(SafetyMonitor.FromTemperature(1, new TempLimits(-40, 180, 5)));
        m.Add(SafetyMonitor.FromTemperature(2, new TempLimits(-40, 180, 5)));
        m.Add(new SafetyLimit(1, "fault", null, 0, null, TimeSpan.FromSeconds(1), SafetyAction.AbortChannel) { FromDeviceLimits = true });
        m.SetRecipeLimit(new SafetyLimit(1, "Tr", null, 80, null, TimeSpan.Zero, SafetyAction.Alarm));

        var removed = m.RemoveDeviceLimits(1);

        Assert.Equal(2, removed);                                            // CH1 的 Tr 底线 + 告警字
        Assert.DoesNotContain(m.Limits, l => l.Channel == 1 && l.FromDeviceLimits);
        Assert.Contains(m.Limits, l => l.Channel == 2 && l.FromDeviceLimits);   // CH2 没受影响
        Assert.Contains(m.Limits, l => l.Channel == 1 && l.FromRecipe);          // 配方限值不是设备底线，留着
    }
}
