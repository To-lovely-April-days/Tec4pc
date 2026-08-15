using Tec.Core.Benches;
using Tec.Core.Catalog;
using Tec.Core.Execution;
using Tec.Core.Recipes;
using Tec.Driver.Abi;
using Tec.Drivers.Simulator;

namespace Tec.App.Services;

/// <summary>
/// 开箱即用的一套台面与配方。整机是固定构型：2 台双通道反应器 + 2 套加料 + 探头。
/// 配件（釜、搅拌桨、温度探头）是整机固定件，只在属性面板里选型，不上台面。
/// </summary>
public static class DemoBench
{
    public static void Fill(Bench bench)
    {
        bench.Devices.Clear();
        bench.Bindings.Clear();
        bench.Stations.Clear();

        bench.Devices.Add(new DeviceInstance
        {
            DriverId = Rd105ReactorDriver.DriverId,
            InstanceId = "R1",
            Label = "反应器 R1",
            Position = new Point(90, 90),
            Connection = ParameterSet.Of(("端口", "COM3"), ("波特率", "115200"), ("校验", "无"), ("站号", 1d)),
            Config = ParameterSet.Of(("釜规格", "100 mL"), ("釜材质", "玻璃"), ("搅拌桨", "锚式"), ("温度探头", "Pt100 四线"))
        });
        bench.Devices.Add(new DeviceInstance
        {
            DriverId = Rd105ReactorDriver.DriverId,
            InstanceId = "R2",
            Label = "反应器 R2",
            Position = new Point(330, 90),
            Connection = ParameterSet.Of(("端口", "COM3"), ("波特率", "115200"), ("校验", "无"), ("站号", 2d)),
            Config = ParameterSet.Of(("釜规格", "100 mL"), ("釜材质", "玻璃"), ("搅拌桨", "锚式"), ("温度探头", "Pt100 四线"))
        });

        bench.Devices.Add(new DeviceInstance
        {
            DriverId = DosingPumpDriver.DriverId,
            InstanceId = "P1",
            Label = "加料泵 P1",
            Position = new Point(90, 280),
            Connection = ParameterSet.Of(("端口", "COM4"), ("波特率", "9600"), ("站号", 2d)),
            Config = ParameterSet.Of(("注射器", "10 mL"), ("管路内径", 1.6d), ("物料", "稀盐酸"), ("标定系数", 0.125d))
        });
        bench.Devices.Add(new DeviceInstance
        {
            DriverId = DosingPumpDriver.DriverId,
            InstanceId = "P2",
            Label = "加料泵 P2",
            Position = new Point(330, 280),
            Connection = ParameterSet.Of(("端口", "COM4"), ("波特率", "9600"), ("站号", 3d)),
            Config = ParameterSet.Of(("注射器", "25 mL"), ("管路内径", 1.6d), ("物料", "乙醇"), ("标定系数", 0.125d))
        });

        bench.Devices.Add(new DeviceInstance
        {
            DriverId = PhProbeDriver.DriverId,
            InstanceId = "PH1",
            Label = "pH 探头",
            Position = new Point(560, 90),
            Connection = ParameterSet.Of(("接入方式", "Modbus TCP"), ("地址", "192.168.1.50:502"), ("点位", "40001"),
                                         ("采样周期", 2d), ("时间戳来源", "本机接收时刻")),
            Config = ParameterSet.Of(("量程下限", 0d), ("量程上限", 14d), ("失效判定", 30d))
        });
        bench.Devices.Add(new DeviceInstance
        {
            DriverId = TurbidityProbeDriver.DriverId,
            InstanceId = "TU1",
            Label = "浊度探头",
            Position = new Point(560, 230),
            Connection = ParameterSet.Of(("接入方式", "厂商 SDK"), ("地址", "USB0"), ("点位", "NTU"),
                                         ("采样周期", 2d), ("时间戳来源", "仪器自带时间戳")),
            Config = ParameterSet.Of(("量程下限", 0d), ("量程上限", 500d), ("失效判定", 30d))
        });

        // 2 套加料系统对 4 个通道——共享，执行期排队
        bench.Bindings.Add(new Binding("P1", 1, BindingMode.Shared));
        bench.Bindings.Add(new Binding("P1", 2, BindingMode.Shared));
        bench.Bindings.Add(new Binding("P2", 3, BindingMode.Shared));
        bench.Bindings.Add(new Binding("P2", 4, BindingMode.Shared));
        bench.Bindings.Add(new Binding("PH1", 1));
        bench.Bindings.Add(new Binding("TU1", 2));

        var a = new Station { Name = "工位 A" };
        a.Channels.AddRange(new[] { 1, 2 });
        var b = new Station { Name = "工位 B" };
        b.Channels.AddRange(new[] { 3, 4 });
        bench.Stations.Add(a);
        bench.Stations.Add(b);
    }

    /// <summary>
    /// 哪条指令要占哪台泵。台面知道，Core 不猜（§7.4）。
    /// CH1/CH2 共用 P1，CH3/CH4 共用 P2——这就是 2 泵 4 通道的真问题。
    /// </summary>
    public static ResourceNeed? ResourceOf(int channel, string commandId)
        => commandId.StartsWith("tec.dose.", StringComparison.Ordinal)
            ? new ResourceNeed(channel <= 2 ? "P1" : "P2", ResourcePolicy.Queue)
            : null;

    /// <summary>
    /// 预置配方按原型 recipes 的构型来：CH1 降温结晶、CH2 pH 反馈加料。
    /// 参数键、默认值与原型 PSPEC 一致，改这里之前先改原型。
    /// </summary>
    public static IEnumerable<Recipe> Recipes()
    {
        yield return Cooling();
        yield return Neutralize();
    }

    /// <summary>
    /// 配方库六条，逐条对应原型 RECIPELIB：名称、描述、更新日期、步骤与参数差异都照抄。
    /// 建步骤的方式也跟原型 instantiate 一致——规格默认值打底，再套库里保存的差异，
    /// 这样库配方与手工新建的步骤参数完全同源。
    /// </summary>
    public static IEnumerable<Recipe> Library(CommandCatalog catalog)
    {
        Step S(string id, params (string Key, object? Value)[] ov)
        {
            var p = new ParameterSet();
            List<ParameterSet>? rows = null;
            if (catalog.TryGet(id, out var d))
            {
                p = p.FillDefaults(d.Parameters);
                if (d.Parameters.Table is not null) rows = DefaultRowsOf(id);
            }
            foreach (var (k, v) in ov) p = p.With(k, v);
            return new Step { CommandId = id, Parameters = p, Rows = rows };
        }

        // 更新日期用原型的 MM/dd，年份取当年——库列表只显示到月日
        Recipe R(string name, string desc, string updated, params Step[] steps)
        {
            var md = updated.Split('/');
            var r = new Recipe
            {
                Name = name,
                Notes = desc,
                Author = "工程师",
                ModifiedAt = new DateTimeOffset(
                    new DateTime(DateTime.Now.Year, int.Parse(md[0]), int.Parse(md[1]), 9, 0, 0),
                    DateTimeOffset.Now.Offset)
            };
            r.Steps.AddRange(steps);
            return r;
        }

        yield return R("降温结晶_梯度筛选", "升温溶解 → 恒温 → 梯度降温结晶，拉曼跟踪晶型", "08/14",
            S(CommandSpecs.SetSpeed, ("rpm", 400d)),
            S(CommandSpecs.RampUp, ("target", 60d), ("rate", 2d)),
            S(CommandSpecs.Hold, ("dur", 30d), ("tol", 0.1d)),
            GradientOf(catalog, (50d, 0.5d, 10d), (30d, 0.2d, 20d), (5d, 0.1d, 30d)),
            S(CommandSpecs.Raman, ("integ", 500d), ("mode", "连续")),
            S(BuiltinCommands.Finish));

        yield return R("硝化_控温加料", "低温恒速滴加，Tr−Tj 放热监控，超温联锁", "08/12",
            S(CommandSpecs.SetSpeed, ("rpm", 500d)),
            S(CommandSpecs.RampDown, ("target", 5d), ("rate", 1d)),
            S(BuiltinCommands.Interlock, ("src", "釜内 Tr"), ("op", ">"), ("val", 15d), ("act", "停止加料")),
            S(CommandSpecs.DoseRate, ("rate", 0.3d), ("vol", 20d), ("liq", "硝酸 65%")),
            S(CommandSpecs.DeltaT, ("interval", 1d)),
            S(CommandSpecs.Hold, ("dur", 60d), ("tol", 0.1d)),
            S(BuiltinCommands.Finish));

        yield return R("溶解度曲线_自动", "循环升温溶清判定，浊度确定溶解点，自动记录", "08/10",
            S(CommandSpecs.SetSpeed, ("rpm", 300d)),
            S(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 8d)),
            S(CommandSpecs.RampUp, ("target", 60d), ("rate", 0.3d)),
            S(CommandSpecs.Turbidity, ("interval", 1d), ("thr", 5d)),
            S(CommandSpecs.Solubility, ("by", "浊度"), ("thr", 5d)),
            S(BuiltinCommands.LoopEnd),
            S(CommandSpecs.PassiveCool, ("target", 25d)),
            S(BuiltinCommands.Finish));

        yield return R("介稳区_自动测定", "循环降温-升温，浊度判定成核点与溶清点", "08/06",
            S(CommandSpecs.SetSpeed, ("rpm", 400d)),
            S(BuiltinCommands.LoopBegin, ("by", "按次数"), ("n", 5d)),
            S(CommandSpecs.RampDown, ("target", 0d), ("rate", 0.5d)),
            S(CommandSpecs.Turbidity, ("interval", 1d), ("thr", 50d)),
            S(CommandSpecs.RampUp, ("target", 60d), ("rate", 0.3d)),
            S(BuiltinCommands.LoopEnd),
            S(BuiltinCommands.Finish));

        yield return R("pH恒定_反应加料", "pH 反馈加料维持恒定，无人值守", "08/02",
            S(CommandSpecs.SetSpeed, ("rpm", 350d)),
            S(CommandSpecs.RampUp, ("target", 40d), ("rate", 2d)),
            S(CommandSpecs.DosePh, ("target", 7d), ("band", 0.2d), ("dur", 120d)),
            S(CommandSpecs.PhAlarm, ("lo", 6.5d), ("hi", 7.5d)),
            S(CommandSpecs.Hold, ("dur", 120d), ("tol", 0.1d)),
            S(BuiltinCommands.Finish));

        yield return R("分段反溶剂_结晶", "三段递减流量加入反溶剂，段间静置成核", "07/28",
            S(CommandSpecs.SetSpeed, ("rpm", 450d)),
            S(CommandSpecs.Hold, ("dur", 15d), ("tol", 0.1d)),
            SegmentsOf(catalog, "反溶剂", (5d, 1d, 5d), (5d, 0.5d, 10d), (5d, 0.2d, 20d)),
            S(CommandSpecs.Turbidity, ("interval", 1d), ("thr", 50d)),
            S(CommandSpecs.RampDown, ("target", 10d), ("rate", 0.3d)),
            S(BuiltinCommands.Finish));
    }

    private static Step GradientOf(CommandCatalog catalog, params (double T, double R, double H)[] rows)
    {
        var p = new ParameterSet();
        if (catalog.TryGet(CommandSpecs.Gradient, out var d)) p = p.FillDefaults(d.Parameters);
        return new Step
        {
            CommandId = CommandSpecs.Gradient,
            Parameters = p,
            Rows = rows.Select(r => ParameterSet.Of(("t", r.T), ("r", r.R), ("h", r.H))).ToList()
        };
    }

    private static Step SegmentsOf(CommandCatalog catalog, string liquid,
                                   params (double V, double R, double W)[] rows)
    {
        var p = new ParameterSet();
        if (catalog.TryGet(CommandSpecs.DoseSegments, out var d)) p = p.FillDefaults(d.Parameters);
        return new Step
        {
            CommandId = CommandSpecs.DoseSegments,
            Parameters = p.With("liq", liquid),
            Rows = rows.Select(r => ParameterSet.Of(("v", r.V), ("r", r.R), ("w", r.W))).ToList()
        };
    }

    /// <summary>带分段表的指令的默认行（与原型 PSPEC.rows 一致）。</summary>
    private static List<ParameterSet>? DefaultRowsOf(string id) => id switch
    {
        CommandSpecs.Gradient => new List<ParameterSet>
        {
            ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)),
            ParameterSet.Of(("t", 30d), ("r", 0.3d), ("h", 20d)),
            ParameterSet.Of(("t", 5d), ("r", 0.1d), ("h", 30d))
        },
        CommandSpecs.DoseSegments => new List<ParameterSet>
        {
            ParameterSet.Of(("v", 5d), ("r", 1d), ("w", 5d)),
            ParameterSet.Of(("v", 5d), ("r", 0.5d), ("w", 10d)),
            ParameterSet.Of(("v", 5d), ("r", 0.2d), ("w", 20d))
        },
        _ => null
    };

    private static Recipe Cooling()
    {
        var r = new Recipe { Name = "降温结晶", Author = "工程师" };
        r.Steps.Add(Mk(CommandSpecs.SetSpeed, ("rpm", 400d), ("ramp", 5d)));
        r.Steps.Add(Mk(CommandSpecs.RampUp, ("target", 60d), ("rate", 2d),
                       ("obj", "釜内 Tr"), ("tol", 0.5d), ("wait", true)));
        r.Steps.Add(Mk(CommandSpecs.Hold, ("dur", 30d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        r.Steps.Add(Mk(CommandSpecs.DoseRate, ("pump", "加料泵 1"), ("liq", "反溶剂"),
                       ("rate", 0.5d), ("vol", 10d), ("sync", true)));
        r.Steps.Add(Gradient());
        r.Steps.Add(Mk(CommandSpecs.Raman, ("integ", 500d), ("avg", 3d), ("mode", "连续"), ("interval", 30d)));
        r.Steps.Add(Mk(BuiltinCommands.Finish, ("cool", true), ("safe", 30d), ("stir", true)));
        return r;
    }

    private static Recipe Neutralize()
    {
        var r = new Recipe { Name = "pH 反馈加料", Author = "工程师" };
        r.Steps.Add(Mk(CommandSpecs.SetSpeed, ("rpm", 400d), ("ramp", 5d)));
        r.Steps.Add(Mk(CommandSpecs.RampUp, ("target", 60d), ("rate", 2d),
                       ("obj", "釜内 Tr"), ("tol", 0.5d), ("wait", true)));
        r.Steps.Add(Mk(CommandSpecs.DosePh, ("target", 7d), ("band", 0.2d), ("pump", "加料泵 2"),
                       ("maxRate", 1d), ("maxVol", 50d), ("dur", 60d)));
        r.Steps.Add(Mk(CommandSpecs.Hold, ("dur", 30d), ("tol", 0.1d), ("obj", "釜内 Tr")));
        r.Steps.Add(Mk(CommandSpecs.PassiveCool, ("target", 25d), ("timeout", 120d)));
        r.Steps.Add(Mk(BuiltinCommands.Finish, ("cool", true), ("safe", 30d), ("stir", true)));
        return r;
    }

    /// <summary>梯度控温带分段表：3 段，与原型 PSPEC 的默认 rows 一致。</summary>
    private static Step Gradient() => new()
    {
        CommandId = CommandSpecs.Gradient,
        Parameters = ParameterSet.Of(("obj", "釜内 Tr"), ("loop", false)),
        Rows = new List<ParameterSet>
        {
            ParameterSet.Of(("t", 60d), ("r", 1d), ("h", 10d)),
            ParameterSet.Of(("t", 30d), ("r", 0.3d), ("h", 20d)),
            ParameterSet.Of(("t", 5d), ("r", 0.1d), ("h", 30d))
        }
    };

    private static Step Mk(string commandId, params (string Key, object? Value)[] p)
        => new() { CommandId = commandId, Parameters = ParameterSet.Of(p) };
}
