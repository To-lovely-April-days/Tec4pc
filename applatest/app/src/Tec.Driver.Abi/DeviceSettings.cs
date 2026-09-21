namespace Tec.Driver.Abi;

/// <summary>
/// 一台设备「参数面板」上的一组参数。用 ParameterSchema 声明，界面照 schema 渲染——
/// 跟连接参数、设备配置的表单是同一套（§4.2），驱动不用画界面，界面不用认设备。
/// </summary>
public sealed record SettingsGroup(string Id, string Title, ParameterSchema Schema)
{
    /// <summary>实时量（温度、电流、告警……）：全部只读，界面按周期刷新，不给「写入」钮。</summary>
    public bool Live { get; init; }
    public string? Tip { get; init; }
}

/// <summary>面板上的一个动作钮（自整定、恢复出厂……）。</summary>
public sealed record SettingsAction(string Id, string Label)
{
    public string? Tip { get; init; }
    /// <summary>挂在哪一组下面显示；null = 面板底部。</summary>
    public string? Group { get; init; }
    /// <summary>要人确认再做——自整定会激起温度振荡、恢复出厂会把参数全抹掉。</summary>
    public bool Confirm { get; init; }
}

/// <summary>
/// 设备参数面板：会话级的可选接口，实现了的设备在工作站里多一扇「参数」窗
/// （顶栏那个机器图标 / 属性栏「温控器参数」）。里面的东西是**设备自己的寄存器**
/// （TEC 最大功率、两路电流、PID、自整定……），不是台面配置——读的是设备此刻的值，
/// 写也是直接写设备；写完读回核对，回执里说清写了什么、拒了什么。
/// </summary>
public interface IDeviceSettings
{
    IReadOnlyList<SettingsGroup> Groups { get; }

    /// <summary>读一组的当前值（键 = FieldSpec.Key，值已换算成工程量 / 下拉项文字）。</summary>
    Task<ParameterSet> ReadAsync(string groupId, CancellationToken ct);

    /// <summary>只写 values 里有的键。返回每一项的回执：写了什么、被设备钳成什么、拒了什么。</summary>
    Task<IReadOnlyList<string>> WriteAsync(string groupId, ParameterSet values, CancellationToken ct);

    IReadOnlyList<SettingsAction> Actions { get; }

    /// <summary>做一个动作，返回一句回执。</summary>
    Task<string> RunAsync(string actionId, CancellationToken ct);
}
