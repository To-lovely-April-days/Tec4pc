using Tec.Driver.Abi;

namespace Tec.Drivers.TouchPanel;

/// <summary>
/// 双工位反应主机（不带触摸屏）。没买触摸屏的客户用：设备的串口线都插在这台 PC 上，
/// 由 PC 自己跑一份控制核心（tec_core，和触摸屏里跑的是同一个程序）去连串口、控温、把着安全；
/// 工作站和「手动控制面板」弹出来的新界面都经本机网络连它（用户 2026-11-24 定的：带不带触摸屏都是同一个界面、同一套控温）。
///
/// 所以它和带触摸屏的那台（TouchPanelDriver）只差一件事：连之前先看本机的控制核心在不在，不在就把它启动起来。
/// 连上以后的读数、能力、配方步骤认领全是同一套，直接交给 TouchPanelDriver 去做。
///
/// 串口号不在台面属性栏里填：在「手动控制面板 → 设置 → 通信设置」里配（存在控制核心自己的设置里）。
/// 台面上有了这台，就不要再摆旧版直连的那几台设备（主机、探头、搅拌、进料）——串口已经被控制核心占着。
/// </summary>
public sealed class LocalCoreDriver : IDeviceDriver
{
    public const string DriverId = "tec.panel.local";

    public static class Fields
    {
        public const string Port = "端口";
    }

    private readonly TouchPanelDriver _panel = new();

    public DriverInfo Info { get; } = new(DriverId, "双工位反应主机（不带触摸屏）", "HTLAB", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = false,
        IconKey = "rd105",      // 和旧版直连那台同一张图：物理上就是同一台主机，台面也是按这个键认「主机」的（BenchDock.IsHost）
        Description = "不带触摸屏的机型：串口线插在这台电脑上，由电脑自己跑控制核心去连设备。串口在「手动控制面板 → 设置 → 通信设置」里配。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(IStirrer), nameof(IDosing), nameof(IScalarSensor) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Num(Fields.Port, "本机端口", 5020, "", 1024, 65535, 1)
    })
    { Tip = "一般不用改。连接时电脑会自己把控制核心启动起来；串口号在「手动控制面板 → 设置 → 通信设置」里配。" };

    public ParameterSchema ConfigSchema { get; } = new(Array.Empty<FieldSpec>());

    public IReadOnlyList<CommandDescriptor> Commands => _panel.Commands;

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        var why = await LocalPrograms.EnsureCoreAsync(connection.Int(Fields.Port, 5020), ct).ConfigureAwait(false);
        if (why.Length > 0) return new ProbeResult(false, why);
        var r = await _panel.ProbeAsync(Loopback(connection), ct).ConfigureAwait(false);
        // TouchPanelDriver 的话里说的是「触摸屏」，这里没有触摸屏，换成「本机控制核心」免得人看糊涂
        return r with { Message = r.Message.Replace("触摸屏", "本机控制核心") };
    }

    public async Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
    {
        var why = await LocalPrograms.EnsureCoreAsync(connection.Int(Fields.Port, 5020), ct).ConfigureAwait(false);
        if (why.Length > 0) throw new IOException(why);
        return await _panel.OpenAsync(Loopback(connection), ctx, ct).ConfigureAwait(false);
    }

    private static ParameterSet Loopback(ParameterSet connection) => new()
    {
        [TouchPanelDriver.Fields.Host] = "127.0.0.1",
        [TouchPanelDriver.Fields.Port] = connection.Int(Fields.Port, 5020)
    };
}
