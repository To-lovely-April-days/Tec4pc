using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecControl.Core;
using TecControl.Core.Control;

namespace TecControl.App.ViewModels;

/// <summary>
/// 单个通道的控制面板视图模型（上位机自研 PID 控温）。
/// 数值编辑框统一用字符串承载，写入前解析校验。
/// </summary>
/// <param name="saveGainSchedule">把增益表写盘（返回是否成功），每次改动后立即调用。</param>
public partial class ChannelViewModel(
    int channel,
    string displayName,
    Func<TecController?> getController,
    Func<HostControlLoop?> getLoop,
    Action<string> setStatus,
    Func<bool> saveGainSchedule) : ObservableObject
{
    public int Channel { get; } = channel;
    public string DisplayName { get; } = displayName;

    // ---- 实时显示 ----
    [ObservableProperty] private string pvText = "--";
    [ObservableProperty] private string dutyText = "--";
    [ObservableProperty] private string outVoltsText = "--";
    [ObservableProperty] private string pidTermsText = "";
    [ObservableProperty] private bool controlRunning;

    // ---- 运行面板：偏差、输出条、稳定度、总体状态 ----
    private readonly StabilityMonitor _stability = new(300);

    // 真实温度口径：10s 滑动平均消除测温电路噪声（真温度 10s 内最多动 ~2mK，
    // 平均只消噪声不掩盖真实变化——见 SlidingAverage 注释里的实测论证），
    // 并对均值再做一套稳定度统计。原始读数与 CSV 记录不受影响，两口径并列显示。
    private readonly SlidingAverage _pvAvg = new(10);
    private readonly StabilityMonitor _stabilityAvg = new(300);

    [ObservableProperty] private string pvAvgText = "--";
    [ObservableProperty] private string stabilityAvgText = "未运行";

    [ObservableProperty] private string deviationText = "--";
    [ObservableProperty] private double dutyBarValue;          // 0~100，进度条用
    [ObservableProperty] private string dutyDirectionText = "";  // 制冷 / 加热 / 停止
    [ObservableProperty] private string stabilityText = "等待数据…";
    [ObservableProperty] private string runStateText = "未启动";
    [ObservableProperty] private string runStateColor = "#888888";

    // ---- 反馈传感器选择（执行器作用在腔体壁上，快环反馈推荐用 TC2 腔体探头） ----
    public string[] FeedbackSensorNames { get; } = ["TC2 腔体（推荐）", "TC1 内部"];
    [ObservableProperty] private int feedbackSensorIndex;
    private int SensorNumber => FeedbackSensorIndex == 0 ? 2 : 1;

    // ---- 控温参数（上位机 PID） ----
    [ObservableProperty] private string targetInput = "25.0";

    partial void OnTargetInputChanged(string value)
    {
        RefreshFeedforwardHint();
        RefreshGainScheduleHint();
    }
    [ObservableProperty] private string kpInput = "10";
    [ObservableProperty] private string kiInput = "0.1";
    [ObservableProperty] private string kdInput = "0";
    // 上位机输出上限与设备固件天花板（LIMITED 寄存器 0~90）保持一致：
    // 若上位机上限高于设备限幅，占空比会被固件悄悄截断，内环"是否饱和"的判断随之失真
    [ObservableProperty] private string maxDutyInput = "90";
    [ObservableProperty] private bool invertOutput;

    // ---- 执行器形态（手动换线过渡：DAM 路由模块到货前，>100℃ 段人工换接加热棒） ----
    [ObservableProperty] private bool heaterMode;

    /// <summary>正在把被拒绝的勾选改回去（抑制回退赋值触发的二次处理）。</summary>
    private bool _revertingHeaterMode;

    partial void OnHeaterModeChanged(bool value)
    {
        if (_revertingHeaterMode) return;
        var loop = getLoop();
        if (loop is null) return;   // 未连接时先记住勾选，启动前 ApplyPid 会统一下发
        try
        {
            loop.SetActuatorMode(Channel, value ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional);
            setStatus(value
                ? $"通道{Channel} 已切换为电加热棒（只加热）模式。启动前务必核对接线与保护：" +
                  "①TEC 功率线已断开；②加热棒经固态继电器接 PWM2/GND；③使能保持接通；" +
                  "④双通道过温上限已按目标温度改写——设备无通信看门狗（断线时输出保持），过温保护是唯一兜底"
                : $"通道{Channel} 已切换回 TEC 双向模式。请确认 TEC 功率线已接回、加热棒市电已断开，" +
                  "PWM 频率将在下次启动时自动恢复高频档");
        }
        catch (Exception ex)
        {
            // 运行中禁止切换：勾选回退到实际状态
            _revertingHeaterMode = true;
            try { HeaterMode = !value; }
            finally { _revertingHeaterMode = false; }
            setStatus(ex.Message);
        }
    }

    // ---- 增益调度（多温区参数表，由自整定自动建立，也可手工编辑） ----
    [ObservableProperty] private bool gainSchedulingEnabled = true;
    [ObservableProperty] private string gainScheduleHint = "";
    [ObservableProperty] private string heatRatioInput = "1.0";

    /// <summary>增益表行（界面 DataGrid 绑定，可直接编辑）。</summary>
    public ObservableCollection<GainPointRow> GainPoints { get; } = [];

    [ObservableProperty] private GainPointRow? selectedGainPoint;
    [ObservableProperty] private string interpolatedGainsText = "";

    // ---- 启动加速：积分预置（界面按显示符号，即制冷为正时填正值） ----
    [ObservableProperty] private bool useIntegralPreset = true;
    [ObservableProperty] private string integralPresetInput = "";
    [ObservableProperty] private string steadyOutputHint = "";

    // ---- 串级控温（Tr 模式：外环控内部温度 TC1，输出腔体温度设定值） ----
    [ObservableProperty] private bool cascadeEnabled;
    // 默认值对应 −10℃ 实测 Tu≈187s：Ti=1.7·Tu → Ki=2.5/318≈0.008（见 PidGainSchedule.SuggestOuter）。
    // 这里是"增益表没登记外环参数时"的兜底值，正常应由增益表按温度调度。
    [ObservableProperty] private string outerKpInput = "2.5";
    [ObservableProperty] private string outerKiInput = "0.008";
    [ObservableProperty] private string outerKdInput = "0";
    [ObservableProperty] private string outerMaxBiasInput = "8";
    [ObservableProperty] private string cascadeStateText = "";

    // ---- 温度曲线（结晶模式） ----
    public string[] ProfileKindNames { get; } = ["分段曲线（线性/阶梯）", "自定义曲线（CSV）"];
    [ObservableProperty] private int profileKindIndex;
    [ObservableProperty] private string profileSegmentsInput = "-10, 0.5, 60";
    [ObservableProperty] private string profileCsvPath = "";
    [ObservableProperty] private string profileStateText = "";
    [ObservableProperty] private bool profileRunning;

    // ---- 手动输出（阶跃测试用） ----
    [ObservableProperty] private string manualDutyInput = "0";

    // ---- 自整定 ----
    [ObservableProperty] private string relayAmpInput = "30";
    [ObservableProperty] private string hysteresisInput = "0.05";
    [ObservableProperty] private bool tuningRunning;
    [ObservableProperty] private string tuneStatusText = "";

    // ---- 设备端硬件保护 ----
    [ObservableProperty] private string hwMaxDutyInput = "";
    [ObservableProperty] private string hwMaxCurrentInput = "";
    [ObservableProperty] private string overTempUpInput = "";
    [ObservableProperty] private string overTempLowerInput = "";

    public void UpdateRealtime(double tempC, double outVolts, ChannelCycleInfo info)
    {
        PvText = double.IsNaN(tempC) ? "未接传感器" : tempC.ToString("F3", CultureInfo.InvariantCulture);
        OutVoltsText = outVolts.ToString("F3", CultureInfo.InvariantCulture);
        DutyText = info.DutyPercent.ToString("F1", CultureInfo.InvariantCulture);
        ControlRunning = info.Active;
        TuningRunning = info.Tuning;

        UpdateRunPanel(tempC, info);
        if (info.Tuning)
            TuneStatusText = $"自整定中：已完成 {info.TuneCycles} 个振荡周期（一般需 4 个）";
        PidTermsText = info.Active
            ? $"P {info.PTerm:F2}  I {info.ITerm:F2}  D {info.DTerm:F2}"
            : "";

        CascadeStateText = info.Cascade
            ? $"串级运行中：内部 {info.OuterMeasuredC:F3}℃ → 腔体设定 {info.InnerSetpointC:F3}℃"
            : "";

        ProfileRunning = info.ProfileProgress is not null && info.ProfileName is not null;
        ProfileStateText = ProfileRunning
            ? $"曲线「{info.ProfileName}」进行中 {info.ProfileProgress * 100:F1}%，当前设定 {info.SetpointC:F3}℃"
            : ProfileStateText;

        RefreshFeedforwardHint();
    }

    /// <summary>刷新运行面板：偏差、输出条、稳定度与总体状态。</summary>
    private void UpdateRunPanel(double tempC, ChannelCycleInfo info)
    {
        var target = info.Cascade ? info.SetpointC : info.InnerSetpointC;
        if (double.IsNaN(target)) target = info.SetpointC;

        // 偏差（串级时以内部温度对主设定值计，与用户关心的量一致）
        var pv = info.Cascade && !double.IsNaN(info.OuterMeasuredC) ? info.OuterMeasuredC : tempC;
        DeviationText = double.IsNaN(pv) || !(info.Active || info.Tuning)
            ? "--"
            : $"{pv - target:+0.000;-0.000;0.000} ℃";

        // 真实温度（10s 均值）：随时可看，不依赖控温状态
        var avg = _pvAvg.Add(DateTime.Now, pv);
        PvAvgText = double.IsNaN(avg)
            ? "--"
            : $"{avg:F4} ℃" + (_pvAvg.IsWindowFull ? "" : "（窗口未满）");

        DutyBarValue = Math.Min(100, Math.Abs(info.DutyPercent));
        DutyDirectionText = Math.Abs(info.DutyPercent) < 0.05
            ? "无输出"
            : (info.DutyPercent > 0 ? "制冷" : "加热");

        // 稳定度（仅闭环运行时累计；停止或自整定时重置）
        if (info.Active && !double.IsNaN(pv))
        {
            _stability.Add(DateTime.Now, pv);
            if (_stability.Compute(target) is { } st)
            {
                var full = _stability.IsWindowFull ? "" : "（窗口未满）";
                // 距设定 = 窗口内离设定值最远的偏差——验收"是否在 ±X℃ 内"直接看它；
                // ± 值只是波动带半宽，均值偏离设定值时它会显得比实际乐观
                StabilityText = $"距设定最大 {st.MaxDeviationC:F3}℃  峰峰 {st.PeakToPeakC:F3}℃" +
                                $"  σ={st.StdDevC:F3}℃  [{st.MinC:F3} ~ {st.MaxC:F3}]{full}";
            }

            // 真实温度口径的稳定度：对 10s 均值再统计（写规格时注明"10s 平均"）
            if (!double.IsNaN(_pvAvg.Value))
            {
                _stabilityAvg.Add(DateTime.Now, _pvAvg.Value);
                if (_stabilityAvg.Compute(target) is { } sa)
                {
                    var fullA = _stabilityAvg.IsWindowFull ? "" : "（窗口未满）";
                    StabilityAvgText = $"距设定最大 {sa.MaxDeviationC:F4}℃  峰峰 {sa.PeakToPeakC:F4}℃" +
                                       $"  σ={sa.StdDevC:F4}℃{fullA}";
                }
            }
        }
        else if (!info.Active)
        {
            _stability.Reset();
            _stabilityAvg.Reset();
            StabilityText = "未运行";
            StabilityAvgText = "未运行";
        }

        UpdateStableState(info, pv, target);

        (RunStateText, RunStateColor) = info switch
        {
            { Tuning: true } => ("自整定中", "#C77800"),
            { Active: true } when info.ProfileProgress is not null => ("曲线执行中", "#0B72B5"),
            { Active: true } when _stableSince is { } since =>
                ($"控温中 · 已稳定 {(DateTime.Now - since).TotalMinutes:F0} 分钟", "#1E8E3E"),
            { Active: true } => ("控温中 · 趋近目标", "#C77800"),
            _ => ("未启动", "#888888"),
        };
    }

    // ---- 判稳状态机（带驻留与回差，杜绝边界抖动导致的"一会稳定一会不稳定"）----
    private const double StableEnterBandC = 0.05;   // 进入判稳带
    private const double StableExitBandC = 0.08;    // 退出带（比进入宽，构成回差）
    private const double StableEnterDwellS = 60;    // 带内驻留满 60s 才算稳定
    private const double StableExitDwellS = 10;     // 出带持续 10s 才取消稳定（瞬时尖峰不摘牌）

    private DateTime? _stableSince;          // 非 null = 当前处于"已稳定"，值为进入时刻
    private DateTime? _enterCandidateSince;  // 连续在带内的起始时刻
    private DateTime? _exitCandidateSince;   // 连续出带的起始时刻
    private double _stableMinC, _stableMaxC; // 自进入稳定以来的实测极值

    [ObservableProperty] private string stableRangeText = "";

    private void UpdateStableState(ChannelCycleInfo info, double pv, double target)
    {
        var now = DateTime.Now;
        if (!info.Active || info.Tuning || double.IsNaN(pv) || double.IsNaN(target))
        {
            _stableSince = _enterCandidateSince = _exitCandidateSince = null;
            StableRangeText = "";
            return;
        }

        var dev = Math.Abs(pv - target);
        if (_stableSince is null)
        {
            _enterCandidateSince = dev <= StableEnterBandC ? _enterCandidateSince ?? now : null;
            if (_enterCandidateSince is { } cand && (now - cand).TotalSeconds >= StableEnterDwellS)
            {
                _stableSince = cand;
                _exitCandidateSince = null;
                _stableMinC = _stableMaxC = pv;
            }
        }
        else
        {
            _stableMinC = Math.Min(_stableMinC, pv);
            _stableMaxC = Math.Max(_stableMaxC, pv);
            _exitCandidateSince = dev > StableExitBandC ? _exitCandidateSince ?? now : null;
            if (_exitCandidateSince is { } cand && (now - cand).TotalSeconds >= StableExitDwellS)
            {
                _stableSince = null;
                _enterCandidateSince = null;
                StableRangeText = "";
                return;
            }
        }

        StableRangeText = _stableSince is { } since
            ? $"稳定 {(DateTime.Now - since).TotalMinutes:F0} 分钟以来：{_stableMinC:F3} ~ {_stableMaxC:F3} ℃" +
              $"（距设定 {_stableMinC - target:+0.000;-0.000}/{_stableMaxC - target:+0.000;-0.000}，" +
              $"带宽 {(_stableMaxC - _stableMinC) * 1000:F0} mK）"
            : "";
    }

    /// <summary>应用串级配置（可在运行中调用）。</summary>
    [RelayCommand]
    private void ApplyCascade()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }

        if (!CascadeEnabled)
        {
            loop.SetStrategy(Channel, ControlStrategy.Direct);
            CascadeStateText = "";
            setStatus($"通道{Channel} 已切换为单环控温（直接控制反馈传感器温度）");
            return;
        }

        if (!TryParseDouble(OuterKpInput, "外环 Kp", out var kp) ||
            !TryParseDouble(OuterKiInput, "外环 Ki", out var ki) ||
            !TryParseDouble(OuterKdInput, "外环 Kd", out var kd) ||
            !TryParseDouble(OuterMaxBiasInput, "最大偏置", out var bias)) return;
        if (bias is <= 0 or > 60) { setStatus("最大偏置建议 1~60℃"); return; }

        // 外环用另一支探头：内环用 TC2 时外环用 TC1，反之亦然
        var outerSensor = SensorNumber == 2 ? 1 : 2;
        loop.ConfigureCascade(Channel, kp, ki, kd, bias, outerSensor);
        loop.SetStrategy(Channel, ControlStrategy.Cascade);
        setStatus($"通道{Channel} 已启用串级：外环 TC{outerSensor}（Kp={kp} Ki={ki} Kd={kd}，偏置±{bias}℃）→ 内环 TC{SensorNumber}");
    }

    /// <summary>启动温度曲线（结晶降温等）。</summary>
    [RelayCommand]
    private void StartProfile()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }

        try
        {
            ITemperatureProfile profile = ProfileKindIndex == 0
                ? ParseSegments(ProfileSegmentsInput)
                : TableProfile.FromCsv(File.ReadAllText(ProfileCsvPath), Path.GetFileName(ProfileCsvPath));

            loop.StartProfile(Channel, profile);
            ProfileRunning = true;
            var total = profile.TotalMinutes(loop.GetChannelStatus(Channel).SetpointC);
            setStatus($"通道{Channel} 已启动曲线「{profile.Name}」，预计时长 {total:F1} 分钟" +
                      (ControlRunning ? "" : "（注意：控温尚未启动，曲线只更新设定值）"));
        }
        catch (Exception ex)
        {
            setStatus($"启动曲线失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void StopProfile()
    {
        getLoop()?.StopProfile(Channel);
        ProfileRunning = false;
        ProfileStateText = "曲线已停止，设定值保持在当前值";
        setStatus($"通道{Channel} 曲线已停止");
    }

    /// <summary>解析分段曲线文本：每行 "目标温度, 速率℃/min, 保温分钟"。</summary>
    private static SegmentProfile ParseSegments(string text)
    {
        var segs = new List<ProfileSegment>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
                throw new FormatException($"曲线段格式应为“目标温度, 速率℃/min[, 保温分钟]”，无法解析：{line}");

            if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var target) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
                throw new FormatException($"曲线段数值无效：{line}");

            var hold = 0.0;
            if (parts.Length >= 3 && parts[2].Length > 0 &&
                !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out hold))
                throw new FormatException($"保温时长无效：{line}");

            segs.Add(new ProfileSegment(target, rate, hold));
        }
        if (segs.Count == 0) throw new FormatException("未解析到任何曲线段");
        return new SegmentProfile(segs, segs.Count == 1 ? "线性降温" : "阶梯降温");
    }

    /// <summary>从控制层重新载入增益表到界面表格。</summary>
    public void RefreshGainScheduleHint()
    {
        var loop = getLoop();
        if (loop is null) return;
        var sched = loop.GetGainSchedule(Channel);

        GainPoints.Clear();
        foreach (var pt in sched.Points)   // 已按温度降序
            GainPoints.Add(GainPointRow.From(pt));

        GainScheduleHint = sched.Count == 0
            ? "增益表为空：使用上方手动参数。在代表温度各自整定一次即可自动建表"
            : $"共 {sched.Count} 个温度点，覆盖 {sched.Points.Min(x => x.TemperatureC):F1} ~ " +
              $"{sched.Points.Max(x => x.TemperatureC):F1}℃";

        if (double.TryParse(TargetInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var t)
            && sched.GainsAt(t) is { } g)
        {
            var line = $"当前目标 {t:F1}℃ → 内环 Kp={g.Kp:F3}　Ki={g.Ki:F5}　Kd={g.Kd:F2}";
            if (sched.OuterAt(t) is { } o)
                line += $"\n外环 Kp={o.Gains.Kp:F2}　Ki={o.Gains.Ki:F5}　偏置±{o.MaxBiasC:F1}℃";
            if (sched.SteadyBiasAt(t) is { } b)
                line += $"　稳态偏置 {b:+0.00;-0.00;0.00}℃（启动时预置）";
            if (sched.Count == 1) line += "（仅 1 点，全温域沿用）";
            InterpolatedGainsText = line;
        }
        else
        {
            InterpolatedGainsText = "";
        }
    }

    /// <summary>
    /// 界面“重新载入”：从磁盘重读增益表（用户表；不存在或为空时退回出厂表），
    /// 丢弃表格上的未应用编辑。Excel 手工改完 CSV 后点此即可生效，不必重连。
    /// </summary>
    [RelayCommand]
    private void RefreshGainSchedule()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }

        var sched = loop.GetGainSchedule(Channel);
        sched.Clear();
        var from = GainScheduleStore.LoadOrSeed(sched);
        var filled = sched.EnsureOuterGains();
        RefreshGainScheduleHint();

        if (from is null)
        {
            setStatus("磁盘上没有可读的增益表（用户表与出厂表均无数据），表格为空");
            return;
        }
        // 从出厂表打底时立即写成用户表，让后续学习在其上累积
        if (from != GainScheduleStore.DefaultPath) saveGainSchedule();
        setStatus($"增益表已从磁盘重新载入（{sched.Count} 个温度点" +
                  (filled > 0 ? $"，{filled} 行自动补全外环参数）" : "）") +
                  (from != GainScheduleStore.DefaultPath ? "，来源：出厂表" : ""));
    }

    /// <summary>把界面表格内容写回控制层（手工编辑后调用）。</summary>
    [RelayCommand]
    private void ApplyGainPoints()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }

        var sched = loop.GetGainSchedule(Channel);
        sched.Clear();
        foreach (var r in GainPoints)
            sched.Learn(r.ToPoint());

        // 没填外环参数的行按各自 Tu 自动补全，免得漏填导致该温区退回手动外环值
        var filled = sched.EnsureOuterGains();
        RefreshGainScheduleHint();
        setStatus($"增益表已应用：{sched.Count} 个温度点" +
                  (filled > 0 ? $"（其中 {filled} 行自动补全了外环参数）" : "") +
                  (saveGainSchedule() ? $"，已保存到 {GainScheduleStore.DefaultPath}" : ""));
    }

    /// <summary>新增一行（以当前目标温度与当前 PID 参数为初值）。</summary>
    [RelayCommand]
    private void AddGainPoint()
    {
        var t = double.TryParse(TargetInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        var kp = double.TryParse(KpInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 10;
        var ki = double.TryParse(KiInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 0.1;
        var kd = double.TryParse(KdInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;

        // 外环列以“工艺”页当前的手动外环参数为初值，不填的话该点就没有外环参数可插值
        var okp = double.TryParse(OuterKpInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 2.5;
        var oki = double.TryParse(OuterKiInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0.008;
        var okd = double.TryParse(OuterKdInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
        var bias = double.TryParse(OuterMaxBiasInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g : 8;

        GainPoints.Add(new GainPointRow
        {
            TemperatureC = t, Kp = kp, Ki = ki, Kd = kd,
            OuterKp = okp, OuterKi = oki, OuterKd = okd, OuterMaxBiasC = Math.Abs(bias),
        });
        setStatus($"已新增 {t:F1}℃ 工作点（含外环参数），编辑后请点“应用表格”");
    }

    [RelayCommand]
    private void RemoveGainPoint()
    {
        if (SelectedGainPoint is null) { setStatus("请先在表格中选中要删除的行"); return; }
        GainPoints.Remove(SelectedGainPoint);
        setStatus("已从表格移除该行，点“应用表格”后生效");
    }

    partial void OnGainSchedulingEnabledChanged(bool value)
    {
        if (getLoop() is { } loop)
        {
            loop.GainSchedulingEnabled = value;
            setStatus(value ? "已启用增益调度（按设定值自动插值参数）" : "已关闭增益调度（固定使用手动参数）");
        }
    }

    /// <summary>清空增益表（回到单组手动参数）。</summary>
    [RelayCommand]
    private void ClearGainSchedule()
    {
        getLoop()?.GetGainSchedule(Channel).Clear();
        RefreshGainScheduleHint();
        saveGainSchedule();
        setStatus($"通道{Channel} 增益表已清空（回到单组手动参数），文件同步清空");
    }

    /// <summary>应用加热/制冷有效度比值（执行器不对称归一化）。</summary>
    [RelayCommand]
    private void ApplyHeatRatio()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(HeatRatioInput, "加热有效度比值", out var ratio)) return;
        if (ratio is <= 0.05 or > 20) { setStatus("加热有效度比值应在 0.05~20 之间"); return; }
        loop.HeatingEffectivenessRatio = ratio;
        setStatus($"加热有效度比值已设为 {ratio:F2}（全局兜底值；增益表行内填了加热比列的温区按表调度优先）");
    }

    /// <summary>刷新前馈模型提示：显示对当前目标温度的预测与已学习的工作点。</summary>
    public void RefreshFeedforwardHint()
    {
        var loop = getLoop();
        if (loop is null) return;

        var parts = new List<string>();
        if (double.TryParse(TargetInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var target) &&
            loop.PredictSteadyOutput(Channel, target) is double predicted)
        {
            // 只加热模式下反向输出被忽略，显示换算同样跳过
            var shown = InvertOutput && !HeaterMode ? -predicted : predicted;
            parts.Add($"预测 {target:F1}℃ 需 {shown:F1}%");
        }
        parts.Add(loop.GetFeedforward(Channel).Describe());
        SteadyOutputHint = string.Join(" · ", parts);
    }

    /// <summary>启动继电器法自整定。</summary>
    [RelayCommand]
    private async Task StartAutoTuneAsync()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(TargetInput, "目标温度", out var target) ||
            !TryParseDouble(RelayAmpInput, "继电幅值", out var amp) ||
            !TryParseDouble(HysteresisInput, "回差", out var hys)) return;
        if (amp is < 5 or > 100) { setStatus("继电幅值建议 5~100%（太小激不起振荡）"); return; }
        if (hys is < 0.01 or > 5) { setStatus("回差范围 0.01~5℃（建议为温度噪声的 3~5 倍）"); return; }

        try
        {
            // 执行器形态与反馈传感器先同步到控制层（勾选可能是在未连接时做的）：
            // 只加热模式的整定器需要输出下限 0，否则弱半周会送出负占空比
            loop.SetActuatorMode(Channel, HeaterMode ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional);
            loop.SetFeedbackSensor(Channel, SensorNumber);
            await loop.StartAutoTuneAsync(Channel, target, amp, hys);
            TuningRunning = true;
            TuneStatusText = "自整定中：等待温度穿越设定值…";
            setStatus($"通道{Channel} 自整定已启动（目标 {target:F2}℃，继电幅值 ±{amp:F0}%，回差 ±{hys:F3}℃）" +
                      (HeaterMode ? "。只加热模式：摆动全程为正，弱半周靠自然散热，周期会比 TEC 整定慢" : ""));
        }
        catch (Exception ex)
        {
            setStatus($"通道{Channel} 启动自整定失败：{ex.Message}");
        }
    }

    /// <summary>自整定结束回调（由 MainViewModel 在 UI 线程调用）。</summary>
    public void OnAutoTuneFinished(AutoTuneOutcome outcome)
    {
        TuningRunning = false;
        if (!outcome.Success || outcome.Result is null)
        {
            TuneStatusText = $"自整定失败：{outcome.Reason}";
            setStatus($"通道{Channel} 自整定失败：{outcome.Reason}");
            return;
        }

        var r = outcome.Result;
        // 默认填入保守（Tyreus–Luyben）参数——温控推荐，平稳无超调优先
        KpInput = r.Conservative.Kp.ToString("F3", CultureInfo.InvariantCulture);
        KiInput = r.Conservative.Ki.ToString("F4", CultureInfo.InvariantCulture);
        KdInput = r.Conservative.Kd.ToString("F3", CultureInfo.InvariantCulture);
        ApplyPid();

        RefreshGainScheduleHint();
        TuneStatusText = $"自整定完成：Ku={r.UltimateGainKu:F2}，Tu={r.UltimatePeriodTuSeconds:F1}s，" +
                         "已填入平稳型参数并登记到增益表";
        setStatus($"通道{Channel} 自整定完成，PID 参数已自动填入并应用，可直接启动控温");

        System.Windows.MessageBox.Show(
            $"通道{Channel} 自整定完成\n\n" +
            $"临界增益 Ku = {r.UltimateGainKu:F3}\n" +
            $"临界周期 Tu = {r.UltimatePeriodTuSeconds:F1} s\n" +
            $"振荡振幅 = ±{r.OscillationAmplitudeC:F4} ℃（继电幅值 ±{r.RelayAmplitudePercent:F0}%）\n\n" +
            $"【平稳型·推荐】{r.Conservative}\n" +
            $"【快速型·可能超调】{r.Fast}\n\n" +
            "平稳型参数已自动填入并应用。若响应太慢，可手动改用快速型参数。",
            "PID 自整定结果", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
    }

    /// <summary>把界面上的 PID 参数下发到控制循环；控温运行中也可调用（在线调参）。</summary>
    [RelayCommand]
    private void ApplyPid()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(KpInput, "Kp", out var kp) ||
            !TryParseDouble(KiInput, "Ki", out var ki) ||
            !TryParseDouble(KdInput, "Kd", out var kd) ||
            !TryParseDouble(MaxDutyInput, "输出上限", out var maxDuty)) return;
        if (maxDuty is <= 0 or > 90)
        {
            setStatus("输出上限范围为 0~90%（设备固件占空比上限即 90，填更高只会让饱和判断失真）");
            return;
        }

        // 执行器形态先于 PID 限幅下发：只加热模式的输出下限为 0（负半轴不存在）
        try { loop.SetActuatorMode(Channel, HeaterMode ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional); }
        catch (Exception ex) { setStatus(ex.Message); return; }
        loop.ConfigurePid(Channel, kp, ki, kd, maxDuty, InvertOutput);
        loop.SetFeedbackSensor(Channel, SensorNumber);
        setStatus($"通道{Channel} PID 参数已应用（Kp={kp} Ki={ki} Kd={kd}，上限 {maxDuty}%" +
                  (HeaterMode ? "，只加热 0~上限" : "") + $"，反馈 TC{SensorNumber}）");
    }

    /// <summary>启动上位机闭环控温。</summary>
    [RelayCommand]
    private async Task StartControlAsync()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(TargetInput, "目标温度", out var target)) return;

        // 交叉检查：目标温度必须落在设备过温保护窗口内，否则设备会在到温前断输出
        if (double.TryParse(OverTempUpInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var otUp) &&
            double.TryParse(OverTempLowerInput, NumberStyles.Float, CultureInfo.InvariantCulture, out var otLow))
        {
            if (target > otUp - 5 || target < otLow + 5)
            {
                setStatus($"目标 {target:F1}℃ 超出或过于接近设备过温保护窗口（{otLow:F1} ~ {otUp:F1}℃）：" +
                          "请到「设置」页放宽过温上/下限（建议比目标温度各留 10℃ 余量）后再启动");
                return;
            }
        }

        try
        {
            ApplyPid();

            // 积分预置：界面输入的是显示符号，需换算回 PID 内部符号。
            // 只加热模式下反向输出被忽略，换算同样跳过（否则正预置会被反成负、再被下限截掉）
            var inv = InvertOutput && !HeaterMode;
            double? preset = null;
            if (UseIntegralPreset && !string.IsNullOrWhiteSpace(IntegralPresetInput))
            {
                if (!TryParseDouble(IntegralPresetInput, "积分预置", out var shown)) return;
                preset = inv ? -shown : shown;
            }
            else if (!UseIntegralPreset)
            {
                preset = 0; // 明确要求从零开始
            }

            await loop.StartChannelAsync(Channel, target, preset);
            ControlRunning = true;
            var presetText = UseIntegralPreset
                ? (preset is double p
                    ? $"，积分预置 {(inv ? -p : p):F1}%（手动）"
                    : loop.PredictSteadyOutput(Channel, target) is double ff
                        ? $"，积分预置 {(inv ? -ff : ff):F1}%（前馈预测）"
                        : loop.GetLastSteadyOutput(Channel) is double s2
                            ? $"，积分预置 {(inv ? -s2 : s2):F1}%（上次稳态）"
                            : "，暂无学习数据（本次从零累积，稳定后即自动学会）")
                : "，积分从零累积";
            setStatus($"通道{Channel} 已启动上位机控温，目标 {target:F5}℃{presetText}");
        }
        catch (Exception ex)
        {
            setStatus($"通道{Channel} 启动控温失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task StopControlAsync()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        try
        {
            await loop.StopChannelAsync(Channel);
            ControlRunning = false;
            TuningRunning = false;
            TuneStatusText = "";
            setStatus($"通道{Channel} 已停止控温/自整定，输出归零");
        }
        catch (Exception ex)
        {
            setStatus($"通道{Channel} 停止控温异常：{ex.Message}");
        }
    }

    /// <summary>控温运行中修改目标温度。</summary>
    [RelayCommand]
    private void WriteTarget()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(TargetInput, "目标温度", out var target)) return;
        loop.SetSetpoint(Channel, target);
        setStatus($"通道{Channel} 目标温度已更新为 {target:F5}℃");
    }

    /// <summary>手动写输出占空比（开环阶跃测试，用于辨识对象特性/整定参数）。</summary>
    [RelayCommand]
    private async Task ManualDutyAsync()
    {
        var loop = getLoop();
        if (loop is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseDouble(ManualDutyInput, "手动占空比", out var duty)) return;
        if (duty is < -100 or > 100) { setStatus("手动占空比范围为 -100~100"); return; }

        try
        {
            // 先同步执行器形态（勾选可能是在未连接时做的）：只加热模式下负值会被拒绝
            loop.SetActuatorMode(Channel, HeaterMode ? ActuatorMode.HeatOnly : ActuatorMode.Bidirectional);
            await loop.ManualDutyAsync(Channel, duty);
            setStatus($"通道{Channel} 手动输出 {duty:F2}%（开环）");
        }
        catch (Exception ex)
        {
            setStatus($"通道{Channel} 手动输出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 读取设备端硬件保护参数。四片 TEC 经两路输出镜像驱动同一热负载，
    /// 两通道的保护参数必须一致——两路都读，发现不一致立即提醒
    /// （通道 2 限幅偏低会表现为其电流始终比通道 1 小一截）。
    /// </summary>
    [RelayCommand]
    private async Task ReadHwConfigAsync()
    {
        var controller = getController();
        if (controller is null) { setStatus("尚未连接设备"); return; }
        try
        {
            var cfg = await controller.ReadChannelConfigAsync(1);
            var cfg2 = await controller.ReadChannelConfigAsync(2);
            HwMaxDutyInput = cfg.MaxDutyPercent.ToString(CultureInfo.InvariantCulture);
            HwMaxCurrentInput = cfg.MaxCurrentA.ToString("F1", CultureInfo.InvariantCulture);
            OverTempUpInput = cfg.OverTempUpC.ToString("F2", CultureInfo.InvariantCulture);
            OverTempLowerInput = cfg.OverTempLowerC.ToString("F2", CultureInfo.InvariantCulture);

            var mismatch = cfg.MaxDutyPercent != cfg2.MaxDutyPercent
                || Math.Abs(cfg.MaxCurrentA - cfg2.MaxCurrentA) > 0.05
                || Math.Abs(cfg.OverTempUpC - cfg2.OverTempUpC) > 0.05
                || Math.Abs(cfg.OverTempLowerC - cfg2.OverTempLowerC) > 0.05;
            setStatus(mismatch
                ? $"警告：两通道硬件保护不一致！通道2 占空比上限 {cfg2.MaxDutyPercent}%、" +
                  $"限流 {cfg2.MaxCurrentA:F1}A（显示为通道1 的值）。点“写入”即可把两通道同步"
                : "两通道硬件保护参数已读取（一致）");
        }
        catch (Exception ex)
        {
            setStatus($"读取硬件参数失败：{ex.Message}");
        }
    }

    /// <summary>写入设备端硬件保护参数（作为上位机控温的兜底保护）。两通道同步写入。</summary>
    [RelayCommand]
    private async Task WriteHwConfigAsync()
    {
        var controller = getController();
        if (controller is null) { setStatus("尚未连接设备"); return; }
        if (!TryParseLong(HwMaxDutyInput, "硬件占空比上限", out var duty) ||
            !TryParseDouble(HwMaxCurrentInput, "最大电流", out var amps) ||
            !TryParseDouble(OverTempUpInput, "过温上限", out var up) ||
            !TryParseDouble(OverTempLowerInput, "过温下限", out var lower)) return;
        if (duty is < 0 or > 90)
        {
            setStatus("硬件占空比上限范围为 0~90（RD105 固件 LIMITED 寄存器上限即 90，不存在 100）");
            return;
        }
        if (up <= lower) { setStatus("过温上限必须大于下限"); return; }

        try
        {
            for (var ch = 1; ch <= 2; ch++)
            {
                await controller.SetMaxDutyAsync(ch, (int)duty);
                await controller.SetMaxCurrentAsync(ch, amps);
                await controller.SetOverTempAsync(ch, up, lower);
            }
            setStatus($"两通道硬件保护参数已同步写入（占空比上限 {duty}%、限流 {amps:F1}A）");
        }
        catch (Exception ex)
        {
            setStatus($"写入硬件参数失败：{ex.Message}");
        }
    }

    private bool TryParseDouble(string text, string name, out double value)
    {
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        setStatus($"{name}不是有效数字：{text}");
        return false;
    }

    private bool TryParseLong(string text, string name, out long value)
    {
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        setStatus($"{name}不是有效整数：{text}");
        return false;
    }
}
