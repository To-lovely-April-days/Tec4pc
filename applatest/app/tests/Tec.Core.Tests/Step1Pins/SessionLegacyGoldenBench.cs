using System.Globalization;
using System.Reflection;
using System.Text;
using TecControl.Core.Comm;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Drivers.Rd105.Modbus;
using Xunit;

namespace Tec.Core.Tests.Step1Pins;

/// <summary>
/// 【本地改动】外环「按实测实时算」第 1 步之前的钉子（2026-11-16，主会话 DECISIONS「证明旧模式一字不变」第 1 层）：
/// 会话金样的台子。温控器 PID 假机 + 假钟 + 手动 PollOnceAsync（同 DuoPushReentryTests / ChillerInterlockTests 那套），
/// 再把三处会抖的地方按死，让同一段脚本每次跑出来逐字一样：
///   ① 会话自带的采集循环（PeriodicTimer 5 s，墙钟）开完会话就取消（反射 _pollCts / _pollTask，同验收 Rig 的做法）——
///      不然测试墙钟一过 5 s 它会用同一个假钟时刻多推一拍，滑窗多一个点、斜率和日志里的 K/min 跟着变；
///   ② 假钟起点写死（2026-11-16 09:00 +08:00），问水的问出 / 截止时刻按相对秒记；
///   ③ 等夹套 / 占空比按「读到的就是写进去的」（差 &lt; 1e-6）才推拍，不用 0.01 的宽容差（相邻两拍差 0.005 时会抢跑）。
/// 逐拍记下：IO8R 收到的每一条请求（读 / 写 DO）、冷水机收到的每一条、RD105 收到的每一条「写」（读和轮询不记：轮询在墙钟上自己转）、
/// 继电器 DO0–7、阀、冷水机状态、RD105 两路 TG / SPEED / ENABLE / MODE、每一路的意图 / 输出 / 目标、挡位文字（Regime）、
/// 压低量（CascadePushC）、逐拍记录 extra 列那段（会话的 LoopExtra）、问水槽位、这一拍新出的日志、这一拍新挂的逐拍记录 Note。
/// 状态类只在变了的那一拍印（第一拍全印），事件类每拍印。
/// 逐拍记录 Note：温控器 PID 方式下 Rd105TemperatureControl 没装记录器，Note 是空操作；这里给两路各装一个不开文件的
/// Rd105LoopRecorder（反射写 internal 的 Recorder），每拍把它攒着的 _pendingNote 取走——会话往逐拍记录里挂的每一句都看得见。
/// 温控器 PID 方式下没有上位机回路：压低（PushJacketBelow）恒返回 false，金样里看到的是「这一路不压夹套目标……段里每拍再试」那句，
/// 真压到回路上的那条路进不了金样（回路走墙钟），归第 2 层（回路两路差分）管。
/// </summary>
internal sealed class SessionLegacyGoldenBench : IAsyncDisposable
{
    /// <summary>假钟起点（写死，金样里的时刻都按它算相对秒）。</summary>
    public static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.FromHours(8));

    private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
    private const BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public readonly FakeRd105Device Rd = new();
    public readonly FakeModbusSlave Io = new();
    public readonly FakeChiller Chiller = new();
    private readonly Rd105WriteTap _rdTap;

    public DateTimeOffset Now = T0;
    public IDeviceSession S = null!;
    public DuoSession D = null!;
    public ITemperatureControl[] T = null!;
    public ICoolingWaterValve Water = null!;

    private readonly List<(string Level, string Text)> _logs = new();
    private Rd105Session _rd = null!;
    private Rd105LoopRecorder[] _rec = null!;
    private readonly StringBuilder _trace = new();
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);
    private int _step, _ioSeen, _chSeen, _logSeen;
    private readonly double[] _duty = { 0, 0 };
    private readonly double[] _tj = { 25, 25 };
    private bool _bInUse;

    private SessionLegacyGoldenBench() => _rdTap = new Rd105WriteTap(Rd);

    /// <summary>金样全文（换行一律 \n）。</summary>
    public string Trace => _trace.ToString();

    /// <summary>
    /// 开会话：温控器 PID 方式、通水延时 0（不然切换序列要真等 5 s）、冷水机联动（假冷水机）、缺省「冷水机」已开 = 双向挡；
    /// 其余全按配置缺省（提前切换偏差 0.1、最短制冷 60 s、再进间隔 60 s、退出超前 180 s、冷水机最短间隔 60 s、等答复 60 s、符号驻留 3 s）。
    /// </summary>
    public static async Task<SessionLegacyGoldenBench> OpenAsync()
    {
        var b = new SessionLegacyGoldenBench();
        var cfg = new ParameterSet
        {
            [Rd105TecDriver.FieldControl] = Rd105TecDriver.ControlDevice,
            [DualStationDriver.Fields.WaterLead] = 0d,
        };
        var drv = new DualStationDriver
        {
            LinksFactory = _ => new DuoLinks
            {
                Rd105 = new Rd105Link(b._rdTap),
                IoPort = b.Io,
                Io = new Io8rClient(new ModbusRtuClient(b.Io, 1, 200)),
                ChillerPort = b.Chiller,
                Chiller = new ChillerClient(new ModbusRtuClient(b.Chiller, 1, 200))
            }
        };
        var ctx = new DriverContext
        {
            InstanceId = "DUO1",
            ChannelNumbers = new[] { 1, 2 },
            Config = cfg,
            Simulated = false,
            TimeScale = 1,
            Clock = () => b.Now,
            Log = (lvl, text) => { lock (b._logs) b._logs.Add((lvl, text)); }
        };
        // RD105 周期 200 ms（下限）、模块轮询 5000 ms（上限；反正下面把会话自带的循环停了）
        b.S = await drv.OpenAsync(ParameterSet.Of((Rd105TecDriver.FieldPeriod, 200d), (DualStationDriver.Fields.Tick, 5000d)), ctx, CancellationToken.None);
        b.D = (DuoSession)b.S;
        b.D.ChillerStopWaitOnDispose = TimeSpan.FromMilliseconds(600);     // 收会话时冷水机在转别真等 15 s（收尾不进金样）

        b._rd = (Rd105Session)(typeof(DuoSession).GetField("_rd", Priv)
            ?? throw new InvalidOperationException("DuoSession._rd 找不到（改名了？）——金样台子要拿它的 LoopExtra（逐拍记录 extra 列）")).GetValue(b.D)!;
        var recorderProp = typeof(Rd105TemperatureControl).GetProperty("Recorder", Priv)
            ?? throw new InvalidOperationException("Rd105TemperatureControl.Recorder 找不到（改名了？）——金样台子靠它接住会话挂的逐拍记录 Note");
        b._rec = new[] { new Rd105LoopRecorder("DUO1", 1, (_, _) => { }), new Rd105LoopRecorder("DUO1", 2, (_, _) => { }) };
        for (var w = 0; w < 2; w++) recorderProp.SetValue(b.D.InnerTemp(w), b._rec[w]);

        await b.S.StartAsync(CancellationToken.None);
        // 会话自带的采集循环停掉（墙钟 PeriodicTimer），以后只由脚本手动推拍
        var cts = (CancellationTokenSource?)(typeof(DuoSession).GetField("_pollCts", Priv)
            ?? throw new InvalidOperationException("DuoSession._pollCts 找不到（改名了？）")).GetValue(b.D);
        var task = (Task?)(typeof(DuoSession).GetField("_pollTask", Priv)
            ?? throw new InvalidOperationException("DuoSession._pollTask 找不到（改名了？）")).GetValue(b.D);
        cts!.Cancel();
        try { await task!; } catch { /* 取消 */ }

        b.T = new[] { Temp(b.S, 0), Temp(b.S, 1) };
        b.Water = b.S.CapabilitiesOf(0).OfType<ICoolingWaterValve>().Single();
        await b.WaitJacket(0, 25);
        await b.WaitJacket(1, 25);
        b.Snap("开会话（温控器 PID、假钟、会话自带采集循环已停，之后手动推拍）");
        return b;
    }

    private static ITemperatureControl Temp(IDeviceSession s, int well) => s.CapabilitiesOf(well).OfType<ITemperatureControl>().Single();

    private static string AB(int w) => w == 0 ? "A" : "B";

    // ── 脚本动作 ─────────────────────────────────────────────────────

    /// <summary>下发目标（缺省 Kind = 釜内，同现有用例；温控器 PID 方式下没人喂 Tr，被控量按夹套）。askWater = HMI 手动开、开那一下没问过（AskWaterNext）。</summary>
    public async Task Target(int w, double value, bool askWater = false)
    {
        if (w == 1) _bInUse = true;
        if (askWater) ((IHeatSource)T[w]).AskWaterNext();
        await T[w].SetTargetAsync(new TempTarget(value), CancellationToken.None);
        Snap($"下发 {AB(w)} 目标 {value:0.##} ℃" + (askWater ? "（手动开、开那一下没问过水：AskWaterNext）" : ""));
    }

    /// <summary>温控器 PID 方式的功率 = 假机里的 PWMDUTY（这台加热棒吃负：−5 = 要加热 5 %，+20 = 要制冷 20 %）。等内层读到再往下走；不单独记一拍（下一拍的标签里带着）。</summary>
    public async Task Duty(int w, double percent)
    {
        if (w == 1) _bInUse = true;
        _duty[w] = percent;
        Rd.Set(w + 1, "PWMDUTY", (long)Math.Round(percent * 20_000));
        var inner = D.InnerTemp(w);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(Math.Abs(inner.LastDuty - percent) < 1e-6)) await Task.Delay(10);
        Assert.True(Math.Abs(inner.LastDuty - percent) < 1e-6, $"工位 {AB(w)} 占空比 {percent} 没读到（{inner.LastDuty}）");
    }

    /// <summary>把夹套拨到 tj（null = 不动）、等内层读到、假钟拨过 dt 秒、推一拍。</summary>
    public async Task Tick(double? tjA, double dt, double? tjB = null, string? why = null)
    {
        if (tjB is not null) _bInUse = true;
        if (tjA is { } a) _tj[0] = a;
        if (tjB is { } bb) _tj[1] = bb;
        for (var w = 0; w < 2; w++)
        {
            Rd.Set(w + 1, "TCADJTEMP", (long)Math.Round(_tj[w] * 1e5));
            await WaitJacket(w, _tj[w]);
        }
        Now = Now.AddSeconds(dt);
        await D.PollOnceAsync(CancellationToken.None);
        var label = $"推一拍 +{dt.ToString("0.###", Ci)} s · A 夹套 {_tj[0].ToString("F2", Ci)} 占空比 {_duty[0].ToString("+0.##;-0.##;0", Ci)} %";
        if (_bInUse) label += $" · B 夹套 {_tj[1].ToString("F2", Ci)} 占空比 {_duty[1].ToString("+0.##;-0.##;0", Ci)} %";
        Snap(why is null ? label : label + " · " + why);
    }

    /// <summary>操作人在面板上答「开不开冷却水」（落地在下一拍）。</summary>
    public void Answer(bool open)
    {
        var q = Water.Pending ?? throw new Xunit.Sdk.XunitException("没挂询问\n" + Tail());
        Assert.True(Water.Answer(q.Id, open));
        Snap($"操作人答询问 #{q.Id}：{(open ? "打开冷却水" : "不开")}");
    }

    /// <summary>面板手动开 / 关冷却水。</summary>
    public async Task PanelWater(bool open)
    {
        await Water.SetAsync(open, CancellationToken.None);
        Snap($"面板{(open ? "打开" : "关闭")}冷却水");
    }

    /// <summary>假冷水机走完延时停机 / 准备启动（不记拍，下一拍的状态里看得见）。</summary>
    public void SettleChiller() => Chiller.Settle();

    /// <summary>停这一路温控（操作人停控）。</summary>
    public async Task Stop(int w)
    {
        await T[w].StopAsync(CancellationToken.None);
        Snap($"停控 {AB(w)}");
    }

    private async Task WaitJacket(int w, double value)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && !(Math.Abs(T[w].CurrentJacket - value) < 1e-6)) await Task.Delay(10);
        Assert.True(Math.Abs(T[w].CurrentJacket - value) < 1e-6, $"工位 {AB(w)} 夹套 {value} 没读到（{T[w].CurrentJacket}）");
    }

    // ── 查询（给脚本里的「先证明走到了那条规则」用）──────────────────

    /// <summary>到目前为止日志里含 needle 的条数。</summary>
    public int Count(string needle) { lock (_logs) return _logs.Count(l => l.Text.Contains(needle, StringComparison.Ordinal)); }

    /// <summary>金样全文里含 needle 的次数（日志 + Note + 状态都算）。</summary>
    public int TraceCount(string needle)
    {
        var text = Trace;
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    public string Tail()
    {
        var text = Trace;
        var lines = text.Split('\n');
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 40)));
    }

    // ── 落一拍 ───────────────────────────────────────────────────────

    private void Snap(string label)
    {
        _step++;
        _trace.Append('[').Append(_step.ToString("D3", Ci)).Append("] t=+").Append((Now - T0).TotalSeconds.ToString("0.0", Ci)).Append(" s　").Append(label).Append('\n');

        // 状态：变了才印
        Put("DO0-7", string.Concat(Io.Coils.Select(c => c ? '1' : '0')));
        Put("阀", $"开={Water.IsOpen} 关水挂着={Water.Closing} 开水在途={Water.Opening} 备好={Water.Ready} 状态「{Water.Status ?? "—"}」 告警「{Water.Alert ?? "—"}」");
        Put("冷水机", Chiller.State.ToString(Ci));
        Put("RD105", string.Join("；", new[] { 1, 2 }.Select(ch =>
            $"TC{ch} TG={Rd.Get(ch, "TG")} SPEED={Rd.Get(ch, "SPEED")} ENABLE={Rd.Get(ch, "ENABLE")} MODE={Rd.Get(ch, "MODE")}")));
        for (var w = 0; w < 2; w++)
        {
            var inner = D.InnerTemp(w);
            Put($"{AB(w)}·控温", $"意图={D.WantEnabled(w)} 输出={inner.Enabled} 目标={Fmt(inner.Setpoint)}");
            Put($"{AB(w)}·挡位", ((IHeatSource)T[w]).Regime ?? "—");
            Put($"{AB(w)}·压低", Fmt(((ITemperatureStatus)T[w]).CascadePushC));
            Put($"{AB(w)}·extra", _rd.LoopExtra?.Invoke(w) ?? "—");
        }
        Put("问水", Water.Pending is { } q
            ? $"#{q.Id} 通道{q.Channel} 「{q.Why}」 问于 +{(q.AskedAt - T0).TotalSeconds.ToString("0.0", Ci)} 截止 +{(q.Deadline - T0).TotalSeconds.ToString("0.0", Ci)} 有人等={q.Waiting}"
            : "无");

        // 事件：每拍印
        var io = Io.Requests;
        if (io.Count > _ioSeen) { _trace.Append("  IO8R：").Append(string.Join(" | ", io.Skip(_ioSeen))).Append('\n'); _ioSeen = io.Count; }
        var chs = Chiller.Requests;
        if (chs.Count > _chSeen) { _trace.Append("  冷水机：").Append(string.Join(" | ", chs.Skip(_chSeen))).Append('\n'); _chSeen = chs.Count; }
        var rdw = _rdTap.TakeWrites();
        if (rdw.Count > 0) _trace.Append("  RD105 写：").Append(string.Join(" | ", rdw)).Append('\n');
        List<(string Level, string Text)> logs;
        lock (_logs) { logs = _logs.Skip(_logSeen).ToList(); _logSeen = _logs.Count; }
        foreach (var (lvl, text) in logs) _trace.Append("  日志·").Append(lvl).Append("  ").Append(OneLine(text)).Append('\n');
        for (var w = 0; w < 2; w++)
            if (TakeNote(_rec[w]) is { } note) _trace.Append("  Note·").Append(AB(w)).Append("  ").Append(OneLine(note)).Append('\n');
    }

    private void Put(string key, string value)
    {
        if (_last.TryGetValue(key, out var was) && was == value) return;
        _last[key] = value;
        _trace.Append("  · ").Append(key).Append(" = ").Append(OneLine(value)).Append('\n');
    }

    private static string Fmt(double? v) => v is { } x ? x.ToString("0.###", Ci) : "—";

    private static string OneLine(string s) => s.Replace("\r", "").Replace("\n", "⏎");

    private static readonly FieldInfo PendingNote = typeof(Rd105LoopRecorder).GetField("_pendingNote", Priv)
        ?? throw new InvalidOperationException("Rd105LoopRecorder._pendingNote 找不到（改名了？）——金样台子靠它取会话挂的逐拍记录 Note");

    /// <summary>取走记录器攒着的 Note（没开文件的记录器只攒不写）。Note 只在推拍 / 下发 / 停控这条流程里挂（会话自带循环已停），跟这里不并发。</summary>
    private static string? TakeNote(Rd105LoopRecorder r)
    {
        var n = (string?)PendingNote.GetValue(r);
        PendingNote.SetValue(r, null);
        return n;
    }

    public async ValueTask DisposeAsync()
    {
        try { await S.DisposeAsync(); } catch { /* 收尾不进金样 */ }
    }

    /// <summary>
    /// RD105 口子上套一层：只记「写」（TCn:XXX=值，值不是「?」、也不是 DATADEMAND 轮询）——会话写给温控器的东西；
    /// 读 / 轮询在墙钟上自己转，条数不确定，不记。按 '\n' 攒成整帧再判。
    /// </summary>
    private sealed class Rd105WriteTap(FakeRd105Device dev) : ISerialTransport
    {
        private readonly StringBuilder _line = new();
        private readonly List<string> _writes = new();
        public bool IsOpen => dev.IsOpen;
        public void Open() => dev.Open();
        public void Close() => dev.Close();
        public void DiscardInput() => dev.DiscardInput();
        public void Dispose() => dev.Dispose();
        public int Read(byte[] buffer, int offset, int count, int timeoutMs) => dev.Read(buffer, offset, count, timeoutMs);

        public void Write(byte[] buffer, int offset, int count)
        {
            lock (_writes)
            {
                for (var i = 0; i < count; i++)
                {
                    var c = (char)buffer[offset + i];
                    _line.Append(c);
                    if (c != '\n') continue;
                    var frame = _line.ToString().Trim();
                    _line.Clear();
                    var body = frame.TrimEnd('@');
                    var eq = body.IndexOf('=');
                    if (eq > 0 && body[(eq + 1)..] != "?" && body[..eq] != "DATADEMAND") _writes.Add(body);
                }
            }
            dev.Write(buffer, offset, count);
        }

        public List<string> TakeWrites()
        {
            lock (_writes)
            {
                var r = _writes.ToList();
                _writes.Clear();
                return r;
            }
        }
    }
}

/// <summary>
/// 【本地改动】金样夹具读写：放在 tests/Tec.Core.Tests/Step1Pins/golden/（测试工程不拷贝进输出目录，从 bin 往上找 csproj，同 LiveOuterFixtures）。
/// 平时只比对：夹具缺了就红（不会自己补录）。重录：环境变量 TEC_STEP1PINS_RECORD=1 写回仓库里的夹具；
/// 设成一个目录就写到那个目录（只用来核「连跑两遍逐字一样」，不碰仓库）。旧模式不许变，正常情况下不该重录。
/// </summary>
internal static class SessionLegacyGolden
{
    private static readonly Lazy<string> DirLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tec.Core.Tests.csproj")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("从 " + AppContext.BaseDirectory + " 往上没找到 Tec.Core.Tests.csproj");
        return Path.Combine(dir.FullName, "Step1Pins", "golden");
    });

    public static string Dir => DirLazy.Value;

    public static void Match(string name, string actual)
    {
        var record = Environment.GetEnvironmentVariable("TEC_STEP1PINS_RECORD");
        if (!string.IsNullOrWhiteSpace(record))
        {
            var dir = record == "1" ? Dir : record;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, name + ".txt"), actual, new UTF8Encoding(false));
            return;
        }
        var path = Path.Combine(Dir, name + ".txt");
        Assert.True(File.Exists(path), $"金样夹具 {path} 不存在。它是第 1 步改产品代码之前在 22b9ebe 上录的，不该缺；不要拿改过的代码补录");
        var want = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n");
        if (want == actual) return;
        var a = actual.Split('\n');
        var w = want.Split('\n');
        var i = 0;
        while (i < a.Length && i < w.Length && a[i] == w[i]) i++;
        var dump = Path.Combine(Path.GetTempPath(), $"step1pins-{name}.actual.txt");
        try { File.WriteAllText(dump, actual, new UTF8Encoding(false)); } catch { dump = "（没写成）"; }
        // 往上找这一拍的拍头，报出来好定位
        var head = "";
        for (var k = Math.Min(i, a.Length - 1); k >= 0; k--)
            if (a[k].StartsWith('[')) { head = a[k]; break; }
        Assert.Fail($"旧模式会话金样「{name}」第 {i + 1} 行起跟夹具不一样（旧模式下会话的线圈 / 挡位 / 问水 / 日志 / Note 必须逐字不变）：\n" +
                    $"  所在拍：{head}\n" +
                    $"  夹具：{(i < w.Length ? w[i] : "（夹具到头了）")}\n" +
                    $"  这次：{(i < a.Length ? a[i] : "（这次到头了）")}\n" +
                    $"  这次全文：{dump}\n  夹具：{path}");
    }
}
