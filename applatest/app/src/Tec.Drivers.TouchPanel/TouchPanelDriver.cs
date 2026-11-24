using System.Text.Json.Nodes;
using Tec.Driver.Abi;

namespace Tec.Drivers.TouchPanel;

/// <summary>
/// 双工位反应主机（带触摸屏）。给买了触摸屏的客户用：设备都接在触摸屏上，由触摸屏自己控温、自己把着安全，
/// 工作站不开任何串口，只经网络连过去看数据、下命令。
///
/// 没有触摸屏的客户仍旧用原来那几台直连串口的设备（双工位反应主机、探头、搅拌、进料系统）——两种连法并存
/// （用户 2026-11-24 定的）。**同一个台面上两种不能混着摆**：串口已经被触摸屏占着，工作站再去开会抢口。
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

    // 名字是用户 2026-11-24 定的：设备库里同一台主机两个版本，「（带触摸屏）」是这台，「（不带触摸屏）」是直连串口的 DualStationDriver
    public DriverInfo Info { get; } = new(DriverId, "双工位反应主机（带触摸屏）", "HTLAB", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "rd105",      // 和旧版直连那台同一张图：物理上就是同一台主机，台面也是按这个键认「主机」的（BenchDock.IsHost）
        Description = "带触摸屏的机型：设备接在触摸屏上，工作站经网络连触摸屏。串口参数在触摸屏的「设置 → 通信设置」里配，这里只填触摸屏的地址和端口。",
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
}
