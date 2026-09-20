using System.Globalization;
using System.Text;
using Tec.Drivers.Rd105.Modbus;
using TecControl.Core.Comm;
using TecControl.Core.Protocol;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 让 TecClient 以为自己在跟 ASCII 口说话、实际走 Modbus-RTU 的桥。
///
/// 现场那台 RD105 接的是 RS485 口——协议 §1：485 口出厂 9600、站号 1，走的是
/// Modbus-RTU（§2.2）。上面整条链路（TecClient → TecController → 控制环）是按 ASCII
/// 写的、在真机上验过；重写一套 Modbus 版的控制器等于开第二条演进线。这里做的是
/// 一个 ISerialTransport：收下 TecClient 写进来的「TC1:TG=?@\n」，查寄存器表，
/// 发 03/10 功能码，再把结果拼成 ASCII 口那样的应答「OKTC1:TG=2500000@\r\n」
/// 放进读缓冲。上层一行不改。
///
/// 三条不将就的：
/// · 寄存器表里没有的指令如实拒绝，不猜地址；
/// · 设完回读设备里的值再回显——ASCII 口回显的就是设备实际存下的（可能被钳位），桥上照做；
/// · DATADEMAND=2 里的 OUTV（实际输出电压）Modbus 没有寄存器，应答里就**不带**这一项，
///   不拿别的量冒充（上层已容忍缺项，读成 NaN）。
/// </summary>
public sealed class Rd105ModbusBridge : ISerialTransport
{
    private readonly ISerialTransport _serial;
    private readonly ModbusRtuClient _mb;
    private readonly byte _station;
    private readonly StringBuilder _rx = new();
    private readonly Queue<byte> _tx = new();

    public Rd105ModbusBridge(ISerialTransport serial, byte station, int timeoutMs = 500)
    {
        _serial = serial;
        _station = station;
        _mb = new ModbusRtuClient(serial, station, timeoutMs);
    }

    public byte Station => _station;

    // ── ISerialTransport ─────────────────────────────────────────────

    public bool IsOpen => _serial.IsOpen;
    public void Open() => _serial.Open();
    public void Close() => _serial.Close();

    public void DiscardInput()
    {
        _tx.Clear();
        _rx.Clear();
        _serial.DiscardInput();
    }

    /// <summary>TecClient 一次写进一整条指令；攒到 '@' 就算一帧，帧尾的换行不算数。</summary>
    public void Write(byte[] buffer, int offset, int count)
    {
        _rx.Append(Encoding.ASCII.GetString(buffer, offset, count));
        while (true)
        {
            var s = _rx.ToString();
            var at = s.IndexOf('@');
            if (at < 0) return;
            var frame = s[..at].Trim('\r', '\n', ' ');
            _rx.Remove(0, at + 1);
            if (frame.Length > 0) Handle(frame);
        }
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        if (_tx.Count == 0)
        {
            // 应答是在 Write 里同步生成的，这里没有就是没有；歇一下别让上层空转
            Thread.Sleep(Math.Clamp(timeoutMs, 1, 5));
            return 0;
        }
        var n = 0;
        while (n < count && _tx.Count > 0) buffer[offset + n++] = _tx.Dequeue();
        return n;
    }

    public void Dispose() => _serial.Dispose();

    // ── ASCII 指令 → Modbus 寄存器 ───────────────────────────────────

    private void Handle(string frame)
    {
        // 形如 "TC1:TG=?" / "TC1:TG=2500000" / "DATADEMAND=2"
        var eq = frame.IndexOf('=');
        if (eq <= 0) throw new TecProtocolException($"Modbus-RTU 桥看不懂指令「{frame}」");
        var key = frame[..eq].Trim();
        var value = frame[(eq + 1)..].Trim();

        int? channel = null;
        var name = key;
        var colon = key.IndexOf(':');
        if (colon > 0)
        {
            var prefix = key[..colon].Trim().ToUpperInvariant();
            channel = prefix switch { "TC1" => 1, "TC2" => 2, _ => throw new TecProtocolException($"Modbus-RTU 桥不认识通道前缀「{prefix}」") };
            name = key[(colon + 1)..].Trim();
        }

        if (name.Equals(TecCmd.DataDemand, StringComparison.OrdinalIgnoreCase))
        {
            Reply(DataDemand(value));
            return;
        }
        if (!Rd105Registers.Map.TryGetValue(name, out var spec))
            throw new TecProtocolException($"Modbus-RTU 桥不支持指令 {name}：协议 §4 的寄存器表里没有它");
        if (spec.PerChannel != channel.HasValue)
            throw new TecProtocolException(spec.PerChannel
                ? $"{name} 是通道指令，要带 TC1:/TC2: 前缀"
                : $"{name} 是通用指令，不带通道前缀");

        var addr = (ushort)(spec.Address + (channel == 2 ? Rd105Registers.ChannelStride : 0));
        var echoKey = channel is { } c ? $"TC{c}:{name}" : name;

        long result;
        if (value == "?")
        {
            result = Rd105Registers.Decode(spec.Type, Read(addr, spec.Count));
        }
        else
        {
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                throw new TecProtocolException($"设置 {echoKey} 的值「{value}」不是整数");
            WriteRegs(addr, Rd105Registers.Encode(spec.Type, v));
            // 回读：ASCII 口回显的是设备实际存下的值（可能被钳位），桥上照做；只写寄存器照下发的回
            result = Rd105Registers.WriteOnly(name) ? v : Rd105Registers.Decode(spec.Type, Read(addr, spec.Count));
        }

        var text = name.Equals(TecCmd.Model, StringComparison.OrdinalIgnoreCase)
            ? Rd105Registers.ModelName(result)
            : result.ToString(CultureInfo.InvariantCulture);
        Reply($"OK{echoKey}={text}@\r\n");
    }

    /// <summary>
    /// DATADEMAND=1/2（一次性查询关键数据，协议 §3.6.2）：Modbus 没有这条聚合指令，
    /// 桥上按两路各读一段连续寄存器（TCADJTEMP 0x1002 与 RESISTOR 0x1004 紧挨着，一帧读 6 个），
    /// 再读温控器自身温度。=1 那份带 PWM（就是 PWMDUTY）；=2 那份 ASCII 口给的是 OUTV
    /// （实际输出电压），寄存器表里没有——不带，上层读成 NaN，不编。
    /// </summary>
    private string DataDemand(string which)
    {
        var sb = new StringBuilder();
        var temp = Rd105Registers.Map["TCADJTEMP"];
        var res = Rd105Registers.Map["RESISTOR"];
        var duty = Rd105Registers.Map["PWMDUTY"];
        for (var ch = 1; ch <= 2; ch++)
        {
            var off = (ushort)(ch == 2 ? Rd105Registers.ChannelStride : 0);
            var block = Read((ushort)(temp.Address + off), temp.Count + res.Count);
            sb.Append($"TC{ch}:TCADJTEMP={Rd105Registers.Decode(temp.Type, block.AsSpan(0, temp.Count))}@");
            sb.Append($"TC{ch}:RESISTOR={Rd105Registers.Decode(res.Type, block.AsSpan(temp.Count, res.Count))}@");
            if (which == "1")
                sb.Append($"TC{ch}:PWM={Rd105Registers.Decode(duty.Type, Read((ushort)(duty.Address + off), duty.Count))}@");
        }
        var it = Rd105Registers.Map["SINTERIORTEMP"];
        sb.Append($"SINTERIORTEMP={Rd105Registers.Decode(it.Type, Read(it.Address, it.Count))}@\r\n");
        return sb.ToString();
    }

    private ushort[] Read(ushort addr, int count)
    {
        try { return _mb.ReadHoldingRegistersAsync(addr, count).GetAwaiter().GetResult(); }
        catch (TimeoutException ex) { throw new TimeoutException($"Modbus-RTU 站号 {_station} 读 0x{addr:X4}：{ex.Message}", ex); }
        catch (ModbusException ex) { throw new TecProtocolException($"Modbus-RTU 站号 {_station} 读 0x{addr:X4}：{ex.Message}"); }
    }

    private void WriteRegs(ushort addr, ushort[] regs)
    {
        try { _mb.WriteRegistersAsync(addr, regs).GetAwaiter().GetResult(); }
        catch (TimeoutException ex) { throw new TimeoutException($"Modbus-RTU 站号 {_station} 写 0x{addr:X4}：{ex.Message}", ex); }
        catch (ModbusException ex) { throw new TecProtocolException($"Modbus-RTU 站号 {_station} 写 0x{addr:X4}：{ex.Message}"); }
    }

    private void Reply(string ascii)
    {
        foreach (var b in Encoding.ASCII.GetBytes(ascii)) _tx.Enqueue(b);
    }
}
