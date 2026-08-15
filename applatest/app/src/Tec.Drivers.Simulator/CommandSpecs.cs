using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 原型 PSPEC / DESC / SECS 的 1:1 移植——温度 8 · 搅拌 3 · 加料 4 · pH 3 · 在线分析 5，
/// 共 23 条设备指令（另有 8 条通用指令在 Tec.Core 的 BuiltinCommands 里）。
///
/// **参数键、默认值、单位、步进、摘要、整句描述、时长估算全部按原型逐条对照，不作"改进"。**
/// 想改工艺语义要先改原型，否则两边会各说各的。
///
/// Id 用 ASCII（进配方文件用），DisplayName 才是原型里的中文指令名。
/// </summary>
public static class CommandSpecs
{
    // ── 模块名，对应原型 GRP.label ──────────────────────────────────
    public const string ModTemp = "温度模块";
    public const string ModStir = "搅拌";
    public const string ModDose = "加料";
    public const string ModPh = "pH 控制";
    public const string ModAna = "在线分析";

    // ── 指令 Id ────────────────────────────────────────────────────
    public const string RampUp = "tec.temp.rampUp";        // 升温至
    public const string RampDown = "tec.temp.rampDown";    // 降温至
    public const string Gradient = "tec.temp.gradient";    // 梯度控温
    public const string Hold = "tec.temp.hold";            // 恒温保持
    public const string Reflux = "tec.temp.reflux";        // 结晶模式（蒸回流）
    public const string JacketCtl = "tec.temp.jacket";     // 夹套控温 Tj
    public const string ReactorCtl = "tec.temp.reactor";   // 釜内控温 Tr
    public const string PassiveCool = "tec.temp.passive";  // 自然冷却

    public const string SetSpeed = "tec.stir.set";         // 设定转速
    public const string SpeedRamp = "tec.stir.ramp";       // 转速梯度
    public const string StopStir = "tec.stir.stop";        // 停止搅拌

    public const string DoseRate = "tec.dose.rate";        // 恒速加料
    public const string DoseVolume = "tec.dose.volume";    // 定量加料
    public const string DoseSegments = "tec.dose.segments";// 分段加料
    public const string DosePh = "tec.dose.ph";            // pH 反馈加料

    public const string PhSample = "tec.ph.sample";        // pH 采集
    public const string PhHold = "tec.ph.hold";            // pH 保持（反馈）
    public const string PhAlarm = "tec.ph.alarm";          // pH 上下限报警

    public const string Raman = "tec.ana.raman";           // 拉曼采集
    public const string Infrared = "tec.ana.ir";           // 红外采集
    public const string Turbidity = "tec.ana.turbidity";   // 浊度采集
    public const string DeltaT = "tec.ana.deltaT";         // Tr−Tj 记录
    public const string Solubility = "tec.ana.solubility"; // 溶解度点测定

    private static readonly string[] Tobj = { "釜内 Tr", "夹套 Tj" };
    private static readonly string[] Pumps = { "加料泵 1", "加料泵 2" };

    private static string F(double v) => Txt.Fx(v);

    /// <summary>自然冷却按 0.5 ℃/min 估算（原型 PASSIVE）。</summary>
    private const double PassiveRate = 0.5;

    // ── 温度模块（8 条）─────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Temperature { get; } = Attach(new[]
    {
        new CommandDescriptor(RampUp, "升温至", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标温度", 60, "℃", -40, 180, 0.1),
                Field.Num("rate", "升温速率", 2, "℃/min", 0.1, 16, 0.1),
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr"),
                Field.Num("tol", "到达允差", 0.5, "℃", 0.1, 5, 0.1),
                Field.Bool("wait", "到达后等待稳定", true)
            }),
            TerminationKind.Setpoint, RampEstimate,
            p => $"升温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，{F(p.Num("rate"))} ℃/min")
        { IconKey = "temp-up", SupportsHotEdit = true },

        new CommandDescriptor(RampDown, "降温至", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标温度", 5, "℃", -40, 180, 0.1),
                Field.Num("rate", "降温速率", 0.5, "℃/min", 0.1, 14, 0.1),
                Field.Sel("obj", "控温对象", Tobj, "釜内 Tr"),
                Field.Num("tol", "到达允差", 0.5, "℃", 0.1, 5, 0.1),
                Field.Bool("wait", "到达后等待稳定", true)
            }),
            TerminationKind.Setpoint, RampEstimate,
            p => $"降温 {p.Str("obj")} 至 {F(p.Num("target"))} ℃，{F(p.Num("rate"))} ℃/min")
        { IconKey = "temp-down", SupportsHotEdit = true },

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

        new CommandDescriptor(Reflux, "结晶模式（蒸回流）", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("temp", "回流温度", 78, "℃", null, null, 0.1),
                Field.Num("dur", "回流时长", 45, "min", 1, null, 1),
                Field.Num("jacket", "夹套过热量", 5, "℃", 0, 30, 0.5)
            }),
            TerminationKind.Timer,
            (p, ctx) =>
            {
                var s = Math.Abs(p.Num("temp") - ctx.Temperature) / 2 * 60 + p.Num("dur") * 60;
                ctx.Temperature = p.Num("temp");
                return TimeSpan.FromSeconds(s);
            },
            p => $"蒸回流 {F(p.Num("temp"))} ℃，保持 {F(p.Num("dur"))} min")
        { IconKey = "reflux" },

        new CommandDescriptor(JacketCtl, "夹套控温 Tj", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "Tj 目标", 55, "℃", -40, 180, 0.1),
                Field.Num("rate", "变化速率", 2, "℃/min", 0.1, 16, 0.1),
                Field.Num("dur", "维持时长", 20, "min", 0, null, 1)
            }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(p.Num("dur") * 60),
            p => $"夹套 Tj 控温至 {F(p.Num("target"))} ℃，维持 {F(p.Num("dur"))} min")
        { IconKey = "jacket" },

        new CommandDescriptor(ReactorCtl, "釜内控温 Tr", ModTemp, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("target", "Tr 目标", 60, "℃", -40, 180, 0.1),
                Field.Num("dur", "维持时长", 30, "min", 0, null, 1),
                Field.Num("limit", "Tj 上限保护", 120, "℃", null, null, 1)
            }),
            TerminationKind.Timer,
            (p, ctx) =>
            {
                var s = Math.Abs(p.Num("target") - ctx.Temperature) / 2 * 60 + p.Num("dur") * 60;
                ctx.Temperature = p.Num("target");
                return TimeSpan.FromSeconds(s);
            },
            p => $"釜内 Tr 控温至 {F(p.Num("target"))} ℃，维持 {F(p.Num("dur"))} min")
        { IconKey = "reactor" },

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

    // ── 搅拌（3 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Stirring { get; } = Attach(new[]
    {
        new CommandDescriptor(SetSpeed, "设定转速", ModStir, typeof(IStirrer),
            new ParameterSchema(new[]
            {
                Field.Num("rpm", "转速", 400, "rpm", 0, 1000, 10),
                Field.Num("ramp", "加速时间", 5, "s", 0, null, 1)
            }),
            TerminationKind.Timer,
            (p, ctx) => { ctx.Rpm = p.Num("rpm"); return TimeSpan.FromSeconds(p.Num("ramp")); },
            p => $"搅拌转速设为 {F(p.Num("rpm"))} rpm")
        { IconKey = "stir", SupportsHotEdit = true },

        new CommandDescriptor(SpeedRamp, "转速梯度", ModStir, typeof(IStirrer),
            new ParameterSchema(new[]
            {
                Field.Num("from", "起始转速", 400, "rpm", 0, 1000, 10),
                Field.Num("to", "结束转速", 800, "rpm", 0, 1000, 10),
                Field.Num("dur", "梯度时长", 10, "min", 1, null, 1)
            }),
            TerminationKind.Timer,
            (p, ctx) => { ctx.Rpm = p.Num("to"); return TimeSpan.FromSeconds(p.Num("dur") * 60); },
            p => $"转速由 {F(p.Num("from"))} 升至 {F(p.Num("to"))} rpm，用时 {F(p.Num("dur"))} min")
        { IconKey = "stir-ramp" },

        new CommandDescriptor(StopStir, "停止搅拌", ModStir, typeof(IStirrer),
            new ParameterSchema(new[] { Field.Num("ramp", "减速时间", 5, "s", 0, null, 1) }),
            TerminationKind.Timer,
            (p, ctx) => { ctx.Rpm = 0; return TimeSpan.FromSeconds(p.Num("ramp")); },
            p => $"停止搅拌（{F(p.Num("ramp"))} s 内减速）")
        { IconKey = "stir-off" }
    });

    // ── 加料（4 条）────────────────────────────────────────────────

    public static IReadOnlyList<CommandDescriptor> Dosing { get; } = Attach(new[]
    {
        new CommandDescriptor(DoseRate, "恒速加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Sel("pump", "加料泵", Pumps, "加料泵 1"),
                Field.Text("liq", "料液", "硝酸 65%"),
                Field.Num("rate", "流量", 0.5, "mL/min", 0.01, 50, 0.01),
                Field.Num("vol", "总量", 10, "mL", 0.1, null, 0.1),
                Field.Bool("sync", "与控温同步启动", true)
            }),
            TerminationKind.Quantity,
            (p, ctx) =>
            {
                ctx.Volume += p.Num("vol");
                return TimeSpan.FromSeconds(p.Num("vol") / Math.Max(p.Num("rate"), 0.001) * 60);
            },
            p => $"{p.Str("pump")} 以 {F(p.Num("rate"))} mL/min 加入 {F(p.Num("vol"))} mL「{p.Str("liq")}」")
        { IconKey = "dose", SupportsHotEdit = true },

        new CommandDescriptor(DoseVolume, "定量加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Sel("pump", "加料泵", Pumps, "加料泵 1"),
                Field.Text("liq", "料液", "溶剂"),
                Field.Num("vol", "加料体积", 10, "mL", 0.1, null, 0.1),
                Field.Num("dur", "完成时间", 5, "min", 0.1, null, 0.1)
            }),
            TerminationKind.Quantity,
            (p, ctx) => { ctx.Volume += p.Num("vol"); return TimeSpan.FromSeconds(p.Num("dur") * 60); },
            p => $"{p.Str("pump")} 在 {F(p.Num("dur"))} min 内加入 {F(p.Num("vol"))} mL「{p.Str("liq")}」")
        { IconKey = "dose-bolus" },

        new CommandDescriptor(DoseSegments, "分段加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Sel("pump", "加料泵", Pumps, "加料泵 1"),
                Field.Text("liq", "料液", "反溶剂")
            })
            {
                Table = new TableSpec("加料分段", new[]
                {
                    Field.Num("v", "体积", 5, "mL", null, null, 0.1),
                    Field.Num("r", "流量", 0.5, "mL/min", null, null, 0.01),
                    Field.Num("w", "段后等待", 5, "min", null, null, 1)
                }),
                Tip = "每一行是一个加料分段：以设定流量加入指定体积后等待，再进入下一段。"
            },
            TerminationKind.Quantity, SegmentEstimate,
            p => $"{p.Str("pump")} 分 {p.RowsOrEmpty.Count} 段加入「{p.Str("liq")}」，"
                 + $"共 {F(p.RowsOrEmpty.Sum(r => r.Num("v")))} mL")
        { IconKey = "dose-seg" },

        new CommandDescriptor(DosePh, "pH 反馈加料", ModDose, typeof(IDosing),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标 pH", 7, "", 0, 14, 0.01),
                Field.Num("band", "死区", 0.2, "pH", 0.01, 2, 0.01),
                Field.Sel("pump", "加料泵", Pumps, "加料泵 2"),
                Field.Num("maxRate", "最大流量", 1, "mL/min", 0.01, 50, 0.01),
                Field.Num("maxVol", "最大总量", 50, "mL", 0.1, null, 0.1),
                Field.Num("dur", "维持时长", 60, "min", 1, null, 1)
            }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(p.Num("dur") * 60),
            p => $"{p.Str("pump")} 反馈加料，维持 pH {F(p.Num("target"))} ± {F(p.Num("band"))}，"
                 + $"{F(p.Num("dur"))} min")
        { IconKey = "dose-fb", SupportsHotEdit = true, AlsoRequires = new[] { typeof(IScalarSensor) } }
    });

    // ── pH 控制（3 条）─────────────────────────────────────────────

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

        new CommandDescriptor(PhHold, "pH 保持（反馈）", ModPh, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("target", "目标 pH", 7, "", 0, 14, 0.01),
                Field.Num("band", "死区", 0.1, "pH", 0.01, 2, 0.01),
                Field.Sel("pump", "调节泵", Pumps, "加料泵 2"),
                Field.Num("dur", "保持时长", 60, "min", 1, null, 1)
            }),
            TerminationKind.Timer,
            (p, _) => TimeSpan.FromSeconds(p.Num("dur") * 60),
            p => $"{p.Str("pump")} 将 pH 维持在 {F(p.Num("target"))} ± {F(p.Num("band"))}，{F(p.Num("dur"))} min")
        { IconKey = "ph-hold", SupportsHotEdit = true, AlsoRequires = new[] { typeof(IDosing) } },

        new CommandDescriptor(PhAlarm, "pH 上下限报警", ModPh, typeof(IScalarSensor),
            new ParameterSchema(new[]
            {
                Field.Num("lo", "下限", 6.5, "", 0, 14, 0.01),
                Field.Num("hi", "上限", 7.5, "", 0, 14, 0.01),
                Field.Sel("act", "触发动作", new[] { "仅报警", "暂停实验", "停止加料" }, "仅报警")
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"pH 超出 {F(p.Num("lo"))} ~ {F(p.Num("hi"))} 时{p.Str("act")}")
        { IconKey = "ph-alarm" }
    });

    // ── 在线分析（5 条）────────────────────────────────────────────

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

    /// <summary>Tr−Tj 记录属于反应器自己（它有 Tr 和 Tj），不需要第三方仪器。</summary>
    public static IReadOnlyList<CommandDescriptor> DeltaTCommands { get; } = Attach(new[]
    {
        new CommandDescriptor(DeltaT, "Tr−Tj 记录", ModAna, typeof(ITemperatureControl),
            new ParameterSchema(new[]
            {
                Field.Num("interval", "采样间隔", 1, "s", 0.1, null, 0.1),
                Field.Bool("kinetics", "用于动力学分析", true)
            }),
            TerminationKind.Immediate,
            (_, _) => TimeSpan.Zero,
            p => $"记录 Tr−Tj 温差，每 {F(p.Num("interval"))} s")
        { IconKey = "deltat" }
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

    // ── 时长估算（原型 SECS）───────────────────────────────────────

    /// <summary>升温/降温：|目标 − 当前| / 速率，并把上下文温度推进到目标。</summary>
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

    private static TimeSpan SegmentEstimate(CommandInput p, EstimationContext ctx)
    {
        var s = 0d;
        foreach (var r in p.RowsOrEmpty)
        {
            s += r.Num("v") / Math.Max(r.Num("r"), 0.001) * 60 + r.Num("w") * 60;
            ctx.Volume += r.Num("v");
        }
        return TimeSpan.FromSeconds(s);
    }

    // ── 步骤卡摘要（原型 PSPEC[name].sum）──────────────────────────

    /// <summary>
    /// 卡片上那一行短摘要。和 Describe 是两句不同的话：
    /// Describe 是整句工艺语句，Summary 是卡片上的一行——原型两者分开写，这里照搬。
    /// </summary>
    public static string Summary(string commandId, CommandInput p) => commandId switch
    {
        RampUp or RampDown => $"{p.Str("obj")} → {F(p.Num("target"))} ℃ · {F(p.Num("rate"))} ℃/min",
        Gradient => p.RowsOrEmpty.Count == 0
            ? "未设分段"
            : $"{p.RowsOrEmpty.Count} 段曲线 · {F(p.RowsOrEmpty[0].Num("t"))}→{F(p.RowsOrEmpty[^1].Num("t"))} ℃",
        Hold => $"{F(p.Num("dur"))} min · ±{F(p.Num("tol"))} ℃",
        Reflux => $"回流 {F(p.Num("temp"))} ℃ · {F(p.Num("dur"))} min",
        JacketCtl => $"Tj = {F(p.Num("target"))} ℃ · {F(p.Num("dur"))} min",
        ReactorCtl => $"Tr = {F(p.Num("target"))} ℃ · {F(p.Num("dur"))} min",
        PassiveCool => $"自然冷却至 {F(p.Num("target"))} ℃",

        SetSpeed => $"{F(p.Num("rpm"))} rpm",
        SpeedRamp => $"{F(p.Num("from"))}→{F(p.Num("to"))} rpm · {F(p.Num("dur"))} min",
        StopStir => $"减速 {F(p.Num("ramp"))} s 停机",

        DoseRate => $"{F(p.Num("rate"))} mL/min · 共 {F(p.Num("vol"))} mL",
        DoseVolume => $"{F(p.Num("vol"))} mL / {F(p.Num("dur"))} min",
        DoseSegments => $"{p.RowsOrEmpty.Count} 段 · 共 {p.RowsOrEmpty.Sum(r => r.Num("v")):F1} mL",
        DosePh => $"维持 pH {F(p.Num("target"))} ± {F(p.Num("band"))}",

        PhSample => $"每 {F(p.Num("interval"))} s 采样",
        PhHold => $"pH {F(p.Num("target"))} ± {F(p.Num("band"))} · {F(p.Num("dur"))} min",
        PhAlarm => $"{F(p.Num("lo"))} ~ {F(p.Num("hi"))} · {p.Str("act")}",

        Raman => $"{F(p.Num("integ"))} ms · {p.Str("mode")}",
        Infrared => $"每 {F(p.Num("interval"))} s · {F(p.Num("scans"))} 次",
        Turbidity => $"每 {F(p.Num("interval"))} s · 阈值 {F(p.Num("thr"))} NTU",
        DeltaT => $"ΔT 记录 · 每 {F(p.Num("interval"))} s",
        Solubility => $"按{p.Str("by")}判定 · 阈值 {F(p.Num("thr"))}",

        _ => ""
    };
}
