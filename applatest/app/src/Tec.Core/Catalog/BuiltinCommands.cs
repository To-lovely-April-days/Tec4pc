using Tec.Core.Recipes;
using Tec.Core.Safety;
using Tec.Driver.Abi;

namespace Tec.Core.Catalog;

/// <summary>
/// 不需要任何设备能力的 9 条指令，由 Core 提供。货架分组照 iControl：
/// 大半在流程控制组，采样提醒在采样组、改限值在安全组。
///
/// 这一组源自原型 CMDS.misc：指令名、参数键、默认值、单位、步进、卡片摘要 sum、
/// 整句描述 DESC、时长估算 SECS 尽量不动，动过的（改限值）由 RecipeMigration 兜着。
///
/// 循环用配对标记而不是嵌套树：排期时用栈处理，甘特上循环体按第一轮展开，
/// 循环开始行画一条覆盖全部轮次的跨度条（§6 第 2 条）。
/// </summary>
public static class BuiltinCommands
{
    // Id 用 ASCII，便于存进配方文件；DisplayName 才是原型里的中文名
    public const string Wait = "tec.flow.wait";            // 等待（按时间 / 按条件）
    /// <summary>老 Id。「条件等待」短暂地当过一条独立指令，现在是「等待」的一种方式；
    /// 存过盘的配方靠 RecipeMigration 翻译回来。</summary>
    public const string WaitUntil = "tec.flow.waitUntil";
    public const string LoopBegin = "tec.flow.loopBegin";  // 循环开始
    public const string LoopEnd = "tec.flow.loopEnd";      // 循环结束
    public const string SetVar = "tec.flow.setVar";        // 设定变量
    public const string Message = "tec.flow.message";      // 消息提示
    public const string Mark = "tec.flow.mark";            // 标记事件
    public const string Sampling = "tec.flow.sampling";    // 采样提醒
    public const string Interlock = "tec.flow.interlock";  // 安全联锁
    public const string Finish = "tec.flow.finish";        // 结束实验
    /// <summary>起始装料与限值（iControl 的 First Fill and Safety Limits）。
    /// 新配方自动以它开头，必须是第一步且不可删除；老配方没有它照样能跑（校验只提醒）。</summary>
    public const string FirstFill = "tec.flow.firstfill";

    /// <summary>
    /// 分组照 iControl：这 9 条大半是流程控制组，采样提醒归采样组、
    /// 改限值归安全组。老组名「通用」不再用——模块名只活在运行时，文件无感。
    /// </summary>
    public const string Module = CommandSpecs.ModFlow;

    /// <summary>自然冷却按 0.5 ℃/min 估算（原型 PASSIVE）。</summary>
    public const double Passive = 0.5;

    public static bool IsLoopBegin(string id) => id == LoopBegin;
    public static bool IsLoopEnd(string id) => id == LoopEnd;

    // 浊度从监测量里撤了：设备库里已经没有浊度探头，选出来也没有那一路信号——
    // 安全层见不到数会按「传感器失效」触发，那不是操作人想要的
    private static readonly string[] InterlockSources = { "釜内 Tr", "夹套 Tj", "pH" };
    private static readonly string[] InterlockOps = { ">", "<" };

    /// <summary>
    /// 安全限值参数表（EasyMax 102 / 402 手册 §6 那张完整表）。
    /// 前六条逐条照抄手册；pH 两条是本机的扩展——这台机器能插 pH 电极，
    /// 手册那张表里没有它，但一路真信号该能设限值。
    ///
    /// 表里另外两个（Tsafe / Rsafe）不在这儿：它们不是「越了就报」的限值，
    /// 是 E 级紧急程序**触发之后**把机器摆成什么样，所以做成步骤上的字段
    /// （见 Interlock / FirstFill 的 eOn / tsafe / rsafeMode / rsafe）。
    /// </summary>
    public static readonly string[] SafetyParams =
    { "Tr max", "Tr min", "Tj max", "Tj min", "T diff max", "Rmax", "pH max", "pH min" };

    /// <summary>Rsafe 的两种写法（手册：Hold 或 0~1000 rpm）。</summary>
    public static readonly string[] RsafeModes = { "保持当前", "指定转速" };

    /// <summary>
    /// 参数名 → 盯哪一路信号、是上限还是下限、单位。
    /// 「T diff max」比较特殊：手册说的是允许的 Tj 与 Tr 温差，
    /// 一条限值管住 dT 的上下两侧（±v），由 LimitsFromRows 展开。
    /// </summary>
    public static (string Tag, bool IsMax, string Unit)? SafetyParamOf(string par) => par switch
    {
        "Tr max" => ("Tr", true, "℃"),
        "Tr min" => ("Tr", false, "℃"),
        "Tj max" => ("Tj", true, "℃"),
        "Tj min" => ("Tj", false, "℃"),
        "T diff max" => ("dT", true, "K"),
        "Rmax" => ("rpm", true, "rpm"),
        "pH max" => ("pH", true, ""),
        "pH min" => ("pH", false, ""),
        _ => null
    };

    /// <summary>动作的严厉程度。同一路信号上下限并在一条记录里，动作取最严的那一档。</summary>
    private static int Severity(SafetyAction a) => a switch
    {
        SafetyAction.Alarm => 0,
        SafetyAction.StopDosing => 1,
        SafetyAction.StopHeating => 2,
        SafetyAction.AbortChannel => 3,
        _ => 4
    };

    /// <summary>
    /// 一张限值表（「改限值」与起始步骤共用）→ 交给安全层的限值记录。
    ///
    /// **同一路信号的上下限必须并成一条**：安全层按「通道 + 信号」认一条配方
    /// 限值，各发各的会互相顶掉——从前 Tr max 和 Tr min 就只能留住后设的那个。
    /// 认不出的参数名照实回报，不当作没写过。
    /// </summary>
    public static IReadOnlyList<SafetyLimit> LimitsFromRows(
        int channel, IEnumerable<ParameterSet> rows, string note, Action<string>? onSkip = null)
    {
        var acc = new Dictionary<string, (double? Min, double? Max, SafetyAction Act)>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var par = row.Str("par");
            if (SafetyParamOf(par) is not { } spec)
            {
                onSkip?.Invoke(par.Length == 0 ? "限值表里有一行没选参数，这一条没有生效"
                                               : $"限值表没认出参数「{par}」，这一条没有生效");
                continue;
            }
            var val = row.Num("val");
            var act = SafetyActionWords.Parse(row.Str("act"));
            acc.TryGetValue(spec.Tag, out var cur);
            // 温差是对称的一条：|Tj − Tr| ≤ v，展开成 dT ∈ [−v, +v]
            if (par == "T diff max") cur = (-Math.Abs(val), Math.Abs(val), cur.Act);
            else if (spec.IsMax) cur = (cur.Min, val, cur.Act);
            else cur = (val, cur.Max, cur.Act);
            acc[spec.Tag] = (cur.Min, cur.Max,
                Severity(act) >= Severity(cur.Act) ? act : cur.Act);
        }
        return acc.Select(kv => new SafetyLimit(channel, kv.Key, kv.Value.Min, kv.Value.Max,
                MaxRatePerMin: null, Debounce: TimeSpan.FromSeconds(3), Action: kv.Value.Act)
            { FromRecipe = true, Note = note }).ToList();
    }

    /// <summary>步骤字段里的 E 级紧急程序参数（没开就返回 null）。</summary>
    public static EmergencyPlan? EmergencyFrom(CommandInput p)
        => !p.Flag("eOn") ? null
           : new EmergencyPlan(p.Num("tsafe", 25),
                               p.Str("rsafeMode", "保持当前") == "指定转速" ? p.Num("rsafe", 0) : null);

    /// <summary>限值表的三列，两处步骤共用一份（列名一变两边一起变）。</summary>
    private static FieldSpec[] LimitColumns() => new[]
    {
        Field.Sel("par", "参数", SafetyParams, "Tr max"),
        Field.Num("val", "值", 100, "", null, null, 0.1),
        Field.Sel("act", "触发动作", InterlockActions, "中止本通道")
    };

    /// <summary>E 级紧急程序的四个字段，两处步骤共用（手册 §6 的 Tsafe / Rsafe）。</summary>
    private static FieldSpec[] EmergencyFields() => new[]
    {
        // 标签留短：属性面板左栏窄，长标题会折成三行把整块撑开
        Field.Bool("eOn", "设 E 级紧急程序", false),
        Field.Num("tsafe", "Tsafe 安全温度", 25, "℃", -40, 180, 0.1) with { VisibleWhen = "eOn=true" },
        Field.Sel("rsafeMode", "Rsafe 紧急转速", RsafeModes, "保持当前") with { VisibleWhen = "eOn=true" },
        Field.Num("rsafe", "Rsafe 转速", 200, "rpm", 0, 1000, 10) with { VisibleWhen = "rsafeMode=指定转速" }
    };

    /// <summary>监测量选项 → 数据管线里那一路信号的名字。「改限值」和起始限值
    /// 共用这一份；「浊度」已从下拉里撤了，但存过盘的老配方还带着，得认。</summary>
    public static string? InterlockTag(string src) => src switch
    {
        "釜内 Tr" => "Tr",
        "夹套 Tj" => "Tj",
        "pH" => "pH",
        "浊度" => "turb",
        _ => null
    };
    /// <summary>触发动作**与安全层的五档一一对应**（RunEngine 真会执行的那五个），
    /// 不再有引擎做不出来的「暂停实验」。老配方的旧值由 RecipeMigration 翻译。
    /// 措辞表在 SafetyActionWords 一处，界面下拉与执行走同一份。</summary>
    private static readonly string[] InterlockActions = SafetyActionWords.All;
    private static readonly string[] TimeoutActions = { "暂停并报警", "继续执行", "按失败处理" };

    /// <summary>「设定变量」的取值来源。「数值」以外都是实时量，键与 Cond.SensorKeys 对应。</summary>
    public static readonly string[] SetVarSources = { "数值", "当前 Tr", "当前 Tj", "当前 pH", "当前 浊度", "当前 rpm" };

    /// <summary>来源选项 → 实时量的键。「数值」返回 null。</summary>
    public static string? SensorOfSource(string src)
        => src.StartsWith("当前", StringComparison.Ordinal) ? src[2..].Trim() : null;

    /// <summary>FieldSpec.ChoicesFrom 的约定值：下拉项 = 当前配方的变量名。</summary>
    public const string ChoicesFromRecipeVars = "recipe.vars";

    /// <summary>这一步等待是不是「按条件」。执行器、排期、描述三处同一个判据。</summary>
    public static bool IsCondWait(CommandInput p) => p.Str("by", "按时间") == "按条件";

    public static IReadOnlyList<CommandDescriptor> All { get; } = Attach(new[]
    {
        // iControl 的 [00] First Fill and Safety Limits：空白实验强制以它开头。
        // 我们放宽半步——新配方自动带上、必须第一步、不可删除，但老配方没有它
        // 照样能跑（校验只提醒不阻断，存过盘的东西不能因为程序升级就打不开）。
        // 执行顺序：起始限值先立起来 → 按配料表投料并确认 → 开搅拌 → 到初温。
        // 干搅空釜没意义也伤桨，所以搅拌在投料确认之后
        new CommandDescriptor(FirstFill, "起始装料与限值", Module, null,
            new ParameterSchema(new[]
            {
                Field.Bool("fill", "按配料表投料并确认", true),
                Field.Num("stir", "初始搅拌转速", 0, "rpm", 0, 1000, 10),
                Field.Bool("tempOn", "设定初始温度", false),
                Field.Num("temp", "初始温度", 25, "℃", -40, 180, 0.1) with { VisibleWhen = "tempOn=true" }
            }.Concat(EmergencyFields()).ToArray())
            {
                // 起始限值与「改限值」共用手册 §6 那张参数表：同一件事只有一套说法
                Table = new TableSpec("起始限值", LimitColumns()),
                Tip = "整趟实验的起点：起始限值先交给安全层（活到下一次启动前），"
                    + "再按配料表完成初始投料并确认，然后开搅拌、到初温。"
                    + "转速填 0 就不开搅拌。这一步必须是第一步，删不掉——空釜跑配方没有意义。"
            },
            TerminationKind.Operator,
            (p, ctx) =>
            {
                ctx.Rpm = p.Num("stir");
                if (!p.Flag("tempOn")) return TimeSpan.Zero;
                // 初温按设备最大变温能力估（真值启动时播种，编辑器用声明缺省）
                var s = Math.Abs(p.Num("temp", 25) - ctx.Temperature)
                        / Math.Max(ctx.MaxTempRatePerMin, 0.01) * 60;
                ctx.Temperature = p.Num("temp", 25);
                return TimeSpan.FromSeconds(s);
            },
            p =>
            {
                var bits = new List<string>();
                if (p.Flag("fill", true)) bits.Add("按配料表投料");
                if (p.Num("stir") > 0) bits.Add($"搅拌 {Txt.Fx(p.Num("stir"))} rpm");
                if (p.Flag("tempOn")) bits.Add($"初温 {Txt.Fx(p.Num("temp"))} ℃");
                if (p.RowsOrEmpty.Count > 0) bits.Add($"起始限值 {Txt.Fx(p.RowsOrEmpty.Count)} 条");
                return bits.Count == 0 ? "起始步骤（未设内容）" : "起始：" + string.Join("，", bits);
            })
        { IconKey = "firstfill",
          TerminationBy = p => p.Flag("fill", true) ? TerminationKind.Operator
                             : p.Flag("tempOn") ? TerminationKind.Setpoint
                             : TerminationKind.Immediate },

        // 等待的两种方式在属性里选（与循环开始「按次数 / 按条件」同一个做法），
        // 库里只占一格一张图
        new CommandDescriptor(Wait, "等待", Module, null,
            new ParameterSchema(new[]
            {
                Field.Sel("by", "等待方式", new[] { "按时间", "按条件" }, "按时间"),
                Field.Num("dur", "等待时长", 10, "min", 0.1, null, 0.1) with { VisibleWhen = "by=按时间" },
                Field.Text("cond", "等待条件", "浊度 > 50", Cond.Help) with { VisibleWhen = "by=按条件" },
                Field.Num("timeout", "最长等待", 60, "min", 0.1, null, 0.1) with { VisibleWhen = "by=按条件" },
                Field.Sel("onTimeout", "超时后", TimeoutActions, "暂停并报警") with { VisibleWhen = "by=按条件" }
            }),
            TerminationKind.Timer,
            // 按条件的等待什么时候满足无法预知，与「消息提示」同一个处理：
            // 按 0 计入排期，实际等的时间全部落进「开始偏差」
            (p, _) => IsCondWait(p) ? TimeSpan.Zero
                                    : TimeSpan.FromSeconds(Math.Max(0, p.Num("dur") * 60)),
            p => IsCondWait(p)
                ? $"等待直到 {p.Str("cond")}（最长 {Txt.Fx(p.Num("timeout"))} min）"
                : $"等待 {Txt.Fx(p.Num("dur"))} min")
        { IconKey = "wait", SupportsHotEdit = true,
          TerminationBy = p => IsCondWait(p) ? TerminationKind.Condition : TerminationKind.Timer },

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

        new CommandDescriptor(Sampling, "采样提醒", CommandSpecs.ModSample, null,
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

        // 原名「安全联锁」，只写一行日志、什么都不联锁。现在它真的把这条限值
        // 交给安全层（SafetyMonitor）随时求值——Id 不动，存过盘的配方原样能开
        new CommandDescriptor(Interlock, "改限值", CommandSpecs.ModSafety, null,
            new ParameterSchema(EmergencyFields())
            {
                Table = new TableSpec("安全限值", LimitColumns())
            },
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p =>
            {
                var bits = p.RowsOrEmpty
                    .Select(r => $"{r.Str("par")} = {Txt.Fx(r.Num("val"))}"
                                 + $"{SafetyParamOf(r.Str("par"))?.Unit ?? ""}")
                    .ToList();
                if (EmergencyFrom(p) is { } e) bits.Add("E 级：" + e.Describe());
                return bits.Count == 0 ? "改限值（未设内容）" : "改限值：" + string.Join("，", bits);
            })
        { IconKey = "interlock",
          Tip = "按 EasyMax 手册 §6 的安全限值参数表设值：Tr / Tj 的上下限、"
              + "允许的 Tj−Tr 温差（T diff max）、转速上限 Rmax（另加本机的 pH 上下限）。"
              + "执行到这一步就把它们交给安全层，由它独立于配方持续盯着；"
              + "同一路信号再改一次是换值不是叠加。底线限值由设备范围推导、"
              + "只能在其内收紧，配方设的限值到下一次启动运行前自动清掉（§7.5）。"
              + "Tsafe / Rsafe 是越限中止之后把机器摆成什么样（E 级紧急程序），"
              + "不勾就是原来的收安全态：切输出、搅拌保持。" },

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
        FirstFill => string.Join(" · ", new[]
        {
            p.Flag("fill", true) ? "投料" : null,
            p.Num("stir") > 0 ? $"{Txt.Fx(p.Num("stir"))} rpm" : null,
            p.Flag("tempOn") ? $"初温 {Txt.Fx(p.Num("temp"))} ℃" : null,
            p.RowsOrEmpty.Count > 0 ? $"限值 {Txt.Fx(p.RowsOrEmpty.Count)} 条" : null
        }.Where(x => x is not null)) is { Length: > 0 } s ? s : "未设内容",
        Wait => IsCondWait(p) ? $"等待直到 {p.Str("cond")}" : $"等待 {Txt.Fx(p.Num("dur"))} min",
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

    /// <summary>
    /// 「改限值」与起始限值往哪儿登记。属性注入：这个 provider 在引擎之前建
    /// （引擎构造要拿它当指令来源），SafetyMonitor 又挂在引擎上，
    /// 只能等引擎建好再补上。没补上时该步骤只记录不生效，且会写明。
    /// </summary>
    public SafetyMonitor? Safety { get; set; }

    /// <summary>
    /// 起始步骤「按配料表投料」要念的那份配料表：按通道号取当前工作配料表。
    /// 由组合根注入（配料表挂在 Workspace 上，Core 不认识它放在哪）。
    /// null 或取不到 = 没有配料表，提示按工艺单投料。
    /// </summary>
    public Func<int, Chemistry.ChargeTable?>? ChargeOf { get; set; }

    public IReadOnlyList<CommandDescriptor> Commands => BuiltinCommands.All;

    public ICommandHandler? Resolve(string commandId) => commandId switch
    {
        BuiltinCommands.Wait => new WaitHandler(),
        BuiltinCommands.Message => new PromptHandler(_gate, "msg"),
        BuiltinCommands.Sampling => new PromptHandler(_gate, "label"),
        BuiltinCommands.Mark => new MarkHandler(_onMark, "tag"),
        BuiltinCommands.Interlock => new LimitHandler(Safety),
        BuiltinCommands.FirstFill => new FirstFillHandler(_gate, Safety, ChargeOf),
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

    /// <summary>
    /// 「改限值」：把这条限值登记进本通道的安全层，之后由 SafetyMonitor
    /// 独立于配方 1 Hz 求值。指令本身瞬时完成——它改的是「谁在盯」，
    /// 不是「现在做什么」。同通道同监测量再执行一次是**换值**不是叠加
    /// （SetRecipeLimit 的语义），底线限值不受影响。
    /// </summary>
    private sealed class LimitHandler : ICommandHandler
    {
        private readonly SafetyMonitor? _safety;
        public LimitHandler(SafetyMonitor? safety) => _safety = safety;

        public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            ArmLimits(ctx, p, _safety, "配方「改限值」步骤设定", "改限值");
            return Task.FromResult(CommandOutcome.Instant());
        }
    }

    /// <summary>
    /// 把一张安全限值表（手册 §6 那八个参数）+ E 级紧急程序参数交给安全层。
    /// 「改限值」与起始步骤共用这一段——两处设的是同一套东西，
    /// 各写一份迟早出现「起始限值认得 T diff max、改限值不认」。
    /// 没挂安全层的场合（裸执行器、部分单测）照实说「仅记录」，不装作生效了。
    /// </summary>
    private static void ArmLimits(CommandContext ctx, CommandInput p,
                                  SafetyMonitor? safety, string note, string what)
    {
        var limits = BuiltinCommands.LimitsFromRows(ctx.Channel, p.RowsOrEmpty, note,
                                                    msg => ctx.Note?.Invoke($"{what}：{msg}"));
        var plan = BuiltinCommands.EmergencyFrom(p);

        if (safety is null)
        {
            if (limits.Count > 0 || plan is not null)
                ctx.Note?.Invoke($"{what}：{BuiltinCommands.Summary(BuiltinCommands.Interlock, p)}"
                                 + "（本会话没有安全层，仅记录）");
            return;
        }

        foreach (var lim in limits) safety.SetRecipeLimit(lim);
        if (limits.Count > 0)
            ctx.Note?.Invoke($"{what}已交给安全层：" + string.Join("；", limits.Select(Describe)));
        if (plan is not null)
        {
            safety.SetEmergencyPlan(ctx.Channel, plan);
            ctx.Note?.Invoke($"E 级紧急程序：{plan.Describe()}");
        }

        static string Describe(SafetyLimit l) =>
            l.Tag + (l.Min is { } lo ? $" ≥ {Txt.Fx(lo)}" : "")
                  + (l.Max is { } hi ? $" ≤ {Txt.Fx(hi)}" : "")
                  + $" → {SafetyActionWords.Of(l.Action)}";
    }

    /// <summary>
    /// 起始装料与限值（iControl 的 First Fill and Safety Limits）。
    /// 顺序有讲究：**限值先立起来**——投料、开搅拌、升初温都得在安全层的
    /// 注视下发生；投料确认在开搅拌之前——干搅空釜没意义也伤桨。
    /// 起始限值走 SetRecipeLimit（FromRecipe 层）：活到下一次启动运行前，
    /// 不是只活这一步——它们是整趟实验的地板，不是这一步的临时覆盖。
    /// </summary>
    private sealed class FirstFillHandler : ICommandHandler
    {
        private readonly IOperatorGate _gate;
        private readonly SafetyMonitor? _safety;
        private readonly Func<int, Chemistry.ChargeTable?>? _chargeOf;

        public FirstFillHandler(IOperatorGate gate, SafetyMonitor? safety,
                                Func<int, Chemistry.ChargeTable?>? chargeOf)
        {
            _gate = gate;
            _safety = safety;
            _chargeOf = chargeOf;
        }

        public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
        {
            var began = ctx.Now();

            // 1) 起始限值 + E 级紧急程序（与「改限值」同一套参数表、同一段代码）
            ArmLimits(ctx, p, _safety, "起始步骤设定", "起始限值");

            // 2) 按配料表投料并确认
            if (p.Flag("fill", true))
            {
                var charge = _chargeOf?.Invoke(ctx.Channel);
                if (charge is { IsEmpty: false })
                    ctx.Note?.Invoke($"初始投料 {charge.Items.Count} 行："
                                     + string.Join("、", charge.Items.Select(i => i.Name)));
                else
                    ctx.Note?.Invoke("本通道没有配料表——请按工艺单完成初始投料");

                var ok = await _gate.ConfirmAsync(ctx.Channel, "初始投料完成后请确认", TimeSpan.Zero, ct)
                                    .ConfigureAwait(false);
                if (!ok)
                    return new CommandOutcome(EndReason.Timeout, ctx.Now() - began)
                    { Note = "操作人未确认初始投料" };
            }

            // 3) 初始搅拌
            var rpm = p.Num("stir");
            if (rpm > 0)
            {
                if (ctx.Capabilities.Get<IStirrer>() is not { } stir)
                    return new CommandOutcome(EndReason.Failed, ctx.Now() - began)
                    { Note = "该通道没有搅拌能力，设不了初始搅拌" };
                await stir.SetSpeedAsync(rpm, ct).ConfigureAwait(false);
            }

            // 4) 初始温度：按设备最大变温能力去，到了才算起点就绪
            if (p.Flag("tempOn"))
            {
                if (ctx.Capabilities.Get<ITemperatureControl>() is not { } temp)
                    return new CommandOutcome(EndReason.Failed, ctx.Now() - began)
                    { Note = "该通道没有温度控制能力，设不了初始温度" };
                var target = p.Num("temp", 25);
                var rate = Math.Max(0.05, temp.Limits.MaxRatePerMin);
                await temp.RampAsync(target, rate, TempChannelKind.Reactor, ct).ConfigureAwait(false);
                var budget = TimeSpan.FromMinutes(Math.Abs(target - temp.CurrentReactor) / rate * 3 + 10);
                var reached = await temp.WaitReachedAsync(target, 1.0, budget, ct).ConfigureAwait(false);
                if (!reached)
                    return new CommandOutcome(EndReason.Timeout, ctx.Now() - began)
                    { Note = $"未在 {budget.TotalMinutes:F0} min 内到达初温 {target:F1} ℃" };
            }

            return new CommandOutcome(
                p.Flag("fill", true) ? EndReason.OperatorConfirmed : EndReason.Completed,
                ctx.Now() - began);
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
