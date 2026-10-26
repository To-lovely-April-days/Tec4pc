using Tec.Drivers.DualStation;

namespace Tec.Io8rTest;

/// <summary>一路 DO：现场接的是什么（跟双工位驱动的缺省点位一致）。</summary>
public sealed record DoPoint(int Index, string Name, string Note);

/// <summary>
/// IO8R 手动测试：连着一块 IO8R，人点哪路就合 / 断哪路，写完读回核对。
/// 只动 IO8R——温控器（RD105）不碰，TEC 由别的电脑控制时单独给通道合制冷（TEC 功率线）这类测试用它。
/// 同一通道的加热棒和制冷不许同时合（跟驱动「两只从不同时合」同一条规矩）；断开 / 退出时八路全断。
/// 读写都串行（Modbus 一问一答，界面的每秒读回和人点的写不能交叉）。
/// </summary>
public sealed class Io8rTester : IAsyncDisposable
{
    public static readonly IReadOnlyList<DoPoint> Points = new DoPoint[]
    {
        new(0, "通道 1 加热棒", "热源切换 DO·工位 A"),
        new(1, "通道 2 加热棒", "热源切换 DO·工位 B"),
        new(2, "通道 1 使能", "TEC 使能 DO·工位 A"),
        new(3, "通道 2 使能", "TEC 使能 DO·工位 B"),
        new(4, "冷却水阀", "占的是「通道 1 辅助加热」那一路"),
        new(5, "（没接）", "「通道 2 辅助加热」那一路"),
        new(6, "通道 1 制冷", "TEC 功率线·工位 A"),
        new(7, "通道 2 制冷", "TEC 功率线·工位 B"),
    };

    /// <summary>同一通道不许同时合的两只：加热棒 ⇄ 制冷（TEC 功率线）。</summary>
    private static readonly (int A, int B)[] Exclusive = { (0, 6), (1, 7) };

    private readonly Io8rClient _io;
    private readonly IDisposable? _link;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>最近一次读回的 8 路继电器（true = 闭合）。</summary>
    public bool[] Relays { get; private set; } = new bool[Io8rClient.Points];
    /// <summary>最近一次读回的 8 路 DI。</summary>
    public bool[] Inputs { get; private set; } = new bool[Io8rClient.Points];

    public Io8rTester(Io8rClient io, IDisposable? link = null)
    {
        _io = io;
        _link = link;
    }

    /// <summary>开真串口（8N1，波特率 / 站号看模块拨码，出厂 9600 / 1）。连上先读一遍，不先全断——人可能就是来看现在的位置。</summary>
    public static async Task<Io8rTester> OpenSerialAsync(string port, int baud, byte station, CancellationToken ct)
    {
        var link = Io8rLink.Serial(port, baud, station);
        try
        {
            link.Open();
            var t = new Io8rTester(link.Io, link);
            await t.RefreshAsync(ct).ConfigureAwait(false);
            return t;
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try { await ReadLockedAsync(ct).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 合 / 断一路。合之前先读一遍再查互斥：同一通道的另一只合着就拒绝，说清先断哪只。
    /// 写完读回核对：命令发了模块没动也照实说。只写这一路，别的不动。
    /// </summary>
    public async Task SetAsync(int index, bool closed, CancellationToken ct)
    {
        if (index is < 0 or >= Io8rClient.Points)
            throw new ArgumentOutOfRangeException(nameof(index), $"IO8R 只有 DO0~DO7，要切的是 DO{index}");
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (closed)
            {
                await ReadLockedAsync(ct).ConfigureAwait(false);
                foreach (var (a, b) in Exclusive)
                {
                    var other = index == a ? b : index == b ? a : -1;
                    if (other >= 0 && Relays[other])
                        throw new InvalidOperationException(
                            $"DO{other}（{Points[other].Name}）合着——同一通道的加热棒和制冷不能同时合，先断 DO{other}");
                }
            }
            await _io.SetRelayAsync(index, closed, ct).ConfigureAwait(false);
            await ReadLockedAsync(ct).ConfigureAwait(false);
            if (Relays[index] != closed)
                throw new InvalidOperationException(
                    $"DO{index} 命令已发，读回还是{(Relays[index] ? "闭合" : "断开")}——看一下模块（供电 / 总线错误模式）");
        }
        finally { _gate.Release(); }
    }

    /// <summary>八路全断（一帧）。</summary>
    public async Task AllOffAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _io.AllOffAsync(ct).ConfigureAwait(false);
            await ReadLockedAsync(ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>制冷合着而冷却水阀没开：TEC 没水会干烧。只提醒不拦——做测试的人可能另有冷却。</summary>
    public string? Warning => (Relays[6] || Relays[7]) && !Relays[4]
        ? "制冷（DO6 / DO7）合着而冷却水阀（DO4）没开——TEC 没有冷却水会干烧，先开 DO4（冷水机本工具不管，在冷水机面板上开）"
        : null;

    private async Task ReadLockedAsync(CancellationToken ct)
    {
        Relays = await _io.ReadRelaysAsync(ct).ConfigureAwait(false);
        Inputs = await _io.ReadInputsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>走人前八路全断（断不了就算了，兜底是模块的总线错误复位），再关串口。</summary>
    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _io.AllOffAsync(CancellationToken.None).ConfigureAwait(false); }
        catch { /* 断不了也得走：串口照关 */ }
        finally
        {
            _gate.Release();
            _link?.Dispose();
        }
    }
}
