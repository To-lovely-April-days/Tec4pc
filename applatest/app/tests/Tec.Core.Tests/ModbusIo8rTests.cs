using Tec.Drivers.DualStation;
using Tec.Drivers.DualStation.Modbus;
using Xunit;

namespace Tec.Core.Tests;

/// <summary>
/// Modbus RTU 帧层 + IO8R 客户端（双工位反应主机实施第 1 步，需求 §8）。
/// 走假从站把整条链路跑一遍：帧对不对拿 golden 字节钉死（CRC 先过标准校验
/// 向量 0x4B37，golden 帧再用它独立算出来，其中读 DO 那帧与 IO8R 手册示例一致）；
/// 容错四条路各自要报对错：不答（超时）、答坏 CRC、别的站插话、回异常码。
/// </summary>
public class ModbusIo8rTests
{
    private static (FakeModbusSlave dev, Io8rClient io, ModbusRtuClient bus) Rig(int timeoutMs = 200)
    {
        var dev = new FakeModbusSlave();
        dev.Open();
        var bus = new ModbusRtuClient(dev, 1, timeoutMs);
        return (dev, new Io8rClient(bus), bus);
    }

    // ── 帧本身 ───────────────────────────────────────────────────────

    [Fact]
    public void CRC_标准校验向量()
        => Assert.Equal(0x4B37, ModbusCrc.Compute("123456789"u8));

    [Fact]
    public async Task 写单点_golden帧_且假从站真切了()
    {
        var (dev, io, _) = Rig();
        await io.SetRelayAsync(3, closed: true);
        Assert.Equal(new byte[] { 0x01, 0x05, 0x00, 0x03, 0xFF, 0x00, 0x7C, 0x3A }, dev.Raw[0]);
        Assert.True(dev.Coils[3]);

        await io.SetRelayAsync(3, closed: false);
        Assert.False(dev.Coils[3]);
    }

    [Fact]
    public async Task 读DO_请求帧与厂家手册示例一致_位序低位在前()
    {
        var (dev, io, _) = Rig();
        dev.Coils[0] = dev.Coils[7] = true;
        var s = await io.ReadRelaysAsync();
        // JYMODBUSIO8R 手册的读 DO 示例帧就是 01 01 00 00 00 08 3D CC
        Assert.Equal(new byte[] { 0x01, 0x01, 0x00, 0x00, 0x00, 0x08, 0x3D, 0xCC }, dev.Raw[0]);
        Assert.Equal(new[] { true, false, false, false, false, false, false, true }, s);
    }

    [Fact]
    public async Task 读DI_光耦状态按位对号()
    {
        var (dev, io, _) = Rig();
        dev.Inputs[1] = true;
        var s = await io.ReadInputsAsync();
        Assert.True(s[1]);
        Assert.Equal(1, s.Count(x => x));
    }

    [Fact]
    public async Task 全断_一帧写完八点()
    {
        var (dev, io, _) = Rig();
        Array.Fill(dev.Coils, true);
        await io.AllOffAsync();
        Assert.Equal(new byte[] { 0x01, 0x0F, 0x00, 0x00, 0x00, 0x08, 0x01, 0x00, 0xFE, 0x95 }, dev.Raw[0]);
        Assert.All(dev.Coils, c => Assert.False(c));
        Assert.Single(dev.Requests);            // 是一帧 FC0F，不是八帧 FC05
    }

    // ── 寄存器（宇电那步就吃这三条功能码） ──────────────────────────

    [Fact]
    public async Task 读保持寄存器_字内大端()
    {
        var (dev, _, bus) = Rig();
        dev.Regs[1536] = 2500;                  // 宇电 PV1 的地址，值 2500 = 250.0 ℃ 那种整数
        dev.Regs[1537] = 0x1234;
        var v = await bus.ReadHoldingRegistersAsync(1536, 2);
        Assert.Equal(new ushort[] { 2500, 0x1234 }, v);
    }

    [Fact]
    public async Task 写单寄存器_回显核对()
    {
        var (dev, _, bus) = Rig();
        await bus.WriteRegisterAsync(2117, 9655);   // 宇电 Srun 全停，将来安全联锁就这条
        Assert.Equal(9655, (int)dev.Regs[2117]);
    }

    [Fact]
    public async Task 写多寄存器()
    {
        var (dev, _, bus) = Rig();
        await bus.WriteRegistersAsync(100, new ushort[] { 1, 2, 3 });
        Assert.Equal(1, (int)dev.Regs[100]);
        Assert.Equal(2, (int)dev.Regs[101]);
        Assert.Equal(3, (int)dev.Regs[102]);
    }

    // ── 容错：四种失败各报各的 ──────────────────────────────────────

    [Fact]
    public async Task 从站回异常码_翻成人话()
    {
        var (dev, io, _) = Rig();
        dev.RejectWith = 0x02;
        var ex = await Assert.ThrowsAsync<ModbusException>(() => io.SetRelayAsync(0, true));
        Assert.Equal((byte)0x02, ex.Code);
        Assert.Contains("非法数据地址", ex.Message);
    }

    [Fact]
    public async Task 不应答_超时报错不挂死()
    {
        var (dev, io, _) = Rig(timeoutMs: 100);
        dev.Mute = true;
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => io.ReadRelaysAsync());
        Assert.Contains("100 ms", ex.Message);
    }

    [Fact]
    public async Task 应答CRC坏_报错说清是线路问题()
    {
        var (dev, io, _) = Rig();
        dev.CorruptCrc = true;
        var ex = await Assert.ThrowsAsync<ModbusException>(() => io.ReadRelaysAsync());
        Assert.Contains("CRC", ex.Message);
    }

    [Fact]
    public async Task 别的站号答话_不认()
    {
        var (dev, io, _) = Rig();
        dev.AnswerStation = 2;
        var ex = await Assert.ThrowsAsync<ModbusException>(() => io.ReadRelaysAsync());
        Assert.Contains("站号", ex.Message);
    }

    [Fact]
    public async Task 点位越界_本地就拒_不上总线()
    {
        var (dev, io, _) = Rig();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => io.SetRelayAsync(8, true));
        Assert.Empty(dev.Raw);
    }
}
