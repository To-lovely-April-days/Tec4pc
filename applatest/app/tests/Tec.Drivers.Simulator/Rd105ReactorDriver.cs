using Tec.Driver.Abi;

namespace Tec.Drivers.Simulator;

/// <summary>
/// 双通道反应器 RD-105。一台设备开出 2 个通道，每个孔位自带温度控制 + 搅拌 + 背景灯。
/// 指令是静态声明的——没连硬件也要能编辑配方（§3.3）。
/// 它认领温度模块 5 条与搅拌 1 条。
/// </summary>
public sealed class Rd105ReactorDriver : IDeviceDriver
{
    public const string DriverId = "sim.reactor.rd105";

    // 名字与副标照 parts_current 那张配件总图：双工位、RD105 控制器控温。
    // 设备图换成 HT-RS2 主机的裸机线稿（rd105.svg），Tr / pH 是独立设备，
    // 插上哪个工位才画哪支——DriverId 不动，已存盘的台面还认得它
    public DriverInfo Info { get; } = new(DriverId, "双工位反应器 RD105", "Tec", "1.0.0")
    {
        ChannelsPerDevice = 2,
        SimulatorIncluded = true,
        IconKey = "rd105",
        Description = "100 mL 玻璃夹套釜 ×2 · 自带顶置搅拌 · −40…180 ℃；控温走 RD105 控制器。",
        Capabilities = new[] { nameof(ITemperatureControl), nameof(IRefluxControl), nameof(IStirrer), nameof(IIllumination) }
    };

    public ParameterSchema ConnectionSchema { get; } = new(new[]
    {
        Field.Port("端口", "串口", "COM3", "下拉里是这台机器当前检测到的串口"),
        Field.Sel("波特率", "波特率", new[] { "9600", "19200", "38400", "57600", "115200" }, "115200"),
        Field.Sel("校验", "校验位", new[] { "无", "奇", "偶" }, "无"),
        Field.Num("站号", "站号", 1, "", 1, 247, 1)
    })
    { Tip = "RD-105 走 RS-485 Modbus RTU。改完点「测试连接」，会回显固件版本与探测到的孔位数。" };

    public ParameterSchema ConfigSchema { get; } = new(new[]
    {
        Field.Sel("釜规格", "反应釜规格", new[] { "25 mL", "50 mL", "100 mL", "250 mL" }, "100 mL"),
        Field.Sel("釜材质", "材质", new[] { "玻璃", "哈氏合金", "316L" }, "玻璃"),
        Field.Sel("搅拌桨", "搅拌桨", new[] { "锚式", "桨式", "磁子" }, "锚式"),
        Field.Sel("温度探头", "温度探头", new[] { "Pt100 四线", "Pt1000", "热电偶 K" }, "Pt100 四线"),
        // 仿真热模型的料液沸点：Tr 到这里停在平台上（潜热吃掉多余的热）。
        // 只影响仿真——真机的沸点由釜里的料决定，程序不知道也不该编
        Field.Num("沸点", "料液沸点（仿真）", 100, "℃", 30, 300, 1),
        // 热源切换（需求 §2）：字段名与真机 DualStationDriver.Fields 一字不差——
        // 属性栏勾 / 去勾「模拟」换的是驱动身份，配置值按名字跟着设备走，
        // 仿真里调好的阈值插上真机就是那个阈值。阈值上限 90 是死的（TEC 通路的工程上限）
        Field.Sel("电加热切换", "电加热切换（IO8R）", new[] { "有", "无" }, "有"),
        Field.Sel("TEC加热", "TEC 加热", new[] { "不启用", "启用" }, "不启用"),
        Field.Num("电加热切换阈值", "电加热切换阈值", 90, "℃", 40, 90, 1),
        Field.Num("回切滞回", "回切滞回", 5, "K", 2, 20, 1),
        Field.Num("热源死区", "热源切换死区", 2, "K", 0.5, 10, 0.5),
        Field.Sel("切换反馈", "切换反馈", new[] { "无", "有" }, "无")
    })
    { Tip = "整机固定的三个配件在这里选型；它们没有独立驱动，不上台面。「料液沸点」只喂给仿真热模型：釜温到沸点就停在平台上，蒸回流的曲线靠它才像真的。热源切换几项与真机同名同义。「TEC 加热」默认不启用：TEC 只当冷源，升温一律切电加热棒，继电器按「该升温还是该降温」切（死区之内保持不动）；启用后才是「目标高于阈值才切电加热」。夹套凉到阈值减滞回才准回切 TEC；「切换反馈」决定状态量报「已核实」还是「未核实」。" };

    public IReadOnlyList<CommandDescriptor> Commands { get; } =
        CommandSpecs.Temperature.Concat(CommandSpecs.Stirring).ToList();

    public async Task<ProbeResult> ProbeAsync(ParameterSet connection, CancellationToken ct)
    {
        await Task.Delay(120, ct).ConfigureAwait(false);
        return new ProbeResult(true, $"{connection.Str("端口", "COM3")} 已响应")
        {
            Firmware = "RD105-FW 1.3.0",
            Serial = "RD105-SIM-0001",
            DetectedChannels = 2
        };
    }

    public Task<IDeviceSession> OpenAsync(ParameterSet connection, DriverContext ctx, CancellationToken ct)
        => Task.FromResult<IDeviceSession>(new Rd105Session(ctx));
}

internal sealed class Rd105Session : SimSession
{
    private readonly ReactorWell[] _wells;

    public Rd105Session(DriverContext ctx) : base(ctx)
    {
        var chs = ctx.ChannelNumbers;
        var cfg = ctx.Config;
        var boiling = cfg.Num("沸点", 100);
        _wells = new ReactorWell[2];
        for (var i = 0; i < 2; i++)
            _wells[i] = new ReactorWell(i < chs.Count ? chs[i] : 0, Emit, () => Scale, () => Now)
            {
                BoilingPoint = boiling is > 0 and < 1000 ? boiling : 100,
                // 热源切换四项：与真机 DuoSession 读同名字段、同样的钳位（阈值 ≤ 90 死上限）
                HasElectric = cfg.Str("电加热切换", "有") == "有",
                TecHeat = cfg.Str("TEC加热", "不启用") == "启用",
                Threshold = Math.Min(90, cfg.Num("电加热切换阈值", 90)),
                Hysteresis = Math.Clamp(cfg.Num("回切滞回", 5), 2, 20),
                Band = Math.Clamp(cfg.Num("热源死区", 2), 0.5, 10),
                Feedback = cfg.Str("切换反馈", "无") == "有"
            };
    }

    public override int WellCount => 2;

    public override IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("Tr", "釜内温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("Tj", "夹套温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 200), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("dT", "Tr−Tj", "℃", DataShape.Scalar)
            { DerivedFrom = "Tr,Tj", Period = TimeSpan.FromSeconds(1) },
        // 控温目标值。只在控温期间有数据——停控（自然冷却）时本来就没有设定值
        new TagDescriptor("Tset", "设定温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(-40, 180), Period = TimeSpan.FromSeconds(1) },
        // 控温输出。放大时最要紧的问题是「夹套已经满功率还压不住放热」，
        // 没有这一路看不出来。正值加热、负值制冷，±100 % 是执行器满出力
        new TagDescriptor("duty", "控温输出", "%", DataShape.Scalar)
            { Nominal = new ValueRange(-100, 100), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("rpm", "搅拌转速", "rpm", DataShape.Scalar)
            { Nominal = new ValueRange(0, 1200), Period = TimeSpan.FromSeconds(1) },
        // 手动控制面板（HMI）读的两路：面板上的每个数都要有真来源，
        // 不能因为「原型上画了」就在界面里编一个
        new TagDescriptor("Tc", "冷媒温度", "℃", DataShape.Scalar)
            { Nominal = new ValueRange(0, 40), Period = TimeSpan.FromSeconds(1) },
        new TagDescriptor("torque", "搅拌扭矩", "mN·m", DataShape.Scalar)
            { Nominal = new ValueRange(0, 59), Period = TimeSpan.FromSeconds(1) },
        // 热源状态量，与真机同一路同一套编码：0 = TEC，1 = 电加热（无反馈回路，未核实），
        // 2 = 电加热（反馈已核实）。记录里看得出什么时候换的挡、换过去有没有核实
        new TagDescriptor("heat", "热源", "", DataShape.State)
            { Nominal = new ValueRange(0, 2), Period = TimeSpan.FromSeconds(1) }
    };

    public override IReadOnlyList<ICapability> CapabilitiesOf(int well)
        => well >= 0 && well < _wells.Length
            ? new ICapability[] { _wells[well], _wells[well].Stirrer, _wells[well].Light }
            : Array.Empty<ICapability>();

    protected override void Tick(double dt)
    {
        RefreshSwitches();
        foreach (var w in _wells) w.Tick(dt, Noise(0.05));
    }

    /// <summary>
    /// 每拍重读属性栏上那两项「随手改、立刻生效」的配置（与真机 DuoSession 同一条规矩）：
    /// 「TEC 加热」是个开关，人按下去就该生效，不该还要先把设备断开重连。
    /// Context.Config 就是台面设备身上那份 ParameterSet 本体，属性栏改的也是它；
    /// 那是个普通字典，界面线程正在写时读可能出事，撞上了就当这一拍没改到。
    /// </summary>
    private void RefreshSwitches()
    {
        try
        {
            var tec = Context.Config.Str("TEC加热", "不启用") == "启用";
            var band = Math.Clamp(Context.Config.Num("热源死区", 2), 0.5, 10);
            foreach (var w in _wells) { w.TecHeat = tec; w.Band = band; }
        }
        catch
        {
            // 下一拍再读
        }
    }

    // 温度五条用 ABI 的能力通用执行器（真机会话认领的是同一份——
    // 仿真调好的配方插上真机能跑）；搅拌那条带斜坡仿真的特判，留在本地
    private static readonly HandlerTable Table = new HandlerTable()
        .Add(CommandSpecs.Control, () => new TempControlHandler())
        .Add(CommandSpecs.Gradient, () => new TempGradientHandler())
        .Add(CommandSpecs.Hold, () => new TempHoldHandler())
        .Add(CommandSpecs.PassiveCool, () => new TempPassiveCoolHandler())
        .Add(CommandSpecs.Reflux, () => new TempRefluxHandler())
        .Add(CommandSpecs.Stir, () => new StirHandler());

    public override ICommandHandler? Resolve(string commandId) => Table.Resolve(commandId);

    /// <summary>
    /// 中止收尾：**切加热，保搅拌。**
    ///
    /// 加热必须切——中止只停了配方，温控器还守着最后那个设定值，
    /// 一路烧到目标温度再稳住，操作人按下停止时想的绝不是这个。
    ///
    /// 搅拌**故意留着**：釜里多半是热的反应液，不搅容易局部过热、结块、暴沸。
    /// 停搅拌比继续搅危险，所以这台设备的「安全态」就是「不加热、继续搅」。
    /// 要停搅拌的话在配方末尾自己写一条转速 0，那是工艺决定，不是停机动作。
    /// </summary>
    public override async ValueTask<IReadOnlyList<string>?> SafeStopAsync(int well, CancellationToken ct)
    {
        if (well < 0 || well >= _wells.Length) return Array.Empty<string>();
        var w = _wells[well];
        var did = new List<string>();

        await w.StopAsync(ct).ConfigureAwait(false);
        did.Add($"已切断加热输出（停在 {w.CurrentReactor:F1} ℃，此后自然冷却）");
        // 与真机同一条：安全停把切换继电器断开、落回 TEC 侧（输出已关，接回 TEC 不带载）
        if (w.Electric)
        {
            w.ForceTec();
            did.Add("热源切换继电器已断开（落回 TEC 侧）");
        }

        did.Add(w.Stirrer.CurrentRpm > 0
            ? $"搅拌保持 {w.Stirrer.CurrentRpm:F0} rpm —— 热液不搅有局部过热风险，停搅拌要在配方里写"
            : "搅拌本来就停着");
        return did;
    }
}

/// <summary>一个孔位。温度、蒸回流、搅拌、背景灯四项能力都由它提供。</summary>
internal sealed class ReactorWell : ITemperatureControl, IRefluxControl, IHeatSource
{
    private readonly Action<int, string, double> _emit;
    private readonly Func<double> _scale;
    private readonly Func<DateTimeOffset> _now;
    private readonly Broadcast<Sample> _temp = new();

    private double _target = 25;
    private double _rate = 2;
    private bool _controlling;
    /// <summary>最后一次下发的控温对象。夹套控温 / 蒸回流时 WaitReached 也要看对地方。</summary>
    private TempChannelKind _kind = TempChannelKind.Reactor;

    // 蒸回流（夹套跟随）状态。跟随环每拍把 _target 重写成 Tr+ΔT（钳在上限），
    // 所以停跟随之后目标自然「停在最后一次下发的值上」——不用另存一份
    private bool _refluxing;
    private double _refluxDt = 5;
    private double _refluxMax = 120;

    /// <summary>夹套物理上限：Tj 无论怎么钳目标，模型都不越过它（对应量程 −40…200）。</summary>
    private const double JacketMax = 200;

    // ── 热源切换（需求 §2），判据与真机 DuoSession 一字不差 ──────────
    //  · 「TEC 加热」不启用（默认）：TEC 只当冷源，升温一律走电加热棒——
    //    按「目标比被控量高还是低一个死区」切，死区之内保持现状；
    //  · 「TEC 加热」启用：回到按阈值判，目标 > 阈值才切电加热；
    //  · 两种模式共同：回 TEC 要实测夹套 ≤ 阈值 − 滞回——夹套还烫着把 TEC 接回去会烧它；
    //  · 电加热用不了（没配 IO8R）时，需要加热的目标直接拒绝，理由写明；
    //  · 每次切换都走全套序列「关输出 → 切继电器 → 核反馈 → 重开输出」，
    //    仿真里就是 2 s 不出力（真机是 ENABLE=0 → DO → DI 2 s → ENABLE=1）。
    public bool HasElectric { get; set; } = true;
    /// <summary>TEC 反向输出加热启不启用。默认 false = 所有加热都走电加热棒。</summary>
    public bool TecHeat { get; set; }
    public double Threshold { get; set; } = 90;
    public double Hysteresis { get; set; } = 5;
    /// <summary>冷热判定死区（K），只在 TEC 加热不启用时用到。</summary>
    public double Band { get; set; } = 2;
    /// <summary>「切换反馈」有 → 状态量报 2（已核实）；无 → 报 1（无反馈回路，未核实）。
    /// 仿真里没有接触器可核，这一位只决定界面上那几个字——跟真机的配置语义对齐。</summary>
    public bool Feedback { get; set; }
    /// <summary>当前热源：false = TEC，true = 电加热。</summary>
    public bool Electric { get; private set; }
    private double _switchHold;             // 切换序列剩余秒数，这期间输出关着
    private double _sinceSwitch = MinDwellSeconds;   // 距上次切换多久了（开机就允许切）
    private const double SwitchSeconds = 2;
    /// <summary>自动切到电加热的最短间隔（仿真秒）。只拦升温方向，抢冷不等。</summary>
    private const double MinDwellSeconds = 30;

    /// <summary>热源状态量：0 = TEC，1 = 电加热（未核实），2 = 电加热（已核实）。</summary>
    public int HeatState => !Electric ? 0 : Feedback ? 2 : 1;

    // ── IHeatSource：界面问「这一路现在能往哪个方向出力」──────────────
    bool IHeatSource.TecHeating => TecHeat;
    bool IHeatSource.ElectricAvailable => HasElectric;
    bool IHeatSource.OnElectric => Electric;

    /// <summary>电加热侧只能升温；TEC 侧要升温得看「TEC 加热」启没启用。</summary>
    private bool CanHeat => Electric || TecHeat;

    /// <summary>冷源只有 TEC——电加热棒制不了冷。</summary>
    private bool CanCool => !Electric;

    /// <summary>安全停用：断继电器落回 TEC 侧（输出已关，不带载）。</summary>
    public void ForceTec()
    {
        Electric = false;
        _switchHold = 0;
        _sinceSwitch = 0;
    }

    /// <summary>
    /// 这个目标该落在哪一侧：true = 电加热，false = TEC，null = 保持现状。
    ///
    /// **按夹套判，不按控温对象判**——真机的 PID 就闭在夹套上（TG 与 TC 探头都在夹套），
    /// 换挡问的是「这一刻 PID 会往哪个方向出力」。孪生要跟真机 DuoSession.SideFor 一致，
    /// 所以这里也恒用 Tj；「这一侧出不出得了这个方向的力」才按模型自己的被控量算。
    /// </summary>
    private bool? SideFor(double target)
    {
        if (TecHeat) return target > Threshold;
        // 安全否决，与真机同：釜里已经不比目标凉了就别再往上加热
        // （「恒温保持」下发的就是当刻的釜温，严格相等——这一档也得挡住）
        if (CurrentReactor >= target) return CurrentReactor > target + Band ? false : null;
        if (target > CurrentJacket + Band) return true;
        if (target < CurrentJacket - Band) return false;
        return null;
    }

    private void EnsureSource(double target)
    {
        if (SideFor(target) is not true) return;
        if (!HasElectric)
            throw new InvalidOperationException(
                (TecHeat
                    ? $"目标 {target:F1} ℃ 高于电加热切换阈值 {Threshold:F0} ℃，"
                    : $"「TEC 加热」没启用，升温只能走电加热棒（目标 {target:F1} ℃ 高过实测），") +
                "但这台没配 IO8R 切换模块——电加热用不了，这个目标上不去");
        if (!Electric) Switch(toElectric: true);
    }

    private void Switch(bool toElectric)
    {
        Electric = toElectric;
        _switchHold = SwitchSeconds;
        _sinceSwitch = 0;
    }

    public ReactorWell(int channel, Action<int, string, double> emit, Func<double> scale, Func<DateTimeOffset> now)
    {
        Channel = channel;
        _emit = emit;
        _scale = scale;
        _now = now;
        Stirrer = new StirrerImpl(channel, emit);
        Light = new LightImpl(channel);
    }

    public int Channel { get; }
    public StirrerImpl Stirrer { get; }
    public LightImpl Light { get; }

    public TempLimits Limits { get; } = new(-40, 180, 16);
    public double CurrentReactor { get; private set; } = 25;
    public double CurrentJacket { get; private set; } = 25;
    /// <summary>控温输出占比 %，正加热负制冷。停控时为 0。</summary>
    public double Duty { get; private set; }
    /// <summary>冷媒温度：自来水回路，随出力略微抬升（换热带走的热进了冷媒）。</summary>
    public double Coolant { get; private set; } = 18.6;
    /// <summary>仿真料液的沸点：Tr 到这里停在平台上（潜热吃掉多余的热）。设备配置「沸点」喂进来。</summary>
    public double BoilingPoint { get; set; } = 100;
    public IObservable<Sample> Temperature => _temp;

    public Task SetTargetAsync(TempTarget target, CancellationToken ct)
    {
        var v = Math.Clamp(target.Value, Limits.Min, Limits.Max);
        // 下发目标前先把热源切到对的一侧；切不了就拒绝，不留下发痕迹（与真机同序）
        try { EnsureSource(v); }
        catch (InvalidOperationException ex) { return Task.FromException(ex); }
        _target = v;
        _kind = target.Kind;
        _controlling = true;
        _refluxing = false;     // 明确下发新目标 = 操作人/下一步接管，跟随环退位
        return Task.CompletedTask;
    }

    public Task RampAsync(double target, double ratePerMin, TempChannelKind kind, CancellationToken ct)
    {
        var v = Math.Clamp(target, Limits.Min, Limits.Max);
        try { EnsureSource(v); }
        catch (InvalidOperationException ex) { return Task.FromException(ex); }
        _target = v;
        _rate = Math.Clamp(ratePerMin <= 0 ? 2 : ratePerMin, 0.05, Limits.MaxRatePerMin);
        _kind = kind;
        _controlling = true;
        _refluxing = false;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        // 安全动作走这里：跟随环必须一起清，不清的话下一拍又把目标写回去
        _controlling = false;
        _refluxing = false;
        return Task.CompletedTask;
    }

    public Task<bool> WaitReachedAsync(double target, double tolerance, TimeSpan timeout, CancellationToken ct)
        => SimTime.PollAsync(
            () => Math.Abs((_kind == TempChannelKind.Jacket ? CurrentJacket : CurrentReactor) - target) <= tolerance,
            timeout, _scale(), _now, ct);

    // ── 蒸回流（IRefluxControl）───────────────────────────────────────
    // StopAsync 与 ITemperatureControl 同签名，必须显式实现分开：
    // 停跟随 ≠ 停控温——跟随收掉后夹套目标停在最后一次下发的值上，
    // 收尾往哪走由下一步（或安全停机）决定

    Task IRefluxControl.StartAsync(double deltaT, double maxTj, CancellationToken ct)
    {
        // 跟随目标永远是 Tr+ΔT，整段都在升温：TEC 加热没启用又没有电加热通路，
        // 那就是一开始就跟不动。与真机 StartRefluxAsync 一样当场拒绝，
        // 不要先答应下来再在 Tick 里静默停掉
        if (!TecHeat && !HasElectric)
            return Task.FromException(new InvalidOperationException(
                "蒸回流开不了：「TEC 加热」没启用，夹套升温只能走电加热棒，" +
                "但这台没配 IO8R 切换模块——要么接上切换模块，要么在设备属性里打开「TEC 加热」"));

        _refluxDt = Math.Clamp(deltaT, 0.5, 30);
        _refluxMax = Math.Clamp(maxTj, Limits.Min, JacketMax);
        _refluxing = true;
        _controlling = true;
        _kind = TempChannelKind.Jacket;
        return Task.CompletedTask;
    }

    Task IRefluxControl.StopAsync(CancellationToken ct)
    {
        _refluxing = false;
        return Task.CompletedTask;
    }

    public bool Active => _refluxing;

    /// <summary>停控 / 输出关着 / 电加热侧要降温：按自然冷却走，0.5 ℃/min，
    /// 与排期估算里的 PASSIVE 一致；出力为 0——输出真的切断了，不是「输出为零的控温」。</summary>
    private void NaturalCool(double dt)
    {
        var toward = 25 - CurrentReactor;
        var step = 0.5 * dt / 60.0;
        CurrentReactor += Math.Clamp(toward, -step, step);
        CurrentJacket += (CurrentReactor - CurrentJacket) * 0.2;
        Duty = 0;
    }

    /// <summary>
    /// 双态热模型：夹套是被控对象，釜内只通过夹套换热升降——
    /// 蒸回流的「Tj 恒高 ΔT、Tr 爬向沸点停在平台」只有这样才画得出来。
    /// 一阶惯性 + 换热能力有限：越靠近目标越慢，
    /// Setpoint 类步骤天然"只会偏慢"，与偏差模型一致（§4.3）。
    /// </summary>
    public void Tick(double dt, double noise)
    {
        if (_refluxing)
        {
            // 跟随环：夹套目标 = 釜内实测 + ΔT，钳在上限。写进 _target，
            // 所以 Tset 曲线、停跟随后的驻留值都是真值，不是另一套账
            var tg = Math.Min(CurrentReactor + _refluxDt, _refluxMax);
            // 联锁②（与真机同）：跟随目标要电加热就走热源切换；电加热用不了的，
            // 跟到那一刻停跟随——TEC 加热启用时是撞上阈值，不启用时是一开始就升不动
            if (SideFor(tg) is true && !Electric)
            {
                if (HasElectric) Switch(toElectric: true);
                else { _refluxing = false; tg = Math.Min(tg, TecHeat ? Threshold : CurrentJacket); }
            }
            _target = tg;
            _kind = TempChannelKind.Jacket;
        }

        // 自动换挡（与真机采集循环 AutoSourceAsync 同一套判据）：
        // 回 TEC 要夹套凉到阈值 − 滞回；切电加热留一道最短间隔，只拦升温方向。
        // 停控的通道只准落回 TEC 侧（安全侧），绝不自动切到电加热。
        // 切换序列进行中不叠加判断
        _sinceSwitch += dt;
        if (_switchHold <= 0)
        {
            var want = _controlling ? SideFor(_target) : (Electric ? false : null);
            if (want is { } side && side != Electric)
            {
                if (side)
                {
                    if (HasElectric && _sinceSwitch >= MinDwellSeconds) Switch(true);
                }
                // 「夹套凉到阈值 − 滞回 才准接回 TEC」只管**输出开着**的情形：
                // 那条防的是让 TEC 带着载贴上超出耐温的热源。停控的通道不带载，
                // 落回 TEC 才是继电器该待的位置（与真机 AutoSourceAsync、SafeStop 一致）
                else if (!_controlling || CurrentJacket <= Threshold - Hysteresis) Switch(false);
            }
        }

        // 切换序列期间输出关着（真机是 ENABLE=0 → 切继电器 → 核反馈 → ENABLE=1），
        // 这几拍按停控走：夹套不被驱动、出力为 0，_controlling 本身不动
        var switching = _switchHold > 0;
        if (switching) _switchHold -= dt;
        var driving = _controlling && !switching;
        // 这一侧出得了这个方向的力吗：电加热棒制不了冷；TEC 侧要升温得「TEC 加热」启用。
        // 出不了力就只能自然凉——等自动换挡把继电器切过去才有主动出力
        var pv = _kind == TempChannelKind.Jacket ? CurrentJacket : CurrentReactor;
        var blocked = driving
            && ((_target > pv + 0.05 && !CanHeat) || (_target < pv - 0.05 && !CanCool));

        if (blocked)
        {
            NaturalCool(dt);
        }
        else if (driving && _kind == TempChannelKind.Jacket)
        {
            // 夹套环：直接驱动 Tj。跟随时限速用设备最大能力（真机的跟随环
            // 也是按 SPEED 限速写 TG），普通夹套控温按指令给的速率
            var rate = _refluxing ? Limits.MaxRatePerMin : _rate;
            var err = _target - CurrentJacket;
            var maxStep = rate * dt / 60.0;
            var move = Math.Clamp(err, -maxStep, maxStep);
            move *= 1 - Math.Exp(-Math.Abs(err) / 3.0) * 0.35;
            CurrentJacket = Math.Min(CurrentJacket + move, JacketMax);
            Duty = maxStep > 0 ? Math.Clamp(move / maxStep, -1, 1) * 100 : 0;

            // 釜内跟着夹套换热走：每分钟收掉温差的 30 %。
            // ΔT 越小升得越慢——蒸回流的升温速率天然由 ΔT 决定，这是物理，不是编的
            CurrentReactor += (CurrentJacket - CurrentReactor) * Math.Min(1, dt / 60.0 * 0.30);
        }
        else if (driving)
        {
            // 釜内环（串级等效）：Tr 沿限速轨迹走，夹套画在前面牵引
            var err = _target - CurrentReactor;
            var maxStep = _rate * dt / 60.0;
            var move = Math.Clamp(err, -maxStep, maxStep);
            move *= 1 - Math.Exp(-Math.Abs(err) / 3.0) * 0.35;
            CurrentReactor += move;
            // 夹套画在前面牵引，但**不越过目标**：真机上 TG 就是夹套的设定值，
            // PID 不会把夹套顶到设定值之外。从前这里让它冲出 0.8 倍偏差，看着像回事，
            // 可热源换挡就是拿「目标 − 夹套」判方向的，冲过头会被判成「该降温」，
            // 一路把继电器扳回 TEC。Tr 是被限速直接驱动的，钳住夹套不影响升温快慢
            var lead = _target;
            CurrentJacket += (Math.Min(lead, JacketMax) - CurrentJacket) * Math.Min(1, dt / 20.0);
            // 出力 = 这一拍用掉了多少「最大可用变温能力」，正加热负制冷。
            // **它是模型自己算出来的那个量**，不是为了让曲线好看另编的一路：
            // 上面那个 move 就是执行器这一拍干的活，除以 maxStep 正是占比
            Duty = maxStep > 0 ? Math.Clamp(move / maxStep, -1, 1) * 100 : 0;
        }
        else
        {
            NaturalCool(dt);
        }

        // 沸点平台：到了就停住，多余的热变成蒸汽（回流），不再抬温。
        // 控温目标高过沸点是到不了的——WaitReached 超时报「没到」，跟真釜一样
        if (CurrentReactor > BoilingPoint) CurrentReactor = BoilingPoint;

        CurrentReactor += noise;
        CurrentJacket += noise * 1.4;

        // 冷媒：18.6 ℃ 上下的自来水，制冷出力越大回水越热（缓慢跟随），停控回落
        var coolTgt = 18.6 + Math.Abs(Math.Min(0, Duty)) / 100.0 * 2.2;
        Coolant += (coolTgt - Coolant) * Math.Min(1, dt / 30.0) + noise * 0.3;

        _emit(Channel, "Tr", Math.Round(CurrentReactor, 2));
        _emit(Channel, "Tj", Math.Round(CurrentJacket, 2));
        _emit(Channel, "dT", Math.Round(CurrentReactor - CurrentJacket, 2));
        _emit(Channel, "duty", Math.Round(Duty, 1));
        _emit(Channel, "Tc", Math.Round(Coolant, 2));
        _emit(Channel, "heat", HeatState);
        // 设定温度只在控温时才存在：停控（自然冷却）没有设定值，
        // 这一路断掉比拿釜温顶替诚实——导出的是要签进记录的数据。
        if (_controlling) _emit(Channel, "Tset", Math.Round(_target, 2));
        Stirrer.Tick(dt);
    }
}

/// <summary>转速斜坡通过 ABI 的可选口 IStirrerRamp 提供——面板与执行器
/// 按接口问，不认识这个具体类（HMI 拆独立项目后它们也见不到这个程序集）。</summary>
public sealed class StirrerImpl : IStirrer, IStirrerRamp
{
    private readonly Action<int, string, double> _emit;
    private readonly Broadcast<Sample> _speed = new();
    private double _target;
    private double _rampSeconds = 5;

    public StirrerImpl(int channel, Action<int, string, double> emit)
    {
        Channel = channel;
        _emit = emit;
    }

    public int Channel { get; }
    public SpeedLimits Limits { get; } = new(0, 1000);
    public double CurrentRpm { get; private set; }
    /// <summary>搅拌扭矩 mN·m：静摩擦底数 + 随转速上升，转着才有；上限 59（磁耦合打滑点）。</summary>
    public double Torque { get; private set; }
    public IObservable<Sample> Speed => _speed;

    public Task SetSpeedAsync(double rpm, CancellationToken ct)
    {
        _target = Math.Clamp(rpm, Limits.Min, Limits.Max);
        return Task.CompletedTask;
    }

    /// <summary>加/减速时间由指令给出（原型的 ramp 参数）。</summary>
    public void SetRampSeconds(double seconds) => _rampSeconds = Math.Max(0.5, seconds);

    public Task StopAsync(CancellationToken ct)
    {
        _target = 0;
        return Task.CompletedTask;
    }

    public void Tick(double dt)
    {
        var step = Limits.Max * dt / _rampSeconds;
        CurrentRpm += Math.Clamp(_target - CurrentRpm, -step, step);
        // 扭矩从转速模型推：4 mN·m 静摩擦底数 + 0.031/rpm 的黏性项（450 rpm ≈ 18），
        // 一阶惯性跟随，停转归零。它是模型算出来的量，不是为了面板好看另编的一路
        var tq = CurrentRpm > 1 ? Math.Min(59, 4 + CurrentRpm * 0.031) : 0;
        Torque += (tq - Torque) * Math.Min(1, dt / 2.0);
        _emit(Channel, "rpm", Math.Round(CurrentRpm, 0));
        _emit(Channel, "torque", Math.Round(Torque, 1));
    }
}

internal sealed class LightImpl : IIllumination
{
    public LightImpl(int channel) => Channel = channel;
    public int Channel { get; }
    public bool On { get; private set; }
    public double Brightness { get; private set; } = 0.8;

    public Task SetAsync(bool on, double brightness, CancellationToken ct)
    {
        On = on;
        Brightness = Math.Clamp(brightness, 0, 1);
        return Task.CompletedTask;
    }
}

// ── 指令处理器：只用 ABI 的能力接口，不认识具体设备类。
//    温度四条已搬到 Tec.Driver.Abi.CapabilityCommands（真机同一套认领），
//    这里只剩搅拌——它带 StirrerImpl 的斜坡仿真特判 ─────────────────────

internal static class TempHelp
{
    public static IStirrer Stir(CommandContext ctx)
        => ctx.Capabilities.Get<IStirrer>()
           ?? throw new InvalidOperationException("该通道没有搅拌能力");
}

/// <summary>
/// 搅拌。转速 0 走 StopAsync 而不是 SetSpeedAsync(0)——
/// 真机上这两条是不同的寄存器操作：一个是设定值归零，一个是关输出。
/// </summary>
internal sealed class StirHandler : ICommandHandler
{
    public async Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var stir = TempHelp.Stir(ctx);
        var began = ctx.Now();
        var rpm = p.Num("rpm");
        // 「立即」走驱动的最短加减速（0.5 s 下限）；电机没有真正的零时间起停
        var ramp = CommandSpecs.StirImmediate(p) ? 0.5 : p.Num("ramp", 5);

        if (stir is IStirrerRamp impl) impl.SetRampSeconds(ramp);
        if (rpm <= 0) await stir.StopAsync(ct).ConfigureAwait(false);
        else await stir.SetSpeedAsync(rpm, ct).ConfigureAwait(false);

        await SimTime.DelayAsync(TimeSpan.FromSeconds(ramp), ctx.TimeScale, ct).ConfigureAwait(false);
        return new CommandOutcome(EndReason.Reached, ctx.Now() - began);
    }
}
