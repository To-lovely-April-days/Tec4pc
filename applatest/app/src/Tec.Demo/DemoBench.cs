using Tec.Core.Benches;
using Tec.Core.Persistence;
using Tec.Driver.Abi;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】演示台面：一台双工位反应主机 + 两支釜内 Tr 探头 + 两台搅拌驱动器 + 两支数字 pH 电极 + 一套两液一气进料系统，
/// 全部接到仿真驱动上，设备名与串口号按现场填（COM7 / COM8 / COM3 / COM14 / COM11 / COM12 / COM13 / COM15）。
///
/// WHY：台面页截图要跟交付现场一样——设备数、名字、停靠位置、串口都照现场；但背后开的是仿真会话，
/// 所以驱动号是 sim.*。停靠（DockHostId / DockAnchor）在这里定好，画布坐标由工作站按主机位置算
/// （插入件的位置依赖设备图的 viewBox，归 Tec.App 的 BenchDock）；HMI 不画台面，直接用。
/// </summary>
public static class DemoBench
{
    public const string Name = "双工位反应主机 · 演示台面";

    public const string Host = "R1", Tr1 = "TR1", Tr2 = "TR2", Ph1 = "PH1", Ph2 = "PH2",
                        Stir1 = "ST1", Stir2 = "ST2", Feed = "FD1";

    /// <summary>主机在画布上的位置（世界坐标）。其余插入件由工作站按它算。</summary>
    public const double HostX = 300, HostY = 120;

    public static BenchDoc Build()
    {
        var doc = new BenchDoc { Name = Name };
        doc.Devices.Add(new DeviceDoc
        {
            DriverId = DemoDrivers.Reactor, InstanceId = Host, Label = "双工位反应主机 HT-RS2",
            X = HostX, Y = HostY,
            Connection = ParameterSet.Of(("端口", "COM7"), ("波特率", "38400"), ("校验", "无"), ("站号", 1d),
                                         ("IO8R串口", "COM8"), ("温度模块串口", "COM3")),
            Config = ParameterSet.Of(("釜规格", "250 mL"), ("釜材质", "玻璃"), ("搅拌桨", "锚式"), ("温度探头", "Pt100 四线"),
                                     // 料液沸点 77 ℃：通道 1 的酯化回流在乙酸乙酯沸点附近建立
                                     ("沸点", 77d), ("自带搅拌", "无"),
                                     ("电加热切换", "有"), ("TEC加热", "不启用"), ("电加热切换阈值", 90d),
                                     ("回切滞回", 5d), ("热源死区", 2d), ("切换反馈", "有"))
        });
        Probe(doc, DemoDrivers.TrProbe, Tr1, "宇电 Tr 探头 · 工位 A", "S1TR", 0, ("串口", "COM3"), ("地址", 1d), ("接线", "四线"));
        Probe(doc, DemoDrivers.TrProbe, Tr2, "宇电 Tr 探头 · 工位 B", "S2TR", 1, ("串口", "COM3"), ("地址", 2d), ("接线", "四线"));
        Probe(doc, DemoDrivers.PhProbe, Ph1, "数字 pH 电极 · 工位 A", "S1PH", 0, ("串口", "COM12"), ("地址", 1d));
        Probe(doc, DemoDrivers.PhProbe, Ph2, "数字 pH 电极 · 工位 B", "S2PH", 1, ("串口", "COM13"), ("地址", 1d));
        Probe(doc, DemoDrivers.Stirrer, Stir1, "搅拌驱动器 · 工位 A", "S1ST", 0, ("串口", "COM14"), ("站号", 1d), ("波特率", "9600"));
        Probe(doc, DemoDrivers.Stirrer, Stir2, "搅拌驱动器 · 工位 B", "S2ST", 1, ("串口", "COM11"), ("站号", 1d), ("波特率", "9600"));
        foreach (var d in doc.Devices.Where(d => d.DriverId == DemoDrivers.Stirrer))
            d.Config = ParameterSet.Of(("转速上限", 1000d), ("电机极数", 4d), ("电机额定电流", 2.5d));

        // 进料系统接在工位 1 的加料口上（管子画到釜盖），泵 1 归通道 1、泵 2 归通道 2——两路共享这一台
        doc.Devices.Add(new DeviceDoc
        {
            DriverId = DemoDrivers.Feed, InstanceId = Feed, Label = "两液一气进料系统 HTLAB",
            X = HostX - 190, Y = HostY + 80,
            DockHostId = Host, DockAnchor = "S1FD", DockSlot = 0, DockSideTag = "L", Dock = DockSide.Left,
            Connection = ParameterSet.Of(("串口", "COM15"), ("站号", 1d), ("波特率", "9600")),
            Config = ParameterSet.Of(("配方加料泵", "泵 1"), ("泵最大流量", 50d), ("单次最大体积", 500d),
                                     ("泵 1 料液", "无水乙醇"), ("泵 2 料液", "盐酸 1 mol/L"), ("气体流量", 0.2d))
        });

        doc.Bindings.Add(new BindingDoc { DeviceId = Tr1, Channel = 1 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Tr2, Channel = 2 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Ph1, Channel = 1 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Ph2, Channel = 2 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Stir1, Channel = 1 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Stir2, Channel = 2 });
        doc.Bindings.Add(new BindingDoc { DeviceId = Feed, Channel = 1, Mode = BindingMode.Shared });
        doc.Bindings.Add(new BindingDoc { DeviceId = Feed, Channel = 2, Mode = BindingMode.Shared, Port = 1 });
        doc.Stations.Add(new StationDoc { Name = "工位 A", Channels = { 1 } });
        doc.Stations.Add(new StationDoc { Name = "工位 B", Channels = { 2 } });
        return doc;
    }

    private static void Probe(BenchDoc doc, string driver, string id, string label, string anchor, int slot,
                              params (string Key, object? Value)[] connection)
        => doc.Devices.Add(new DeviceDoc
        {
            DriverId = driver, InstanceId = id, Label = label,
            X = HostX + (slot == 1 ? 200 : 0), Y = HostY,
            DockHostId = Host, DockAnchor = anchor, DockSlot = slot, Dock = DockSide.Top,
            Connection = ParameterSet.Of(connection)
        });
}
