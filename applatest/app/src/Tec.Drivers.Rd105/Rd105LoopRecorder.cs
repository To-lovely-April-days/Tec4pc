using System.Globalization;
using System.Text;
using TecControl.Core.Control;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 一次控温的逐拍记录（用户要的：每次启动控温把数据记到一个 CSV，好排查）。一路一个文件、一次控温一个文件：
/// %AppData%\TecDrivers\loops\&lt;实例&gt;-tc&lt;n&gt;-&lt;yyyyMMdd-HHmmss&gt;.csv（环境变量 TEC_LOOPLOG_DIR 可改），
/// 下发目标时开、停控 / 回路自己停了时关；控着的时候再下发目标只记一行「目标改为」，不另开文件。
/// 每拍一行：设定、内环设定、Tr、Tj、写入 / 回显的占空比、内环 P/I/D、外环 P/I/D、执行器形态、Tr 丢失保持秒数、
/// 串口耗时、热源侧（组合会话喂）、事件（开始 / 停止原因 / 回路的提示）。
/// 回路线程写、控制线程开关：一把锁。每 10 行 flush 一次（掉电最多丢 5 s）。记录开不了文件不影响控温。
/// </summary>
public sealed class Rd105LoopRecorder : IDisposable
{
    public const string Header =
        "time,elapsed_s,kind,setpoint,inner_sp,tr,tj,duty_written,duty_applied,p,i,d,outer_p,outer_i,outer_d,actuator,hold_s,cycle_ms,extra,note";

    private readonly string _instanceId;
    private readonly int _tc;
    private readonly Action<string, string> _log;
    private readonly object _gate = new();
    private StreamWriter? _w;
    private DateTimeOffset _startedAt;
    private string? _pendingNote;
    private int _rows;
    private string? _path;

    public Rd105LoopRecorder(string instanceId, int tc, Action<string, string> log)
    {
        _instanceId = instanceId;
        _tc = tc;
        _log = log;
    }

    /// <summary>正在记的那个文件；没在记为 null。</summary>
    public string? Path { get { lock (_gate) return _w is null ? null : _path; } }

    public bool Active { get { lock (_gate) return _w is not null; } }

    /// <summary>下发目标：开一个新文件（已经在记就只记一行「目标改为」）。</summary>
    public void Start(string kind, double setpoint, string how)
    {
        lock (_gate)
        {
            if (_w is not null)
            {
                _pendingNote = Join(_pendingNote, $"目标改为 {setpoint:0.##} ℃（{kind}，{how}）");
                return;
            }
            try
            {
                var dir = Rd105HostControl.LoopLogDir;
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir,
                    $"{Rd105HostControl.SafeName(_instanceId)}-tc{_tc}-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.csv");
                _w = new StreamWriter(_path, false, new UTF8Encoding(true));   // 带 BOM：Excel 直接打开，中文不乱
                _w.WriteLine(Header);
                _startedAt = DateTimeOffset.Now;
                _rows = 0;
                _pendingNote = $"开始：{kind} {setpoint:0.##} ℃（{how}）";
                _log("info", $"{_instanceId} TC{_tc} 控温记录 → {_path}");
                Prune(dir);
            }
            catch (Exception ex)
            {
                _w = null;
                _path = null;
                _log("warn", $"{_instanceId} TC{_tc} 控温记录开不了文件：{ex.Message}（不影响控温）");
            }
        }
    }

    /// <summary>回路的提示 / 停控原因这类事件：挂到下一行的 note 里。</summary>
    public void Note(string text)
    {
        lock (_gate) if (_w is not null) _pendingNote = Join(_pendingNote, text);
    }

    /// <summary>回路每拍一行。</summary>
    public void Write(ChannelCycleInfo info, double tr, string actuator, double cycleMs, string? extra)
    {
        lock (_gate)
        {
            if (_w is null) return;
            var now = DateTimeOffset.Now;
            var sb = new StringBuilder(256);
            sb.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(',');
            sb.Append((now - _startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)).Append(',');
            sb.Append(info.Cascade ? "Tr" : "Tj").Append(',');
            Num(sb, info.SetpointC);
            Num(sb, info.Cascade ? info.InnerSetpointC : info.SetpointC);
            Num(sb, tr);
            Num(sb, info.MeasuredC);
            Num(sb, info.DutyPercent);
            Num(sb, info.AppliedDutyPercent ?? double.NaN);
            Num(sb, info.PTerm);
            Num(sb, info.ITerm);
            Num(sb, info.DTerm);
            Num(sb, info.OuterP);
            Num(sb, info.OuterI);
            Num(sb, info.OuterD);
            sb.Append(actuator).Append(',');
            Num(sb, info.OuterHoldSeconds ?? double.NaN);
            Num(sb, cycleMs);
            sb.Append(Csv(extra)).Append(',').Append(Csv(_pendingNote));
            _pendingNote = null;
            try
            {
                _w.WriteLine(sb.ToString());
                if (++_rows % 10 == 0) _w.Flush();
            }
            catch (Exception ex)
            {
                _log("warn", $"{_instanceId} TC{_tc} 控温记录写不进去：{ex.Message}——这次不记了");
                try { _w.Dispose(); } catch { }
                _w = null;
            }
        }
    }

    /// <summary>停控 / 回路停了 / 会话关了：记最后一行、收文件。</summary>
    public void Stop(string reason)
    {
        lock (_gate)
        {
            if (_w is null) return;
            var now = DateTimeOffset.Now;
            try
            {
                var note = Join(_pendingNote, $"停止：{reason}");
                _pendingNote = null;
                _w.WriteLine($"{now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}," +
                             $"{(now - _startedAt).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}" +
                             new string(',', 18) + Csv(note));
                _w.Flush();
            }
            catch { /* 收尾尽力而为 */ }
            finally
            {
                try { _w.Dispose(); } catch { }
                _w = null;
            }
            _log("info", $"{_instanceId} TC{_tc} 控温记录已收尾（{reason}）：{_path}，{_rows} 拍");
        }
    }

    public void Dispose() => Stop("会话关闭");

    private static void Num(StringBuilder sb, double v)
    {
        if (double.IsFinite(v)) sb.Append(v.ToString("0.###", CultureInfo.InvariantCulture));
        sb.Append(',');
    }

    private static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : a + "；" + b;

    /// <summary>目录里 60 天前的记录删掉（尽力而为），别让它无限长。</summary>
    private static void Prune(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-60);
            foreach (var f in Directory.EnumerateFiles(dir, "*.csv"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { }
    }
}
