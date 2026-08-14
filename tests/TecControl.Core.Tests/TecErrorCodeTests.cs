using TecControl.Core.Protocol;

namespace TecControl.Core.Tests;

public class TecErrorCodeTests
{
    [Fact]
    public void Value32_IsCh1SensorOutOfRange()
    {
        // 协议 3.5.8：值为 32 时 Bit5 为 1，通道 1 传感器温度越限
        var code = (TecErrorCode)32;
        Assert.Equal(TecErrorCode.Ch1SensorOutOfRange, code);
        var texts = code.Describe();
        Assert.Single(texts);
        Assert.Contains("通道1", texts[0]);
    }

    [Fact]
    public void None_DescribesEmpty() =>
        Assert.Empty(TecErrorCode.None.Describe());

    [Fact]
    public void MultipleBits_AllDescribed()
    {
        var code = TecErrorCode.UnderVoltage | TecErrorCode.Ch2CurrentLimiting;
        var texts = code.Describe();
        Assert.Equal(2, texts.Count);
    }
}
