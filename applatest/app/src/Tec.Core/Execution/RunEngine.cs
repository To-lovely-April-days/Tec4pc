using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;

namespace Tec.Core.Execution;

/// <summary>
/// 批次编排。它**不**是"整机启动"按钮——通道各自启动，
/// 批次只是把同时段的通道子记录归拢在一起（§7.1）。
/// </summary>
public sealed class RunEngine
{
    private readonly Dictionary<int, ChannelRunner> _runners = new();
    private readonly List<IDisposable> _subs = new();

    public RunEngine(ICommandCatalog catalog, ICommandProvider builtins,
                     IResourceArbiter arbiter, DataPipeline pipeline,
                     Func<DateTimeOffset>? now = null)
    {
        Catalog = catalog;
        Builtins = builtins;
        Arbiter = arbiter;
        Pipeline = pipeline;
        Now = now ?? (() => DateTimeOffset.Now);
        Safety = new SafetyMonitor(pipeline, Now);
        Safety.Triggered += OnSafetyTriggered;
        Safety.Cleared += OnSafetyCleared;
    }

    public ICommandCatalog Catalog { get; }
    public ICommandProvider Builtins { get; }
    public IResourceArbiter Arbiter { get; }
    public DataPipeline Pipeline { get; }
    public SafetyMonitor Safety { get; }
    /// <summary>报警本。安全层判断，它记「还有几条没人管」（§7.5）。</summary>
    public AlarmBook Alarms { get; } = new();
    public Func<DateTimeOffset> Now { get; }

    public RunRecord Record { get; private set; } = new() { CreatedAt = DateTimeOffset.Now };
    public IReadOnlyCollection<ChannelRunner> Runners => _runners.Values;

    private readonly List<RunRecord> _batches = new();

    /// <summary>
    /// 这一开机跑过的全部批次，新的在后面。<see cref="Record"/> 是其中最后一条。
    ///
    /// 从前开一个新批次就把上一个丢了：跑完一炉再起一炉，上一炉的执行记录当场
    /// 从内存里消失，导出页也就再也找不到它。批次只占几百条记录，采样在管线里
    /// 本来就还留着——丢掉它没有任何好处。
    ///
    /// **只是内存里的**：关掉程序就没了，跨次开机要等归档文件读取做出来。
    /// </summary>
    public IReadOnlyList<RunRecord> Batches => _batches.ToArray();

    public event EventHandler? Changed;

    /// <summary>共享资源的声明：某通道执行某条指令时要占用什么。台面知道，Core 不猜。</summary>
    public Func<int, string, ResourceNeed?> ResourceOf { get; set; } = (_, _) => null;

    public double TimeScale { get; set; } = 1;

    public void NewBatch(string name, string? op, string benchName)
    {
        // 上一个批次一条通道都没跑过就把它丢掉——那是「按了运行又没启动成」
        // 留下的空壳，摆进导出页的记录表里只会占一行，点进去什么都没有
        if (_batches.Count > 0 && _batches[^1].Channels.Count == 0) _batches.RemoveAt(_batches.Count - 1);

        var at = Now();
        Record = new RunRecord
        {
            RunId = NextRunId(at),
            CreatedAt = at,
            Name = name,
            Operator = op,
            BenchName = benchName
        };
        _batches.Add(Record);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 启动之前叫一声：该开新批次就开，不该开就沿用当前这个。返回是不是开了新的。
    ///
    /// **批次 = 同一时段的那几路**（§7.1）。判据只有一条：还有没有通道在跑。
    /// 有就归到这一炉里（先起 CH1、一分钟后再起 CH2，是同一炉）；
    /// 全都停下来之后再按启动，那是下一炉——它该在导出页里单独占一行。
    ///
    /// 从前的判据是「记录里一条通道都没有」，于是**这一开机永远只有一个批次**：
    /// 跑完一炉再跑一炉，两炉的子记录混在同一条记录里，导出时分不开。
    /// </summary>
    public bool EnsureBatch(string name, string? op, string benchName)
    {
        var live = _runners.Values.Any(r => r.State is ChannelRunState.Running
                                                    or ChannelRunState.Paused
                                                    or ChannelRunState.Aborting);
        if (Record.Channels.Count > 0 && live) return false;
        NewBatch(name, op, benchName);
        return true;
    }

    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// 把归档里已经有的记录编号报进来，开机时叫一次。
    ///
    /// 不报的话，同一天里关掉程序再开，这一开机的第一炉又会拿到 -001——
    /// 而编号是归档目录名和导出目录名，撞上就是**上午那一炉被下午这一炉盖掉**。
    /// </summary>
    public void ReserveRunIds(IEnumerable<string> ids)
    {
        foreach (var id in ids) _usedIds.Add(id);
    }

    /// <summary>
    /// 记录编号 EXP-yyyyMMdd-nnn，当天第几炉就是几。序号往后找第一个没人用过的，
    /// 不用时分秒——同一秒里连开两个批次会撞号，而记录编号是导出文件的文件名。
    /// </summary>
    private string NextRunId(DateTimeOffset at)
    {
        var prefix = $"EXP-{at:yyyyMMdd}-";
        for (var n = 1; ; n++)
        {
            var id = prefix + n.ToString("D3");
            if (_usedIds.Contains(id)) continue;
            if (_batches.Any(b => b.RunId == id)) continue;
            _usedIds.Add(id);
            return id;
        }
    }

    /// <summary>
    /// 把一条通道接进引擎。同一条通道重复接就是同一个执行器。
    ///
    /// **台面重建之后必须换执行器。** 台面一动（拖进一台设备、删掉一台）就会
    /// 重开全部会话、重建全部 Channel 对象；这时通道号还是那个号，但对象和
    /// 它背后的会话都是新的。老执行器手里攥着已经 Dispose 的会话，让它接着跑
    /// 等于对着空气下指令——界面上通道明明「运行中」，釜温却一动不动，
    /// 而且一声不吭。所以对象换了就把老的摘掉重来。
    /// </summary>
    public ChannelRunner Attach(Channel channel)
    {
        if (_runners.TryGetValue(channel.Number, out var existing))
        {
            if (ReferenceEquals(existing.Channel, channel)) return existing;
            Detach(channel.Number, "台面重建，设备会话已重开");
        }
        var r = new ChannelRunner(channel, Catalog, Builtins, Arbiter, Now, (ch, id) => ResourceOf(ch, id))
        {
            TimeScale = TimeScale
        };
        r.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        _runners[channel.Number] = r;
        return r;
    }

    /// <summary>
    /// 摘掉一条通道。正在跑的先中止——它的设备会话已经没了，
    /// 让记录停在「运行中」比停在「中止」糟得多：后者至少写清了为什么停。
    /// </summary>
    public void Detach(int channel, string reason = "通道从台面移除")
    {
        if (_runners.Remove(channel, out var r) && r.State is ChannelRunState.Running or ChannelRunState.Paused)
            r.Abort(null, reason);
    }

    public ChannelRunner? Runner(int channel)
        => _runners.TryGetValue(channel, out var r) ? r : null;

    /// <summary>启动一个通道。用户用几个就是几个。</summary>
    public ChannelRun StartChannel(int channel, Recipe recipe, string? user = null, EstimationContext? seed = null)
    {
        var r = _runners.TryGetValue(channel, out var found)
            ? found
            : throw new InvalidOperationException($"CH{channel} 不在台面上。");
        r.TimeScale = TimeScale;
        var run = r.Start(recipe, seed ?? SeedFor(r.Channel), user);
        Record.Append(run);
        Changed?.Invoke(this, EventArgs.Empty);
        return run;
    }

    /// <summary>把当前实测温度等作为估算起点——排期才不会从 25 ℃ 一路空想。</summary>
    private EstimationContext SeedFor(Channel ch)
    {
        var ctx = new EstimationContext();
        if (ch.Capabilities.Get<ITemperatureControl>() is { } t)
        {
            ctx.Temperature = t.CurrentReactor;
            ctx.Jacket = t.CurrentJacket;
        }
        if (ch.Capabilities.Get<IStirrer>() is { } s) ctx.Rpm = s.CurrentRpm;
        if (ch.Capabilities.Get<IDosing>() is { } d) ctx.Volume = d.TotalVolume;
        return ctx;
    }

    /// <summary>
    /// 手动标记：操作人在运行途中记的一笔（「取样一次」「投料完成」「看到析晶」）。
    /// 记录只追加不修改（§8.3），所以标记就是一条 OperatorMark 事件，
    /// 落在这条通道的记录链上，导出时跟步骤行一起走。
    ///
    /// 只有跑起来的通道才能标——没有运行记录就没有链可挂，
    /// 挂到哪儿都是编的。返回 false 表示这条通道还没启动。
    /// </summary>
    public bool Mark(int channel, string text, string? user = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var run = Record.Of(channel);
        if (run is null) return false;
        run.Append(new EventRecord
        {
            At = Now(),
            Channel = channel,
            Kind = EventKind.OperatorMark,
            Text = text.Trim(),
            User = user,
            StepIndex = run.Current?.Index
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void PauseAll(string? user = null)
    {
        foreach (var r in _runners.Values) r.Pause(user);
    }

    public void AbortAll(string? user = null, string reason = "全机中止")
    {
        foreach (var r in _runners.Values) r.Abort(user, reason);
    }

    /// <summary>把设备会话的采样接进管线。断线时驱动必须自己发 Bad/Stale（§9.4）。</summary>
    public void Ingest(IDeviceSession session)
    {
        foreach (var t in session.Tags) Pipeline.DescribeTag(t);
        _subs.Add(session.Samples.SubscribeTo(s => Pipeline.Push(s)));
    }

    // ── 报警 ─────────────────────────────────────────────────────────

    /// <summary>安全动作要在几秒内落到设备上。串口不响应也不能把安全层卡住。</summary>
    private static readonly TimeSpan ActBudget = TimeSpan.FromSeconds(5);

    private void OnSafetyTriggered(object? sender, SafetyEvent e)
    {
        // 先动手，再记账：记录里那句「已切断加热输出」必须真的做过才写得出来
        var did = ActOn(e);
        Alarms.Raise(e, did, e.At);

        var run = Record.Of(e.Channel);
        if (run is not null)
        {
            run.Append(new EventRecord
            {
                At = e.At,
                Channel = e.Channel,
                Kind = EventKind.Alarm,
                Text = e.Limit.Action == SafetyAction.Alarm
                    ? e.Message
                    : $"{e.Message} —— 动作：{ActionWord(e.Limit.Action)}"
            });
            foreach (var line in did)
                run.Append(new EventRecord
                {
                    At = e.At,
                    Channel = e.Channel,
                    Kind = EventKind.SafetyAction,
                    Text = line
                });
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSafetyCleared(object? sender, SafetyEvent e)
    {
        if (Alarms.Resolve(SafetyMonitor.KeyOf(e.Limit), e.At) is not { } a) return;
        Record.Of(e.Channel)?.Append(new EventRecord
        {
            At = e.At,
            Channel = e.Channel,
            Kind = EventKind.AlarmCleared,
            Text = $"{e.Message}（持续 {Fmt.Hms(a.Duration(e.At))}）"
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string ActionWord(SafetyAction a) => a switch
    {
        SafetyAction.Alarm => "仅报警",
        SafetyAction.StopDosing => "停加料",
        SafetyAction.StopHeating => "停加热",
        SafetyAction.AbortChannel => "中止本通道",
        SafetyAction.StopAll => "中止全部通道",
        _ => a.ToString()
    };

    /// <summary>
    /// 照限值声明的动作真的去做，返回做了什么。
    ///
    /// **停加料 / 停加热从前落在 default 分支上什么也没做**——限值上写着
    /// 「超限停加料」，超限了泵却照打，比没有这条限值更糟：现场以为有人兜着。
    /// 走能力接口而不是认设备型号：上层只问「这一路能不能停加热」（§3.2）。
    /// </summary>
    private IReadOnlyList<string> ActOn(SafetyEvent e)
    {
        var caps = Runner(e.Channel)?.Channel.Capabilities;
        switch (e.Limit.Action)
        {
            case SafetyAction.Alarm:
                // 只报警。不动设备也是一种明确的处置，别偷偷替它做点什么
                return Array.Empty<string>();

            case SafetyAction.StopHeating:
                return new[] { caps?.Get<ITemperatureControl>() is { } t
                    ? Act("切断加热输出", ct => t.StopAsync(ct))
                    : "这一路没有可停的控温设备，加热输出未动" };

            case SafetyAction.StopDosing:
                return new[] { caps?.Get<IDosing>() is { } d
                    ? Act("停加料泵", ct => d.StopAsync(ct))
                    : "这一路没有加料设备，无需停泵" };

            case SafetyAction.AbortChannel:
                if (Runner(e.Channel) is not { } r ||
                    r.State is not (ChannelRunState.Running or ChannelRunState.Paused))
                    return new[] { "该通道未在运行，无需中止" };
                r.Abort(null, e.Message);
                // 收安全态由 ChannelRunner 做，逐条另记（EventKind.SafeStop）
                return new[] { "已中止本通道，设备转入安全态" };

            case SafetyAction.StopAll:
                var live = Runners.Count(x => x.State is ChannelRunState.Running or ChannelRunState.Paused);
                if (live == 0) return new[] { "没有正在运行的通道，无需中止" };
                AbortAll(null, e.Message);
                return new[] { $"已中止全部 {live} 条运行中的通道" };

            default:
                return Array.Empty<string>();
        }
    }

    private static string Act(string what, Func<CancellationToken, Task> go)
    {
        try
        {
            using var cts = new CancellationTokenSource(ActBudget);
            var task = go(cts.Token);
            if (!task.Wait(ActBudget)) return $"{what}：超时未响应，输出可能仍开着";
            return $"已{what}";
        }
        catch (Exception ex)
        {
            // 安全动作没做成是最该留痕的一种：机器还开着，而且没人知道
            return $"{what}失败：{(ex is AggregateException ag ? ag.InnerException?.Message ?? ag.Message : ex.Message)}";
        }
    }

    /// <summary>
    /// 有人确认了一条报警。**确认不等于解除**：条件还成立的话它继续挂在本上，
    /// 只是记录里从此答得出「谁在什么时候看见了它」。
    /// </summary>
    public bool AckAlarm(string key, string? user, string? note = null)
    {
        if (Alarms.Ack(key, user, note, Now()) is not { } a) return false;
        Record.Of(a.Channel)?.Append(new EventRecord
        {
            At = Now(),
            Channel = a.Channel,
            Kind = EventKind.AlarmAck,
            // 「确认的那一刻条件还成不成立」是两回事：确认了但还在报，
            // 和确认时早已恢复，事后追责起来完全不同。
            // 不套 StateText——它自己带括号，套进来就成了「（报警中（已确认））」
            Text = $"确认报警（{(a.Standing ? "条件仍成立" : "已恢复")}）：{a.Message}"
                   + (a.AckNote is null ? "" : $" —— {a.AckNote}"),
            User = user
        });
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>一次确认全部。返回真的确认掉了几条。</summary>
    public int AckAllAlarms(string? user, string? note = null)
    {
        var n = 0;
        foreach (var a in Alarms.Live.Where(x => !x.Acknowledged).ToList())
            if (AckAlarm(a.Key, user, note)) n++;
        return n;
    }

    /// <summary>
    /// 限值重设（台面重建）。正在报的那几条一并收尾——它们的限值马上就没了，
    /// 留着只会等一个再也不会来的恢复。没确认过的仍留在本上等人确认。
    /// </summary>
    public void ResetSafety(string reason)
    {
        Safety.Clear();
        foreach (var a in Alarms.ResolveAll(Now(), reason))
            Record.Of(a.Channel)?.Append(new EventRecord
            {
                At = Now(),
                Channel = a.Channel,
                Kind = EventKind.AlarmCleared,
                Text = $"{a.Message}（持续 {Fmt.Hms(a.Duration(Now()))}）"
            });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        foreach (var s in _subs) s.Dispose();
        _subs.Clear();
    }
}
