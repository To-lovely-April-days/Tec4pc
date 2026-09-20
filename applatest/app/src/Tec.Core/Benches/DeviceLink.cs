using Tec.Driver.Abi;

namespace Tec.Core.Benches;

/// <summary>
/// 一台设备此刻的链路状态怎么说。台面属性栏、HMI 头卡、HMI 系统页念的是同一句——
/// 三处各写一遍早晚对不上。
///
/// 「已连接」只在会话真开着（串口开了、开机握手过了）时才说；打不开就把驱动
/// 报的原因原话带出来，不写「未连接」三个字让人猜。设备自己报故障
/// （超温停机、轮询无应答）也照实说——链路通着但数据不可信，跟没连上不是一回事。
/// </summary>
public static class DeviceLink
{
    public static (string Text, bool Ok) Describe(IDeviceSession? session, string? openFailure)
    {
        if (session is null)
            return (string.IsNullOrWhiteSpace(openFailure) ? "未连接" : $"未连接：{openFailure}", false);

        return session.State switch
        {
            DeviceState.Ready or DeviceState.Connected => ("已连接", true),
            DeviceState.Faulted => ("已连接 · 设备报故障（详见系统日志）", false),
            DeviceState.Disposed => ("已断开", false),
            _ => ("连接中", false)
        };
    }
}
