using TecControl.Core.Comm;

namespace Tec.Drivers.DualStation.Modbus;

/// <summary>
/// Modbus RTU 主站，问答式。IO8R（线圈/离散输入）和宇电两台（保持寄存器）共用这一层，
/// 各自的语义留在各自的客户端里，这里只管把一问一答走对。
///
/// 建在 ISerialTransport 上，理由同 Rd105Link：换成假串口就能在没有硬件的情况下
/// 把整条链路跑一遍。RTU 的帧边界靠 3.5 字符静默，但主站这边不用去数静默——
/// 应答长度由功能码决定，按长度读满即可；每次发问前清一次接收缓冲，
/// 上一问超时后迟到的应答不会混进这一问。
/// </summary>
public sealed class ModbusRtuClient
{
    private readonly ISerialTransport _transport;
    private readonly SemaphoreSlim _lock;               // 半双工总线，一次只许一问
    private readonly int _timeoutMs;

    /// <param name="busLock">共口时传进来的总线锁：宇电两台导轨拼接后 485 自动并联
    /// （手册 §3.3），一条串口上挂两个从站，两个客户端必须共用一把锁，
    /// 否则一问未答又来一问，应答就串台了。不共口就不传，各用各的。</param>
    public ModbusRtuClient(ISerialTransport transport, byte station, int timeoutMs = 500,
                           SemaphoreSlim? busLock = null)
    {
        if (station is 0 or > 247)
            throw new ArgumentOutOfRangeException(nameof(station),
                $"Modbus 从站地址要在 1~247，给的是 {station}（0 是广播，收不到应答）");
        _transport = transport;
        Station = station;
        _timeoutMs = Math.Max(50, timeoutMs);
        _lock = busLock ?? new SemaphoreSlim(1, 1);
    }

    public byte Station { get; }

    // ── 读 ─────────────────────────────────────────────────────────────

    /// <summary>FC 01 读线圈（IO8R 的继电器 DO）。位序低位在前：第 0 点在首字节 bit0。</summary>
    public Task<bool[]> ReadCoilsAsync(ushort start, int count, CancellationToken ct = default)
        => Task.Run(() => ReadBits(0x01, start, count, ct), ct);

    /// <summary>FC 02 读离散输入（IO8R 的光耦 DI）。</summary>
    public Task<bool[]> ReadDiscreteInputsAsync(ushort start, int count, CancellationToken ct = default)
        => Task.Run(() => ReadBits(0x02, start, count, ct), ct);

    /// <summary>FC 03 读保持寄存器，字内大端。宇电整张表都从这里读。</summary>
    public Task<ushort[]> ReadHoldingRegistersAsync(ushort start, int count, CancellationToken ct = default)
        => Task.Run(() => ReadRegs(start, count, ct), ct);

    // ── 写 ─────────────────────────────────────────────────────────────

    /// <summary>FC 05 写单个线圈。协议只有 FF00（闭合）/ 0000（断开）两个值。</summary>
    public Task WriteCoilAsync(ushort address, bool on, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var value = on ? 0xFF00 : 0x0000;
            var body = Transact(0x05, new[] { Hi(address), Lo(address), Hi(value), Lo(value) }, ct);
            VerifyEcho(address, value, body);
        }, ct);

    /// <summary>FC 06 写单个保持寄存器。</summary>
    public Task WriteRegisterAsync(ushort address, ushort value, CancellationToken ct = default)
        => Task.Run(() =>
        {
            var body = Transact(0x06, new[] { Hi(address), Lo(address), Hi(value), Lo(value) }, ct);
            VerifyEcho(address, value, body);
        }, ct);

    /// <summary>FC 0F 写多个线圈。一帧落全部点位——安全场合「全断」就该是一帧，不是八帧。</summary>
    public Task WriteCoilsAsync(ushort start, IReadOnlyList<bool> values, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (values.Count is < 1 or > 1968)
                throw new ArgumentOutOfRangeException(nameof(values), $"一次写 1~1968 个线圈，给了 {values.Count}");
            var nBytes = (values.Count + 7) / 8;
            var payload = new byte[5 + nBytes];
            payload[0] = Hi(start); payload[1] = Lo(start);
            payload[2] = Hi(values.Count); payload[3] = Lo(values.Count);
            payload[4] = (byte)nBytes;
            for (var i = 0; i < values.Count; i++)
                if (values[i]) payload[5 + i / 8] |= (byte)(1 << i % 8);
            var body = Transact(0x0F, payload, ct);
            VerifyEcho(start, values.Count, body);
        }, ct);

    /// <summary>FC 10 写多个保持寄存器。宇电限一帧 16 个，这个上限由它的客户端把，不在这层写死。</summary>
    public Task WriteRegistersAsync(ushort start, IReadOnlyList<ushort> values, CancellationToken ct = default)
        => Task.Run(() =>
        {
            if (values.Count is < 1 or > 123)
                throw new ArgumentOutOfRangeException(nameof(values), $"一次写 1~123 个寄存器，给了 {values.Count}");
            var payload = new byte[5 + values.Count * 2];
            payload[0] = Hi(start); payload[1] = Lo(start);
            payload[2] = Hi(values.Count); payload[3] = Lo(values.Count);
            payload[4] = (byte)(values.Count * 2);
            for (var i = 0; i < values.Count; i++)
            {
                payload[5 + i * 2] = Hi(values[i]);
                payload[6 + i * 2] = Lo(values[i]);
            }
            var body = Transact(0x10, payload, ct);
            VerifyEcho(start, values.Count, body);
        }, ct);

    // ── 一问一答 ───────────────────────────────────────────────────────

    private bool[] ReadBits(byte fc, ushort start, int count, CancellationToken ct)
    {
        if (count is < 1 or > 2000)
            throw new ArgumentOutOfRangeException(nameof(count), $"一次读 1~2000 个位，要了 {count}");
        var body = Transact(fc, new[] { Hi(start), Lo(start), Hi(count), Lo(count) }, ct);
        var expect = (count + 7) / 8;
        if (body.Length != 1 + expect || body[0] != expect)
            throw new ModbusException($"应答长度不对：读 {count} 位应答 {expect} 字节数据，收到的说自己有 {body[0]} 字节");
        var bits = new bool[count];
        for (var i = 0; i < count; i++)
            bits[i] = (body[1 + i / 8] >> i % 8 & 1) != 0;
        return bits;
    }

    private ushort[] ReadRegs(ushort start, int count, CancellationToken ct)
    {
        if (count is < 1 or > 125)
            throw new ArgumentOutOfRangeException(nameof(count), $"一次读 1~125 个寄存器，要了 {count}");
        var body = Transact(0x03, new[] { Hi(start), Lo(start), Hi(count), Lo(count) }, ct);
        if (body.Length != 1 + count * 2 || body[0] != count * 2)
            throw new ModbusException($"应答长度不对：读 {count} 个寄存器应答 {count * 2} 字节数据，收到的说自己有 {body[0]} 字节");
        var regs = new ushort[count];
        for (var i = 0; i < count; i++)
            regs[i] = (ushort)(body[1 + i * 2] << 8 | body[2 + i * 2]);
        return regs;
    }

    /// <summary>发一帧、收一帧。返回应答里去掉站号/功能码/CRC 的正文。</summary>
    private byte[] Transact(byte fc, byte[] payload, CancellationToken ct)
    {
        _lock.Wait(ct);
        try
        {
            var frame = new byte[2 + payload.Length + 2];
            frame[0] = Station;
            frame[1] = fc;
            Array.Copy(payload, 0, frame, 2, payload.Length);
            ModbusCrc.Append(frame);

            _transport.DiscardInput();          // 上一问超时后迟到的字节别混进这一答
            _transport.Write(frame, 0, frame.Length);
            return ReadResponse(fc, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private byte[] ReadResponse(byte fc, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + _timeoutMs;

        var head = new byte[2];
        ReadExact(head, deadline, "应答", ct);

        if (head[0] != Station)
            throw new ModbusException(
                $"应答站号不对：问的是 {Station} 号，答话的是 {head[0]} 号——查一下总线上是不是接错/配错了站号");

        if (head[1] == (fc | 0x80))
        {
            // 异常应答：功能码置高位 + 1 字节异常码
            var tail = new byte[3];
            ReadExact(tail, deadline, "异常应答", ct);
            VerifyCrc(new[] { head[0], head[1], tail[0] }, tail[1], tail[2]);
            throw new ModbusException(tail[0]);
        }

        if (head[1] != fc)
            throw new ModbusException($"应答功能码不对：问 {fc:X2}，答 {head[1]:X2}");

        if (fc is 0x01 or 0x02 or 0x03)
        {
            // 变长应答：字节数 + 数据 + CRC
            var cnt = new byte[1];
            ReadExact(cnt, deadline, "应答长度", ct);
            var rest = new byte[cnt[0] + 2];
            ReadExact(rest, deadline, "应答数据", ct);

            var whole = new byte[3 + cnt[0]];
            whole[0] = head[0]; whole[1] = head[1]; whole[2] = cnt[0];
            Array.Copy(rest, 0, whole, 3, cnt[0]);
            VerifyCrc(whole, rest[^2], rest[^1]);

            var body = new byte[1 + cnt[0]];
            body[0] = cnt[0];
            Array.Copy(rest, 0, body, 1, cnt[0]);
            return body;
        }
        else
        {
            // 定长应答（05/06/0F/10）：地址 + 值（或个数）+ CRC
            var rest = new byte[6];
            ReadExact(rest, deadline, "应答数据", ct);
            VerifyCrc(new[] { head[0], head[1], rest[0], rest[1], rest[2], rest[3] }, rest[4], rest[5]);
            return rest[..4];
        }
    }

    private void ReadExact(byte[] buf, long deadline, string what, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            ct.ThrowIfCancellationRequested();
            var remain = (int)(deadline - Environment.TickCount64);
            if (remain <= 0)
                throw new TimeoutException(
                    $"{_timeoutMs} ms 内没等到完整{what}（收到 {got}/{buf.Length} 字节）——" +
                    "从站没上电、站号/波特率不对，或者线断了");
            var n = _transport.Read(buf, got, buf.Length - got, Math.Min(remain, 50));
            if (n == 0) Thread.Sleep(1);        // 假串口的 Read 立即返回 0；真串口自己会等
            else got += n;
        }
    }

    private static void VerifyCrc(byte[] content, byte lo, byte hi)
    {
        var crc = ModbusCrc.Compute(content);
        if (lo != (byte)crc || hi != (byte)(crc >> 8))
            throw new ModbusException(
                $"应答 CRC 校验不过（算得 {crc:X4}，收到 {hi:X2}{lo:X2}）——线路有干扰或波特率不对");
    }

    /// <summary>05/06 回显地址+值，0F/10 回显地址+个数。对不上说明从站没照办。</summary>
    private static void VerifyEcho(int address, int value, byte[] body)
    {
        if (body.Length != 4 || (body[0] << 8 | body[1]) != address || (body[2] << 8 | body[3]) != value)
            throw new ModbusException("写应答的回显与下发不一致——从站没有按下发的内容执行");
    }

    private static byte Hi(int v) => (byte)(v >> 8);
    private static byte Lo(int v) => (byte)v;
}
