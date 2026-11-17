using System.Globalization;
using System.Text;
using TecControl.Core.Control;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 一次控温的逐拍记录（用户要的：每次启动控温把数据记到一个 CSV，好排查）。一路一个文件、一次控温一个文件：
/// %AppData%\TecDrivers\loops\&lt;实例&gt;-tc&lt;n&gt;-&lt;yyyyMMdd-HHmmss&gt;.csv（环境变量 TEC_LOOPLOG_DIR 可改），
/// 下发目标时开、停控 / 回路自己停了时关；控着的时候再下发目标只记一行「目标改为」，不另开文件。
/// 每拍一行：设定、内环设定、Tr、Tj、写入 / 回显的占空比、内环 P/I/D、外环 P/I/D、大降温收尾多给的偏置、
/// 升温加速（boost = 1 时夹套目标归速度环管；v_m 釜内实测升温速度、v_star 刹车曲线允许的速度，K/min；brake_f 刹车偏置分数）、执行器形态、Tr 丢失保持秒数、
/// 串口耗时、热源侧（组合会话喂）、事件（开始 / 停止原因 / 回路的提示）。
/// 回路线程写、控制线程开关：一把锁。每 10 行 flush 一次（掉电最多丢 5 s）。记录开不了文件不影响控温。
///
/// 【本地改动】（2026-11-14，外环改「按实测实时算」第 0 步：新算法只算不控）行尾追加 8 列，原 25 列（time … note）的列号和内容一个不动：
///   algo      哪个外环在控：0 = 旧外环（或不在控 / 控夹套），1 = 新算法在控（第 1 步起：开控时选了「新」、锁定成了新的那一场的每一拍，挂起 / Tr 丢失保持那几拍也是 1）
///   lo_isp    新算法想给的夹套目标（℃，已夹到 −50…200）——只是算出来看的，没有给内环
///   lo_b      它实时算的维持温差 b̂（K）：稳住时 = 夹套 − 釜内
///   lo_xr     它滤过的釜内 T̂（℃）
///   lo_wall   它的釜壁估计 Ŵ（℃）
///   lo_tau    它认的釜时间常数 τ̂（s，进公式的那个）
///   lo_tau2   第二路认的 τ（s，只管「τ̂ 往小改不许小过它」）
///   lo_alarm  放热报警的条件这一拍成立（1 / 0）。第 0 步回路不知道继电器在哪一侧，恒 0
///             【本地改动】第 1 步起会话每拍给「TEC 此刻真接着」、两种外环都喂，旧外环在控时这一列也可能是 1（只是记；报警只在新算法在控时报）
/// 后 7 列在影子还没起算（没拿到过「釜内、夹套都有效」的读数）、断档超过 60 s 在等重摆、或者影子停掉时留空。
/// 影子 ≥ 2 s 才算一拍，记录是每个回路周期一行：两拍之间的行重复上一拍的数。
/// 怎么读才可靠：extra / note 两列是自由文字，含逗号 / 引号 / 换行时整格用双引号包起来、里面的引号写两个（标准 CSV）——
/// 用认引号的 CSV 解析器按列名读；图省事按逗号硬拆的，新列要从行尾倒着数（新 8 列全是数字，不含逗号）。
/// 「停止」那一行不是逐拍行：只有 time、elapsed_s、note 三格有内容，note 还在它那一列，别的列（含新 8 列）都空——每一行的列数都等于表头。
///
/// 【本地改动】（2026-11-17，S1「读数对不上就停控」）lo_alarm 后面再加 3 列（前 33 列的列号和内容一个不动），新外环在控时 S1 判据最近一拍的量
/// （HostControlLoop 的 ReadingCheck；两拍之间重复上一拍的数；旧外环在控 / 不在控 / 还没算过留空）：
///   s1_ra     A「读数没跟着动」：最慢的釜按最近 950 s 里夹套推的量该挪多少（K，带号）。≥ 0.25 停控，≤ −0.25 只报警；读数动过 0.02 K 就回 0
///   s1_d      B「加热时读数往下掉」：最近 2850 s 里加热拍读数的最高 − 此刻（K）；不是加热拍 0
///   s1_u      B：最慢的釜这会儿比读数高多少（K）。s1_d ≥ 8 且 s1_u ≥ 0.25 停控
/// 现场拿回记录看这三列离门槛多近（定稿的门槛没在真机放热 / 加料上核过）。
/// </summary>
public sealed class Rd105LoopRecorder : IDisposable
{
    public const string Header =
        "time,elapsed_s,kind,setpoint,inner_sp,tr,tj,duty_written,duty_applied,p,i,d,outer_p,outer_i,outer_d,outer_fin,boost,v_m,v_star,brake_f,actuator,hold_s,cycle_ms,extra,note"
        + ",algo,lo_isp,lo_b,lo_xr,lo_wall,lo_tau,lo_tau2,lo_alarm"        // 【本地改动】外环实时算第 0 步：只在行尾加，前 25 列不动
        + ",s1_ra,s1_d,s1_u";                                              // 【本地改动】（2026-11-17，S1）再在行尾加 3 列，前 33 列不动

    // 【本地改动】「停止」行要把 note 放回它自己那一列、后面补齐空列：列数和 note 的位置都从表头算，表头再加列也不用改
    private static readonly string[] Columns = Header.Split(',');
    private static readonly int NoteIndex = Array.IndexOf(Columns, "note");

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

    /// <summary>下发目标：开一个新文件（已经在记就只记一行「目标改为」）。开文件之前攒下的事件（组合会话定挡那句）跟在「开始」后面。</summary>
    public void Start(string kind, double setpoint, string how)
    {
        lock (_gate)
        {
            if (_w is not null)
            {
                _pendingNote = Join(_pendingNote, $"目标改为 {setpoint:0.##} ℃（{kind}，{how}）");
                return;
            }
            var before = _pendingNote;
            _pendingNote = null;
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
                _pendingNote = Join($"开始：{kind} {setpoint:0.##} ℃（{how}）", before ?? "");
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

    /// <summary>回路的提示 / 停控原因 / 组合会话的定挡这类事件：挂到下一行的 note 里。还没开文件的（下发目标先定挡再开文件）攒着，开了跟在「开始」后面。</summary>
    public void Note(string text)
    {
        lock (_gate) _pendingNote = Join(_pendingNote, text);
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
            Num(sb, info.OuterFinishC);
            Num(sb, info.HeatBoosting ? 1 : double.NaN);         // 【本地改动】升温加速
            Num(sb, info.HeatBoostVmCPerMin);
            Num(sb, info.HeatBoostVstarCPerMin);
            Num(sb, info.HeatBoostBrake);
            sb.Append(actuator).Append(',');
            Num(sb, info.OuterHoldSeconds ?? double.NaN);
            Num(sb, cycleMs);
            sb.Append(Csv(extra)).Append(',').Append(Csv(_pendingNote));
            AppendLiveOuter(sb, info);                           // 【本地改动】外环实时算第 0 步：行尾 8 列
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
                             // 【本地改动】note 放在它自己那一列（原来是「放最后一列」——表头在 note 后面加了列以后会落到新算法的最后一列去），
                             // 后面补齐空列：每一行的列数都等于表头
                             new string(',', NoteIndex - 1) + Csv(note) + new string(',', Columns.Length - 1 - NoteIndex));
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

    /// <summary>
    /// 【本地改动】（外环实时算第 0 步）接在 note 后面的 8 列：algo + 影子这一拍的 7 个读数（没有就 7 个空格子）。
    /// 这一段在 Write 自己的 try 外面、又在回路那条流上：这里漏一个异常会被当成串口故障（这一拍两路不写、可能重开串口），
    /// 所以自己兜住——出了事就把这 8 列写成「0 + 7 个空」，原 25 列照写。
    /// </summary>
    private static void AppendLiveOuter(StringBuilder sb, ChannelCycleInfo info)
    {
        var mark = sb.Length;
        try
        {
            sb.Append(info.LiveOuterInControl ? ",1," : ",0,"); // algo：【本地改动】第 1 步起新算法在控写 1；旧外环在控 / 不在控写 0（旧模式下跟第 0 步逐字节相同）
            if (info.LiveOuter is { } lo)
            {
                Num(sb, lo.JacketSetpointC);
                Num(sb, lo.BHat);
                Num(sb, lo.THat);
                Num(sb, lo.Wall);
                Num(sb, lo.Tau);
                Num(sb, lo.TauEkf);
                sb.Append(lo.AlarmOn ? '1' : '0');
            }
            else sb.Append(',', 6);                              // 7 个空格子之间是 6 个逗号
            // 【本地改动】（2026-11-17，S1）s1_ra / s1_d / s1_u：只在新外环在控、判据算过时有数（三位小数），不然 3 个空格子
            sb.Append(',');
            if (info.LiveOuterInControl && info.ReadingBeat is { } rb)
            {
                Num(sb, rb.Ra);
                Num(sb, rb.D);
                if (double.IsFinite(rb.U)) sb.Append(rb.U.ToString("0.###", CultureInfo.InvariantCulture));
            }
            else sb.Append(',', 2);
        }
        catch
        {
            sb.Length = mark;
            sb.Append(info.LiveOuterInControl ? ",1" : ",0").Append(',', 7);   // 【本地改动】algo 出了事也按实际写（第 1 步）
            sb.Append(',', 3);                                                  // 【本地改动】（S1）s1_ 那 3 列留空
        }
    }

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

    private static string Join(string? a, string b)
        => string.IsNullOrEmpty(a) ? b : string.IsNullOrEmpty(b) ? a : a + "；" + b;

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
