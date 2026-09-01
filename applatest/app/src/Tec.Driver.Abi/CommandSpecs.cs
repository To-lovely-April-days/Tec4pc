namespace Tec.Driver.Abi;

/// <summary>
/// 设备指令表：温控 5 · 搅拌 1 · 加料 2（加料 + pH 反馈加料）· 采样 1（pH 采集）
/// · 在线分析 4，共 13 条（流程控制与安全的 9 条在 Tec.Core 的 BuiltinCommands 里）。
///
/// **一条指令 = 设备真正会做的一个动作。** 同一个动作的不同用法是参数，不是新指令——
/// 原型按「工艺说法」列了 23 条，其中一大半落到硬件上是同一个动作：
/// 温控器只有「按速率去某个目标」这一件事，泵只有「按流量送某个体积」这一件事。
/// 拆成多条的代价是操作人得先选对那一条：选了「升温至」却填了更低的目标，
/// 设备照样降温，界面却在说升温——这种对不上是配方出错的常见来源。
///
/// 放在 ABI 里而不是仿真项目里：这 13 条是配方与**任何**驱动之间的共同语言，
/// 仿真机和真机必须认同一套。真机驱动要是自己另写一份「控温」，
/// 拿仿真调好的配方插上真机就跑不了了。
///
/// Id 用 ASCII（进配方文件用），DisplayName 才是界面上的中文指令名。
/// 旧配方里的老 Id 由 Tec.Core 的 RecipeMigration 负责翻译，这里不留兼容别名。
/// </summary>
public static class CommandSpecs
{
    // ── 模块名 ────────────────────────────────────────────────────
    // 分组照 iControl Recipe Library：温控 / 搅拌 / 加料 / 采样 / 流程控制 / 安全。
    // pH 反馈加料归加料组（iControl 把 Control pH 放在加料组——它的执行机构是泵）；
    // pH 采集归采样组。流程控制与安全在 Tec.Core 的 BuiltinCommands 里用。
    // 模块名只活在运行时（配方文件存的是指令 Id），改名不动文件。
    public const string ModTemp = "温控";
    public const string ModStir = "搅拌";
    public const string ModDose = "加料";
    public const string ModSample = "采样";
    public const string ModFlow = "流程控制";
    public const string ModSafety = "安全";
    public const string ModAna = "在线分析";

    // ── 指令 Id ────────────────────────────────────────────────────
    public const string Control = "tec.temp.control";      // 控温（升温 / 降温 / Tr / Tj 都是它）
    public const string Gradient = "tec.temp.gradient";    // 梯度控温
    public const string Hold = "tec.temp.hold";            // 恒温保持
    public const string PassiveCool = "tec.temp.passive";  // 自然冷却
    // 蒸回流：沿用原型的老 Id——老配方里这条从前「有意不翻译」（没有对应硬件），
    // 现在夹套跟随能力有了，老步骤经迁移直接活过来
    public const string Reflux = "tec.temp.reflux";        // 蒸回流（夹套跟随 Tj = Tr + ΔT）

    public const string Stir = "tec.stir.set";             // 搅拌（转速 0 即停机）

    public const string Dose = "tec.dose.add";             // 加料

    public const string PhSample = "tec.ph.sample";        // pH 采集
    public const string PhHold = "tec.ph.hold";            // pH 反馈加料

    public const string Raman = "tec.ana.raman";           // 拉曼采集
    public const string Infrared = "tec.ana.ir";           // 红外采集
    public const string Turbidity = "tec.ana.turbidity";   // 浊度采集
    public const string Solubility = "tec.ana.solubility"; // 溶解度点测定

    private static readonly string[] Tobj = { "釜内 Tr", "夹套 Tj" };
    private static readonly string[] Pumps = { "加料泵 1", "加料泵 2" };

    // 到达方式（iControl 的 Task 段）：怎么去目标是任务，去哪里是数值。
    // 选项顺序照 iControl（As fast as possible / Ramp by duration / Ramp by rate），
    // 缺省是「按速率」——存过盘的老步骤没有 task 键，缺省必须还原老行为
    private static readonly string[] TempTasks = { "尽快", "按时长", "按速率" };
    private static readonly string[] StirTasks = { "立即", "按时长" };
    private static readonly string[] DoseTasks = { "一次加入", "按速率" };

    private static string F(double v) => Txt.Fx(v);

    /// <summary>自然冷却按 0.5 ℃/min 估算。</summary>
    private const double PassiveRate = 0.5;

    // ── 温度模块（5 条）─────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Temperature { get; } = Attach(new[]
    {
        // 原型的「升温至 / 降温至 / 夹套控温 Tj / 釜内控温 Tr」是同一条：
        // 方向由目标温度与当前温度的关系决定，控温对象是一个参数。
        // RD105 收到的也只是「TG = 目标、SPEED = 速率」这一组寄存器。
        // 到达方式按 iControl Heat/Cool 的 Task 三档：尽快（设备最大能力）/
        // 按时长（速率执行时由温差 ÷ 时长现算）/ 按速率（老行为，也是老文件的缺省）。
        // 非线性斜坡（iControl 的 Ramp shape 指数）不做——驱动只有线性斜坡这一件事，
        // 形状曲线用「梯度控温」的分段表达，不装一个执行不出来的参数
        new CommandDescriptor(Control, "控温", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标温度", 60, "℃", -40, 180, 0.1),
                Field.Sel("task", "到达方式", TempTasks, "按速率"),
                Field.Num("rate", "变温速率", 2, "℃/min", 0.1, 16, 0.1) with { VisibleWhen = "task=按速率" },
                Field.Num("dur", "变温时长", 30, "min", 0.1, null, 0.1) with { VisibleWhen = "task=按时长" },
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr"),
                Field.Num("tol", "到达允差", 0.5, "℃", 0.1, 5, 0.1),
                Field.Bool("wait", "到达后等待稳定", true)
            })
            { Tip = "升温还是降温由目标温度决定，不用分两条指令。「尽快」按设备最大变温能力走；「按时长」的速率 = 温差 ÷ 时长，执行时按当时的实测温度现算。到达即结束；要停留就在后面接「恒温保持」。" },
            TerminationKind.Setpoint, RampEstimate,
            p => p.Str("task", "按速率") switch
            {
                "尽快" => $"控温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，尽快到达",
                "按时长" => $"控温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，{F(p.Num("dur"))} min 内到达",
                _ => $"控温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，{F(p.Num("rate"))} ℃/min"
            })
        { IconKey = "temp-up", SupportsHotEdit = true },

        new CommandDescriptor(Hold, "恒温保持", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("dur", "保持时长", 30, "min", 1, null, 1),
                Field.Num("tol", "控温允差", 0.1, "℃", 0.05, 2, 0.05),
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr")
            }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(p.Num("dur") * 60),
            p => $"保持当前温度 {F(p.Num("dur"))} min，允差 ±{F(p.Num("tol"))} ℃")
        { IconKey = "temp-hold", SupportsHotEdit = true },

        // 分段表留着不是为了好看：二十段的结晶曲线拆成四十条步骤没法看，
        // 而且 TecControl.Core 的 TemperatureProfile 本来就是按分段走的。
        new CommandDescriptor(Gradient, "梯度控温", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr"),
                Field.Bool("loop", "循环执行该曲线", false)
            })
            {
                Table = new TableSpec("温度分段", new[]
                {
                    Field.Num("t", "目标温度", 25, "℃", null, null, 0.1),
                    Field.Num("r", "速率", 0.5, "℃/min", null, null, 0.01),
                    Field.Num("h", "保持", 10, "min", null, null, 1)
                })
                { Chart = "temp-profile" },
                Tip = "每一行是一个温度分段：以设定速率升/降到目标温度后保持指定时间，再进入下一段。"
            },
            TerminationKind.Timer, GradientEstimate,
            p => p.RowsOrEmpty.Count == 0
                ? "按曲线控温（未设分段）"
                : $"{p.Str("obj")} 按 {p.RowsOrEmpty.Count} 段曲线控温，"
                  + $"{F(p.RowsOrEmpty[0].Num("t"))}→{F(p.RowsOrEmpty[^1].Num("t"))} ℃")
        { IconKey = "temp-ramp" },

        // 停输出靠环境降温。这跟「控温到 25 ℃」是两回事：那个会为了追目标而制冷
        new CommandDescriptor(PassiveCool, "自然冷却", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "冷却至", 25, "℃", null, null, 0.1),
                Field.Num("timeout", "超时放弃", 120, "min", 1, null, 1)
            }),
            TerminationKind.Setpoint,
            (p, ctx) =>
            {
                var s = Math.Abs(ctx.Temperature - p.Num("target")) / PassiveRate * 60;
                ctx.Temperature = p.Num("target");
                return TimeSpan.FromSeconds(s);
            },
            p => $"自然冷却至 {F(p.Num("target"))} ℃")
        { IconKey = "cool" },

        // 蒸回流：夹套跟着釜内走（Tj 目标 = Tr + ΔT）。回流建立后 Tr 停在沸点，
        // 「到达判据」无意义——按时长结束。要求设备有夹套跟随能力（IRefluxControl）：
        // 真机是主机采集循环里的跟随环，仿真是热模型的跟随 Tick——没有就是灰的，
        // 不摆一个执行不出来的步骤
        new CommandDescriptor(Reflux, "蒸回流", ModTemp, typeof(IRefluxControl),
            new ParameterSchema(new[]
            {
                Field.Num("dt", "ΔT（Tj−Tr）", 5, "K", 1, 30, 0.5),
                Field.Num("tjmax", "夹套上限", 120, "℃", 0, 300, 1),
                Field.Num("dur", "回流时长", 60, "min", 1, null, 1)
            })
            { Tip = "夹套目标 = 釜内实测 + ΔT，持续跟随；釜内到沸点后停在平台上（回流建立），夹套恒高 ΔT。沸点未知时用它，不用「控温」猜目标。夹套上限之外还有设备自己的超温保护寄存器兜底。注意 ΔT 别与安全限值里的 T diff max 冲突——回流稳态下 Tj−Tr 恒等于 ΔT。" },
            TerminationKind.Timer,
            (p, ctx) =>
            {
                // 回流时长即步时长。釜内温度不动（沸点未知，不编一个）；
                // 夹套推进到 Tr + ΔT——后续步骤的起点温度按它算
                ctx.Jacket = ctx.Temperature + p.Num("dt", 5);
                return TimeSpan.FromSeconds(p.Num("dur", 60) * 60);
            },
            p => $"蒸回流：Tj 跟随 Tr+{F(p.Num("dt", 5))} K · {F(p.Num("dur", 60))} min · Tj ≤ {F(p.Num("tjmax", 120))} ℃")
        { IconKey = "temp-up" }
    });

    // ── 搅拌（1 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Stirring { get; } = Attach(new[]
    {
        // 搅拌器只认一个转速设定值。原型的「转速梯度」是「到达用时」写长一点，
        // 「停止搅拌」是转速填 0——都不值得单开一条。
        // 到达方式照 iControl Stir 的两档：立即 / 按时长斜坡。缺省「按时长」，
        // 老步骤没有 task 键，缺省必须还原老行为（它们都带着 ramp 值）
        new CommandDescriptor(Stir, "搅拌", ModStir, typeof(IStirrer),
            new ParameterSchema(new[]
            {
                Field.Num("rpm", "转速", 400, "rpm", 0, 1000, 10),
                Field.Sel("task", "到达方式", StirTasks, "按时长"),
                Field.Num("ramp", "到达用时", 5, "s", 0, null, 1) with { VisibleWhen = "task=按时长" }
            })
            { Tip = "转速填 0 就是停机。「到达用时」是从当前转速升/降到目标转速的时间，填长一点就是转速梯度；「立即」按驱动的最短加减速走。" },
            TerminationKind.Timer,
            (p, ctx) =>
            {
                ctx.Rpm = p.Num("rpm");
                return StirImmediate(p) ? TimeSpan.Zero : TimeSpan.FromSeconds(p.Num("ramp"));
            },
            p =>
            {
                var imm = StirImmediate(p);
                return p.Num("rpm") <= 0
                    ? imm ? "停止搅拌" : $"停止搅拌（{F(p.Num("ramp"))} s 内减速）"
                    : $"搅拌转速设为 {F(p.Num("rpm"))} rpm" + (imm ? "" : $"（{F(p.Num("ramp"))} s 内到达）");
            })
        { IconKey = "stir", SupportsHotEdit = true }
    });

    // ── 加料（1 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Dosing { get; } = Attach(new[]
    {
        // 泵只认「按这个流量送这么多体积」。原型的「定量加料」给的是体积 + 完成时间，
        // 换算过来就是流量，是同一件事的两种写法。分段加料用循环表达。
        // 加入方式照 iControl 的 Add at Once / Dose at Rate 两档：
        // 「一次加入」按泵最大流量送完设定体积（真按设备 Limits 走，不是瞬移）
        new CommandDescriptor(Dose, "加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Sel("pump", "加料泵", Pumps, "加料泵 1"),
                Field.Text("liq", "料液", "硝酸 65%"),
                Field.Num("vol", "加料体积", 10, "mL", 0.1, null, 0.1),
                Field.Sel("task", "加入方式", DoseTasks, "按速率"),
                Field.Num("rate", "流量", 0.5, "mL/min", 0.01, 50, 0.01) with { VisibleWhen = "task=按速率" },
                Field.Bool("sync", "与控温同步启动", true)
            })
            { Tip = "加料时长 = 体积 ÷ 流量，不用另填。「一次加入」按泵的最大流量送——泵送液要时间，没有真正的瞬时加入。要分几批加就用「循环开始 / 循环结束」把这一条圈起来。" },
            TerminationKind.Quantity,
            (p, ctx) =>
            {
                ctx.Volume += p.Num("vol");
                var rate = DoseAtOnce(p) ? Math.Max(ctx.MaxDoseRatePerMin, 0.001)
                                         : Math.Max(p.Num("rate"), 0.001);
                return TimeSpan.FromSeconds(p.Num("vol") / rate * 60);
            },
            p => DoseAtOnce(p)
                ? $"{p.Str("pump")} 一次加入 {F(p.Num("vol"))} mL「{p.Str("liq")}」（按泵最大流量）"
                : $"{p.Str("pump")} 以 {F(p.Num("rate"))} mL/min 加入 {F(p.Num("vol"))} mL「{p.Str("liq")}」")
        { IconKey = "dose", SupportsHotEdit = true }
    });

    // ── pH（2 条）──────────────────────────────────────────────────
    // 采集归采样组，反馈加料归加料组（iControl 把 Control pH 放在加料组）。
    // 两条仍由 pH 电极驱动认领——模块只是库里的货架，不是归属

    public static IReadOnlyList<CommandDescriptor> Ph { get; } = Attach(new[]
    {
        new CommandDescriptor(PhSample, "pH 采集", ModSample, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("interval", "采样间隔", 1, "s", 0.1, null, 0.1),
                Field.Bool("log", "写入实验记录", true)
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"每 {F(p.Num("interval"))} s 采集一次 pH")
        { IconKey = "ph" },

        // 原型里这件事写了两遍：加料模块的「pH 反馈加料」和 pH 模块的「pH 保持」。
        // 落到硬件上是同一个闭环——pH 电极测、加料泵调——所以只留一条，
        // 放在 pH 模块下（它的判据是 pH，泵只是执行机构）。
        new CommandDescriptor(PhHold, "pH 反馈加料", ModDose, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标 pH", 7, "", 0, 14, 0.01),
                Field.Num("band", "死区", 0.2, "pH", 0.01, 2, 0.01),
                Field.Sel("pump", "调节泵", Pumps, "加料泵 2"),
                Field.Num("maxRate", "最大流量", 1, "mL/min", 0.01, 50, 0.01),
                Field.Num("maxVol", "最大总量", 50, "mL", 0.1, null, 0.1),
                Field.Num("dur", "维持时长", 60, "min", 1, null, 1)
            }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(p.Num("dur") * 60),
            p => $"{p.Str("pump")} 反馈加料，维持 pH {F(p.Num("target"))} ± {F(p.Num("band"))}，"
                 + $"{F(p.Num("dur"))} min")
        { IconKey = "ph-hold", SupportsHotEdit = true, AlsoRequires = new[] { typeof(IDosing) } }
    });

    // ── 在线分析（4 条，一台仪器一条）──────────────────────────────

    public static IReadOnlyList<CommandDescriptor> RamanCommands { get; } = Attach(new[]
    {
        new CommandDescriptor(Raman, "拉曼采集", ModAna, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("integ", "积分时间", 500, "ms", 10, null, 10),
                Field.Num("avg", "平均次数", 3, "次", 1, null, 1),
                Field.Sel("mode", "采集方式", new[] { "连续", "按间隔" }, "连续"),
                Field.Num("interval", "采集间隔", 30, "s", 1, null, 1)
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"拉曼采集，积分 {F(p.Num("integ"))} ms，{p.Str("mode")}")
        { IconKey = "raman" }
    });

    public static IReadOnlyList<CommandDescriptor> InfraredCommands { get; } = Attach(new[]
    {
        new CommandDescriptor(Infrared, "红外采集", ModAna, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("interval", "扫描间隔", 15, "s", 1, null, 1),
                Field.Num("scans", "扫描次数", 16, "次", 1, null, 1),
                Field.Num("res", "分辨率", 4, "cm⁻¹", 1, null, 1)
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"红外采集，每 {F(p.Num("interval"))} s 扫描 {F(p.Num("scans"))} 次")
        { IconKey = "ir" }
    });

    public static IReadOnlyList<CommandDescriptor> TurbidityCommands { get; } = Attach(new[]
    {
        new CommandDescriptor(Turbidity, "浊度采集", ModAna, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("interval", "采样间隔", 1, "s", 0.1, null, 0.1),
                Field.Num("thr", "成核阈值", 50, "NTU", 0, null, 1),
                Field.Bool("mark", "越阈自动打点", true)
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"浊度采集，每 {F(p.Num("interval"))} s；超过 {F(p.Num("thr"))} NTU 记为成核")
        { IconKey = "turb" },

        // 与「浊度采集」不是一回事：这一条要等到判据成立才结束，是条件终止步
        new CommandDescriptor(Solubility, "溶解度点测定", ModAna, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Sel("by", "判定依据", new[] { "浊度", "拉曼", "图像" }, "浊度"),
                Field.Num("thr", "溶清阈值", 5, "NTU", 0, null, 1),
                Field.Num("hold", "确认时长", 2, "min", 0.1, null, 0.1)
            }),
            TerminationKind.Condition,
            (p, _) => TimeSpan.FromSeconds(p.Num("hold") * 60),
            p => $"按{p.Str("by")}判定溶清点，阈值 {F(p.Num("thr"))}")
        { IconKey = "solubility" }
    });

    private static IReadOnlyList<CommandDescriptor> Attach(CommandDescriptor[] list)
    {
        for (var i = 0; i < list.Length; i++)
        {
            var id = list[i].Id;
            list[i] = list[i] with { Summarize = p => Summary(id, p) };
        }
        return list;
    }

    // ── 到达方式判读（估算、摘要、执行器三处同一个判据）────────────

    /// <summary>控温的到达方式。老步骤没有 task 键 → 按速率（老行为）。</summary>
    public static string TempTaskOf(CommandInput p) => p.Str("task", "按速率");

    /// <summary>搅拌是不是「立即」。缺省按时长——老步骤都带着 ramp 值。</summary>
    public static bool StirImmediate(CommandInput p) => p.Str("task", "按时长") == "立即";

    /// <summary>加料是不是「一次加入」（按泵最大流量）。缺省按速率。</summary>
    public static bool DoseAtOnce(CommandInput p) => p.Str("task", "按速率") == "一次加入";

    // ── 时长估算 ───────────────────────────────────────────────────

    /// <summary>
    /// 控温：按时长直接用时长；尽快按设备最大变温能力（ctx 播种的真值）；
    /// 按速率照旧 |目标 − 当前| / 速率。都把上下文温度推进到目标。
    /// </summary>
    private static TimeSpan RampEstimate(CommandInput p, EstimationContext ctx)
    {
        var s = TempTaskOf(p) switch
        {
            "按时长" => Math.Max(0, p.Num("dur")) * 60,
            "尽快" => Math.Abs(p.Num("target") - ctx.Temperature) / Math.Max(ctx.MaxTempRatePerMin, 0.01) * 60,
            _ => Math.Abs(p.Num("target") - ctx.Temperature) / Math.Max(p.Num("rate"), 0.01) * 60
        };
        ctx.Temperature = p.Num("target");
        return TimeSpan.FromSeconds(s);
    }

    private static TimeSpan GradientEstimate(CommandInput p, EstimationContext ctx)
    {
        var s = 0d;
        foreach (var r in p.RowsOrEmpty)
        {
            s += Math.Abs(r.Num("t") - ctx.Temperature) / Math.Max(r.Num("r"), 0.01) * 60 + r.Num("h") * 60;
            ctx.Temperature = r.Num("t");
        }
        return TimeSpan.FromSeconds(s);
    }

    // ── 步骤卡摘要 ─────────────────────────────────────────────────

    /// <summary>
    /// 卡片上那一行短摘要。和 Describe 是两句不同的话：
    /// Describe 是整句工艺语句，Summary 是卡片上的一行。
    /// </summary>
    public static string Summary(string commandId, CommandInput p) => commandId switch
    {
        Control => TempTaskOf(p) switch
        {
            "尽快" => $"{p.Str("obj")} → {F(p.Num("target"))} ℃ · 尽快",
            "按时长" => $"{p.Str("obj")} → {F(p.Num("target"))} ℃ · {F(p.Num("dur"))} min",
            _ => $"{p.Str("obj")} → {F(p.Num("target"))} ℃ · {F(p.Num("rate"))} ℃/min"
        },
        Gradient => p.RowsOrEmpty.Count == 0
            ? "未设分段"
            : $"{p.RowsOrEmpty.Count} 段曲线 · {F(p.RowsOrEmpty[0].Num("t"))}→{F(p.RowsOrEmpty[^1].Num("t"))} ℃",
        Hold => $"{F(p.Num("dur"))} min · ±{F(p.Num("tol"))} ℃",
        PassiveCool => $"自然冷却至 {F(p.Num("target"))} ℃",
        Reflux => $"Tj = Tr+{F(p.Num("dt", 5))} K · {F(p.Num("dur", 60))} min",

        Stir => p.Num("rpm") <= 0
            ? StirImmediate(p) ? "停机" : $"停机 · 减速 {F(p.Num("ramp"))} s"
            : $"{F(p.Num("rpm"))} rpm",

        Dose => DoseAtOnce(p)
            ? $"{F(p.Num("vol"))} mL · 一次加入"
            : $"{F(p.Num("vol"))} mL · {F(p.Num("rate"))} mL/min",

        PhSample => $"每 {F(p.Num("interval"))} s 采样",
        PhHold => $"pH {F(p.Num("target"))} ± {F(p.Num("band"))} · {F(p.Num("dur"))} min",

        Raman => $"{F(p.Num("integ"))} ms · {p.Str("mode")}",
        Infrared => $"每 {F(p.Num("interval"))} s · {F(p.Num("scans"))} 次",
        Turbidity => $"每 {F(p.Num("interval"))} s · 阈值 {F(p.Num("thr"))} NTU",
        Solubility => $"按{p.Str("by")}判定 · 阈值 {F(p.Num("thr"))}",

        _ => ""
    };
}
