namespace Tec.App.Services;

/// <summary>
/// 仿真 ⇄ 真机的孪生对应表。属性栏那个「模拟」勾选框靠它换身份：
/// 勾了走仿真驱动，去了勾走真机驱动——同一台台面设备、两副驱动身份，
/// 连接表单、测试连接、会话全部跟着当前身份走。
///
/// 只有成对的设备才在表里。探头类不在：它们本来就不自己产数
/// （或按模拟位闭嘴），真机上的数值由主机端出来；加料泵也不在：
/// 它还没有真机驱动，去勾会被如实拒绝，而不是悄悄继续仿真。
/// </summary>
public static class SimRealTwins
{
    public static string? RealOf(string driverId) => driverId switch
    {
        Tec.Drivers.Simulator.Rd105ReactorDriver.DriverId => Tec.Drivers.DualStation.DualStationDriver.DriverId,
        _ => null
    };

    public static string? SimOf(string driverId) => driverId switch
    {
        Tec.Drivers.DualStation.DualStationDriver.DriverId => Tec.Drivers.Simulator.Rd105ReactorDriver.DriverId,
        _ => null
    };
}
