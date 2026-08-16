namespace Tec.Driver.Abi;

/// <summary>
/// 设备指令表：温度 4 · 搅拌 1 · 加料 1 · pH 2 · 在线分析 4，共 12 条
/// （另有 8 条通用指令在 Tec.Core 的 BuiltinCommands 里）。
///
/// **一条指令 = 设备真正会做的一个动作。** 同一个动作的不同用法是参数，不是新指令——
/// 原型按「工艺说法」列了 23 条，其中一大半落到硬件上是同一个动作：
/// 温控器只有「按速率去某个目标」这一件事，泵只有「按流量送某个体积」这一件事。
/// 拆成多条的代价是操作人得先选对那一条：选了「升温至」却填了更低的目标，
/// 设备照样降温，界面却在说升温——这种对不上是配方出错的常见来源。
///
/// 放在 ABI 里而不是仿真项目里：这 12 条是配方与**任何**驱动之间的共同语言，
/// 仿真机和真机必须认同一套。真机驱动要是自己另写一份「控温」，
/// 拿仿真调好的配方插上真机就跑不了了。
///
/// Id 用 ASCII（进配方文件用），DisplayName 才是界面上的中文指令名。
/// 旧配方里的老 Id 由 Tec.Core 的 RecipeMigration 负责翻译，这里不留兼容别名。
/// </summary>
public static class CommandSpecs
{
    // ── 模块名 ────────────────────────────────────────────────────
    public const string ModTemp = "温度模块";
    public const string ModStir = "搅拌";
    public const string ModDose = "加料";
    public const string ModPh = "pH 控制";
    public const string ModAna = "在线分析";

    // ── 指令 Id ────────────────────────────────────────────────────
    public const string Control = "tec.temp.control";      // 控温（升温 / 降温 / Tr / Tj 都是它）
    public const string Gradient = "tec.temp.gradient";    // 梯度控温
    public const string Hold = "tec.temp.hold";            // 恒温保持
    public const string PassiveCool = "tec.temp.passive";  // 自然冷却

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

    private static string F(double v) => Txt.Fx(v);

    /// <summary>自然冷却按 0.5 ℃/min 估算。</summary>
    private const double PassiveRate = 0.5;

    // ── 温度模块（4 条）─────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Temperature { get; } = Attach(new[]
    {
        // 原型的「升温至 / 降温至 / 夹套控温 Tj / 釜内控温 Tr」是同一条：
        // 方向由目标温度与当前温度的关系决定，控温对象是一个参数。
        // RD105 收到的也只是「TG = 目标、SPEED = 速率」这一组寄存器。
        new CommandDescriptor(Control, "控温", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标温度", 60, "℃", -40, 180, 0.1),
                Field.Num("rate", "变温速率", 2, "℃/min", 0.1, 16, 0.1),
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr"),
                Field.Num("tol", "到达允差", 0.5, "℃", 0.1, 5, 0.1),
                Field.Bool("wait", "到达后等待稳定", true)
            })
            { Tip = "升温还是降温由目标温度决定，不用分两条指令。到达即结束；要在目标温度上停留，后面接一条「恒温保持」。" },
            TerminationKind.Setpoint, RampEstimate,
            p => $"控温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，{F(p.Num("rate"))} ℃/min")
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
                }),
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
        { IconKey = "cool" }
    });

    // ── 搅拌（1 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Stirring { get; } = Attach(new[]
    {
        // 搅拌器只认一个转速设定值。原型的「转速梯度」是「到达用时」写长一点，
        // 「停止搅拌」是转速填 0——都不值得单开一条。
        new CommandDescriptor(Stir, "搅拌", ModStir, typeof(IStirrer),
            new ParameterSchema(new[]
            {
                Field.Num("rpm", "转速", 400, "rpm", 0, 1000, 10),
                Field.Num("ramp", "到达用时", 5, "s", 0, null, 1)
            })
            { Tip = "转速填 0 就是停机。「到达用时」是从当前转速升/降到目标转速的时间，填长一点就是转速梯度。" },
            TerminationKind.Timer,
            (p, ctx) => { ctx.Rpm = p.Num("rpm"); return TimeSpan.FromSeconds(p.Num("ramp")); },
            p => p.Num("rpm") <= 0
                ? $"停止搅拌（{F(p.Num("ramp"))} s 内减速）"
                : $"搅拌转速设为 {F(p.Num("rpm"))} rpm（{F(p.Num("ramp"))} s 内到达）")
        { IconKey = "stir", SupportsHotEdit = true }
    });

    // ── 加料（1 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Dosing { get; } = Attach(new[]
    {
        // 泵只认「按这个流量送这么多体积」。原型的「定量加料」给的是体积 + 完成时间，
        // 换算过来就是流量，是同一件事的两种写法。分段加料用循环表达。
        new CommandDescriptor(Dose, "加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Sel("pump", "加料泵", Pumps, "加料泵 1"),
                Field.Text("liq", "料液", "硝酸 65%"),
                Field.Num("vol", "加料体积", 10, "mL", 0.1, null, 0.1),
                Field.Num("rate", "流量", 0.5, "mL/min", 0.01, 50, 0.01),
                Field.Bool("sync", "与控温同步启动", true)
            })
            { Tip = "加料时长 = 体积 ÷ 流量，不用另填。要分几批加就用「循环开始 / 循环结束」把这一条圈起来。" },
            TerminationKind.Quantity,
            (p, ctx) =>
            {
                ctx.Volume += p.Num("vol");
                return TimeSpan.FromSeconds(p.Num("vol") / Math.Max(p.Num("rate"), 0.001) * 60);
            },
            p => $"{p.Str("pump")} 以 {F(p.Num("rate"))} mL/min 加入 {F(p.Num("vol"))} mL「{p.Str("liq")}」")
        { IconKey = "dose", SupportsHotEdit = true }
    });

    // ── pH 控制（2 条）─────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Ph { get; } = Attach(new[]
    {
        new CommandDescriptor(PhSample, "pH 采集", ModPh, typeof(IScalarSensor),
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
        new CommandDescriptor(PhHold, "pH 反馈加料", ModPh, typeof(IScalarSensor),
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

    // ── 时长估算 ───────────────────────────────────────────────────

    /// <summary>控温：|目标 − 当前| / 速率，并把上下文温度推进到目标。</summary>
    private static TimeSpan RampEstimate(CommandInput p, EstimationContext ctx)
    {
        var s = Math.Abs(p.Num("target") - ctx.Temperature) / Math.Max(p.Num("rate"), 0.01) * 60;
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
        Control => $"{p.Str("obj")} → {F(p.Num("target"))} ℃ · {F(p.Num("rate"))} ℃/min",
        Gradient => p.RowsOrEmpty.Count == 0
            ? "未设分段"
            : $"{p.RowsOrEmpty.Count} 段曲线 · {F(p.RowsOrEmpty[0].Num("t"))}→{F(p.RowsOrEmpty[^1].Num("t"))} ℃",
        Hold => $"{F(p.Num("dur"))} min · ±{F(p.Num("tol"))} ℃",
        PassiveCool => $"自然冷却至 {F(p.Num("target"))} ℃",

        Stir => p.Num("rpm") <= 0 ? $"停机 · 减速 {F(p.Num("ramp"))} s" : $"{F(p.Num("rpm"))} rpm",

        Dose => $"{F(p.Num("vol"))} mL · {F(p.Num("rate"))} mL/min",

        PhSample => $"每 {F(p.Num("interval"))} s 采样",
        PhHold => $"pH {F(p.Num("target"))} ± {F(p.Num("band"))} · {F(p.Num("dur"))} min",

        Raman => $"{F(p.Num("integ"))} ms · {p.Str("mode")}",
        Infrared => $"每 {F(p.Num("interval"))} s · {F(p.Num("scans"))} 次",
        Turbidity => $"每 {F(p.Num("interval"))} s · 阈值 {F(p.Num("thr"))} NTU",
        Solubility => $"按{p.Str("by")}判定 · 阈值 {F(p.Num("thr"))}",

        _ => ""
    };
}
