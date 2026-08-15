using Tec.Driver.Abi;

namespace Tec.Core.Catalog;

/// <summary>
/// 流程指令由 Core 提供，不属于任何驱动——它们不需要任何能力就能执行。
/// 循环用配对标记而不是嵌套树：排期时用栈处理，甘特上循环体按第一轮展开，
/// 循环开始行画一条覆盖全部轮次的跨度条（§6 第 2 条）。
/// </summary>
public static class BuiltinCommands
{
    public const string Wait = "tec.flow.wait";
    public const string Prompt = "tec.flow.prompt";
    public const string LoopBegin = "tec.flow.loopBegin";
    public const string LoopEnd = "tec.flow.loopEnd";
    public const string Mark = "tec.flow.mark";

    public const string Module = "流程";

    public static bool IsLoopBegin(string id) => id == LoopBegin;
    public static bool IsLoopEnd(string id) => id == LoopEnd;

    private static readonly CommandDescriptor WaitCmd = new(
        Wait, "等待", Module, null,
        new ParameterSchema(new[]
        {
            new FieldSpec("时长", "时长", FieldKind.Duration) { Default = 300d, Unit = "s", Min = 0, Max = 86400 }
        }),
        TerminationKind.Timer,
        (p, _) => TimeSpan.FromSeconds(Math.Max(0, p.Num("时长"))),
        p => $"等待 {Fmt.Hms(TimeSpan.FromSeconds(p.Num("时长")))}")
    { IconKey = "wait", SupportsHotEdit = true };

    private static readonly CommandDescriptor PromptCmd = new(
        Prompt, "提示操作人", Module, null,
        new ParameterSchema(new[]
        {
            new FieldSpec("提示", "提示内容", FieldKind.Text) { Default = "请确认后继续" },
            new FieldSpec("超时", "无人确认超时", FieldKind.Duration) { Default = 0d, Unit = "s", Min = 0, Max = 86400,
                Tip = "0 = 一直等" }
        }),
        TerminationKind.Operator,
        // 估算：等人的时间无法预测，按 0 计入排期，实际偏差进"开始偏差"里
        (_, _) => TimeSpan.Zero,
        p => $"提示操作人：{p.Str("提示")}")
    { IconKey = "prompt" };

    private static readonly CommandDescriptor LoopBeginCmd = new(
        LoopBegin, "循环开始", Module, null,
        new ParameterSchema(new[]
        {
            new FieldSpec("方式", "循环方式", FieldKind.Choice) { Default = "按次数", Choices = new[] { "按次数", "按条件" } },
            new FieldSpec("次数", "次数", FieldKind.Number) { Default = 3d, Min = 1, Max = 999, Step = 1, Decimals = 0,
                VisibleWhen = "方式=按次数" }
        }),
        TerminationKind.Immediate,
        (_, _) => TimeSpan.Zero,
        p => p.Str("方式") == "按次数" ? $"循环开始 × {p.Int("次数", 1)}" : "循环开始（按条件）")
    { IconKey = "loop" };

    private static readonly CommandDescriptor LoopEndCmd = new(
        LoopEnd, "循环结束", Module, null,
        ParameterSchema.Empty,
        TerminationKind.Immediate,
        (_, _) => TimeSpan.Zero,
        _ => "循环结束")
    { IconKey = "loop" };

    private static readonly CommandDescriptor MarkCmd = new(
        Mark, "标记事件", Module, null,
        new ParameterSchema(new[]
        {
            new FieldSpec("标记", "标记文字", FieldKind.Text) { Default = "取样" }
        }),
        TerminationKind.Immediate,
        (_, _) => TimeSpan.Zero,
        p => $"标记：{p.Str("标记")}")
    { IconKey = "mark" };

    public static IReadOnlyList<CommandDescriptor> All { get; } = new[]
    {
        WaitCmd, PromptCmd, LoopBeginCmd, LoopEndCmd, MarkCmd
    };

    /// <summary>循环开始声明的轮次。按条件循环在排期里按 1 轮估（无法预知）。</summary>
    public static int RepeatsOf(CommandInput p)
        => p.Str("方式", "按次数") == "按次数" ? Math.Max(1, p.Int("次数", 1)) : 1;
}

/// <summary>操作人确认的入口。Core 只定义契约，界面负责弹框（§7.6 同理）。</summary>
public interface IOperatorGate
{
    Task<bool> ConfirmAsync(int channel, string message, TimeSpan timeout, CancellationToken ct);
}

/// <summary>没有界面时（测试、无人值守）默认立刻通过，并在记录里留痕。</summary>
public sealed class AutoOperatorGate : IOperatorGate
{
    public Task<bool> ConfirmAsync(int channel, string message, TimeSpan timeout, CancellationToken ct)
        => Task.FromResult(true);
}

public sealed class BuiltinCommandProvider : ICommandProvider
{
    private readonly IOperatorGate _gate;
    private readonly Action<int, string>? _onMark;

    public BuiltinCommandProvider(IOperatorGate gate, Action<int, string>? onMark = null)
    {
        _gate = gate;
        _onMark = onMark;
    }

    public IReadOnlyList<CommandDescriptor> Commands => BuiltinCommands.All;

    public ICommandHandler? Resolve(string commandId) => commandId switch
    {
        BuiltinCommands.Wait => new WaitHandler(),
        BuiltinCommands.Prompt => new PromptHandler(_gate),
        BuiltinCommands.Mark => new MarkHandler(_onMark),
        // 循环标记由执行引擎处理，没有 handler
        _ => null
    };

    private sealed class WaitHandler : ICommandHandler
    {
        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var plan = TimeSpan.FromSeconds(Math.Max(0, p.Num("时长")));
            var scaled = ctx.TimeScale > 0 ? TimeSpan.FromSeconds(plan.TotalSeconds / ctx.TimeScale) : plan;
            var began = ctx.Now();
            await Task.Delay(scaled, ct).ConfigureAwait(false);
            return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
        }
    }

    private sealed class PromptHandler : ICommandHandler
    {
        private readonly IOperatorGate _gate;
        public PromptHandler(IOperatorGate gate) => _gate = gate;

        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var began = ctx.Now();
            var timeout = TimeSpan.FromSeconds(Math.Max(0, p.Num("超时")));
            var ok = await _gate.ConfirmAsync(ctx.Channel, p.Str("提示"), timeout, ct).ConfigureAwait(false);
            return new CommandOutcome(ok ? EndReason.OperatorConfirmed : EndReason.Timeout, ctx.Now() - began);
        }
    }

    private sealed class MarkHandler : ICommandHandler
    {
        private readonly Action<int, string>? _onMark;
        public MarkHandler(Action<int, string>? onMark) => _onMark = onMark;

        public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var text = p.Str("标记");
            _onMark?.Invoke(ctx.Channel, text);
            ctx.Note?.Invoke(text);
            return Task.FromResult(CommandOutcome.Instant());
        }
    }
}
