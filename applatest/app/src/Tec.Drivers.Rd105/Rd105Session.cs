using TecControl.Core.Control;
using TecControl.Core.Models;
using TecControl.Core.Protocol;
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
    private readonly HostControlLoop _loop;
    private readonly Rd105Tuning _tuning;
    private readonly TimeSpan _period;
    private DeviceState _state = DeviceState.Connected;
    private TecErrorCode _fault = TecErrorCode.None;
    private int _dutyBusy;
    private long _dutyAt;

    /// <summary>控温回路挂在温控器的 TC1 上（釜内 Tr），输出占空比也读它这一路。</summary>
    private const int DutyTc = 1;

    public Rd105Session(Rd105Link link, DriverContext ctx, ParameterSet connection)
    {
        _link = link;
        _ctx = ctx;
        _period = TimeSpan.FromMilliseconds(
            Math.Clamp(connection.Num(Rd105TecDriver.FieldPeriod, 500), 200, 5000));

        var ch = ctx.ChannelNumbers.Count > 0 ? ctx.ChannelNumbers[0] : 0;
        _temp = new Rd105TemperatureControl(ch, link, ctx.Config, _out);

        // 主机侧控制环：串级（Tr 外环 / Tj 内环）、继电器法自整定、增益调度都在它里面。
        // 这里只建不启——控温照旧走温控器自己的 PID，只有整定与串级才用得上它，
        // 免得没整定过的现场一上来就被主机环接管。
        _loop = new HostControlLoop(link.Controller) { Period = _period };
        _tuning = new Rd105Tuning(ch, _loop, (lvl, text) => ctx.Log?.Invoke(lvl, text));

        _link.Controller.SnapshotReceived += OnSnapshot;
        _link.Controller.ErrorCodeReceived += OnErrorCode;
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
            { Nominal = new ValueRange(-40, 150) },
        // 控温输出（PWMDUTY，±100 %，正加热负制冷）。放大时最要紧的问题是
        // 「夹套已经满功率还压不住放热」，没有这一路看不出来
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100) },
        // 设备告警字。安全层盯着它：非 0 即告警，> 0 就该动作。
        // 发成一路采样而不是另开一条通道，是因为安全层本来就是按采样求值的，
        // 顺带还能进记录、能画在时间轴上——告警什么时候出现的一目了然
        new TagDescriptor("fault", "设备告警字", "", DataShape.State)
            { Nominal = new ValueRange(0, 0) }
    };

    public IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well == 0 ? new ICapability[] { _temp, _tuning } : Array.Empty<ICapability>();

    public ICommandHandler? Resolve(string commandId) => _temp.Resolve(commandId);

    /// <summary>把保护值写进设备。OpenAsync 里调，早于任何控温动作。</summary>
    public Task ApplyProtectionAsync(CancellationToken ct) => _temp.ApplyProtectionAsync(ct);

    public async Task StartAsync(CancellationToken ct)
    {
        _link.Open();

        // 先读一次告警字再开轮询。轮询循环是「先取快照、后读告警」，
        // 不先读的话第一帧温度是在不知道有没有告警的情况下发出去的——
        // 传感器已经越限了却发成 Good，安全层就漏掉了第一拍。
        try { OnErrorCode(await _link.Controller.ReadErrorCodeAsync(ct).ConfigureAwait(false)); }
        catch (Exception ex) { _ctx.Log?.Invoke("warn", $"{InstanceId} 初次读告警字失败：{ex.Message}"); }

        _link.Controller.StartPolling(_period);
        if (State != DeviceState.Faulted) State = DeviceState.Ready;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _link.Controller.StopPolling();
        State = DeviceState.Connected;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 中止收尾：**关加热输出，轮询照旧。**
    ///
    /// 关输出是这台温控器唯一该做也做得到的事——它只管温度，没有搅拌、没有泵。
    /// 轮询不停：釜里还是热的，停下来之后那段自然降温曲线照样要记，
    /// 不然记录在最需要它的那一刻断了。
    /// </summary>
    public async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well != 0) return Array.Empty<string>();
        await _temp.StopAsync(ct).ConfigureAwait(false);
        return new[] { $"已关闭加热输出（停在 {_temp.CurrentReactor:F1} ℃，此后自然冷却；采集不停）" };
    }

    private void OnSnapshot(TecSnapshot s)
    {
        var at = DateTimeOffset.Now;
        _temp.Observe(s.Temp1C, s.Temp2C);

        // 传感器越限时这一路的读数不可信，发成 Bad——安全层见 Bad 就触发。
        // 「读不到值当作正常」是最危险的失败模式（§7.5）
        var q1 = _fault.HasFlag(TecErrorCode.Ch1SensorOutOfRange) ? Quality.Bad : Quality.Good;
        var q2 = _fault.HasFlag(TecErrorCode.Ch2SensorOutOfRange) ? Quality.Bad : Quality.Good;

        // NaN = 该路没接传感器。宁可不发，也不要往曲线里塞一个假读数
        Push("Tr", s.Temp1C, at, q1);
        Push("Tj", s.Temp2C, at, q2);
        if (!double.IsNaN(s.Temp1C) && !double.IsNaN(s.Temp2C))
            Push("dT", s.Temp1C - s.Temp2C, at,
                 q1 == Quality.Good && q2 == Quality.Good ? Quality.Good : Quality.Bad);
        if (_temp.Setpoint is { } sp) Push("Tset", sp, at, Quality.Good);
        ReadDuty();
    }

    /// <summary>
    /// 读一次控温输出占空比。
    ///
    /// 它不在轮询快照里（快照只有两路温度和输出电压），得单独问一次 PWMDUTY，
    /// 所以放在轮询回调后面顺手发一条查询、**不等它**——等的话整个采集线程
    /// 会被串口往返卡住，温度那两路跟着一起晚。
    ///
    /// 上一拍还没回来就跳过这一拍：串口是独占的，堆着问只会越堆越多。
    /// 读不到就什么都不发——曲线上断一截，比塞一个「上次那个值」诚实得多（§9.4）。
    /// </summary>
    private void ReadDuty()
    {
        // 最快一秒一次。轮询周期可以短到 200 ms，而输出占空比是个慢变量——
        // 跟着每一拍问一次只是白占串口：那条链上还排着温度和告警字的查询，
        // 多一条就把它们往后推一个来回
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _dutyAt) < 1000) return;
        if (Interlocked.Exchange(ref _dutyBusy, 1) == 1) return;
        Interlocked.Exchange(ref _dutyAt, now);
        _ = Task.Run(async () =>
        {
            try
            {
                var duty = await _link.Controller.ReadDutyPercentAsync(DutyTc).ConfigureAwait(false);
                Push("duty", duty, DateTimeOffset.Now, Quality.Good);
            }
            catch (Exception ex)
            {
                _ctx.Log?.Invoke("warn", $"{InstanceId} 读控温输出失败：{ex.Message}");
            }
            finally { Interlocked.Exchange(ref _dutyBusy, 0); }
        });
    }

    /// <summary>
    /// 每个轮询周期一条告警字。硬告警（过温停输出、供电过高过低）把设备标成故障；
    /// 告警字本身照发，安全层按「非 0 即越限」处理。
    /// </summary>
    private void OnErrorCode(TecErrorCode code)
    {
        var at = DateTimeOffset.Now;
        var was = _fault;
        _fault = code;
        _temp.Faults = code.Describe();

        Push("fault", (ushort)code, at, Quality.Good);

        if (code != was)
        {
            foreach (var text in _temp.Faults) _ctx.Log?.Invoke("warn", $"{InstanceId} 告警：{text}");
            if (was != TecErrorCode.None && code == TecErrorCode.None)
                _ctx.Log?.Invoke("info", $"{InstanceId} 告警已解除");
        }

        // 这几条是「已经在损坏或已经停输出」，不是「正在限流」这种可以接着跑的
        var hard = code & (TecErrorCode.OverTempShutdown | TecErrorCode.UnderVoltage
                           | TecErrorCode.OverVoltage);
        if (hard != TecErrorCode.None) State = DeviceState.Faulted;
        else if (State == DeviceState.Faulted && code == TecErrorCode.None) State = DeviceState.Ready;
    }

    private void Push(string tag, double value, DateTimeOffset at, Quality quality)
    {
        if (double.IsNaN(value)) return;
        _out.Push(new Sample(_temp.Channel, tag, at.UtcTicks, at, value, quality));
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
        _link.Controller.ErrorCodeReceived -= OnErrorCode;
        _link.Controller.PollFaulted -= OnFaulted;
        _tuning.Detach();
        try { await _loop.ShutdownAsync().ConfigureAwait(false); } catch { }
        _loop.Dispose();
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _out.Complete();
        _link.Dispose();
        State = DeviceState.Disposed;
    }
}
