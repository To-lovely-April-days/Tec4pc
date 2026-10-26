using System.Diagnostics;
using Tec.Drivers.Rd105;
using TecControl.Core.Protocol;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// RD105 通信异常的应答整帧丢掉、清缓冲、重发同一条，直到拿到校验通过的——最多 2 s（用户定的）。
/// ASCII 口（TecClient）和 Modbus-RTU 口（桥下的 ModbusRtuClient）两条路都按这个规矩走；
/// 2 s 内收不到对的照常报错，线真断了不会卡死总线。
/// </summary>
public sealed class Rd105RetryBudgetTests
{
    // ── ASCII 口 ─────────────────────────────────────────────────────

    private static (FakeRd105Device dev, Rd105Link link) Ascii()
    {
        var dev = new FakeRd105Device();
        var link = new Rd105Link(dev);
        link.Client.ReplyTimeoutMs = 50;
        link.Open();
        return (dev, link);
    }

    [Fact]
    public void 链路缺省按2秒重试()
    {
        var (_, link) = Ascii();
        using var _ = link;
        Assert.Equal(2000, Rd105Link.RetryBudgetMs);
        Assert.Equal(2000, link.Client.RetryBudgetMs);
    }

    [Fact]
    public async Task ASCII_连着好几帧坏帧_一直重发到拿到对的()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.GarbleNext = 5;                              // 次数制下（坏帧重试 1 次）这里早就报错了

        var tg = await link.Client.QueryAsync(1, "TG");

        Assert.Equal(25_00000, tg);
        Assert.Equal(6, dev.Commands.Count(c => c.StartsWith("TC1:TG=?")));   // 坏的 5 帧全丢掉、同一条发了 6 遍
    }

    [Fact]
    public async Task ASCII_连着好几帧没应答_一直重发到拿到对的()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.MuteNext = 4;

        var tg = await link.Client.QueryAsync(1, "TG");

        Assert.Equal(25_00000, tg);
        Assert.Equal(5, dev.Commands.Count(c => c.StartsWith("TC1:TG=?")));
    }

    [Fact]
    public async Task ASCII_写也重发到拿到对的回显()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.GarbleNext = 3;

        await link.Client.SetAsync(1, "TG", 30_00000);

        Assert.Equal(30_00000, dev.Get(1, "TG"));
    }

    [Fact]
    public async Task ASCII_一直不答_2秒左右认输_报错带重发遍数()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.Mute = true;

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => link.Client.QueryAsync(1, "TG"));
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 1800, 2600);
        Assert.Contains("2000 ms 内共发", ex.Message);
        Assert.True(dev.Commands.Count > 5, $"2 s 里只发了 {dev.Commands.Count} 遍");
    }

    [Fact]
    public async Task ASCII_一直坏帧_2秒左右认输_报协议错()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.GarbleNext = int.MaxValue;

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TecProtocolException>(() => link.Client.QueryAsync(1, "TG"));
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 1800, 2600);
        Assert.Contains("2000 ms 内共发", ex.Message);
    }

    [Fact]
    public async Task ASCII_回显真不符_只再发一次就报错_不空打2秒()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        dev.DutyMinPercent = 5;                          // 设备把小占空比改写成 5 %：帧是好的，值就是不符

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TecProtocolException>(() => link.Client.SetAsync(1, "PWMDUTY", 20_000));
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"回显不符等了 {sw.ElapsedMilliseconds} ms");
        Assert.Equal(2, dev.Commands.Count(c => c.StartsWith("TC1:PWMDUTY=")));
    }

    [Fact]
    public async Task 探测关掉按时长重试_一试一报()
    {
        var (dev, link) = Ascii();
        using var _ = link;
        link.DisableRetriesForProbe();
        dev.Mute = true;

        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => link.Client.QueryAsync(1, "TG"));
        Assert.True(sw.ElapsedMilliseconds < 500, $"探测等了 {sw.ElapsedMilliseconds} ms");
        Assert.Single(dev.Commands);
    }

    // ── Modbus-RTU 口 ────────────────────────────────────────────────

    private static (FakeModbusSlave dev, Rd105Link link) Modbus()
    {
        var dev = new FakeModbusSlave { Station = 1 };
        dev.Regs[0x1000] = (ushort)(25_00000 >> 16);
        dev.Regs[0x1001] = unchecked((ushort)25_00000);
        var link = new Rd105Link(new Rd105ModbusBridge(dev, 1, 100));
        link.Client.ReplyTimeoutMs = 2500;               // 上层别抢在桥下前面认输
        link.Bridge!.Modbus.RetryBackoffMs = 20;
        link.Open();
        return (dev, link);
    }

    [Fact]
    public async Task Modbus_连着好几帧CRC坏_桥下一直重发到拿到对的()
    {
        var (dev, link) = Modbus();
        using var _ = link;
        dev.CorruptCrcNext = 5;                          // 次数制下（坏帧重试 2 次）这里早就报错了

        var tg = await link.Client.QueryAsync(1, "TG");

        Assert.Equal(25_00000, tg);
        Assert.Equal(6, dev.Raw.Count);
    }

    [Fact]
    public async Task Modbus_连着好几帧烂尾_桥下一直重发到拿到对的()
    {
        var (dev, link) = Modbus();
        using var _ = link;
        dev.TruncateNext = 4;

        Assert.Equal(25_00000, await link.Client.QueryAsync(1, "TG"));
    }

    [Fact]
    public async Task Modbus_固件永远答不对的可缺量_不按2秒空打_快照照常快()
    {
        // 现场那台读 RESISTOR 只给一半长度：可缺量按次数重试，不在它身上耗 2 s
        var (dev, link) = Modbus();
        using var _ = link;
        dev.RegsPerReadCap = 2;

        var sw = Stopwatch.StartNew();
        var snap = await link.Controller.ReadSnapshotAsync();
        sw.Stop();

        Assert.True(double.IsNaN(snap.Resistor1Ohms));
        Assert.True(sw.ElapsedMilliseconds < 1000, $"一拍快照用了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Modbus_电阻永远答一半_丢掉当没有_温度照常_以后不再问()
    {
        // 现场截图：读 0x1004 收 01 03 08 00 00 00 00 06 9C 83（报 8 字节只来 7 个）——
        // 从前这个「应答烂尾」漏过桥、整拍 DATADEMAND 失败
        var (dev, link) = Modbus();
        using var _ = link;
        dev.Regs[0x1002] = (ushort)(22_59187 >> 16);
        dev.Regs[0x1003] = unchecked((ushort)22_59187);
        dev.TruncateAlwaysAt.Add(0x1004);

        var snap = await link.Controller.ReadSnapshotAsync();
        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.True(double.IsNaN(snap.Resistor1Ohms));
        var asked = dev.Requests.Count(r => r == "读寄存器 4100+4");

        var sw = Stopwatch.StartNew();
        snap = await link.Controller.ReadSnapshotAsync();
        sw.Stop();
        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.Equal(asked, dev.Requests.Count(r => r == "读寄存器 4100+4"));   // 第二拍不再问它
        Assert.True(sw.ElapsedMilliseconds < 300, $"第二拍用了 {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Modbus_温度这一路烂尾_照样按2秒重发救回()
    {
        // 必须有的量（TCADJTEMP）不享受「当没有」：烂尾就丢掉重发
        var (dev, link) = Modbus();
        using var _ = link;
        dev.Regs[0x1002] = (ushort)(22_59187 >> 16);
        dev.Regs[0x1003] = unchecked((ushort)22_59187);
        dev.TruncateNext = 6;

        var snap = await link.Controller.ReadSnapshotAsync();
        Assert.Equal(22.59187, snap.Temp1C, 5);
        Assert.True(dev.Requests.Count(r => r == "读寄存器 4098+2") >= 7);   // 坏的 6 帧都丢了、重发到拿到对的
    }

    [Fact]
    public async Task Modbus_一直不答_2秒左右认输_不是每帧各等2秒()
    {
        var (dev, link) = Modbus();
        using var _ = link;
        dev.Mute = true;

        var sw = Stopwatch.StartNew();
        // 写 = 桥下两帧（写 + 回读）：两帧共用一个 2 s，不是 4 s
        await Assert.ThrowsAsync<TimeoutException>(() => link.Client.SetAsync(1, "TG", 30_00000));
        sw.Stop();

        Assert.InRange(sw.ElapsedMilliseconds, 1800, 2800);
    }
}
