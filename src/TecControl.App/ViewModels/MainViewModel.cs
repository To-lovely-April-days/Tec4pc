using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TecControl.Core;
using TecControl.Core.Comm;
using TecControl.Core.Control;
using TecControl.Core.Logging;
using TecControl.Core.Protocol;

namespace TecControl.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private SerialPortTransport? _transport;
    private TecClient? _client;
    private TecController? _controller;
    private HostControlLoop? _loop;
    private readonly CsvLogger _logger = new();
    private TecErrorCode _lastError = TecErrorCode.None;

    public MainViewModel()
    {
        // CONTMODE=2：通道 2 输出硬件跟随通道 1，四片 TEC 同进退 + PWM 加热分程；
        // 上位机只跑一个主控制环（通道 1），反馈传感器可选 TC1/TC2
        Ch1 = new ChannelViewModel(1, "主控温环（四片 TEC 同步驱动）",
            () => _controller, () => _loop, s => StatusText = s, SaveGainSchedule);
        RefreshPorts();
    }

    public ChannelViewModel Ch1 { get; }

    // ---- 监视（两路温度、通道 2 输出电压、两路实测电流） ----
    [ObservableProperty] private string tc1Text = "--";
    [ObservableProperty] private string tc2Text = "--";
    [ObservableProperty] private string out2VoltsText = "--";
    [ObservableProperty] private string current1Text = "--";
    [ObservableProperty] private string current2Text = "--";

    /// <summary>每个控制周期结束后在 UI 线程上通知视图（用于绘图）。</summary>
    public event Action<ControlCycleResult>? CycleArrived;

    // ---- 连接 ----
    public ObservableCollection<string> Ports { get; } = [];
    [ObservableProperty] private string? selectedPort;
    [ObservableProperty] private int baudRate = 38400;
    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string deviceInfo = "";
    [ObservableProperty] private int controlPeriodMs = 500;

    public int[] ControlPeriodOptions { get; } = [200, 500, 1000, 2000];

    // ---- PWM 输出频率（FPWM，全局参数） ----
    public string[] PwmFrequencyNames { get; } = ["0.5 Hz", "1 Hz", "10 Hz（出厂）", "100 Hz（推荐）"];
    [ObservableProperty] private int pwmFrequencyIndex = 2;

    [RelayCommand]
    private async Task ApplyPwmFrequencyAsync()
    {
        if (_controller is null) { StatusText = "尚未连接设备"; return; }
        try
        {
            await _controller.SetPwmFrequencyAsync(PwmFrequencyIndex);
            StatusText = $"PWM 输出频率已设为 {PwmFrequencyNames[PwmFrequencyIndex]}" +
                         "（TEC 制冷量正比于电流、焦耳热正比于电流平方，脉动越小效率越高）";
        }
        catch (Exception ex)
        {
            StatusText = $"设置 PWM 频率失败：{ex.Message}";
        }
    }

    // ---- 周期负载监视 ----
    [ObservableProperty] private string cycleLoadText = "--";

    // ---- 四片 TEC 同步方式 ----
    public string[] SyncModeNames { get; } = ["上位机镜像（CONTMODE=0，推荐）", "硬件跟随（CONTMODE=2）"];
    [ObservableProperty] private int syncModeIndex;

    private ChannelSyncMode SelectedSyncMode =>
        SyncModeIndex == 0 ? ChannelSyncMode.HostMirror : ChannelSyncMode.HardwareFollow;

    partial void OnSyncModeIndexChanged(int value)
    {
        if (_loop is { } loop)
        {
            loop.SyncMode = SelectedSyncMode;
            StatusText = $"通道同步方式已切换为：{SyncModeNames[value]}（下次启动控温/自整定完全生效）";
        }
    }

    // ---- 状态与告警 ----
    [ObservableProperty] private string statusText = "未连接";
    [ObservableProperty] private string internalTempText = "--";
    [ObservableProperty] private bool hasAlarm;
    public ObservableCollection<string> Alarms { get; } = [];

    // ---- 数据记录 ----
    [ObservableProperty] private bool isLogging;
    [ObservableProperty] private string logPathText = "";

    [RelayCommand]
    private void RefreshPorts()
    {
        Ports.Clear();
        foreach (var p in SerialPortTransport.GetPortNames().OrderBy(x => x))
            Ports.Add(p);
        SelectedPort ??= Ports.FirstOrDefault();
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (IsConnected) return;
        if (string.IsNullOrEmpty(SelectedPort))
        {
            StatusText = "请先选择串口";
            return;
        }

        try
        {
            _transport = new SerialPortTransport(SelectedPort, BaudRate);
            _transport.Open();
            _client = new TecClient(_transport);
            _controller = new TecController(_client);

            // 设备信息只做显示，解析异常不阻断连接（实测固件应答格式与文档存在出入）
            try
            {
                var (model, firmware, contMode) = await _controller.ReadDeviceInfoAsync();
                DeviceInfo = $"型号 {model} / 固件 {firmware}";
                if (contMode is int cm && cm != 0)
                    StatusText = $"注意：设备 CONTMODE={cm}，上位机控温方案要求为 0（各通道独立），请先用厂家工具改回";
            }
            catch (TecProtocolException ex)
            {
                DeviceInfo = "设备信息读取异常（不影响控温）";
                StatusText = $"设备信息解析异常：{ex.Message}";
            }

            await Ch1.ReadHwConfigCommand.ExecuteAsync(null);

            try { PwmFrequencyIndex = await _controller.ReadPwmFrequencyAsync(); }
            catch (TecProtocolException) { /* 个别固件不支持 FPWM 查询，保持默认显示 */ }

            // 镜像方案要求设备处于 CONTMODE=0（各通道独立）；写入失败则提醒用厂家工具确认
            if (SelectedSyncMode == ChannelSyncMode.HostMirror)
            {
                try
                {
                    await _controller.SetControllerModeAsync(0);
                    StatusText = "已写入 CONTMODE=0（通道独立），四片 TEC 由上位机镜像同步";
                }
                catch (Exception ex)
                {
                    StatusText = $"注意：写入 CONTMODE=0 失败（{ex.Message}）。" +
                                 "请用厂家 TSC 工具将温控器模式设为 0，否则设备可能仍在硬件跟随状态";
                }
            }

            _loop = new HostControlLoop(_controller)
            {
                Period = TimeSpan.FromMilliseconds(ControlPeriodMs),
                SyncMode = SelectedSyncMode,
            };
            _loop.CycleCompleted += OnCycle;
            _loop.CycleFaulted += OnCycleFaulted;
            _loop.SafetyTripped += OnSafetyTripped;
            _loop.SaturationWarning += OnSaturationWarning;

            // 载入历次学习到的"温度→稳态输出"工作点与各温区增益表，跨重启复用
            try { FeedforwardStore.Load(_loop.GetFeedforward(1), FeedforwardStore.DefaultPath); }
            catch (Exception ex) { StatusText = $"前馈模型载入失败（不影响控温）：{ex.Message}"; }

            var upgraded = 0;
            var seeded = false;
            try
            {
                var sched = _loop.GetGainSchedule(1);
                // 用户表不存在时用随程序发布的出厂表打底（含 −10℃ 已整定的工作点）
                var from = GainScheduleStore.LoadOrSeed(sched);
                seeded = from is not null && from != GainScheduleStore.DefaultPath;
                // 旧版增益表只有内环参数，按各点自身的 Tu 补一组推荐外环参数
                upgraded = sched.EnsureOuterGains();
                if (upgraded > 0 || seeded) SaveGainSchedule();
            }
            catch (Exception ex) { StatusText = $"增益表载入失败（不影响控温）：{ex.Message}"; }

            Ch1.RefreshFeedforwardHint();
            Ch1.RefreshGainScheduleHint();
            _loop.AutoTuneFinished += OnAutoTuneFinished;
            _loop.GainScheduleChanged += OnGainScheduleChanged;
            _loop.Start();

            IsConnected = true;
            StatusText = $"已连接 {SelectedPort}（{BaudRate},N,8,1），控制周期 {ControlPeriodMs}ms。两通道均未启动控温"
                + (seeded ? "。用户增益表不存在或为空，已导入出厂增益表" : "")
                + (upgraded > 0 ? $"。已为增益表中 {upgraded} 个旧工作点补全外环参数" : "")
                + (seeded || upgraded > 0 ? $"，并保存到 {GainScheduleStore.DefaultPath}" : "");
        }
        catch (Exception ex)
        {
            await CleanupAsync();
            StatusText = $"连接失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await CleanupAsync();
        IsConnected = false;
        DeviceInfo = "";
        StatusText = "已断开连接（输出已归零）";
    }

    partial void OnControlPeriodMsChanged(int value)
    {
        if (_loop is { } loop)
            loop.Period = TimeSpan.FromMilliseconds(value);
    }

    [RelayCommand]
    private void ToggleLogging()
    {
        if (IsLogging)
        {
            _logger.Stop();
            IsLogging = false;
            StatusText = "已停止记录";
            return;
        }

        var dir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var path = Path.Combine(dir, $"tec-log-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        try
        {
            _logger.Start(path);
            LogPathText = path;
            IsLogging = true;
            StatusText = $"正在记录：{path}";
        }
        catch (Exception ex)
        {
            StatusText = $"启动记录失败：{ex.Message}";
        }
    }

    private void OnCycle(ControlCycleResult cycle)
    {
        if (cycle.ErrorCode is { } code) UpdateAlarms(code);
        if (IsLogging) _logger.Append(cycle, _lastError);

        RunOnUi(() =>
        {
            var s = cycle.Snapshot;
            // 主环面板的 PV 显示所选反馈传感器的温度
            Ch1.UpdateRealtime(cycle.Ch1.MeasuredC, s.OutVolts1, cycle.Ch1);
            Tc1Text = Fmt(s.Temp1C);
            Tc2Text = Fmt(s.Temp2C);
            Out2VoltsText = $"{s.OutVolts2:F3} V";
            if (cycle.Current1A is double c1) Current1Text = $"{c1:F3} A";
            if (cycle.Current2A is double c2) Current2Text = $"{c2:F3} A";
            CycleLoadText = $"周期耗时 {cycle.CycleMs:F0} ms / {ControlPeriodMs} ms（占用 {cycle.CycleLoad * 100:F0}%）"
                            + (cycle.CycleLoad > 0.85 ? " ⚠ 串口已接近饱和，建议加大周期或提高波特率" : "");
            InternalTempText = $"{s.InternalTempC:F0} ℃";
            CycleArrived?.Invoke(cycle);
        });

        static string Fmt(double t) =>
            double.IsNaN(t) ? "未接传感器" : t.ToString("F5", System.Globalization.CultureInfo.InvariantCulture) + " ℃";
    }

    private void UpdateAlarms(TecErrorCode code)
    {
        if (code == _lastError) return;
        _lastError = code;

        RunOnUi(() =>
        {
            Alarms.Clear();
            foreach (var text in code.Describe())
                Alarms.Add(text);
            HasAlarm = Alarms.Count > 0;
        });
    }

    private void OnCycleFaulted(Exception ex) =>
        RunOnUi(() => StatusText = $"控制周期通信异常：{ex.Message}");

    private void OnAutoTuneFinished(AutoTuneOutcome outcome)
    {
        if (outcome.Channel == 1)
            RunOnUi(() => Ch1.OnAutoTuneFinished(outcome));
    }

    private void OnSaturationWarning(string message) =>
        RunOnUi(() => StatusText = $"⚠ {message}");

    private void OnSafetyTripped(string reason) =>
        RunOnUi(() =>
        {
            Ch1.ControlRunning = _loop?.GetChannelStatus(1).Active ?? false;
            StatusText = $"⚠ 安全停机：{reason}";
        });

    /// <summary>
    /// 增益表落盘。控制线程与界面线程都会调用，用锁串行化，写盘失败只提示不抛出。
    /// 返回是否写成功，供调用方决定提示文案。
    /// </summary>
    public bool SaveGainSchedule()
    {
        if (_loop is not { } loop) return false;
        try
        {
            lock (_gainSaveLock)
                GainScheduleStore.Save(loop.GetGainSchedule(1), GainScheduleStore.DefaultPath);
            return true;
        }
        catch (Exception ex)
        {
            RunOnUi(() => StatusText = $"增益表保存失败：{ex.Message}");
            return false;
        }
    }

    private readonly object _gainSaveLock = new();

    /// <summary>
    /// 控制线程报告增益表有实质变化（自整定登记新点、学到稳态偏置、限幅被自动放宽）：
    /// 立刻落盘，并把表格刷新到界面，避免异常退出时丢掉学习成果。
    /// </summary>
    private void OnGainScheduleChanged(int ch)
    {
        if (ch != 1) return;
        var ok = SaveGainSchedule();
        RunOnUi(() =>
        {
            Ch1.RefreshGainScheduleHint();
            if (ok) StatusText = $"增益表已更新并保存：{GainScheduleStore.DefaultPath}";
        });
    }

    private async Task CleanupAsync()
    {
        if (_loop is { } loop)
        {
            try { FeedforwardStore.Save(loop.GetFeedforward(1), FeedforwardStore.DefaultPath); }
            catch { /* 保存失败不影响退出 */ }
            SaveGainSchedule();

            loop.CycleCompleted -= OnCycle;
            loop.CycleFaulted -= OnCycleFaulted;
            loop.SafetyTripped -= OnSafetyTripped;
            loop.SaturationWarning -= OnSaturationWarning;
            loop.AutoTuneFinished -= OnAutoTuneFinished;
            loop.GainScheduleChanged -= OnGainScheduleChanged;
            await loop.ShutdownAsync();
            loop.Dispose();
        }
        _loop = null;
        _controller?.Dispose();
        _controller = null;
        _client?.Dispose();
        _client = null;
        _transport?.Dispose();
        _transport = null;
        _logger.Stop();
        IsLogging = false;
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    public void OnWindowClosing() => CleanupAsync().GetAwaiter().GetResult();
}
