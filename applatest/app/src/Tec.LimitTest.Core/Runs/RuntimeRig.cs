using Tec.Core.Benches;
using Tec.Core.Safety;
using Tec.Driver.Abi;
using Tec.Drivers.DualStation;
using Tec.Drivers.Rd105;
using Tec.Hmi.Runtime;

namespace Tec.LimitTest.Runs;

/// <summary>
/// 把 HmiRuntime（真机驱动 + 管线 + 安全层）接成 Runner 要的那几个口子。
/// 读数从管线取最新值（过期 / Bad 按 null）；电流管线里没有——温控器「实时状态」组每 5 s 读一次；
/// 下发走通道的 ITemperatureControl（夹套目标、尽快），热源切换驱动自己做；
/// 安全层触发（Tr 越限、设备告警字、信号丢失）转成 Tripped。
/// </summary>
public sealed class RuntimeRig : IRig, IDisposable, IAsyncDisposable
{
    private readonly HmiRuntime _rt;
    private readonly Channel[] _wells;
    private readonly IDeviceSession? _host;
    private readonly IDeviceSettings? _settings;
    private readonly DeviceInstance? _hostDev;
    private readonly double?[] _cur = new double?[2];
    private DateTimeOffset _curAt = DateTimeOffset.MinValue;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _curLoop;
    private readonly Action<string, string>? _log;

    /// <summary>电流多久读一次（温控器「实时状态」组，一次读一串寄存器）。</summary>
    public TimeSpan CurrentPeriod { get; init; } = TimeSpan.FromSeconds(5);

    public RuntimeRig(HmiRuntime rt, Action<string, string>? log = null)
    {
        _rt = rt;
        _log = log;
        _wells = rt.Channels.Where(c => c.Capabilities.Has<ITemperatureControl>())
                            .OrderBy(c => c.Number).Take(2).ToArray();
        if (_wells.Length < 2)
            throw new InvalidOperationException(_wells.Length == 0
                ? "台面上没有带控温能力的通道——主机没连上？看日志里「打开失败」那条"
                : "只有一个带控温能力的通道，极限测试要两个工位一起跑");
        _host = rt.Session(_wells[0].HostInstanceId);
        _settings = _host as IDeviceSettings;
        _hostDev = rt.Bench.Device(_wells[0].HostInstanceId);
        rt.Engine.Safety.Triggered += OnTrip;
        if (_settings is not null && _settings.Groups.Any(g => g.Id == Rd105Settings.GroupStatus))
            _curLoop = Task.Run(CurrentLoopAsync);
    }

    /// <summary>「双工位反应主机（R1）· CH1 / CH2」——名字来自台面里的标签，跟左栏设备行一个写法。</summary>
    public string Describe
    {
        get
        {
            var id = _wells[0].HostInstanceId;
            var name = _hostDev?.Display ?? id;
            var who = name == id ? id : $"{name}（{id}）";
            return $"{who} · {_wells[0].Name} / {_wells[1].Name}";
        }
    }

    public int WellCount => 2;

    public event Action<string>? Tripped;

    private ITemperatureControl Temp(int well) => _wells[well].Capabilities.Get<ITemperatureControl>()!;

    public WellReading Read(int well)
    {
        var ch = _wells[well].Number;
        var now = _rt.Clock.Now;
        double? Get(string tag)
            => _rt.Pipeline.TryLatest(ch, tag, now, out var s) && s.Quality == Quality.Good && !double.IsNaN(s.Value)
                ? s.Value : null;
        var heat = Get("heat");
        var pwr = Get("tecpwr");
        // 电流：上位机 PID 下会话自己每两秒发一路 cur；温控器 PID 下没有，退回参数窗每 5 s 读的那份
        var cur = Get("cur") ?? (now - _curAt <= CurrentPeriod * 3 ? _cur[well] : null);
        return new WellReading(
            Tj: Get("Tj"),
            Tr: Get("Tr"),
            Out: Get("duty"),
            Cur: cur,
            Source: heat is null ? null : heat == 0 ? "TEC" : "电加热",
            TecPower: pwr is null ? null : pwr != 0);
    }

    public bool IsAlive(out string why)
    {
        if (_host is null)
        {
            why = _rt.OpenFailure(_wells[0].HostInstanceId) ?? "主机会话没打开";
            return false;
        }
        if (_host.State is DeviceState.Ready or DeviceState.Connected)
        {
            why = "";
            return true;
        }
        why = $"主机会话状态 {_host.State}";
        return false;
    }

    public TempLimits? Limits(int well) => Temp(well).Limits;

    public double? Threshold => Cfg(DualStationDriver.Fields.Threshold, 90);
    public double? Band => Cfg(DualStationDriver.Fields.Band, 2);

    private double? Cfg(string key, double schemaDefault)
    {
        var cfg = _hostDev?.Config;
        if (cfg is null) return null;
        return cfg.Has(key) ? cfg.Num(key, schemaDefault) : schemaDefault;
    }

    public Task SetTargetAsync(int well, double target, TempChannelKind kind, CancellationToken ct)
        => Temp(well).SetTargetAsync(new TempTarget(target, kind), ct);

    public Task StopAsync(int well, CancellationToken ct) => Temp(well).StopAsync(ct);

    public async Task<IReadOnlyList<string>> SafeStopAsync(CancellationToken ct)
    {
        var did = new List<string>();
        foreach (var ch in _wells)
            foreach (var a in ch.Attachments)
            {
                IReadOnlyList<string>? r;
                try { r = await a.Session.SafeStopAsync(a.Well, ct).ConfigureAwait(false); }
                catch (Exception ex) { did.Add($"{ch.Name} {a.Session.InstanceId}：停机动作报错 {ex.Message}"); continue; }
                if (r is null) did.Add($"{ch.Name} {a.Session.InstanceId}：这个驱动没实现停机动作，输出保持原样");
                else foreach (var s in r) did.Add($"{ch.Name} {s}");
            }
        return did;
    }

    public async Task<double?> ReadLimitedAsync(int well, CancellationToken ct)
        => await ReadChannelAsync(well, Rd105Settings.KLimited, ct).ConfigureAwait(false);

    public async Task<double?> ReadOverLimitAsync(int well, CancellationToken ct)
        => await ReadChannelAsync(well, Rd105Settings.KOverUp, ct).ConfigureAwait(false);

    private async Task<double?> ReadChannelAsync(int well, string key, CancellationToken ct)
    {
        if (_settings is null) return null;
        var group = well == 0 ? Rd105Settings.GroupTc1 : Rd105Settings.GroupTc2;
        if (_settings.Groups.All(g => g.Id != group)) return null;
        var p = await _settings.ReadAsync(group, ct).ConfigureAwait(false);
        return p.Has(key) ? p.Num(key) : null;
    }

    private async Task CurrentLoopAsync()
    {
        var ct = _cts.Token;
        var failed = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var p = await _settings!.ReadAsync(Rd105Settings.GroupStatus, ct).ConfigureAwait(false);
                for (var tc = 1; tc <= 2; tc++)
                {
                    var k = $"tc{tc}.{Rd105Settings.KCurrent}";
                    _cur[tc - 1] = p.Has(k) ? p.Num(k) : null;
                }
                _curAt = _rt.Clock.Now;
                if (failed > 0) { _log?.Invoke("info", $"电流读回来了（失败 {failed} 次后）"); failed = 0; }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (failed++ == 0) _log?.Invoke("warn", $"读温控器实时状态（电流）失败：{ex.Message}（连续失败只报第一次；表里电流留空）");
            }
            try { await Task.Delay(CurrentPeriod, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void OnTrip(object? sender, SafetyEvent e)
    {
        if (_wells.All(w => w.Number != e.Channel)) return;
        Tripped?.Invoke($"CH{e.Channel} {e.Message}");
    }

    public void Dispose()
    {
        _rt.Engine.Safety.Triggered -= OnTrip;
        _cts.Cancel();
        try { _curLoop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
    }

    /// <summary>同 Dispose，但等电流轮询收尾时不占着调用线程（界面上重连时用它）。</summary>
    public async ValueTask DisposeAsync()
    {
        _rt.Engine.Safety.Triggered -= OnTrip;
        _cts.Cancel();
        if (_curLoop is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
