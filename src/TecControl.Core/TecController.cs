using TecControl.Core.Comm;
using TecControl.Core.Models;
using TecControl.Core.Protocol;

namespace TecControl.Core;

/// <summary>
/// 面向应用层的温控器服务：类型化读写 + 后台轮询。
/// 所有指令经由同一个 TecClient 串行执行，轮询与用户操作不会在总线上冲突。
/// </summary>
public sealed class TecController(TecClient client) : IDisposable
{
    private CancellationTokenSource? _pollCts;
    private Task? _pollTask;

    /// <summary>每个轮询周期发布一次实时数据（后台线程回调）。</summary>
    public event Action<TecSnapshot>? SnapshotReceived;

    /// <summary>每个轮询周期发布一次告警状态字（后台线程回调）。</summary>
    public event Action<TecErrorCode>? ErrorCodeReceived;

    /// <summary>轮询过程中发生通信异常时回调（不会终止轮询）。</summary>
    public event Action<Exception>? PollFaulted;

    // ---------- 实时数据 ----------

    public Task<TecSnapshot> ReadSnapshotAsync(CancellationToken ct = default) =>
        // 经 ExecParsedAsync：应答坏帧（如现场实测 '@' 被干扰打成 'P' 导致字段粘连）自动重发
        client.ExecParsedAsync($"{TecCmd.DataDemand}=2@\n", reply =>
        {
            var f = TecAscii.ParseFields(reply);

            long Get(string key) =>
                f.TryGetValue(key, out var v)
                    ? v
                    : throw new TecProtocolException($"DATADEMAND 应答缺少字段 {key}：{TecAscii.Printable(reply)}");

            return new TecSnapshot(
                DateTime.Now,
                TecScale.TempFromRaw(Get("TC1:TCADJTEMP")),
                TecScale.OhmsFromRaw(Get("TC1:RESISTOR")),
                TecScale.VoltsFromRaw(Get("TC1:OUTV")),
                TecScale.TempFromRaw(Get("TC2:TCADJTEMP")),
                TecScale.OhmsFromRaw(Get("TC2:RESISTOR")),
                TecScale.VoltsFromRaw(Get("TC2:OUTV")),
                Get(TecCmd.InternalTemp));
        }, ct);

    public async Task<TecErrorCode> ReadErrorCodeAsync(CancellationToken ct = default) =>
        (TecErrorCode)await client.QueryAsync(null, TecCmd.ErrorCode, ct).ConfigureAwait(false);

    // ---------- 通道参数 ----------

    public async Task<ChannelConfig> ReadChannelConfigAsync(int ch, CancellationToken ct = default) =>
        new()
        {
            TargetC = TecScale.TempFromRaw(await client.QueryAsync(ch, TecCmd.Target, ct).ConfigureAwait(false)),
            Enabled = await client.QueryAsync(ch, TecCmd.Enable, ct).ConfigureAwait(false) != 0,
            Mode = (int)await client.QueryAsync(ch, TecCmd.Mode, ct).ConfigureAwait(false),
            Kp = await client.QueryAsync(ch, TecCmd.Kp, ct).ConfigureAwait(false),
            Ki = await client.QueryAsync(ch, TecCmd.Ki, ct).ConfigureAwait(false),
            Kd = await client.QueryAsync(ch, TecCmd.Kd, ct).ConfigureAwait(false),
            MaxDutyPercent = (int)await client.QueryAsync(ch, TecCmd.MaxDuty, ct).ConfigureAwait(false),
            SpeedCPerSec = TecScale.SpeedFromRaw(await client.QueryAsync(ch, TecCmd.Speed, ct).ConfigureAwait(false)),
            OverTempUpC = TecScale.TempFromRaw(await client.QueryAsync(ch, TecCmd.OverTempUp, ct).ConfigureAwait(false)),
            OverTempLowerC = TecScale.TempFromRaw(await client.QueryAsync(ch, TecCmd.OverTempLower, ct).ConfigureAwait(false)),
            MaxCurrentA = TecScale.MaxCurrentFromRaw(await client.QueryAsync(ch, TecCmd.SetCurrent, ct).ConfigureAwait(false)),
            AutoPid = (int)await client.QueryAsync(ch, TecCmd.AutoPid, ct).ConfigureAwait(false),
        };

    public Task SetTargetAsync(int ch, double celsius, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.Target, TecScale.TempToRaw(celsius), ct);

    public Task SetEnableAsync(int ch, bool enable, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.Enable, enable ? 1 : 0, ct);

    public Task SetModeAsync(int ch, int mode, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.Mode, mode, ct);

    /// <summary>写输出占空比（PWMDUTY，±100%）。设备须处于 MODE=3（通信设定电压百分比）。</summary>
    public Task SetDutyPercentAsync(int ch, double percent, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.PwmDuty, TecScale.DutyPercentToRaw(Math.Clamp(percent, -100, 100)), ct);

    public async Task<double> ReadDutyPercentAsync(int ch, CancellationToken ct = default) =>
        TecScale.DutyPercentFromRaw(await client.QueryAsync(ch, TecCmd.PwmDuty, ct).ConfigureAwait(false));

    /// <summary>读取通道实测输出电流（A）。用于确认两组 TEC 是否都在出力。</summary>
    public async Task<double> ReadCurrentAsync(int ch, CancellationToken ct = default) =>
        TecScale.CurrentFromRaw(await client.QueryAsync(ch, TecCmd.Current, ct).ConfigureAwait(false));

    /// <summary>
    /// 读取 PWM 输出频率档位（FPWM，通用参数）：0=0.5Hz，1=1Hz，2=10Hz，3=100Hz。
    /// TEC 制冷量正比于 I 而焦耳热正比于 I²，电流脉动越小效率越高，低温工况建议用高频档。
    /// </summary>
    public async Task<int> ReadPwmFrequencyAsync(CancellationToken ct = default) =>
        (int)await client.QueryAsync(null, TecCmd.PwmFrequency, ct).ConfigureAwait(false);

    public Task SetPwmFrequencyAsync(int level, CancellationToken ct = default)
    {
        if (level is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(level), "FPWM 档位只能是 0~3");
        return client.SetAsync(null, TecCmd.PwmFrequency, level, ct);
    }

    /// <summary>
    /// 设置开机电源输出模式（POWERMODE）：0=跟随断电前状态（出厂默认），1=上电输出，2=上电关闭。
    /// 上位机控温一律应设 2：固件无通信看门狗，若断电重启后带旧占空比复活而上位机不在，
    /// 输出会无人监管地一直保持（对加热棒尤其危险）。
    /// </summary>
    public Task SetPowerModeAsync(int ch, int mode, CancellationToken ct = default)
    {
        if (mode is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(mode), "POWERMODE 只能是 0~2");
        return client.SetAsync(ch, TecCmd.PowerMode, mode, ct);
    }

    /// <summary>设置温控器模式选择（CONTMODE）。0=各通道独立（上位机镜像方案要求）。
    /// 注意：该固件对 CONTMODE 查询应答异常，写入是否生效以电流对称性验证为准。</summary>
    public Task SetControllerModeAsync(int mode, CancellationToken ct = default) =>
        client.SetAsync(null, TecCmd.ControllerMode, mode, ct);

    public async Task SetPidAsync(int ch, long kp, long ki, long kd, CancellationToken ct = default)
    {
        await client.SetAsync(ch, TecCmd.Kp, kp, ct).ConfigureAwait(false);
        await client.SetAsync(ch, TecCmd.Ki, ki, ct).ConfigureAwait(false);
        await client.SetAsync(ch, TecCmd.Kd, kd, ct).ConfigureAwait(false);
    }

    /// <summary>启动 PID 自整定（AUTOPID=1）；完成后设备会自动回到 0，可轮询 ReadAutoPidAsync 判断。</summary>
    public Task StartAutoTuneAsync(int ch, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.AutoPid, 1, ct);

    public async Task<int> ReadAutoPidAsync(int ch, CancellationToken ct = default) =>
        (int)await client.QueryAsync(ch, TecCmd.AutoPid, ct).ConfigureAwait(false);

    public Task SetMaxDutyAsync(int ch, int percent, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.MaxDuty, percent, ct);

    public Task SetSpeedAsync(int ch, double degPerSec, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.Speed, TecScale.SpeedToRaw(degPerSec), ct);

    public async Task SetOverTempAsync(int ch, double upC, double lowerC, CancellationToken ct = default)
    {
        await client.SetAsync(ch, TecCmd.OverTempUp, TecScale.TempToRaw(upC), ct).ConfigureAwait(false);
        await client.SetAsync(ch, TecCmd.OverTempLower, TecScale.TempToRaw(lowerC), ct).ConfigureAwait(false);
    }

    public Task SetMaxCurrentAsync(int ch, double amps, CancellationToken ct = default) =>
        client.SetAsync(ch, TecCmd.SetCurrent, TecScale.MaxCurrentToRaw(amps), ct);

    // ---------- 设备信息 ----------

    /// <summary>
    /// 读取型号/固件/温控器模式。
    /// 注意：协议表中 TEC 寄存器标为数值代码，但实测 ASCII 应答直接返回型号名（如 "215L"），按文本处理；
    /// 固件与 CONTMODE 同样容错为文本，能解析为数字时再格式化。
    /// </summary>
    public async Task<(string Model, string Firmware, int? ControllerMode)> ReadDeviceInfoAsync(CancellationToken ct = default)
    {
        // 逐字段容错：个别查询固件不支持/格式意外时不影响其余字段（通信超时仍然抛出）
        string model;
        try { model = await client.QueryTextAsync(null, TecCmd.Model, ct).ConfigureAwait(false); }
        catch (TecProtocolException) { model = "未知"; }

        string firmware;
        try
        {
            var fpvText = await client.QueryTextAsync(null, TecCmd.FirmwareVersion, ct).ConfigureAwait(false);
            firmware = long.TryParse(fpvText, out var f) ? $"v{f / 100}.{f / 10 % 10}.{f % 10}" : fpvText;
        }
        catch (TecProtocolException) { firmware = "未知"; }

        int? contMode = null;
        try
        {
            var cmText = await client.QueryTextAsync(null, TecCmd.ControllerMode, ct).ConfigureAwait(false);
            if (long.TryParse(cmText, out var cm)) contMode = (int)cm;
        }
        catch (TecProtocolException) { /* 该固件可能不支持 CONTMODE 查询 */ }

        return (model, firmware, contMode);
    }

    // ---------- 后台轮询 ----------

    public bool IsPolling => _pollTask is { IsCompleted: false };

    public void StartPolling(TimeSpan period)
    {
        StopPolling();
        _pollCts = new CancellationTokenSource();
        var ct = _pollCts.Token;
        _pollTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var cycleStart = Environment.TickCount64;
                try
                {
                    var snapshot = await ReadSnapshotAsync(ct).ConfigureAwait(false);
                    SnapshotReceived?.Invoke(snapshot);

                    var error = await ReadErrorCodeAsync(ct).ConfigureAwait(false);
                    ErrorCodeReceived?.Invoke(error);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    PollFaulted?.Invoke(ex);
                }

                var elapsed = Environment.TickCount64 - cycleStart;
                var remaining = (int)(period.TotalMilliseconds - elapsed);
                if (remaining > 0)
                {
                    try { await Task.Delay(remaining, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }, ct);
    }

    public void StopPolling()
    {
        _pollCts?.Cancel();
        try { _pollTask?.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { /* 轮询任务取消 */ }
        _pollCts?.Dispose();
        _pollCts = null;
        _pollTask = null;
    }

    public void Dispose() => StopPolling();
}
