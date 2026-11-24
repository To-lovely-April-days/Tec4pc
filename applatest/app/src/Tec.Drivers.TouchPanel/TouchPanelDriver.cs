using System.Text.Json.Nodes;
using Tec.Driver.Abi;

namespace Tec.Drivers.TouchPanel;

/// <summary>
/// 双工位反应主机。机器自带触摸屏，设备都接在触摸屏上，由触摸屏自己控温、跑序列、把着安全；
/// 工作站不开任何串口，只经网络连过去看数据、下命令（用户 2026-11-25 定的：所有机器都带触摸屏，不再有直连的机型）。
///
/// 原来那套「工作站自己开串口控温」的驱动（DualStationDriver）代码还在、还能打开老台面，但设备库里不再有，
/// 拖不出新的。**台面上有了这台，就不要再摆探头、搅拌、进料那几台直连设备**：串口已经被触摸屏占着，工作站再去开会抢口。
///
/// 对上面它就是一台两个工位的主机，每个工位给出控温、蒸回流、搅拌、加料、pH 这几样能力；
/// 配方里的温控 / 搅拌 / 加料步骤由它认领，执行时一步一步把命令发给触摸屏（配方还是工作站自己在跑）。
/// </summary>
public sealed class TouchPanelDriver : IDeviceDriver
{
    public const string DriverId = "tec.panel.tcp";

    public static class Fields
    {
        public const string Host = "地址";
        public const string Port = "端口";
    }

    public const string TagTr = "Tr", TagTj = "Tj", TagDt = "dT", TagTset = "Tset", TagDuty = "duty", TagTjSet = "Tjset",
                        TagPh = "pH", TagRpm = "rpm", TagVol = "vol";

    // 设备库里只有这一台主机了（用户 2026-11-25：都带触摸屏），所以名字不用再加后缀
    public DriverInfo Info { get; } = new(DriverId, "双工位反应主机", "HTLAB", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "rd105",      // 和旧版直连那台同一张图：物理上就是同一台主机，台面也是按这个键认「主机」的（BenchDock.IsHost）
        Description = "设备接在机器自带的触摸屏上，工作站经网络连触摸屏。串口参数在触摸屏的「设置 → 通信设置」里配，这里只填触摸屏的地址和端口。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(IStirrer), nameof(IDosing), nameof(IScalarSensor) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Text(Fields.Host, "触摸屏地址", "192.168.1.50", "触摸屏「设置 → 通信设置」里的本机地址"),
        Field.Num(Fields.Port, "端口", 5020, "", 1024, 65535, 1)
    })
    { Tip = "地址和端口照触摸屏「设置 → 通信设置 → 对外通信」里的填。那一页的「允许远程控制」关着时，工作站只能看、不能下命令。" };

    public ParameterSchema ConfigSchema { get; } = new(Array.Empty<FieldSpec>());

    public IReadOnlyList<CommandDescriptor> Commands { get; } =
        CommandSpecs.Temperature.Concat(CommandSpecs.Stirring).Concat(CommandSpecs.Dosing).ToArray();

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await using var client = ClientOf(connection);
        try
        {
            var info = await client.AskAsync("sys.info", ct).ConfigureAwait(false);
            var state = await client.AskAsync("state", ct).ConfigureAwait(false);
            var offline = state["offline"]?.GetValue<string>() ?? "";
            var sim = info["sim"]?.GetValue<bool>() == true;
            var text = $"已连上触摸屏 {client.Endpoint}" + (sim ? "（仿真模式，没有连真设备）" : "") +
                       (offline.Length > 0 ? $"；但触摸屏报告：{offline}" : "");
            return new ProbeResult(true, text) { Firmware = info["version"]?.GetValue<string>(), DetectedChannels = 2 };
        }
        catch (Exception ex) when (ex is IOException or PanelRefusedException)
        {
            return new ProbeResult(false, ex.Message);
        }
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var session = new PanelSession(ClientOf(connection), ctx);
        try
        {
            await session.InitAsync(ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static PanelClient ClientOf(ParameterSet connection)
        => new(connection.Str(Fields.Host, "127.0.0.1").Trim(), connection.Int(Fields.Port, 5020));

    /// <summary>
    /// 这台设备的「手动控制面板」该连哪里。不是这个机型就回 null（老台面上那台直连的，还开原来那扇窗）。
    /// </summary>
    public static (string Host, int Port)? PanelEndpointOf(string driverId, ParameterSet connection)
        => driverId == DriverId ? (connection.Str(Fields.Host, "127.0.0.1").Trim(), connection.Int(Fields.Port, 5020)) : null;
}
