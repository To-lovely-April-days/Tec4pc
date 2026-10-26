using TecControl.Core.Comm;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Core.Tests;

/// <summary>
/// 一台假冷水机主板（《主机触模屏通讯协议2023》）：FC05 写 0x0200 开机 / 0x0201 关机，FC06 写 0x0200 = 0x0072 / 0x0070，
/// FC04 / FC03 读 0x0000 运行状态、FC03 读 0x0403 起停方式。停机走「延时停机」：要测试自己调 Settle() 才变空闲——
/// 驱动「冷水机停稳之前不关阀」就靠这一步验。
/// </summary>
public sealed class FakeChiller : ISerialTransport
{
    private readonly List<byte> _rx = new();
    private readonly Queue<byte> _tx = new();

    public byte Station { get; set; } = 1;

    /// <summary>机组运行状态：0 空闲 / 1 准备启动 / 2 正常运行 / 3 延时停机 / 4 故障。</summary>
    public ushort State { get; set; }

    /// <summary>起停方式（0x0403）：0 本地 / 1 本地+远程 / 2 远程。0 时开关机指令照收、不动作（真机就是不理）。</summary>
    public ushort StartStopMode { get; set; } = 1;

    /// <summary>收到停机后直接空闲（不经延时停机）。</summary>
    public bool StopsAtOnce { get; set; }

    /// <summary>不认 FC04（回非法功能码），只认 FC03——协议应用实例那种。</summary>
    public bool OnlyFc03 { get; set; }

    /// <summary>不认 FC05（回非法功能码），只认 FC06 写命令字。</summary>
    public bool OnlyFc06 { get; set; }

    /// <summary>静默丢 FC04：连异常码都不回（有的固件对不认识的功能码就是不搭理）。
    /// 主站只能等到超时——验证「超时也换 FC03」那条退路。</summary>
    public bool DropFc04 { get; set; }

    /// <summary>静默丢 FC05：同上，验证「超时也换 FC06」。</summary>
    public bool DropFc05 { get; set; }

    /// <summary>置 true 后不应答（冷水机断电 / 线断了）。</summary>
    public bool Mute { get; set; }

    /// <summary>收到的指令（人话），按先后顺序：「开机」「关机」「读状态」「读起停方式」。</summary>
    public List<string> Requests { get; } = new();

    /// <summary>延时停机走完：变空闲。</summary>
    public void Settle()
    {
        if (State == 3) State = 0;
    }

    public bool IsOpen { get; private set; }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void DiscardInput() => _tx.Clear();
    public void Dispose() => IsOpen = false;

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!IsOpen) throw new InvalidOperationException("串口没打开");
        for (var i = 0; i < count; i++) _rx.Add(buffer[offset + i]);
        while (_rx.Count >= 8)
        {
            var frame = _rx.GetRange(0, 8).ToArray();
            _rx.RemoveRange(0, 8);
            Handle(frame);
        }
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var n = 0;
        while (n < count && _tx.Count > 0) buffer[offset + n++] = _tx.Dequeue();
        return n;
    }

    private void Handle(byte[] f)
    {
        if (!ModbusCrc.Check(f) || f[0] != Station) return;
        if (Mute) { Requests.Add("muted"); return; }
        var fc = f[1];
        if (fc == 0x04 && DropFc04) { Requests.Add("FC04静默丢弃"); return; }
        if (fc == 0x05 && DropFc05) { Requests.Add("FC05静默丢弃"); return; }
        var addr = f[2] << 8 | f[3];
        var val = f[4] << 8 | f[5];
        switch (fc)
        {
            case 0x05 when OnlyFc06:
                Reject(fc, 0x01);
                return;
            case 0x05 when addr is 0x0200 or 0x0201 && val == 0xFF00:
                Command(addr == 0x0200);
                Reply(f[1..6]);
                return;
            case 0x06 when addr == 0x0200 && val is 0x0072 or 0x0070:
                Command(val == 0x0072);
                Reply(f[1..6]);
                return;
            case 0x04 when OnlyFc03:
                Reject(fc, 0x01);
                return;
            case 0x03 or 0x04 when addr == 0x0000 && val == 1:
                Requests.Add("读状态");
                Reply(new byte[] { fc, 2, (byte)(State >> 8), (byte)State });
                return;
            case 0x03 when addr == 0x0403 && val == 1:
                Requests.Add("读起停方式");
                Reply(new byte[] { fc, 2, (byte)(StartStopMode >> 8), (byte)StartStopMode });
                return;
            default:
                Reject(fc, 0x02);
                return;
        }
    }

    private void Command(bool start)
    {
        Requests.Add(start ? "开机" : "关机");
        if (StartStopMode == 0) return;                          // 只能本地启停：远程指令不理
        if (start) { if (State is 0 or 3) State = 2; }
        else if (State is 1 or 2) State = (ushort)(StopsAtOnce ? 0 : 3);
    }

    private void Reject(byte fc, byte code) => Reply(new byte[] { (byte)(fc | 0x80), code });

    private void Reply(byte[] pdu)
    {
        var frame = new byte[1 + pdu.Length + 2];
        frame[0] = Station;
        Array.Copy(pdu, 0, frame, 1, pdu.Length);
        ModbusCrc.Append(frame);
        foreach (var b in frame) _tx.Enqueue(b);
    }
}
