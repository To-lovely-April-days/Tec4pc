using Tec.Driver.Abi;

namespace Tec.Drivers.DualStation;

/// <summary>
/// 一个工位的 pH 传感能力：台面上那支「pH 玻璃电极」在真机上物理接的就是
/// 宇电 AI-8848GD91J4 的一路输入（用户明确的对应关系）。电极本身不占串口、
/// 不自己产数——数从主机组合会话的采集循环里来，这里只是把最新一拍以
/// IScalarSensor 端出去：台面读数标签、pH 判据、配方校验吃的都是这个能力。
///
/// 没配 pH 模块（或自检没过）时主机不端出这个能力——没有来源的能力挂出来，
/// 等于对配方校验撒谎。
/// </summary>
internal sealed class DuoPhSensor : IScalarSensor
{
    private readonly Broadcast<Sample> _values = new();
    private Sample _latest;
    private bool _has;

    public DuoPhSensor(int channel) => Channel = channel;

    public int Channel { get; }

    public IReadOnlyList<TagDescriptor> Tags { get; } = new[]
    {
        new TagDescriptor("pH", "pH", "", DataShape.Scalar) { Nominal = new ValueRange(0, 14) }
    };

    public IObservable<Sample> Values => _values;

    /// <summary>还没有一拍有效读数就返回 false——不编一个数出来。</summary>
    public bool TryReadLatest(string tag, out Sample sample)
    {
        sample = _latest;
        return _has && (_latest.Tag == tag || tag.Length == 0);
    }

    /// <summary>采集循环喂一拍进来。断线那拍 Quality 是 Bad，读的人自己看质量位。</summary>
    internal void Update(in Sample s)
    {
        _latest = s;
        _has = true;
        _values.Push(s);
    }
}

/// <summary>
/// 「pH 采集」在真机上的认领。语义与仿真电极那份一致：采集本身是常驻的
/// （模块一直在被轮询），这条指令只是「从这里开始记进本次实验」，Immediate 不占时间。
/// </summary>
internal sealed class DuoPhSampleHandler : ICommandHandler
{
    public Task<CommandOutcome> ExecuteAsync(CommandContext ctx, CommandInput p, CancellationToken ct)
    {
        var sensor = ctx.Capabilities.Get<IScalarSensor>();
        var now = sensor is not null && sensor.TryReadLatest("pH", out var s)
            ? $"当前 pH {s.Value:F2}（{s.Quality}）"
            : "当前无有效读数";
        ctx.Note?.Invoke($"开始采集 pH，每 {p.Num("interval", 1):0.##} s；{now}");
        return Task.FromResult(CommandOutcome.Instant());
    }
}
