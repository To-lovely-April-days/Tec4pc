using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105.Modbus;
using Tec.Io8rTest;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// IO8R 继电器测试小程序的逻辑：只动人点的那一路、读回核对、同通道加热棒与制冷互斥、制冷没水提醒、走人全断。
/// TEC 由别的电脑控制时，现场拿它单独给通道 1 合制冷（DO6）。
/// </summary>
public sealed class Io8rTesterTests
{
    private static (FakeModbusSlave Io, Io8rTester T) Rig()
    {
        var io = new FakeModbusSlave();
        io.Open();                                        // 真程序里 OpenSerialAsync 开口
        return (io, new Io8rTester(new Io8rClient(new ModbusRtuClient(io, 1, 200))));
    }

    private static List<string> Writes(FakeModbusSlave io) => io.Requests.Where(r => r.StartsWith("写")).ToList();

    [Fact]
    public async Task 合通道1制冷_只写DO6_读回闭合()
    {
        var (io, t) = Rig();
        io.Coils[4] = true;                               // 冷却水阀本来开着：不该被碰
        await t.RefreshAsync(CancellationToken.None);

        await t.SetAsync(6, true, CancellationToken.None);

        Assert.True(io.Coils[6]);
        Assert.True(t.Relays[6]);
        Assert.True(io.Coils[4]);
        Assert.Equal(new[] { "写DO 6=闭" }, Writes(io));
        Assert.Null(t.Warning);                           // 有水：不提醒

        await t.SetAsync(6, false, CancellationToken.None);
        Assert.False(io.Coils[6]);
    }

    [Fact]
    public async Task 连上先读回_不先全断()
    {
        var (io, t) = Rig();
        io.Coils[2] = true;
        await t.RefreshAsync(CancellationToken.None);
        Assert.True(t.Relays[2]);
        Assert.Empty(Writes(io));
    }

    [Fact]
    public async Task 同通道加热棒合着_制冷合不上_反过来也一样()
    {
        var (io, t) = Rig();
        io.Coils[0] = true;                               // 模块上人手合的：合之前要先读一遍才看得见

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.SetAsync(6, true, CancellationToken.None));
        Assert.Contains("先断 DO0", ex.Message);
        Assert.False(io.Coils[6]);

        io.Coils[0] = false;
        io.Coils[7] = true;
        ex = await Assert.ThrowsAsync<InvalidOperationException>(() => t.SetAsync(1, true, CancellationToken.None));
        Assert.Contains("先断 DO7", ex.Message);

        await t.SetAsync(0, true, CancellationToken.None);   // 另一个通道的制冷合着不碍事
        Assert.True(io.Coils[0]);
    }

    [Fact]
    public async Task 制冷合着冷却水阀没开_提醒不拦()
    {
        var (io, t) = Rig();
        await t.SetAsync(6, true, CancellationToken.None);
        Assert.True(io.Coils[6]);
        Assert.Contains("干烧", t.Warning);

        await t.SetAsync(4, true, CancellationToken.None);
        Assert.Null(t.Warning);
    }

    [Fact]
    public async Task 八路全断_走人也全断()
    {
        var (io, t) = Rig();
        await t.SetAsync(4, true, CancellationToken.None);
        await t.SetAsync(6, true, CancellationToken.None);

        await t.AllOffAsync(CancellationToken.None);
        Assert.DoesNotContain(true, io.Coils);

        await t.SetAsync(2, true, CancellationToken.None);
        await t.DisposeAsync();
        Assert.DoesNotContain(true, io.Coils);
    }

    [Fact]
    public void 八路说明跟现场接线一致()
    {
        Assert.Equal(8, Io8rTester.Points.Count);
        Assert.Equal("通道 1 制冷", Io8rTester.Points[6].Name);
        Assert.Equal("冷却水阀", Io8rTester.Points[4].Name);
        Assert.Equal("通道 2 使能", Io8rTester.Points[3].Name);
    }
}
