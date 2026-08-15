using Tec.Core.Benches;
using Tec.Core.Catalog;
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
    public ChannelRun Start(Recipe recipe, EstimationContext? seed = null, string? user = null)
    {
        if (State is ChannelRunState.Running or ChannelRunState.Paused)
            throw new InvalidOperationException($"CH{Number} 已在运行。");

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
                ApprovedBy = user
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

        Log(EventKind.ChannelStarted, $"CH{Number} 启动：{frozenRecipe.Name}", user);
        Raise();

        _loop = Task.Run(() => RunLoopAsync(run, _cts.Token));
        return run;
    }

    public Task Completion => _loop ?? Task.CompletedTask;

    public bool IsPaused => _pauseGate is not null;

    public void Pause(string? user = null)
    {
        if (State != ChannelRunState.Running) return;
        if (_pauseGate is not null) return;
        _pauseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        State = ChannelRunState.Paused;
        if (Run is not null) Run.State = ChannelRunState.Paused;
        Log(EventKind.Paused, $"CH{Number} 暂停", user);
        Raise();
    }

    public void Resume(string? user = null)
    {
        var gate = _pauseGate;
        if (gate is null) return;
        _pauseGate = null;
        State = ChannelRunState.Running;
        if (Run is not null) Run.State = ChannelRunState.Running;
        gate.TrySetResult(true);
        Log(EventKind.Resumed, $"CH{Number} 继续", user);
        Raise();
    }

    public void Abort(string? user = null, string reason = "操作人中止")
    {
        if (State is ChannelRunState.Idle or ChannelRunState.Completed) return;
        State = ChannelRunState.Aborting;
        if (Run is not null) Run.State = ChannelRunState.Aborting;
        Log(EventKind.Note, $"CH{Number} 中止：{reason}", user);
        if (IsPaused) Resume(user);
        _cts?.Cancel();
        Raise();
    }

    public void SkipCurrent(string? user = null, string reason = "操作人跳过")
    {
        if (State != ChannelRunState.Running && State != ChannelRunState.Paused) return;
        _skipRequested = true;
        Log(EventKind.StepSkipped, $"跳过当前步：{reason}", user);
        if (IsPaused) Resume(user);
    }

    /// <summary>
    /// 运行中修改（FR-5.5 / §7.6）：提案 → 校验 → 应用 → 审计。
    /// 基线不变，偏差照常累计。
    /// </summary>
    public HotEditResult ProposeEdit(string stepId, ParameterSet next, string? user, string? reason = null)
    {
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
        var newStep = new Step
        {
            StepId = step.StepId,
            CommandId = step.CommandId,
            Parameters = applied,
            Rows = step.Rows,
            Enabled = step.Enabled,
            Comment = step.Comment
        };
        var idx = _live.Steps.IndexOf(step);
        _live.Steps[idx] = newStep;

        Run?.Append(new EventRecord
        {
            At = _now(),
            Channel = Number,
            Kind = EventKind.ParameterChanged,
            Text = reason is null ? $"参数修改：{d.DisplayName}" : $"参数修改：{d.DisplayName}（{reason}）",
            User = user,
            StepIndex = idx,
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
                await ExecuteStepAsync(run, step, entry, planShift, iteration, ct).ConfigureAwait(false);
                pc++;
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
            Log(EventKind.DeviceFault, $"CH{Number} 执行异常：{ex.Message}", null);
        }
        finally
        {
            run.FinishedAt = _now();
            run.State = faulted ? ChannelRunState.Faulted
                : _cts is { IsCancellationRequested: true } ? ChannelRunState.Aborting
                : ChannelRunState.Completed;
            State = run.State == ChannelRunState.Aborting ? ChannelRunState.Idle : run.State;
            Log(EventKind.ChannelFinished,
                $"CH{Number} 结束：{run.State}，用时 {Fmt.Hms(run.Elapsed(_now()))}", Operator);
            Raise();
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
            Status = StepStatus.Running
        };
    }

    private async Task ExecuteStepAsync(ChannelRun run, Step step, ScheduleEntry? entry,
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
            return;
        }

        if (!known)
        {
            Finish(rec, EndReason.Failed, StepStatus.Failed, "缺少驱动，已跳过");
            return;
        }

        // 2. 校验：参数是否仍在设备 Limits 内（设备可能被换过）
        var handler = _builtins.Resolve(step.CommandId) ?? _channel.ResolveHandler(step.CommandId);
        if (handler is null)
        {
            Finish(rec, EndReason.Failed, StepStatus.Failed, "该通道没有能执行此指令的设备");
            return;
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
                return;
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
                    $"等待 {need.ResourceId} {Fmt.Hms(waited)}", Operator, rec.Index);
        }

        try
        {
            var ctx = new CommandContext
            {
                Channel = Number,
                Capabilities = _channel.Capabilities,
                Now = _now,
                TimeScale = TimeScale,
                Note = text => Log(EventKind.Note, text, Operator, rec.Index),
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

    private void Log(EventKind kind, string text, string? user, int? stepIndex = null)
    {
        var e = new EventRecord
        {
            At = _now(),
            Channel = Number,
            Kind = kind,
            Text = text,
            User = user,
            StepIndex = stepIndex
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
