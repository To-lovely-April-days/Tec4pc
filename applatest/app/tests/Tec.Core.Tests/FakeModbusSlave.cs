using TecControl.Core.Comm;
using Tec.Drivers.Rd105.Modbus;

namespace Tec.Core.Tests;

/// <summary>
/// 一台假的 Modbus RTU 从站：8 线圈 + 8 离散输入 + 一张保持寄存器表。
/// IO8R（线圈/输入）和宇电（寄存器）的回归测试共用它；
/// 也能故意不答、答坏 CRC、冒充别的站号、一律回异常码，验证主站的容错。
///
/// CRC 用的是驱动侧同一份 ModbusCrc——单独有一条「标准校验向量 0x4B37」
/// 的测试锁算法本身，锁住之后这里复用就不算循环论证。
/// </summary>
public sealed class FakeModbusSlave : ISerialTransport
{
    private readonly List<byte> _rx = new();
    private readonly Queue<byte> _tx = new();

    public byte Station { get; set; } = 1;
    public bool[] Coils { get; } = new bool[8];
    public bool[] Inputs { get; } = new bool[8];
    public Dictionary<int, ushort> Regs { get; } = new();

    /// <summary>收到的每帧原始字节。golden 帧断言用。</summary>
    public List<byte[]> Raw { get; } = new();

    /// <summary>收到的请求（人话），按先后顺序。</summary>
    public List<string> Requests { get; } = new();

    /// <summary>置 true 后收指令但不应答，验证超时路径。</summary>
    public bool Mute { get; set; }

    /// <summary>应答的 CRC 故意写坏。</summary>
    public bool CorruptCrc { get; set; }

    /// <summary>用这个站号应答（冒充总线上别的从站插话）。</summary>
    public byte? AnswerStation { get; set; }

    /// <summary>一律回这个异常码。</summary>
    public byte? RejectWith { get; set; }

    /// <summary>每次应答前先吐这几个脏字节——模拟 485 收发切换的毛刺（现场读到的是 F8 打头）。</summary>
    public byte[] LeadingNoise { get; set; } = Array.Empty<byte>();

    // ── ISerialTransport ─────────────────────────────────────────────

    public bool IsOpen { get; private set; }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void DiscardInput() => _tx.Clear();
    public void Dispose() => IsOpen = false;

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!IsOpen) throw new InvalidOperationException("串口没打开");
        for (var i = 0; i < count; i++) _rx.Add(buffer[offset + i]);
        while (TryTakeFrame(out var frame)) Handle(frame);
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var n = 0;
        while (n < count && _tx.Count > 0) buffer[offset + n++] = _tx.Dequeue();
        return n;                           // 没数据返回 0，等同「超时内无数据」
    }

    // ── 协议 ─────────────────────────────────────────────────────────

    /// <summary>RTU 没有帧定界符，按功能码算请求该有多长，攒够了才算一帧。</summary>
    private bool TryTakeFrame(out byte[] frame)
    {
        frame = Array.Empty<byte>();
        if (_rx.Count < 8) return false;
        var need = _rx[1] is 0x0F or 0x10 ? 7 + _rx[6] + 2 : 8;
        if (_rx.Count < need) return false;
        frame = _rx.GetRange(0, need).ToArray();
        _rx.RemoveRange(0, need);
        return true;
    }

    private void Handle(byte[] frame)
    {
        Raw.Add(frame);

        // CRC 坏的请求真从站不理（当线路噪声），这里也一样
        if (!ModbusCrc.Check(frame)) { Requests.Add("crc-bad"); return; }
        if (frame[0] != Station) { Requests.Add($"station-{frame[0]}"); return; }
        if (Mute) { Requests.Add("muted"); return; }

        var fc = frame[1];
        if (RejectWith is { } code)
        {
            Requests.Add($"reject-{code:X2}");
            Reply((byte)(fc | 0x80), new[] { code });
            return;
        }

        int At(int i) => frame[i] << 8 | frame[i + 1];

        switch (fc)
        {
            case 0x01 or 0x02:
            {
                var start = At(2);
                var count = At(4);
                var src = fc == 0x01 ? Coils : Inputs;
                Requests.Add($"{(fc == 0x01 ? "读DO" : "读DI")} {start}+{count}");
                var nb = (count + 7) / 8;
                var data = new byte[1 + nb];
                data[0] = (byte)nb;
                for (var i = 0; i < count; i++)
                    if (src[start + i]) data[1 + i / 8] |= (byte)(1 << i % 8);
                Reply(fc, data);
                break;
            }
            case 0x03:
            {
                var start = At(2);
                var count = At(4);
                Requests.Add($"读寄存器 {start}+{count}");
                var data = new byte[1 + count * 2];
                data[0] = (byte)(count * 2);
                for (var i = 0; i < count; i++)
                {
                    var v = Regs.TryGetValue(start + i, out var r) ? r : (ushort)0;
                    data[1 + i * 2] = (byte)(v >> 8);
                    data[2 + i * 2] = (byte)v;
                }
                Reply(fc, data);
                break;
            }
            case 0x05:
            {
                var addr = At(2);
                Coils[addr] = At(4) == 0xFF00;
                Requests.Add($"写DO {addr}={(Coils[addr] ? "闭" : "断")}");
                Reply(fc, frame[2..6]);     // 回显地址+值
                break;
            }
            case 0x06:
            {
                var addr = At(2);
                Regs[addr] = (ushort)At(4);
                Requests.Add($"写寄存器 {addr}={Regs[addr]}");
                Reply(fc, frame[2..6]);
                break;
            }
            case 0x0F:
            {
                var start = At(2);
                var count = At(4);
                for (var i = 0; i < count; i++)
                    Coils[start + i] = (frame[7 + i / 8] >> i % 8 & 1) != 0;
                Requests.Add($"写多DO {start}+{count}");
                Reply(fc, frame[2..6]);     // 回显地址+个数
                break;
            }
            case 0x10:
            {
                var start = At(2);
                var count = At(4);
                for (var i = 0; i < count; i++)
                    Regs[start + i] = (ushort)(frame[7 + i * 2] << 8 | frame[8 + i * 2]);
                Requests.Add($"写多寄存器 {start}+{count}");
                Reply(fc, frame[2..6]);
                break;
            }
            default:
                Requests.Add($"fc-{fc:X2}");
                Reply((byte)(fc | 0x80), new byte[] { 0x01 });
                break;
        }
    }

    private void Reply(byte fc, byte[] data)
    {
        var frame = new byte[2 + data.Length + 2];
        frame[0] = AnswerStation ?? Station;
        frame[1] = fc;
        Array.Copy(data, 0, frame, 2, data.Length);
        ModbusCrc.Append(frame);
        if (CorruptCrc) frame[^1] ^= 0xFF;
        foreach (var b in LeadingNoise) _tx.Enqueue(b);
        foreach (var b in frame) _tx.Enqueue(b);
    }
}

/// <summary>
/// 一条挂着多台从站的 485 总线（宇电导轨拼接共口那种接法）。
/// 每帧广播给所有从站，只有站号对上的那台会应答——
/// 跟真总线一个行为，FakeModbusSlave 一行不用改。
/// </summary>
public sealed class FakeModbusBus : ISerialTransport
{
    private readonly FakeModbusSlave[] _slaves;

    public FakeModbusBus(params FakeModbusSlave[] slaves) => _slaves = slaves;

    public bool IsOpen { get; private set; }

    public void Open()
    {
        IsOpen = true;
        foreach (var s in _slaves) s.Open();
    }

    public void Close()
    {
        IsOpen = false;
        foreach (var s in _slaves) s.Close();
    }

    public void DiscardInput()
    {
        foreach (var s in _slaves) s.DiscardInput();
    }

    public void Write(byte[] buffer, int offset, int count)
    {
        foreach (var s in _slaves) s.Write(buffer, offset, count);
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        foreach (var s in _slaves)
        {
            var n = s.Read(buffer, offset, count, timeoutMs);
            if (n > 0) return n;
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var s in _slaves) s.Dispose();
    }
}
