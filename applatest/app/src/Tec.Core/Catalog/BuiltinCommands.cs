using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.Core.Catalog;

/// <summary>
/// 通用（流程）指令，对应原型 CMDS.misc 的 8 条。由 Core 提供——它们不需要任何设备能力。
///
/// 这一组与原型逐字对应：指令名、参数键、默认值、单位、步进、卡片摘要 sum、
/// 整句描述 DESC、时长估算 SECS 都不改。改了就对不上原型了。
///
/// 循环用配对标记而不是嵌套树：排期时用栈处理，甘特上循环体按第一轮展开，
/// 循环开始行画一条覆盖全部轮次的跨度条（§6 第 2 条）。
/// </summary>
public static class BuiltinCommands
{
    // Id 用 ASCII，便于存进配方文件；DisplayName 才是原型里的中文名
    public const string Wait = "tec.flow.wait";            // 等待
    public const string WaitUntil = "tec.flow.waitUntil";  // 条件等待
    public const string LoopBegin = "tec.flow.loopBegin";  // 循环开始
    public const string LoopEnd = "tec.flow.loopEnd";      // 循环结束
    public const string SetVar = "tec.flow.setVar";        // 设定变量
    public const string Message = "tec.flow.message";      // 消息提示
    public const string Mark = "tec.flow.mark";            // 标记事件
    public const string Sampling = "tec.flow.sampling";    // 采样提醒
    public const string Interlock = "tec.flow.interlock";  // 安全联锁
    public const string Finish = "tec.flow.finish";        // 结束实验

    /// <summary>原型 GRP.misc.label。</summary>
    public const string Module = "通用";

    /// <summary>自然冷却按 0.5 ℃/min 估算（原型 PASSIVE）。</summary>
    public const double Passive = 0.5;

    public static bool IsLoopBegin(string id) => id == LoopBegin;
    public static bool IsLoopEnd(string id) => id == LoopEnd;

    private static readonly string[] InterlockSources = { "釜内 Tr", "夹套 Tj", "pH", "浊度" };
    private static readonly string[] InterlockOps = { ">", "<" };
    private static readonly string[] InterlockActions = { "停止实验", "暂停实验", "停止加料", "仅报警" };
    private static readonly string[] TimeoutActions = { "暂停并报警", "继续执行", "按失败处理" };

    /// <summary>「设定变量」的取值来源。「数值」以外都是实时量，键与 Cond.SensorKeys 对应。</summary>
    public static readonly string[] SetVarSources = { "数值", "当前 Tr", "当前 Tj", "当前 pH", "当前 浊度", "当前 rpm" };

    /// <summary>来源选项 → 实时量的键。「数值」返回 null。</summary>
    public static string? SensorOfSource(string src)
        => src.StartsWith("当前", StringComparison.Ordinal) ? src[2..].Trim() : null;

    /// <summary>FieldSpec.ChoicesFrom 的约定值：下拉项 = 当前配方的变量名。</summary>
    public const string ChoicesFromRecipeVars = "recipe.vars";

    public static IReadOnlyList<CommandDescriptor> All { get; } = Attach(new[]
    {
        new CommandDescriptor(Wait, "等待", Module, null,
            new ParameterSchema(new[] { Field.Num("dur", "等待时长", 10, "min", 0.1, null, 0.1) }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(Math.Max(0, p.Num("dur") * 60)),
            p => $"等待 {Txt.Fx(p.Num("dur"))} min")
        { IconKey = "wait", SupportsHotEdit = true },

        new CommandDescriptor(WaitUntil, "条件等待", Module, null,
            new ParameterSchema(new[]
            {
                Field.Text("cond", "等待条件", "浊度 > 50", Cond.Help),
                Field.Num("timeout", "最长等待", 60, "min", 0.1, null, 0.1),
                Field.Sel("onTimeout", "超时后", TimeoutActions, "暂停并报警")
            }),
            TerminationKind.Condition,
            // 条件什么时候满足无法预知，与「消息提示」同一个处理：按 0 计入排期，
            // 实际等的时间全部落进「开始偏差」
            (_, _) => TimeSpan.Zero,
            p => $"等待直到 {p.Str("cond")}（最长 {Txt.Fx(p.Num("timeout"))} min）")
        { IconKey = "waituntil" },

        new CommandDescriptor(LoopBegin, "循环开始", Module, null,
            new ParameterSchema(new[]
            {
                Field.Sel("by", "循环方式", new[] { "按次数", "按条件" }, "按次数"),
                Field.Num("n", "循环次数", 3, "次", 1, null, 1) with { VisibleWhen = "by=按次数" },
                Field.Text("cond", "结束条件", "浊度 > 50", Cond.Help) with { VisibleWhen = "by=按条件" },
                // 到不了的条件不能让通道永远转圈——上限是护栏，不是目标
                Field.Num("max", "最多轮次", 100, "次", 1, 10000, 1) with { VisibleWhen = "by=按条件" }
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => p.Str("by") == "按次数"
                ? $"循环开始，共 {Txt.Fx(p.Num("n"))} 次"
                : $"循环开始，直到 {p.Str("cond")}")
        // 开始和结束各有各的图（cmd-loop-begin / cmd-loop-end），
        // 原先两条都写 "loop"，指向一张并不存在的 cmd-loop.svg，界面上两格是空的
        { IconKey = "loop-begin" },

        new CommandDescriptor(LoopEnd, "循环结束", Module, null,
            ParameterSchema.Empty, TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero, _ => "循环结束")
        { IconKey = "loop-end" },

        new CommandDescriptor(SetVar, "设定变量", Module, null,
            new ParameterSchema(new[]
            {
                // 下拉的选项是**这条配方**的变量表，由界面在建表单时装进来
                // （ChoicesFrom）——指令声明是静态的，不知道配方里有什么变量
                new FieldSpec("var", "变量", FieldKind.Choice) { ChoicesFrom = ChoicesFromRecipeVars },
                Field.Sel("op", "操作", new[] { "设为", "加上", "减去" }, "设为"),
                Field.Sel("src", "取值", SetVarSources, "数值"),
                Field.Num("val", "数值", 0, "", null, null, 0.01) with { VisibleWhen = "src=数值" }
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p =>
            {
                var v = p.Str("var").Length > 0 ? p.Str("var") : "（没选变量）";
                var what = p.Str("src", "数值") == "数值" ? Txt.Fx(p.Num("val")) : p.Str("src");
                return $"变量 {v} {p.Str("op", "设为")} {what}";
            })
        { IconKey = "setvar" },

        new CommandDescriptor(Message, "消息提示", Module, null,
            new ParameterSchema(new[]
            {
                Field.Text("msg", "提示内容", "请确认取样完成"),
                Field.Bool("pause", "暂停等待确认", true)
            }),
            TerminationKind.Operator,
            // 等人的时间无法预知，按 0 计入排期，实际全部落进"开始偏差"
            (_, _) => TimeSpan.Zero,
            p => $"提示「{p.Str("msg")}」{(p.Flag("pause") ? "并暂停等待确认" : "")}")
        { IconKey = "prompt", SupportsHotEdit = true },

        new CommandDescriptor(Mark, "标记事件", Module, null,
            new ParameterSchema(new[] { Field.Text("tag", "事件标签", "成核点") }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"标记事件「{p.Str("tag")}」")
        { IconKey = "mark", SupportsHotEdit = true },

        new CommandDescriptor(Sampling, "采样提醒", Module, null,
            new ParameterSchema(new[]
            {
                Field.Text("label", "取样标签", "中控样"),
                Field.Num("vol", "取样量", 1, "mL", 0.1, null, 0.1),
                Field.Bool("pause", "暂停等待取样", true)
            }),
            TerminationKind.Operator,
            (_, _) => TimeSpan.Zero,
            p => $"提醒取样 {Txt.Fx(p.Num("vol"))} mL（{p.Str("label")}）")
        { IconKey = "sample", SupportsHotEdit = true },

        new CommandDescriptor(Interlock, "安全联锁", Module, null,
            new ParameterSchema(new[]
            {
                Field.Sel("src", "监测量", InterlockSources, "釜内 Tr"),
                Field.Sel("op", "条件", InterlockOps, ">"),
                Field.Num("val", "阈值", 100, "", null, null, 0.1),
                Field.Sel("act", "触发动作", InterlockActions, "停止实验")
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"当 {p.Str("src")} {p.Str("op")} {Txt.Fx(p.Num("val"))} 时{p.Str("act")}")
        { IconKey = "interlock",
          Tip = "这一条是工艺逻辑，不是安全功能。真正的安全层独立于配方、优先于一切（§7.5）。" },

        new CommandDescriptor(Finish, "结束实验", Module, null,
            new ParameterSchema(new[]
            {
                Field.Bool("cool", "结束前降温至安全温度", true),
                Field.Num("safe", "安全温度", 30, "℃", null, null, 1),
                Field.Bool("stir", "同时停止搅拌", true)
            }),
            TerminationKind.Setpoint,
            (p, ctx) =>
            {
                if (!p.Flag("cool") || ctx.Temperature <= p.Num("safe")) return TimeSpan.Zero;
                var secs = (ctx.Temperature - p.Num("safe")) / Passive * 60;
                ctx.Temperature = p.Num("safe");
                return TimeSpan.FromSeconds(secs);
            },
            p => $"结束实验{(p.Flag("cool") ? $"，先降温至 {Txt.Fx(p.Num("safe"))} ℃" : "")}"
                 + $"{(p.Flag("stir") ? "，并停止搅拌" : "")}")
        { IconKey = "finish" }
    });

    /// <summary>把 Summary 挂到每条指令上，省得界面再查一次表。</summary>
    private static IReadOnlyList<CommandDescriptor> Attach(CommandDescriptor[] list)
    {
        for (var i = 0; i < list.Length; i++)
        {
            var id = list[i].Id;
            list[i] = list[i] with { Summarize = p => Summary(id, p) };
        }
        return list;
    }

    /// <summary>步骤卡上的一行摘要，对应原型 PSPEC[name].sum。</summary>
    public static string Summary(string commandId, CommandInput p) => commandId switch
    {
        Wait => $"等待 {Txt.Fx(p.Num("dur"))} min",
        WaitUntil => $"等待直到 {p.Str("cond")}",
        LoopBegin => p.Str("by") == "按次数" ? $"循环 ×{Txt.Fx(p.Num("n"))}" : $"循环直到 {p.Str("cond")}",
        LoopEnd => "",
        SetVar => $"{(p.Str("var").Length > 0 ? p.Str("var") : "变量")} {p.Str("op", "设为")} "
                  + (p.Str("src", "数值") == "数值" ? Txt.Fx(p.Num("val")) : p.Str("src")),
        Message => p.Str("msg").Length > 0 ? p.Str("msg") : "消息提示",
        Mark => $"标记「{p.Str("tag")}」",
        Sampling => $"取样 {Txt.Fx(p.Num("vol"))} mL",
        Interlock => $"{p.Str("src")} {p.Str("op")} {Txt.Fx(p.Num("val"))} → {p.Str("act")}",
        Finish => p.Flag("cool") ? $"降温至 {Txt.Fx(p.Num("safe"))} ℃ 后结束" : "直接结束",
        _ => ""
    };

    /// <summary>循环开始声明的轮次。按条件循环在排期里按 1 轮估（无法预知）。</summary>
    public static int RepeatsOf(CommandInput p)
        => p.Str("by", "按次数") == "按次数" ? Math.Max(1, p.Int("n", 1)) : 1;
}

/// <summary>操作人确认的入口。Core 只定义契约，界面负责弹框。</summary>
public interface IOperatorGate
{
    Task<bool> ConfirmAsync(int channel, string message, TimeSpan timeout, CancellationToken ct);
}

/// <summary>没有界面时（测试、无人值守）默认立刻通过。</summary>
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
        BuiltinCommands.Message => new PromptHandler(_gate, "msg"),
        BuiltinCommands.Sampling => new PromptHandler(_gate, "label"),
        BuiltinCommands.Mark => new MarkHandler(_onMark, "tag"),
        BuiltinCommands.Interlock => new NoteHandler(p => $"安全联锁生效：{BuiltinCommands.Summary(BuiltinCommands.Interlock, p)}"),
        BuiltinCommands.Finish => new FinishHandler(),
        // 循环标记由执行引擎处理，没有 handler
        _ => null
    };

    private sealed class WaitHandler : ICommandHandler
    {
        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var began = ctx.Now();
            var plan = TimeSpan.FromSeconds(Math.Max(0, p.Num("dur") * 60));
            var scale = ctx.TimeScale > 0 ? ctx.TimeScale : 1;
            await Task.Delay(TimeSpan.FromTicks((long)(plan.Ticks / scale)), ct).ConfigureAwait(false);
            return new CommandOutcome(EndReason.TimerElapsed, ctx.Now() - began);
        }
    }

    /// <summary>消息提示 / 采样提醒：勾了"暂停等待"才真的等人。</summary>
    private sealed class PromptHandler : ICommandHandler
    {
        private readonly IOperatorGate _gate;
        private readonly string _textKey;

        public PromptHandler(IOperatorGate gate, string textKey)
        {
            _gate = gate;
            _textKey = textKey;
        }

        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var began = ctx.Now();
            var text = p.Str(_textKey);
            ctx.Note?.Invoke(text);
            if (!p.Flag("pause", true))
                return new CommandOutcome(EndReason.Completed, TimeSpan.Zero);

            var ok = await _gate.ConfirmAsync(ctx.Channel, text, TimeSpan.Zero, ct).ConfigureAwait(false);
            return new CommandOutcome(ok ? EndReason.OperatorConfirmed : EndReason.Timeout, ctx.Now() - began);
        }
    }

    private sealed class MarkHandler : ICommandHandler
    {
        private readonly Action<int, string>? _onMark;
        private readonly string _key;

        public MarkHandler(Action<int, string>? onMark, string key)
        {
            _onMark = onMark;
            _key = key;
        }

        public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var text = p.Str(_key);
            _onMark?.Invoke(ctx.Channel, text);
            ctx.Note?.Invoke(text);
            return Task.FromResult(CommandOutcome.Instant());
        }
    }

    private sealed class NoteHandler : ICommandHandler
    {
        private readonly Func<CommandInput, string> _text;
        public NoteHandler(Func<CommandInput, string> text) => _text = text;

        public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            ctx.Note?.Invoke(_text(p));
            return Task.FromResult(CommandOutcome.Instant());
        }
    }

    /// <summary>结束实验：可选先降到安全温度，再停搅拌。</summary>
    private sealed class FinishHandler : ICommandHandler
    {
        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var began = ctx.Now();
            var reason = EndReason.Completed;

            if (p.Flag("cool", true) && ctx.Capabilities.Get<ITemperatureControl>() is { } temp)
            {
                var safe = p.Num("safe", 30);
                await temp.RampAsync(safe, BuiltinCommands.Passive, TempChannelKind.Reactor, ct).ConfigureAwait(false);
                var reached = await temp.WaitReachedAsync(safe, 1.0, TimeSpan.FromHours(4), ct).ConfigureAwait(false);
                await temp.StopAsync(ct).ConfigureAwait(false);
                reason = reached ? EndReason.Reached : EndReason.Timeout;
            }

            if (p.Flag("stir", true) && ctx.Capabilities.Get<IStirrer>() is { } stir)
                await stir.StopAsync(ct).ConfigureAwait(false);

            return new CommandOutcome(reason, ctx.Now() - began);
        }
    }
}
