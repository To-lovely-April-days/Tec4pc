using System.Text;
using TecControl.Core.Comm;

namespace Tec.Core.Tests;

/// <summary>
/// 一台假的 RD105 温控器，说 RD105 的 ASCII 协议。
/// 有它就能在没有硬件的情况下把「打开串口 → 读型号 → 设定 → 轮询」整条路跑一遍；
/// 也能故意让它答错、答慢、掉字节，验证驱动那边的容错。
/// </summary>
public sealed class FakeRd105Device : ISerialTransport
{
    private readonly StringBuilder _rx = new();          // 主机发来的字节，攒到 '\n' 才算一条
    private readonly Queue<byte> _tx = new();            // 待读回主机的应答
    private readonly Dictionary<string, long> _regs = new(StringComparer.Ordinal);

    public FakeRd105Device()
    {
        // 出厂值。温度这几个按 RD105 的标度：℃ ×10^5
        Set(1, "TG", 25_00000); Set(2, "TG", 25_00000);
        Set(1, "ENABLE", 0); Set(2, "ENABLE", 0);
        Set(1, "MODE", 0); Set(2, "MODE", 0);
        Set(1, "SPEED", 0); Set(2, "SPEED", 0);
        Set(1, "TCADJTEMP", 25_00000); Set(2, "TCADJTEMP", 25_00000);
        Set(1, "RESISTOR", 0); Set(2, "RESISTOR", 0);
        Set(1, "OUTV", 0); Set(2, "OUTV", 0);
        _regs["SINTERIORTEMP"] = 24_00000;
        _regs["ERRORCODE"] = 0;
        _regs["FPV"] = 130;
    }

    /// <summary>型号寄存器实测返回的是文本（如 "215L"），不是数字。</summary>
    public string Model { get; set; } = "215L";

    /// <summary>置 true 后所有查询都不应答，用来验证超时路径。</summary>
    public bool Mute { get; set; }

    /// <summary>收到过的完整指令，按先后顺序。断言用。</summary>
    public List<string> Commands { get; } = new();

    public long Get(int? ch, string name) => _regs.TryGetValue(Key(ch, name), out var v) ? v : 0;
    public void Set(int? ch, string name, long value) => _regs[Key(ch, name)] = value;

    private static string Key(int? ch, string name) => ch is null ? name : $"TC{ch}:{name}";

    // ── ISerialTransport ─────────────────────────────────────────────

    public bool IsOpen { get; private set; }
    public void Open() => IsOpen = true;
    public void Close() => IsOpen = false;
    public void DiscardInput() => _tx.Clear();

    public void Write(byte[] buffer, int offset, int count)
    {
        if (!IsOpen) throw new InvalidOperationException("串口没打开");
        for (var i = 0; i < count; i++)
        {
            var c = (char)buffer[offset + i];
            _rx.Append(c);
            if (c != '\n') continue;
            Handle(_rx.ToString());
            _rx.Clear();
        }
    }

    public int Read(byte[] buffer, int offset, int count, int timeoutMs)
    {
        var n = 0;
        while (n < count && _tx.Count > 0) buffer[offset + n++] = _tx.Dequeue();
        return n;                       // 没有待发数据就返回 0，等同于「超时内无数据」
    }

    public void Dispose() => IsOpen = false;

    // ── 协议 ─────────────────────────────────────────────────────────

    private void Handle(string frame)
    {
        Commands.Add(frame.Trim());
        if (Mute) return;

        // 形如 "TC1:TG=2500000@\n" 或 "TC1:TG=?@\n" 或 "DATADEMAND=2@\n"
        var body = frame.Trim().TrimEnd('@', '\n').TrimEnd('@');
        var eq = body.IndexOf('=');
        if (eq < 0) return;
        var key = body[..eq];
        var value = body[(eq + 1)..].TrimEnd('@');

        if (value == "?") { Reply(key, Answer(key)); return; }

        // 写：存下来并原样回显，真机就是这么答的
        if (key == "DATADEMAND") { ReplyAll(); return; }
        if (long.TryParse(value, out var v)) _regs[key] = v;
        Reply(key, value);
    }

    private string Answer(string key)
    {
        if (key == "TEC") return Model;
        return _regs.TryGetValue(key, out var v) ? v.ToString() : "0";
    }

    private void Reply(string key, string value) => Send($"{key}={value}@\n");

    /// <summary>
    /// DATADEMAND=2 的全量应答：两路的温度 / 电阻 / 输出电压 + 内部温度。
    /// 字段之间用 '@' 分隔，不是逗号——ParseFieldsText 就是按 '@' 切的。
    /// </summary>
    private void ReplyAll()
    {
        var f = new[]
        {
            $"TC1:TCADJTEMP={Get(1, "TCADJTEMP")}",
            $"TC1:RESISTOR={Get(1, "RESISTOR")}",
            $"TC1:OUTV={Get(1, "OUTV")}",
            $"TC2:TCADJTEMP={Get(2, "TCADJTEMP")}",
            $"TC2:RESISTOR={Get(2, "RESISTOR")}",
            $"TC2:OUTV={Get(2, "OUTV")}",
            $"SINTERIORTEMP={_regs["SINTERIORTEMP"]}"
        };
        Send(string.Join("@", f) + "@\n");
    }

    private void Send(string text)
    {
        foreach (var b in Encoding.ASCII.GetBytes(text)) _tx.Enqueue(b);
    }
}
