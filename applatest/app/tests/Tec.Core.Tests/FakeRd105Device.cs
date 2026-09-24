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
        _regs["SINTERIORTEMP"] = 24;        // 协议 §3.5.6：20 就是 20 ℃，这一项不带 1e-5 标度
        _regs["ERRORCODE"] = 0;
        _regs["FPV"] = 130;
    }

    /// <summary>型号寄存器实测返回的是文本（如 "215L"），不是数字。</summary>
    public string Model { get; set; } = "215L";

    /// <summary>置 true 后所有查询都不应答，用来验证超时路径。</summary>
    public bool Mute { get; set; }

    /// <summary>
    /// 像现场那台一样按自己的规矩改写小占空比：非零、绝对值小于这个百分比的 PWMDUTY 存成这个百分比（带符号）并回显。
    /// 0 = 原样存、原样回显。现场看到：写 −4.41 % 回显 −6.43 %。
    /// </summary>
    public double DutyMinPercent { get; set; }

    /// <summary>像现场那台一样回显慢一拍：写 PWMDUTY 时存新值、回显**上一次**存的值（现场逐拍记录 89 % 的拍是这样）。</summary>
    public bool EchoLag { get; set; }
    private readonly Dictionary<string, long> _prevDuty = new();

    /// <summary>收到过的完整指令，按先后顺序。断言用。</summary>
    public List<string> Commands { get; } = new();

    // ── 热模型（只给上位机回路的测试用）──────────────────────────────
    // MODE=3 且 ENABLE=1 时按 PWMDUTY 积分夹套温度：100 % 时每秒 GainCPerSec 度，再向环境漏热。
    // HeatSign 说哪个符号是升温：+1 = 正占空比升温（TecControl 的约定）；现场那台是 −1（降温 +90、升温 −90）。

    /// <summary>置 true 后 DATADEMAND 应答前先按真实流逝的时间推一步温度。</summary>
    public bool Thermal { get; set; }
    public double HeatSign { get; set; } = 1;
    public double GainCPerSec { get; set; } = 0.3;
    public double LeakPerSec { get; set; } = 0.002;
    public double AmbientC { get; set; } = 25;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastStep;

    private void Step()
    {
        if (!Thermal) return;
        var now = _clock.Elapsed.TotalSeconds;
        var dt = now - _lastStep;
        _lastStep = now;
        if (dt <= 0) return;
        for (var ch = 1; ch <= 2; ch++)
        {
            var t = Get(ch, "TCADJTEMP") / 1e5;
            var duty = Get(ch, "MODE") == 3 && Get(ch, "ENABLE") == 1 ? Get(ch, "PWMDUTY") / 20_000.0 : 0;
            t += (HeatSign * duty / 100.0 * GainCPerSec - (t - AmbientC) * LeakPerSec) * dt;
            Set(ch, "TCADJTEMP", (long)Math.Round(t * 1e5));
        }
    }

    public long Get(int? ch, string name) => _regs.TryGetValue(Key(ch, name), out var v) ? v : 0;
    public void Set(int? ch, string name, long value) => _regs[Key(ch, name)] = value;

    private static string Key(int? ch, string name) => ch is null ? name : $"TC{ch}:{name}";

    // ── ISerialTransport ─────────────────────────────────────────────

    public bool IsOpen { get; private set; }
    /// <summary>置 true = 口子的句柄死了：一写就抛 IO 错，关掉重开才活过来。</summary>
    public bool Dead { get; set; }
    public int Opens { get; private set; }
    public void Open() { IsOpen = true; Dead = false; Opens++; }
    public void Close() => IsOpen = false;
    public void DiscardInput() => _tx.Clear();

    public void Write(byte[] buffer, int offset, int count)
    {
        if (Dead) throw new IOException("函数不正确。");
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
        if (long.TryParse(value, out var v))
        {
            if (key.EndsWith(":PWMDUTY", StringComparison.Ordinal) && v != 0 && DutyMinPercent > 0
                && Math.Abs(v) / 20_000.0 < DutyMinPercent)
                v = (long)Math.Round(Math.Sign(v) * DutyMinPercent * 20_000);
            var echoV = v;
            if (EchoLag && key.EndsWith(":PWMDUTY", StringComparison.Ordinal))
            {
                echoV = _prevDuty.TryGetValue(key, out var p) ? p : 0;
                _prevDuty[key] = v;
            }
            _regs[key] = v;
            Reply(key, echoV.ToString());
            return;
        }
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
        Step();
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
