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

    public static IEnumerable<Recipe> Recipes()
    {
        yield return Cooling();
        yield return Neutralize();
    }

    private static Recipe Cooling()
    {
        var r = new Recipe { Name = "降温结晶 · 介稳区", Author = "工程师" };
        r.Steps.Add(Mk("tec.stir.set", ("转速", 350d)));
        r.Steps.Add(Mk("tec.light.set", ("开关", true), ("亮度", 0.8d)));
        r.Steps.Add(Mk("tec.temp.rampTo", ("目标", 60d), ("速率", 2d), ("控温对象", "釜内 Tr"),
                         ("容差", 0.5d), ("超时", 3600d)));
        r.Steps.Add(Mk("tec.temp.hold", ("温度", 60d), ("时长", 1800d)));
        r.Steps.Add(Mk("tec.flow.loopBegin", ("方式", "按次数"), ("次数", 3d)));
        r.Steps.Add(Mk("tec.temp.coolTo", ("目标", 15d), ("速率", 0.4d), ("容差", 0.5d), ("超时", 7200d)));
        r.Steps.Add(Mk("tec.turb.waitThreshold", ("阈值", 25d), ("方向", "≥ 阈值"), ("保持", 20d),
                         ("预计", 1800d), ("超时", 10800d), ("信号失效", "停止并报警")));
        r.Steps.Add(Mk("tec.flow.mark", ("标记", "成核点")));
        r.Steps.Add(Mk("tec.temp.rampTo", ("目标", 55d), ("速率", 1.5d), ("控温对象", "釜内 Tr"),
                         ("容差", 0.5d), ("超时", 3600d)));
        r.Steps.Add(Mk("tec.flow.loopEnd"));
        r.Steps.Add(Mk("tec.temp.coolTo", ("目标", 5d), ("速率", 0.3d), ("容差", 0.5d), ("超时", 7200d)));
        r.Steps.Add(Mk("tec.flow.wait", ("时长", 3600d)));
        r.Steps.Add(Mk("tec.stir.stop"));
        r.Steps.Add(Mk("tec.temp.stop"));
        return r;
    }

    private static Recipe Neutralize()
    {
        var r = new Recipe { Name = "中和反应 · pH 反馈", Author = "工程师" };
        r.Steps.Add(Mk("tec.stir.set", ("转速", 450d)));
        r.Steps.Add(Mk("tec.temp.rampTo", ("目标", 35d), ("速率", 3d), ("控温对象", "釜内 Tr"),
                         ("容差", 0.5d), ("超时", 1800d)));
        r.Steps.Add(Mk("tec.dose.constant", ("体积", 8d), ("流量", 2d), ("物料", "底液")));
        r.Steps.Add(Mk("tec.dose.feedback", ("判据标签", "pH"), ("目标", 5.5d), ("方向", "下降至目标"),
                         ("流量", 0.5d), ("最大体积", 20d), ("预计", 1200d), ("超时", 5400d),
                         ("信号失效", "停止并报警")));
        r.Steps.Add(Mk("tec.ph.record", ("标注", "终点 pH")));
        r.Steps.Add(Mk("tec.temp.hold", ("温度", 35d), ("时长", 900d)));
        r.Steps.Add(Mk("tec.flow.prompt", ("提示", "取样送检后继续"), ("超时", 0d)));
        r.Steps.Add(Mk("tec.temp.coolTo", ("目标", 20d), ("速率", 1d), ("容差", 0.5d), ("超时", 3600d)));
        r.Steps.Add(Mk("tec.stir.stop"));
        return r;
    }

    private static Step Mk(string commandId, params (string Key, object? Value)[] p)
        => new() { CommandId = commandId, Parameters = ParameterSet.Of(p) };
}
