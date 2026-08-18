using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Chemistry;
using Tec.Core.Records;
using Tec.Core.Recipes;
using Tec.Core.Scheduling;
using Tec.Driver.Abi;

namespace Tec.Core.Execution;

/// <summary>共享资源的用法由指令声明：加料泵是典型的共享件（§5.1 / §7.4）。</summary>
public sealed record ResourceNeed(string ResourceId, ResourcePolicy Policy, Priority Priority = Priority.Normal);

public sealed record HotEditResult(bool Applied, string Message);

/// <summary>
/// 每通道一台状态机（§7.1）。
/// Idle → Loading → Ready → Running ⇄ Paused → Completing → Completed
///                              ↓                  ↓
///                           Aborting           Faulted
/// </summary>
public sealed class ChannelRunner
{
    private readonly Channel _channel;
    private readonly ICommandCatalog _catalog;
    private readonly ICommandProvider _builtins;
    private readonly IResourceArbiter _arbiter;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<int, string, ResourceNeed?> _resourceOf;

    private Recipe? _live;                       // 运行中可热改的那一份，基线不动
    private CancellationTokenSource? _cts;
    private volatile TaskCompletionSource<bool>? _pauseGate;   // 非空 = 暂停中
    private volatile bool _skipRequested;
    private Task? _loop;
    private StepRecord? _pending;                // Start() 里同步建好的第一步，循环接手它而不是另建一条

    public ChannelRunner(
        Channel channel,
        ICommandCatalog catalog,
        ICommandProvider builtins,
        IResourceArbiter arbiter,
        Func<DateTimeOffset>? now = null,
        Func<int, string, ResourceNeed?>? resourceOf = null)
    {
        _channel = channel;
        _catalog = catalog;
        _builtins = builtins;
        _arbiter = arbiter;
        _now = now ?? (() => DateTimeOffset.Now);
        _resourceOf = resourceOf ?? ((_, _) => null);
    }

    public Channel Channel => _channel;
    public int Number => _channel.Number;
    public ChannelRunState State { get; private set; } = ChannelRunState.Idle;
    public ChannelRun? Run { get; private set; }
    public string? Operator { get; private set; }
    public double TimeScale { get; set; } = 1;
    public bool Simulated { get; set; } = true;

    public event EventHandler? Changed;
    public event EventHandler<StepRecord>? StepChanged;
    public event EventHandler<EventRecord>? EventLogged;

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 启动。按下的那一刻把配方 + 排期快照下来作为审计基线（§7.2）。
    /// 通道各自启动，谁先谁后由操作人决定。
    /// </summary>
    public ChannelRun Start(Recipe recipe, EstimationContext? seed = null, string? user = null,
                            ChargeTable? charge = null)
    {
        if (!CanStart)
            throw new InvalidOperationException(State switch
            {
                ChannelRunState.Running => $"CH{Number} 已在运行。",
                ChannelRunState.Paused => $"CH{Number} 正暂停着，按「继续」而不是重新启动。",
                ChannelRunState.Aborting => $"CH{Number} 正在停止，等它收完尾再启动。",
                _ => $"CH{Number} 正在准备，稍候再启动。"
            });

        // Error 级问题真的挡启动（CH-5「阻断下发」）。从前校验器只管把配方页的
        // 提示条染红，启动路径看都不看——「错误」和「警告」的差别只剩颜色。
        // 拦在这一层而不是界面上：能启动与否由执行器说了算，界面不另立判据，
        // 谁来调（按钮、整机启动、将来的远程接口）都过同一道闸。
        // 配料表不传库就解：盖过章的行自足，这正是快照语义买来的便宜
        var blocked = RecipeValidator.Validate(recipe, _catalog, _channel,
                          charge: charge is { IsEmpty: false } ? Stoichiometry.Solve(charge) : null)
                      .Where(x => x.Level == IssueLevel.Error).ToList();
        if (blocked.Count > 0) throw new RecipeRejectedException(Number, blocked);

        State = ChannelRunState.Loading;
        Operator = user;

        var frozenRecipe = recipe.Snapshot();
        var frozenSchedule = Schedule.Build(frozenRecipe, _catalog, seed);
        var startedAt = _now();

        var run = new ChannelRun
        {
            Channel = Number,
            StartedAt = startedAt,
            Simulated = Simulated,
            Operator = user,
            State = ChannelRunState.Running,
            Baseline = new RunBaseline
            {
                Recipe = frozenRecipe,
                Schedule = frozenSchedule,
                FrozenAt = startedAt,
                ApprovedBy = user,
                // 冻一份副本。启动之后配料表页上再改，改的是下一炉
                Charge = charge is { IsEmpty: false } ? charge.Clone() : null
            }
        };

        Run = run;
        _live = recipe.Snapshot();
        _skipRequested = false;
        _pauseGate = null;
        _pending = null;
        _cts = new CancellationTokenSource();
        State = ChannelRunState.Running;

        // 启动即成立：执行循环是异步跑的，但记录不能等它被调度上才出现。
        // 操作人看到通道「运行中」的那一刻，第一步就必须已经在记录里可查——
        // 否则此刻读记录的界面与导出会看到一张空表（§7.3）。
        if (FirstExecutable(frozenRecipe, frozenSchedule) is { } first)
        {
            _pending = NewRecord(run, first.Step, first.Entry, TimeSpan.Zero, 1, startedAt);
            run.Append(_pending);
        }

        Log(EventKind.ChannelStarted, $"启动：{frozenRecipe.Name}", user);
        Raise();

        _loop = Task.Run(() => RunLoopAsync(run, _cts.Token));
        return run;
    }

    public Task Completion => _loop ?? Task.CompletedTask;

    public bool IsPaused => _pauseGate is not null;

    /// <summary>
    /// 这一路现在按「启动」会不会真的动起来。
    ///
    /// **界面按钮的可用性和 Start() 的拦截走同一个判据。** 两处各写一套的话，
    /// 迟早出现「按钮亮着，按下去抛异常」——尤其是 Aborting 这种过渡态：
    /// 按了急停但驱动还没收完尾，那几秒里通道既不是 Running 也不能重开，
    /// 只排除 Running 的写法会在这几秒里放行，同一路上跑起两条执行循环。
    ///
    /// 暂停着的不在这里：那一路要的是「继续」（<see cref="CanResume"/>），不是重开一趟。
    /// </summary>
    public bool CanStart => State is ChannelRunState.Idle or ChannelRunState.Ready
                                  or ChannelRunState.Completed or ChannelRunState.Faulted
                                  or ChannelRunState.Aborted;

    /// <summary>暂停着，按下去是继续。</summary>
    public bool CanResume => State == ChannelRunState.Paused;

    /// <summary>
    /// 运行中那一份配方的步骤（热改改的就是它）。基线在 <see cref="ChannelRun.Baseline"/> 里，
    /// 一旦冻结永不变——两份分开摆才看得出「这一趟被改过什么」（§7.2）。
    /// 返回快照：执行循环在另一个线程上读同一张表。
    /// </summary>
    public IReadOnlyList<Step> LiveSteps => _live?.Steps.ToArray() ?? Array.Empty<Step>();

    /// <summary>现在能不能改参数。跑完了就不能改——那趟记录已经收口了。</summary>
    public bool CanEdit => State is ChannelRunState.Running or ChannelRunState.Paused;

    public void Pause(string? user = null)
    {
        if (State != ChannelRunState.Running) return;
        if (_pauseGate is not null) return;
        _pauseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = ChannelRunState.Paused;
        if (Run is not null) Run.State = ChannelRunState.Paused;
        Log(EventKind.Paused, "暂停", user);
        Raise();
    }

    public void Resume(string? user = null)
    {
        if (!Unblock()) return;
        Log(EventKind.Resumed, "继续", user);
        Raise();
    }

    /// <summary>
    /// 放开暂停闸门，**不写记录**。中止时要先放开闸门才收得了尾，
    /// 但那不是操作人按了「继续」——记成一条继续，记录上会出现
    /// 「中止」后面紧跟一条「继续」，读的人会以为有人又把它开起来了。
    /// 返回 false 表示本来就没在暂停。
    /// </summary>
    private bool Unblock()
    {
        var gate = _pauseGate;
        if (gate is null) return false;
        _pauseGate = null;
        State = ChannelRunState.Running;
        if (Run is not null) Run.State = ChannelRunState.Running;
        gate.TrySetResult(true);
        return true;
    }

    public void Abort(string? user = null, string reason = "操作人中止")
    {
        if (State is ChannelRunState.Idle or ChannelRunState.Completed) return;
        State = ChannelRunState.Aborting;
        if (Run is not null) Run.State = ChannelRunState.Aborting;
        Log(EventKind.Aborted, $"中止：{reason}", user);
        Unblock();                     // 放开闸门才收得了尾，但这不是「继续」，不记
        
        _cts?.Cancel();
        Raise();
    }

    public void SkipCurrent(string? user = null, string reason = "操作人跳过")
    {
        if (State != ChannelRunState.Running && State != ChannelRunState.Paused) return;
        _skipRequested = true;
        Log(EventKind.StepSkipped, $"跳过当前步：{reason}", user);
        // 暂停着按跳过，这一路确实从暂停回到运行了——那是操作人要的，照实记
        if (IsPaused) Resume(user);
    }

    /// <summary>
    /// 运行中修改（FR-5.5 / §7.6）：提案 → 校验 → 应用 → 审计。
    /// 基线不变，偏差照常累计。
    /// </summary>
    public HotEditResult ProposeEdit(string stepId, ParameterSet next, string? user, string? reason = null)
    {
        // 跑完 / 被停掉之后 _live 还留着最后那一份，但那趟记录已经收口了。
        // 让它照改会往一条结束了的记录链上再追一条「参数修改」——
        // 读记录的人只能理解成"结束之后又有人动了参数"
        if (!CanEdit)
            return new HotEditResult(false, $"CH{Number} 没在运行，改参数请去「配方」页改下一趟。");
        if (_live is null) return new HotEditResult(false, "通道未在运行。");
        var step = _live.Steps.FirstOrDefault(s => s.StepId == stepId);
        if (step is null) return new HotEditResult(false, "找不到该步骤。");
        if (!_catalog.TryGet(step.CommandId, out var d))
            return new HotEditResult(false, $"缺少驱动：{step.CommandId}");

        var current = Run?.Current;
        var isCurrent = current is not null && current.StepId == stepId;
        if (isCurrent && !d.SupportsHotEdit)
            return new HotEditResult(false, $"{d.DisplayName} 不支持热改，只能改后续步骤或先暂停。");

        foreach (var f in d.Parameters.Fields)
        {
            if (f.Kind is not (FieldKind.Number or FieldKind.Duration)) continue;
            if (!next.Has(f.Key)) continue;
            var v = next.Num(f.Key);
            if (f.Min is { } min && v < min) return new HotEditResult(false, $"{f.Label} 低于下限 {min}。");
            if (f.Max is { } max && v > max) return new HotEditResult(false, $"{f.Label} 高于上限 {max}。");
        }

        var before = Describe(d, step.Parameters);
        var applied = step.Parameters.Clone();
        foreach (var key in next.Keys) applied[key] = next[key];
        // 除了参数，这一步的其它属性一律照搬。手抄字段清单抄漏过一次：
        // 热改之后「失败时暂停并报警」退回默认的 true，操作人关掉的开关自己又开了
        var newStep = step.CopyWith(parameters: applied);
        var idx = _live.Steps.IndexOf(step);
        _live.Steps[idx] = newStep;

        Run?.Append(new EventRecord
        {
            At = _now(),
            Channel = Number,
            Kind = EventKind.ParameterChanged,
            Text = reason is null ? $"参数修改：{d.DisplayName}" : $"参数修改：{d.DisplayName}（{reason}）",
            User = user,
            StepId = newStep.StepId,
            Before = before,
            After = Describe(d, applied)
        });
        Raise();
        return new HotEditResult(true, isCurrent ? "已热改当前步。" : "下一步生效。");
    }

    // ── 执行主循环 ───────────────────────────────────────────────────

    private async Task RunLoopAsync(ChannelRun run, CancellationToken ct)
    {
        var steps = _live!.Steps;
        var schedule = run.Baseline.Schedule;
        var loops = new Stack<RuntimeLoop>();
        var planShift = TimeSpan.Zero;
        var pc = 0;
        var faulted = false;

        try
        {
            while (pc < steps.Count)
            {
                ct.ThrowIfCancellationRequested();
                await WaitWhilePausedAsync(ct).ConfigureAwait(false);

                var step = steps[pc];
                var entry = pc < schedule.Entries.Count ? schedule.Entries[pc] : null;

                if (!step.Enabled) { pc++; continue; }

                if (BuiltinCommands.IsLoopBegin(step.CommandId))
                {
                    var repeats = BuiltinCommands.RepeatsOf(step.Parameters);   // 隐式转 CommandInput
                    var one = entry is null || repeats == 0
                        ? TimeSpan.Zero
                        : TimeSpan.FromTicks(entry.Span.Ticks / Math.Max(1, repeats));
                    loops.Push(new RuntimeLoop(pc, repeats, one));
                    pc++;
                    continue;
                }

                if (BuiltinCommands.IsLoopEnd(step.CommandId))
                {
                    if (loops.Count == 0) { pc++; continue; }
                    var frame = loops.Peek();
                    if (frame.Iteration < frame.Repeats)
                    {
                        frame.Iteration++;
                        frame.Accumulated += frame.OneSpan;
                        planShift += frame.OneSpan;
                        pc = frame.BeginIndex + 1;
                    }
                    else
                    {
                        loops.Pop();
                        planShift -= frame.Accumulated;
                        pc++;
                    }
                    continue;
                }

                var iteration = loops.Count > 0 ? loops.Peek().Iteration : 1;
                var done = await ExecuteStepAsync(run, step, entry, planShift, iteration, ct)
                                 .ConfigureAwait(false);
                pc++;

                // 这一步没做成，接下来的步骤多半是建立在它做成了的前提上——
                // 「升温失败了照样往下加料」是实打实的事故。默认停下来等人看一眼；
                // 明确勾掉的（比如一条可有可无的采集）才继续跑
                if (done.Status == StepStatus.Failed && step.PauseOnFault)
                {
                    Log(EventKind.Alarm,
                        $"第 {pc} 步「{done.Title}」失败，已暂停：{done.Note ?? "未说明原因"}",
                        null);
                    Pause();
                }
            }
        }
        catch (OperationCanceledException)
        {
            MarkRunningAborted(run);
        }
        catch (Exception ex)
        {
            faulted = true;
            MarkRunningFailed(run, ex);
            Log(EventKind.DeviceFault, $"执行异常：{ex.Message}", null);
        }
        finally
        {
            run.FinishedAt = _now();
            // 收尾完了这一趟的结局是 Aborted，不是过渡态 Aborting——
            // 记录上留 Aborting 的话，一个早已停下的通道会一直显示成「正在停止」
            run.State = faulted ? ChannelRunState.Faulted
                : _cts is { IsCancellationRequested: true } ? ChannelRunState.Aborted
                : ChannelRunState.Completed;

            // 非正常结束才把设备收到安全态。中止只取消了配方的执行循环，
            // 机器不会因此停下来——温控器还守着最后那个设定值，泵还在打。
            // 正常跑完不动它：收尾状态是配方作者定的（降温结晶跑完就该保持在 5 ℃）
            if (run.State != ChannelRunState.Completed) await SafeStopAsync(run).ConfigureAwait(false);

            // 中止过的通道回到 Idle，好让操作人再起一趟（记录里是新的一条，不覆盖旧的）
            State = run.State == ChannelRunState.Aborted ? ChannelRunState.Idle : run.State;
            Log(EventKind.ChannelFinished,
                $"结束：{RunStateWords.Of(run.State)}，用时 {Fmt.Hms(run.Elapsed(_now()))}", Operator);
            Raise();
        }
    }

    /// <summary>停机收尾能等多久。真机不响应时不能把收尾卡死在这儿。</summary>
    private static readonly TimeSpan SafeStopBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 挨个问这一路上的设备：把你在这一路的输出收到安全态。
    /// **该关什么归驱动说**，Core 不认识具体设备（§核心架构 §1）。
    /// 做了什么逐条进记录——GLP 要答得出「停的那一刻机器被动了什么」。
    ///
    /// 这里**不用** _cts：它刚刚才被 Cancel，拿它去停设备等于一条指令都发不出去。
    /// 单开一个带预算的令牌，某台设备不响应也不能把别的几台拖住。
    /// </summary>
    private async Task SafeStopAsync(ChannelRun run)
    {
        using var cts = new CancellationTokenSource(SafeStopBudget);
        foreach (var a in _channel.Attachments)
        {
            IReadOnlyList<string>? did;
            try
            {
                did = await a.Session.SafeStopAsync(a.Well, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 停不下来是最该留痕的一种情况：机器还开着，而且没人知道
                Log(EventKind.SafeStop, $"{a.Session.InstanceId} 停机失败：{ex.Message}", null);
                continue;
            }

            if (did is null)
            {
                // 驱动没实现。不假装停干净了——现场得知道这台还开着
                Log(EventKind.SafeStop, $"{a.Session.InstanceId} 未声明停机动作，输出保持原样", null);
                continue;
            }
            foreach (var line in did)
                Log(EventKind.SafeStop, $"{a.Session.InstanceId} {line}", null);
        }
    }

    /// <summary>
    /// 循环真正会执行的第一步（跳过停用步与循环标记）。循环标记不产生记录行，
    /// 所以配方以「循环开始」打头时，第一步是它后面那一条。
    /// </summary>
    private static (Step Step, ScheduleEntry? Entry)? FirstExecutable(Recipe recipe, Schedule schedule)
    {
        for (var i = 0; i < recipe.Steps.Count; i++)
        {
            var s = recipe.Steps[i];
            if (!s.Enabled) continue;
            if (BuiltinCommands.IsLoopBegin(s.CommandId) || BuiltinCommands.IsLoopEnd(s.CommandId)) continue;
            return (s, i < schedule.Entries.Count ? schedule.Entries[i] : null);
        }
        return null;
    }

    /// <summary>
    /// 建记录行。执行开始就建，不是等执行完才写：这样"正在跑什么"和"跑完了什么"
    /// 是同一张表的不同状态，界面不需要维护第二份状态（§7.3）。
    /// </summary>
    private StepRecord NewRecord(ChannelRun run, Step step, ScheduleEntry? entry,
                                 TimeSpan planShift, int iteration, DateTimeOffset at)
    {
        var known = _catalog.TryGet(step.CommandId, out var d);
        return new StepRecord
        {
            Index = entry?.Index ?? run.Steps.Count,
            StepId = step.StepId,
            CommandId = step.CommandId,
            Title = known ? Describe(d, new CommandInput(step.Parameters, step.Rows))
                          : $"缺少驱动：{step.CommandId}",
            Termination = known ? d.Termination : TerminationKind.Immediate,
            Iteration = iteration,
            PlanStart = (entry?.Start ?? TimeSpan.Zero) + planShift,
            PlanDuration = entry?.Duration ?? TimeSpan.Zero,
            ChannelStart = run.StartedAt,
            ActualStart = at,
            Status = StepStatus.Running,
            // 控温对象与工艺阶段跟着这一步一起冻下来。事后翻记录时，
            // 「这一段按 Tr 还是按 Tj 控」决定了同一条 Tr−Tj 曲线该怎么读；
            // 之后再改配方也不影响已经记下来的这一笔
            ControlMode = Mode(step),
            Phase = string.IsNullOrWhiteSpace(step.Phase) ? null : step.Phase
        };
    }

    /// <summary>
    /// 这一步的控温对象。只有温度模块那几条指令有；别的指令返回 null——
    /// 给「加料」标一个「釜内 Tr」是无中生有。
    /// </summary>
    private static string? Mode(Step step)
    {
        var obj = step.Parameters.Str("obj", "");
        return obj.Length == 0 ? null : obj;
    }

    /// <summary>跑一步，把这一步的记录还回去——主循环要按它的结果决定停不停。</summary>
    private async Task<StepRecord> ExecuteStepAsync(ChannelRun run, Step step, ScheduleEntry? entry,
                                                    TimeSpan planShift, int iteration, CancellationToken ct)
    {
        var known = _catalog.TryGet(step.CommandId, out var d);
        var input = new CommandInput(step.Parameters, step.Rows);

        // Start() 已经把第一步建好了就接手它，别再建第二条
        StepRecord rec;
        if (_pending is { } pre && pre.StepId == step.StepId && pre.Iteration == iteration)
        {
            _pending = null;
            rec = pre;
            rec.ActualStart = _now();
        }
        else
        {
            rec = NewRecord(run, step, entry, planShift, iteration, _now());
            run.Append(rec);
        }
        StepChanged?.Invoke(this, rec);
        Raise();

        if (_skipRequested)
        {
            _skipRequested = false;
            Finish(rec, EndReason.Skipped, StepStatus.Skipped);
            return rec;
        }

        if (!known)
        {
            Finish(rec, EndReason.Failed, StepStatus.Failed, "缺少驱动，已跳过");
            return rec;
        }

        // 2. 校验：参数是否仍在设备 Limits 内（设备可能被换过）
        var handler = _builtins.Resolve(step.CommandId) ?? _channel.ResolveHandler(step.CommandId);
        if (handler is null)
        {
            Finish(rec, EndReason.Failed, StepStatus.Failed, "该通道没有能执行此指令的设备");
            return rec;
        }

        // 3. 申请资源
        IResourceLease? lease = null;
        var need = _resourceOf(Number, step.CommandId);
        if (need is not null)
        {
            // 等待时长按仿真时钟量：租约里的 Waited 走的是真实墙钟，
            // 在时标加速下（仿真 400×）算出来永远接近 0，写进记录没有意义。
            var waitBegan = _now();
            lease = await _arbiter.AcquireAsync(need.ResourceId, Number, need.Priority, need.Policy, ct)
                                  .ConfigureAwait(false);
            if (lease is null)
            {
                Finish(rec, EndReason.Failed, StepStatus.Failed, $"资源被占用：{need.ResourceId}");
                return rec;
            }

            var waited = _now() - waitBegan;
            if (waited > TimeSpan.Zero)
            {
                // 排队的时候这一步并没有在执行。实际开始按拿到资源的时刻记，
                // 否则记录里这一步的区间会把等待也算进去，看上去两个通道
                // 同时占着同一台泵——共享设备的排队就查不出来了（§7.4）。
                rec.ActualStart = _now();
                StepChanged?.Invoke(this, rec);
                Raise();
            }
            if (waited > TimeSpan.FromSeconds(1))
                Log(EventKind.ResourceWait,
                    $"等待 {need.ResourceId} {Fmt.Hms(waited)}", Operator, rec.StepId);
        }

        try
        {
            var ctx = new CommandContext
            {
                Channel = Number,
                Capabilities = _channel.Capabilities,
                Now = _now,
                TimeScale = TimeScale,
                Note = text => Log(EventKind.Note, text, Operator, rec.StepId),
                Progress = null
            };

            var began = _now();
            var outcome = await handler.ExecuteAsync(ctx, input, ct).ConfigureAwait(false);
            rec.ActualDuration = outcome.Actual > TimeSpan.Zero ? outcome.Actual : _now() - began;
            Finish(rec, outcome.Reason, StatusOf(outcome.Reason), outcome.Note);
        }
        catch (OperationCanceledException)
        {
            Finish(rec, EndReason.Aborted, StepStatus.Aborted);
            throw;
        }
        catch (Exception ex)
        {
            Finish(rec, EndReason.Failed, StepStatus.Failed, ex.Message);
            throw;
        }
        finally
        {
            lease?.Dispose();
        }
        return rec;
    }

    private static StepStatus StatusOf(EndReason r) => r switch
    {
        EndReason.Aborted => StepStatus.Aborted,
        EndReason.Skipped => StepStatus.Skipped,
        EndReason.Failed => StepStatus.Failed,
        EndReason.Alarm => StepStatus.Aborted,
        _ => StepStatus.Done
    };

    private void Finish(StepRecord rec, EndReason reason, StepStatus status, string? note = null)
    {
        rec.ActualEnd = _now();
        rec.ActualDuration ??= rec.ActualEnd - rec.ActualStart;
        rec.Reason = reason;
        rec.Status = status;
        if (note is not null) rec.Note = note;
        StepChanged?.Invoke(this, rec);
        Raise();
    }

    private void MarkRunningAborted(ChannelRun run)
    {
        foreach (var s in run.Steps)
            if (s.Status == StepStatus.Running)
            {
                s.Status = StepStatus.Aborted;
                s.Reason = EndReason.Aborted;
                s.ActualEnd = _now();
                s.ActualDuration ??= s.ActualEnd - s.ActualStart;
            }
    }

    private void MarkRunningFailed(ChannelRun run, Exception ex)
    {
        foreach (var s in run.Steps)
            if (s.Status == StepStatus.Running)
            {
                s.Status = StepStatus.Failed;
                s.Reason = EndReason.Failed;
                s.Note = ex.Message;
                s.ActualEnd = _now();
                s.ActualDuration ??= s.ActualEnd - s.ActualStart;
            }
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        var gate = _pauseGate;
        if (gate is null) return;
        await gate.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private void Log(EventKind kind, string text, string? user, string? stepId = null)
    {
        var e = new EventRecord
        {
            At = _now(),
            Channel = Number,
            Kind = kind,
            Text = text,
            User = user,
            StepId = stepId
        };
        Run?.Append(e);
        EventLogged?.Invoke(this, e);
    }

    private static string Describe(CommandDescriptor d, CommandInput p)
    {
        try { return d.Describe(p); }
        catch { return d.DisplayName; }
    }

    private sealed class RuntimeLoop
    {
        public RuntimeLoop(int beginIndex, int repeats, TimeSpan oneSpan)
        {
            BeginIndex = beginIndex;
            Repeats = Math.Max(1, repeats);
            OneSpan = oneSpan;
        }
        public int BeginIndex { get; }
        public int Repeats { get; }
        public TimeSpan OneSpan { get; }
        public int Iteration { get; set; } = 1;
        public TimeSpan Accumulated { get; set; }
    }
}
