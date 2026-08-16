using TecControl.Core.Models;
using Tec.Driver.Abi;

namespace Tec.Drivers.Rd105;

/// <summary>
/// 一台 RD105 温控器 = 台面上的一个反应通道。
/// TC1 测釜内 Tr，TC2 测夹套 Tj——「一个通道有两个温度」说的就是这个。
///
/// 控温交给温控器自己的 PID（TG 目标 + SPEED 速率 + MODE），上位机只下发目标、
/// 收数据、判到达。TecControl.Core 里那套主机侧串级（HostControlLoop）等
/// 整定策略定下来再接——那涉及一堆现场标定出来的增益，不该由这里替它决定。
/// </summary>
internal sealed class Rd105Session : IDeviceSession
{
    private readonly Rd105Link _link;
    private readonly DriverContext _ctx;
    private readonly Broadcast<Sample> _out = new();
    private readonly Rd105TemperatureControl _temp;
    private readonly TimeSpan _period;
    private DeviceState _state = DeviceState.Connected;

    public Rd105Session(Rd105Link link, DriverContext ctx, ParameterSet connection)
    {
        _link = link;
        _ctx = ctx;
        _period = TimeSpan.FromMilliseconds(
            Math.Clamp(connection.Num(Rd105TecDriver.FieldPeriod, 500), 200, 5000));

        var ch = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers[0] : 0;
        _temp = new Rd105TemperatureControl(ch, link, ctx.Config, _out);

        _link.Controller.SnapshotReceived += OnSnapshot;
        _link.Controller.PollFaulted += OnFaulted;
    }

    public string InstanceId => _ctx.InstanceId;

    public DeviceState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<DeviceState>? StateChanged;

    public IObservable<Sample> Samples => _out;

    /// <summary>一台温控器只带一个反应通道。</summary>
    public int WellCount => 1;

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("Tr", "釜内温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) },
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) },
        new TagDescriptor("dT", "Tr−Tj 温差", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-60, 60) },
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 150) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well == 0 ? new ICapability[] { _temp } : Array.Empty<ICapability>();

    public ICommandHandler? Resolve(string commandId) => _temp.Resolve(commandId);

    /// <summary>把保护值写进设备。OpenAsync 里调，早于任何控温动作。</summary>
    public Task ApplyProtectionAsync(CancellationToken ct) => _temp.ApplyProtectionAsync(ct);

    public Task StartAsync(CancellationToken ct)
    {
        _link.Open();
        _link.Controller.StartPolling(_period);
        State = DeviceState.Ready;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _link.Controller.StopPolling();
        State = DeviceState.Connected;
        return Task.CompletedTask;
    }

    private void OnSnapshot(TecSnapshot s)
    {
        var at = DateTimeOffset.Now;
        _temp.Observe(s.Temp1C, s.Temp2C);

        // NaN = 该路没接传感器。宁可不发，也不要往曲线里塞一个假读数
        Push("Tr", s.Temp1C, at);
        Push("Tj", s.Temp2C, at);
        if (!double.IsNaN(s.Temp1C) && !double.IsNaN(s.Temp2C))
            Push("dT", s.Temp1C - s.Temp2C, at);
        if (_temp.Setpoint is { } sp) Push("Tset", sp, at);
    }

    private void Push(string tag, double value, DateTimeOffset at)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(_temp.Channel, tag, at.UtcTicks, at, value, Quality.Good));
    }

    /// <summary>
    /// 轮询出错不终止轮询（TecController 自己会接着转），但要把设备标成故障——
    /// 界面上得看得出这台机器现在的数据不可信。
    /// </summary>
    private void OnFaulted(Exception ex)
    {
        _ctx.Log?.Invoke("error", $"{InstanceId} 轮询异常：{ex.Message}");
        State = DeviceState.Faulted;
    }

    public async ValueTask DisposeAsync()
    {
        _link.Controller.SnapshotReceived -= OnSnapshot;
        _link.Controller.PollFaulted -= OnFaulted;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        _link.Dispose();
        State = DeviceState.Disposed;
    }
}
