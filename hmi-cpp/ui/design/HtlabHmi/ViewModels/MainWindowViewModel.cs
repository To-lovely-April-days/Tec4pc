using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HtlabHmi.Models;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;

namespace HtlabHmi.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _timer;
    private readonly Random _rng = new();
    private DateTime _toastUntil = DateTime.MinValue;

    public ChannelViewModel Channel1 { get; }
    public ChannelViewModel Channel2 { get; }

    [ObservableProperty] private TopSection _currentSection = TopSection.Overview;
    [ObservableProperty] private int _activeSecondaryTabIndex;
    [ObservableProperty] private ChannelViewModel _activeChannel;

    public ObservableCollection<SecondaryTabItem> SecondaryTabs { get; } = new();

    // Global top bar states
    [ObservableProperty] private bool _waterCoolingOn = true;
    [ObservableProperty] private string _currentUser = "admin";
    [ObservableProperty] private string _clockTimeText = "";
    [ObservableProperty] private string _clockDateText = "";
    [ObservableProperty] private string _fullDateTimeText = "";
    [ObservableProperty] private string _uptimeText = "0.1.0 · 已运行 1 h 12 min";
    [ObservableProperty] private string _toastMessage = "";
    [ObservableProperty] private bool _isToastVisible;
    private int _uptimeSeconds = 4320;

    // Recipes (Files)
    public ObservableCollection<RecipeItem> Recipes { get; } = new();
    [ObservableProperty] private RecipeItem? _selectedRecipe;

    // PID table (shared bidirectional table)
    public ObservableCollection<PidGainRow> PidRows { get; } = new();
    [ObservableProperty] private double _pidLookupTemp = 45.0;
    [ObservableProperty] private double _pidBoostLimitK = 0.0;

    // Export page
    public ObservableCollection<ExportBatchItem> ExportBatches { get; } = new();
    [ObservableProperty] private int _exportIntervalSec = 10;
    [ObservableProperty] private bool _exportRangeAll = true;
    [ObservableProperty] private int _exportFromMin = 0;
    [ObservableProperty] private int _exportToMin = 60;

    // Comm settings
    public ObservableCollection<CommDeviceConfig> CommDevices { get; } = new();
    public ObservableCollection<ConnectedPcInfo> ConnectedPcs { get; } = new();
    [ObservableProperty] private int _tcpPort = 5020;
    [ObservableProperty] private int _tcpTimeoutSec = 10;
    [ObservableProperty] private bool _tcpEnabled = true;
    [ObservableProperty] private bool _allowRemoteControl = true;

    // Alarms & Dialogs
    public ObservableCollection<AlarmRecord> Alarms { get; } = new();
    [ObservableProperty] private bool _isAlarmListOpen;
    [ObservableProperty] private bool _isAlarmInterlockOpen;
    [ObservableProperty] private AlarmRecord? _activeInterlockAlarm;

    partial void OnActiveInterlockAlarmChanged(AlarmRecord? value)
        => OnPropertyChanged(nameof(ActiveAlarmWhenText));

    public KeypadDialogViewModel Keypad { get; } = new();
    public TextDialogViewModel TextDialog { get; } = new();
    public StepTypeDialogViewModel StepTypeDialog { get; } = new();
    public CommDeviceDialogViewModel CommDialog { get; } = new();

    public MainWindowViewModel()
    {
        Channel1 = new ChannelViewModel(1)
        {
            SchemeName = "结晶工艺-0824",
            Tr = 60.0,
            Tj = 63.4,
            TjSetpoint = 63.5,
            Tc = 20.5,
            Ph = 4.62,
            Rpm = 300,
            TargetRpm = 300,
            StirCurrent = 0.82,
            DosedMl = 12.5,
            RampRate = 0.0,
            OutputPowerPct = 31,
            LightOn = false,
            StirRunning = true,
            TempControlRunning = true,
            ControlMode = TempControlMode.Tr,
            ArriveMode = ArriveMode.Fast,
            TargetTemp = 65.0,
            LastTargetTemp = 60.0,
            IsSequenceRunning = true,
            CurrentStepIndex = 2,
            ElapsedSeconds = 5370, // 01:29:30
            RemainingSeconds = 3420, // 00:57:00
            StepRemainingSeconds = 2940 // 00:49:00
        };
        SeedChannel1Steps();
        Channel1.SeedInitialTrend(60.0, 63.4, true);

        Channel2 = new ChannelViewModel(2)
        {
            SchemeName = "低温筛选-B3",
            Tr = 25.3,
            Tj = 25.1,
            TjSetpoint = 25.0,
            Tc = 20.4,
            Ph = 6.98,
            Rpm = 0,
            TargetRpm = 300,
            StirCurrent = 0.0,
            DosedMl = 0.0,
            RampRate = 0.0,
            OutputPowerPct = 0,
            LightOn = false,
            StirRunning = false,
            TempControlRunning = false,
            ControlMode = TempControlMode.Tj,
            ArriveMode = ArriveMode.Rate,
            TargetTemp = 25.0,
            LastTargetTemp = 25.0,
            IsSequenceRunning = false,
            CurrentStepIndex = 0,
            ElapsedSeconds = 0,
            RemainingSeconds = 7800,
            StepRemainingSeconds = 2100
        };
        SeedChannel2Steps();
        Channel2.SeedInitialTrend(25.3, 25.1, false);

        _activeChannel = Channel1;

        SeedRecipes();
        SeedPidTable();
        SeedExportBatches();
        SeedCommDevices();
        SeedAlarms();
        UpdateClock();

        NavigateSection("Overview");

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    // Section booleans for easy binding
    public bool IsSectionOverview => CurrentSection == TopSection.Overview;
    public bool IsSectionCh1 => CurrentSection == TopSection.Ch1;
    public bool IsSectionCh2 => CurrentSection == TopSection.Ch2;
    public bool IsSectionChannel => CurrentSection == TopSection.Ch1 || CurrentSection == TopSection.Ch2;
    public bool IsSectionFiles => CurrentSection == TopSection.Files;
    public bool IsSectionSettings => CurrentSection == TopSection.Settings;
    public bool IsSectionUsers => CurrentSection == TopSection.Users;

    // Active Page visibility flags
    public bool ShowMainOverview => IsSectionOverview && ActiveSecondaryTabIndex == 0;
    public bool ShowSchemeOverview => IsSectionOverview && ActiveSecondaryTabIndex == 1;
    public bool ShowTrendsOverview => IsSectionOverview && ActiveSecondaryTabIndex == 2;

    public bool ShowChannelCtl => IsSectionChannel && ActiveSecondaryTabIndex == 0;
    public bool ShowChannelTrend => IsSectionChannel && ActiveSecondaryTabIndex == 1;
    public bool ShowChannelSeq => IsSectionChannel && ActiveSecondaryTabIndex == 2;
    public bool ShowChannelSafety => IsSectionChannel && ActiveSecondaryTabIndex == 3;
    public bool ShowChannelExport => IsSectionChannel && ActiveSecondaryTabIndex == 4;
    public bool ShowChannelPid => IsSectionChannel && ActiveSecondaryTabIndex == 5;

    public bool ShowFiles => IsSectionFiles;
    public bool ShowSettingsSys => IsSectionSettings && ActiveSecondaryTabIndex == 0;
    public bool ShowSettingsComm => IsSectionSettings && ActiveSecondaryTabIndex == 1;
    public bool ShowUsers => IsSectionUsers;

    public int UnreadAlarmCount => Alarms.Count(a => !a.IsAcknowledged);
    public bool HasUnreadAlarms => UnreadAlarmCount > 0;
    public string WaterCoolingText => WaterCoolingOn ? "冷却水已开" : "冷却水已关";
    public string SysCoolingText => WaterCoolingOn ? "已开 · 冷水机运行中" : "已关 · 冷水机待机";
    public string Ch1LoadBtnText => Channel1.IsSequenceRunning ? "通道 1 运行中" : "装入通道 1";
    public string Ch2LoadBtnText => Channel2.IsSequenceRunning ? "通道 2 运行中" : "装入通道 2";
    public string ActiveAlarmWhenText => ActiveInterlockAlarm == null ? "" : $"{ActiveInterlockAlarm.ChannelName} · {ActiveInterlockAlarm.Timestamp} · 第 1 回";

    public string ExportSelectedCountText => $"已选 {ExportBatches.Count(b => b.IsSelected)} / {ExportBatches.Count}";
    public string ExportButtonText => $"导出所选（{ExportBatches.Count(b => b.IsSelected)}）";
    public bool IsExportInterval1 => ExportIntervalSec == 1;
    public bool IsExportInterval10 => ExportIntervalSec == 10;
    public bool IsExportInterval60 => ExportIntervalSec == 60;
    public bool ExportRangeCustom => !ExportRangeAll;

    public string PidLookupResultText
    {
        get
        {
            var (kp, ki, kd, hr) = InterpolatePid(PidLookupTemp);
            return $"℃ 会用：Kp {kp:F2} · Ki {ki:F4} · Kd {kd:F1} · 加热比 {hr:F2}";
        }
    }

    public string PidLiveParamsText
    {
        get
        {
            var (kp, ki, kd, hr) = InterpolatePid(ActiveChannel.TjSetpoint);
            return $"Kp {kp:F2} · Ki {ki:F4} · Kd {kd:F1} · 加热比 {hr:F2}";
        }
    }

    public string PidLiveOutputText =>
        $"夹套目标 {ActiveChannel.TjSetpoint:F1} ℃ · 出力 {ActiveChannel.OutputPowerPct:F0} %（{(ActiveChannel.TjSetpoint >= 25 ? "TEC 加热" : "TEC 制冷")}）";

    public string PidStabilityLine1 =>
        $"近 5 min：Tr {ActiveChannel.Tr:F2} ℃，波动 ±0.03 ℃";

    public string PidStabilityLine2 =>
        $"出力 {ActiveChannel.OutputPowerPct:F0} %，{(ActiveChannel.OutputPowerPct >= 98 ? "已接近满载" : "没有顶到头")}";

    public string PidOuterLoopLine1 =>
        $"维持温差（估）{(ActiveChannel.Tj - ActiveChannel.Tr >= 0 ? "+" : "")}{ActiveChannel.Tj - ActiveChannel.Tr:F1} K　·　釜的时间常数（估）388 s";

    public string PidOuterLoopLine2 =>
        $"快速升温此刻加码 +{PidBoostLimitK:F1} K　·　夹套目标 {ActiveChannel.TjSetpoint:F1} ℃";

    private (double kp, double ki, double kd, double hr) InterpolatePid(double temp)
    {
        if (PidRows.Count == 0) return (9.4, 0.037, 171.0, 2.1);
        var sorted = PidRows.OrderBy(r => r.Temp).ToList();
        if (temp <= sorted[0].Temp) return (sorted[0].Kp, sorted[0].Ki, sorted[0].Kd, sorted[0].HeatRatio);
        if (temp >= sorted[^1].Temp) return (sorted[^1].Kp, sorted[^1].Ki, sorted[^1].Kd, sorted[^1].HeatRatio);

        for (int i = 0; i < sorted.Count - 1; i++)
        {
            var a = sorted[i];
            var b = sorted[i + 1];
            if (temp >= a.Temp && temp <= b.Temp)
            {
                double t = (temp - a.Temp) / Math.Max(0.001, b.Temp - a.Temp);
                return (
                    a.Kp + (b.Kp - a.Kp) * t,
                    a.Ki + (b.Ki - a.Ki) * t,
                    a.Kd + (b.Kd - a.Kd) * t,
                    a.HeatRatio + (b.HeatRatio - a.HeatRatio) * t
                );
            }
        }
        var last = sorted[^1];
        return (last.Kp, last.Ki, last.Kd, last.HeatRatio);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _uptimeSeconds++;
        UpdateClock();
        Channel1.TickSimulation(_rng);
        Channel2.TickSimulation(_rng);

        // Safety 1 Hz evaluation
        EvaluateSafety(Channel1);
        EvaluateSafety(Channel2);

        // Refresh PID readouts
        OnPropertyChanged(nameof(PidLiveParamsText));
        OnPropertyChanged(nameof(PidLiveOutputText));
        OnPropertyChanged(nameof(PidStabilityLine1));
        OnPropertyChanged(nameof(PidStabilityLine2));
        OnPropertyChanged(nameof(PidOuterLoopLine1));
        OnPropertyChanged(nameof(PidOuterLoopLine2));

        if (IsToastVisible && DateTime.Now > _toastUntil)
            IsToastVisible = false;
    }

    private void EvaluateSafety(ChannelViewModel ch)
    {
        if (!ch.TempControlRunning) return;
        if (ch.Tr > ch.TrMax)
        {
            ch.TempControlRunning = false;
            ch.IsSequenceRunning = false;
            TriggerInterlock(ch.ChannelTitle, $"Tr 超过上限 {ch.TrMax:F1} ℃（实测 {ch.Tr:F1} ℃）", $"实测 {ch.Tr:F1} ℃");
        }
        else if (ch.Tr < ch.TrMin)
        {
            ch.TempControlRunning = false;
            ch.IsSequenceRunning = false;
            TriggerInterlock(ch.ChannelTitle, $"Tr 低于下限 {ch.TrMin:F1} ℃（实测 {ch.Tr:F1} ℃）", $"实测 {ch.Tr:F1} ℃");
        }
    }

    private void TriggerInterlock(string chName, string msg, string recovered)
    {
        var rec = new AlarmRecord
        {
            Timestamp = DateTime.Now.ToString("MM-dd HH:mm:ss"),
            ChannelName = chName,
            Title = "安全联锁 · 停止控温",
            Message = msg,
            RecoveredText = recovered,
            ActionTaken = "安全层已执行：停止控温，搅拌保持",
            IsAcknowledged = false
        };
        Alarms.Insert(0, rec);
        ActiveInterlockAlarm = rec;
        IsAlarmInterlockOpen = true;
        NotifyAlarmsChanged();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTimeText = now.ToString("HH:mm");
        ClockDateText = now.ToString("yyyy.MM.dd");
        FullDateTimeText = now.ToString("yyyy-MM-dd HH:mm:ss");
        UptimeText = $"0.1.0 · 已运行 {_uptimeSeconds / 3600} h {(_uptimeSeconds % 3600) / 60} min";
    }

    public void ShowToast(string msg)
    {
        ToastMessage = msg;
        IsToastVisible = true;
        _toastUntil = DateTime.Now.AddSeconds(3);
    }

    // ===== NAVIGATION COMMANDS =====
    [RelayCommand]
    public void NavigateSection(string sectionName)
    {
        if (!Enum.TryParse<TopSection>(sectionName, out var sec)) return;
        CurrentSection = sec;
        if (sec == TopSection.Ch1) ActiveChannel = Channel1;
        else if (sec == TopSection.Ch2) ActiveChannel = Channel2;

        BuildSecondaryTabs(sec);
        ActiveSecondaryTabIndex = 0;
        UpdateTabSelections();
        NotifyPageVisibility();
    }

    [RelayCommand]
    public void SelectSecondaryTab(int index)
    {
        if (index < 0 || index >= SecondaryTabs.Count) return;
        ActiveSecondaryTabIndex = index;
        UpdateTabSelections();
        NotifyPageVisibility();
    }

    private void BuildSecondaryTabs(TopSection sec)
    {
        SecondaryTabs.Clear();
        switch (sec)
        {
            case TopSection.Overview:
                SecondaryTabs.Add(new SecondaryTabItem { Index = 0, Title = "装置总览", IconKey = "IconReactor" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 1, Title = "方案总览", IconKey = "IconScheme" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 2, Title = "曲线总览", IconKey = "IconTrend" });
                break;
            case TopSection.Ch1:
            case TopSection.Ch2:
                SecondaryTabs.Add(new SecondaryTabItem { Index = 0, Title = "设备控制", IconKey = "IconReactor" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 1, Title = "趋势曲线", IconKey = "IconTrend" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 2, Title = "任务序列", IconKey = "IconSeq" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 3, Title = "反应釜与安全", IconKey = "IconSafety" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 4, Title = "数据导出", IconKey = "IconExport" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 5, Title = "PID 整定", IconKey = "IconPid" });
                break;
            case TopSection.Files:
                SecondaryTabs.Add(new SecondaryTabItem { Index = 0, Title = "配方库", IconKey = "IconFiles" });
                break;
            case TopSection.Settings:
                SecondaryTabs.Add(new SecondaryTabItem { Index = 0, Title = "系统设置", IconKey = "IconSettings" });
                SecondaryTabs.Add(new SecondaryTabItem { Index = 1, Title = "通信设置", IconKey = "IconComm" });
                break;
            case TopSection.Users:
                SecondaryTabs.Add(new SecondaryTabItem { Index = 0, Title = "用户与权限", IconKey = "IconUsers" });
                break;
        }
    }

    private void UpdateTabSelections()
    {
        foreach (var t in SecondaryTabs)
            t.IsSelected = t.Index == ActiveSecondaryTabIndex;
    }

    private void NotifyPageVisibility()
    {
        OnPropertyChanged(nameof(IsSectionOverview));
        OnPropertyChanged(nameof(IsSectionCh1));
        OnPropertyChanged(nameof(IsSectionCh2));
        OnPropertyChanged(nameof(IsSectionChannel));
        OnPropertyChanged(nameof(IsSectionFiles));
        OnPropertyChanged(nameof(IsSectionSettings));
        OnPropertyChanged(nameof(IsSectionUsers));

        OnPropertyChanged(nameof(ShowMainOverview));
        OnPropertyChanged(nameof(ShowSchemeOverview));
        OnPropertyChanged(nameof(ShowTrendsOverview));
        OnPropertyChanged(nameof(ShowChannelCtl));
        OnPropertyChanged(nameof(ShowChannelTrend));
        OnPropertyChanged(nameof(ShowChannelSeq));
        OnPropertyChanged(nameof(ShowChannelSafety));
        OnPropertyChanged(nameof(ShowChannelExport));
        OnPropertyChanged(nameof(ShowChannelPid));
        OnPropertyChanged(nameof(ShowFiles));
        OnPropertyChanged(nameof(ShowSettingsSys));
        OnPropertyChanged(nameof(ShowSettingsComm));
        OnPropertyChanged(nameof(ShowUsers));
        OnPropertyChanged(nameof(PidLiveParamsText));
        OnPropertyChanged(nameof(PidLiveOutputText));
        OnPropertyChanged(nameof(Ch1LoadBtnText));
        OnPropertyChanged(nameof(Ch2LoadBtnText));
    }

    // ===== CHANNEL CONTROL COMMANDS =====
    [RelayCommand]
    private void EditMainSp(string chStr)
    {
        var ch = chStr == "2" ? Channel2 : Channel1;
        Keypad.Open($"{ch.ChannelTitle} · 目标温度 SP", "℃", ch.TargetTemp, ch.TrMin, ch.TrMax, ch.Tr, val =>
        {
            ch.LastTargetTemp = ch.TargetTemp;
            ch.TargetTemp = val;
            ch.TempControlRunning = true;
            ch.NotifyAllDisplay();
            ShowToast($"{ch.ChannelTitle} 目标温度已设为 {val:F1} ℃");
        });
    }

    [RelayCommand]
    private void ToggleLight(string chStr)
    {
        var ch = chStr == "active" ? ActiveChannel : (chStr == "2" ? Channel2 : Channel1);
        ch.LightOn = !ch.LightOn;
        ch.NotifyAllDisplay();
        ShowToast($"{ch.ChannelTitle} 釜体照明已{(ch.LightOn ? "开启" : "关闭")}");
    }

    [RelayCommand]
    private void ToggleStir(string chStr)
    {
        var ch = chStr == "active" ? ActiveChannel : (chStr == "2" ? Channel2 : Channel1);
        ch.StirRunning = !ch.StirRunning;
        if (ch.StirRunning && ch.TargetRpm < 50) ch.TargetRpm = 300;
        ch.NotifyAllDisplay();
        ShowToast($"{ch.ChannelTitle} 搅拌已{(ch.StirRunning ? $"启动（{ch.TargetRpm} rpm）" : "停止")}");
    }

    [RelayCommand]
    private void EditActiveTargetTemp()
    {
        var ch = ActiveChannel;
        if (ch.ControlMode == TempControlMode.Reflux)
        {
            Keypad.Open($"{ch.ChannelTitle} · 蒸回流温差 ΔT", "K", ch.RefluxDeltaK, 1, 40, Math.Abs(ch.Tj - ch.Tr), val =>
            {
                ch.RefluxDeltaK = val;
                ch.NotifyAllDisplay();
                ShowToast($"蒸回流温差设为 +{val:F1} K");
            });
            return;
        }
        Keypad.Open($"{ch.ChannelTitle} · {ch.TargetLabel}", "℃", ch.TargetTemp, ch.TrMin, ch.TrMax, ch.Tr, val =>
        {
            ch.LastTargetTemp = ch.TargetTemp;
            ch.TargetTemp = val;
            ch.NotifyAllDisplay();
            ShowToast($"{ch.ChannelTitle} {ch.TargetLabel} 已设为 {val:F1} ℃");
        });
    }

    [RelayCommand]
    private void EditActiveStirRpm()
    {
        var ch = ActiveChannel;
        Keypad.Open($"{ch.ChannelTitle} · 搅拌转速", "rpm", ch.TargetRpm, 50, 1000, ch.Rpm, val =>
        {
            ch.TargetRpm = (int)Math.Round(val);
            ch.StirRunning = true;
            ch.NotifyAllDisplay();
            ShowToast($"{ch.ChannelTitle} 搅拌转速已设为 {ch.TargetRpm} rpm");
        });
    }

    [RelayCommand]
    private void SetControlMode(string modeStr)
    {
        if (Enum.TryParse<TempControlMode>(modeStr, out var m))
        {
            ActiveChannel.ControlMode = m;
            ActiveChannel.NotifyAllDisplay();
        }
    }

    [RelayCommand]
    private void SetArriveMode(string modeStr)
    {
        if (Enum.TryParse<ArriveMode>(modeStr, out var m))
        {
            ActiveChannel.ArriveMode = m;
            ActiveChannel.NotifyAllDisplay();
        }
    }

    [RelayCommand]
    private void ToggleTempControl()
    {
        ActiveChannel.TempControlRunning = !ActiveChannel.TempControlRunning;
        ActiveChannel.NotifyAllDisplay();
        ShowToast(ActiveChannel.TempControlRunning ? $"{ActiveChannel.ChannelTitle} 控温已启动" : $"{ActiveChannel.ChannelTitle} 控温已停止");
    }

    [RelayCommand]
    private void ToggleTrSensor()
    {
        ActiveChannel.TrSensorEnabled = !ActiveChannel.TrSensorEnabled;
        ShowToast($"Tr 传感器已{(ActiveChannel.TrSensorEnabled ? "启用" : "停用")}");
    }

    [RelayCommand]
    private void TogglePhElectrode()
    {
        ActiveChannel.PhElectrodeEnabled = !ActiveChannel.PhElectrodeEnabled;
        ShowToast($"pH 电极已{(ActiveChannel.PhElectrodeEnabled ? "启用" : "停用")}");
    }

    [RelayCommand]
    private void ToggleTrendSeries(string idxStr)
    {
        var ch = ActiveChannel;
        switch (idxStr)
        {
            case "0": ch.ShowTr = !ch.ShowTr; break;
            case "1": ch.ShowTj = !ch.ShowTj; break;
            case "2": ch.ShowTjSp = !ch.ShowTjSp; break;
            case "3": ch.ShowDeltaT = !ch.ShowDeltaT; break;
            case "4": ch.ShowPh = !ch.ShowPh; break;
            case "5": ch.ShowRpm = !ch.ShowRpm; break;
        }
        ch.ChartRevision++;
    }

    [RelayCommand]
    private void SetSelectedStepArrive(string modeStr)
    {
        var st = ActiveChannel.SelectedStep;
        if (st == null || ActiveChannel.IsSequenceRunning) { ShowToast("已开始的序列不可修改"); return; }
        if (Enum.TryParse<ArriveMode>(modeStr, out var m))
        {
            st.ArriveMode = m;
            st.RefreshComputed();
        }
    }

    [RelayCommand]
    private void SetSelectedStepStirKeep()
    {
        var st = ActiveChannel.SelectedStep;
        if (st == null || ActiveChannel.IsSequenceRunning) { ShowToast("已开始的序列不可修改"); return; }
        st.StirUnchanged = !st.StirUnchanged;
        st.RefreshComputed();
        ShowToast(st.StirUnchanged ? "该步骤将保持当前搅拌转速不变" : $"该步骤搅拌设为 {st.StirRpm} rpm");
    }

    [RelayCommand]
    private void ConfirmStepPause() => ShowToast("已确认继续执行当前步骤");

    [RelayCommand]
    private void DeletePidRow(PidGainRow row)
    {
        if (PidRows.Count <= 1) { ShowToast("至少保留 1 行增益参数"); return; }
        PidRows.Remove(row);
        ShowToast($"已删除 {row.Temp:F0} ℃ 行");
    }

    [RelayCommand]
    private void ClearPidTable()
    {
        PidRows.Clear();
        ShowToast("已清空增益表（点「从盘上重读」可恢复）");
    }

    [RelayCommand]
    private void UndoPidTable()
    {
        SeedPidTable();
        ShowToast("已放弃未应用的修改");
    }

    [RelayCommand]
    private void ToggleTcpEnabled()
    {
        TcpEnabled = !TcpEnabled;
        ShowToast(TcpEnabled ? "对外 TCP 通信已开启" : "对外 TCP 通信已关闭");
    }

    // ===== SEQUENCE COMMANDS =====
    [RelayCommand]
    private void StartSequence()
    {
        var ch = ActiveChannel;
        if (ch.Steps.Count == 0)
        {
            ShowToast("请先添加至少一个步骤");
            return;
        }
        ch.IsSequenceRunning = true;
        ch.TempControlRunning = true;
        ch.CurrentStepIndex = 0;
        ch.ElapsedSeconds = 0;
        ch.RemainingSeconds = 5400;
        ch.StepRemainingSeconds = 1800;
        for (int i = 0; i < ch.Steps.Count; i++)
        {
            ch.Steps[i].ProgressPct = i == 0 ? 5 : 0;
            ch.Steps[i].IsRunning = i == 0;
            ch.Steps[i].IsCompleted = false;
        }
        if (ch.Steps[0].Kind == StepKind.Tr || ch.Steps[0].Kind == StepKind.Tj)
            ch.TargetTemp = ch.Steps[0].TargetTemp;
        ch.NotifyAllDisplay();
        ShowToast($"{ch.ChannelTitle} 已开始执行序列「{ch.SchemeName}」");
    }

    [RelayCommand]
    private void EndSequence()
    {
        var ch = ActiveChannel;
        ch.IsSequenceRunning = false;
        foreach (var s in ch.Steps) s.IsRunning = false;
        ch.NotifyAllDisplay();
        ShowToast($"{ch.ChannelTitle} 任务序列已结束");
    }

    [RelayCommand]
    private void SelectSequenceStep(SequenceStep step) => ActiveChannel.SelectStep(step);

    [RelayCommand]
    private void OpenAddStepDialog()
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning) { ShowToast("已开始的序列不可增改"); return; }
        if (ch.Steps.Count >= 6) { ShowToast("每个序列最多 6 步"); return; }
        StepTypeDialog.Open(ch.Steps.Count + 1, kind =>
        {
            var step = new SequenceStep
            {
                StepNumber = ch.Steps.Count + 1,
                Kind = kind,
                TargetTemp = kind == StepKind.Tj ? 20.0 : 60.0,
                DurationMin = 30,
                StirRpm = 300,
                StirUnchanged = kind != StepKind.Stir
            };
            ch.Steps.Add(step);
            ch.RenumberSteps();
            ch.SelectStep(step);
            ShowToast($"已添加第 {step.StepNumber} 步：{step.KindFullTitle}");
        });
    }

    [RelayCommand]
    private void ChangeSelectedStepKind()
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning || ch.SelectedStep == null) { ShowToast("已开始的序列不可修改"); return; }
        var target = ch.SelectedStep;
        StepTypeDialog.Open(target.StepNumber, kind =>
        {
            target.Kind = kind;
            target.RefreshComputed();
            ShowToast($"第 {target.StepNumber} 步已切换为 {target.KindFullTitle}");
        });
    }

    [RelayCommand]
    private void DeleteSelectedStep()
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning || ch.SelectedStep == null) { ShowToast("已开始的序列不可删除"); return; }
        ch.Steps.Remove(ch.SelectedStep);
        ch.RenumberSteps();
        ch.SelectStep(ch.Steps.FirstOrDefault());
    }

    [RelayCommand]
    private void ClearAllSteps()
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning) { ShowToast("请先结束当前序列"); return; }
        ch.Steps.Clear();
        ch.SelectStep(null);
        ch.RenumberSteps();
        ShowToast("已清除全部步骤");
    }

    [RelayCommand]
    private void NewSequence()
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning) { ShowToast("请先结束当前序列"); return; }
        TextDialog.Open("给新序列起个名字", "新合成工艺-01", name =>
        {
            ch.SchemeName = name;
            ch.Steps.Clear();
            ch.RenumberSteps();
            ch.NotifyAllDisplay();
            ShowToast($"已新建空序列「{name}」");
        });
    }

    [RelayCommand]
    private void GoToRecipeLibrary() => NavigateSection("Files");

    [RelayCommand]
    private void EditSelectedStepParam(string paramName)
    {
        var ch = ActiveChannel;
        var st = ch.SelectedStep;
        if (st == null || ch.IsSequenceRunning) { ShowToast("运行中不可修改步骤参数"); return; }
        switch (paramName)
        {
            case "Temp":
                Keypad.Open($"第 {st.StepNumber} 步 · 目标温度", "℃", st.TargetTemp, -40, 180, ch.Tr, v => { st.TargetTemp = v; st.RefreshComputed(); });
                break;
            case "Rate":
                Keypad.Open($"第 {st.StepNumber} 步 · 变温速率", "℃/min", st.Rate, 0.05, 10, st.Rate, v => { st.Rate = v; st.RefreshComputed(); });
                break;
            case "Duration":
                Keypad.Open($"第 {st.StepNumber} 步 · 时长", "min", st.DurationMin, 1, 1440, st.DurationMin, v => { st.DurationMin = (int)Math.Round(v); st.RefreshComputed(); });
                break;
            case "Stir":
                Keypad.Open($"第 {st.StepNumber} 步 · 搅拌转速", "rpm", st.StirRpm, 50, 1000, ch.Rpm, v => { st.StirRpm = (int)Math.Round(v); st.StirUnchanged = false; st.RefreshComputed(); });
                break;
            case "DoseVol":
                Keypad.Open($"第 {st.StepNumber} 步 · 加料体积", "mL", st.DoseVolumeMl, 0.1, 500, st.DoseVolumeMl, v => { st.DoseVolumeMl = v; st.RefreshComputed(); });
                break;
            case "DoseFlow":
                Keypad.Open($"第 {st.StepNumber} 步 · 加料流量", "mL/min", st.DoseFlowMlMin, 0.05, 50, st.DoseFlowMlMin, v => { st.DoseFlowMlMin = v; st.RefreshComputed(); });
                break;
            case "PausePrompt":
                TextDialog.Open("暂停确认提示文字", st.PausePrompt, s => { st.PausePrompt = s; st.RefreshComputed(); });
                break;
        }
    }

    // ===== TREND COMMANDS =====
    [RelayCommand]
    private void AddTrendMarker()
    {
        var ch = ActiveChannel;
        double elapsed = ch.TrendHistory.Count > 0 ? ch.TrendHistory[^1].ElapsedMin : 0;
        ch.TrendMarkers.Add(new TrendMarker { ElapsedMin = elapsed, Label = $"M{ch.TrendMarkers.Count + 1}" });
        ch.ChartRevision++;
        ShowToast($"已在 {DateTime.Now:HH:mm:ss} 添加时间标记 M{ch.TrendMarkers.Count}");
    }

    [RelayCommand]
    private void TakeTrendSnapshot() => ShowToast($"{ActiveChannel.ChannelTitle} 已拍摄趋势快照");

    [RelayCommand]
    private void SetTrendXAxis(string mode)
    {
        ActiveChannel.XAxisClock = mode == "clock";
        ActiveChannel.ChartRevision++;
    }

    [RelayCommand]
    private void SetTrendYAxis(string mode)
    {
        ActiveChannel.YAxisAll = mode == "all";
        ActiveChannel.ChartRevision++;
    }

    // ===== SAFETY COMMANDS =====
    [RelayCommand]
    private void EditSafetyLimit(string field)
    {
        var ch = ActiveChannel;
        if (ch.IsSequenceRunning)
        {
            ShowToast("序列运行中不可修改安全限值");
            return;
        }
        switch (field)
        {
            case "TrMin":
                Keypad.Open($"{ch.ChannelTitle} · Tr min（操作人下限）", "℃", ch.TrMin, -40, ch.TrMax - 5, ch.Tr, v =>
                {
                    ch.TrMin = v; ch.NotifyAllDisplay(); ShowToast($"Tr 下限已更新为 {v:F1} ℃");
                });
                break;
            case "TrMax":
                Keypad.Open($"{ch.ChannelTitle} · Tr max（操作人上限）", "℃", ch.TrMax, ch.TrMin + 5, 180, ch.Tr, v =>
                {
                    ch.TrMax = v; ch.NotifyAllDisplay(); ShowToast($"Tr 上限已更新为 {v:F1} ℃");
                });
                break;
            case "MaxRamp":
                Keypad.Open($"{ch.ChannelTitle} · 变温速率上限（0=关）", "℃/min", ch.MaxRampRate, 0, 20, Math.Abs(ch.RampRate), v =>
                {
                    ch.MaxRampRate = v; ch.NotifyAllDisplay(); ShowToast(v <= 0 ? "变温速率监控已关闭" : $"变温速率上限已设为 {v:F2} ℃/min");
                });
                break;
        }
    }

    // ===== EXPORT COMMANDS =====
    [RelayCommand]
    private void SetExportInterval(string secStr)
    {
        if (int.TryParse(secStr, out int s))
        {
            ExportIntervalSec = s;
            OnPropertyChanged(nameof(IsExportInterval1));
            OnPropertyChanged(nameof(IsExportInterval10));
            OnPropertyChanged(nameof(IsExportInterval60));
        }
    }

    [RelayCommand]
    private void SetExportRange(string mode)
    {
        ExportRangeAll = mode == "all";
        OnPropertyChanged(nameof(ExportRangeCustom));
    }

    [RelayCommand]
    private void EditExportTime(string which)
    {
        if (which == "from")
            Keypad.Open("导出起始时间", "min", ExportFromMin, 0, ExportToMin, ExportFromMin, v => ExportFromMin = (int)v);
        else
            Keypad.Open("导出结束时间", "min", ExportToMin, ExportFromMin, 1440, ExportToMin, v => ExportToMin = (int)v);
    }

    [RelayCommand]
    private void ToggleExportBatch(ExportBatchItem item)
    {
        item.IsSelected = !item.IsSelected;
        OnPropertyChanged(nameof(ExportSelectedCountText));
        OnPropertyChanged(nameof(ExportButtonText));
    }

    [RelayCommand]
    private void ToggleSelectAllBatches()
    {
        bool selectAll = ExportBatches.Any(b => !b.IsSelected);
        foreach (var b in ExportBatches) b.IsSelected = selectAll;
        OnPropertyChanged(nameof(ExportSelectedCountText));
        OnPropertyChanged(nameof(ExportButtonText));
    }

    [RelayCommand]
    private void DoExportCsv()
    {
        var chosen = ExportBatches.Where(b => b.IsSelected).ToList();
        if (chosen.Count == 0) { ShowToast("请至少勾选一炉数据记录"); return; }
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Exports");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, $"HTLAB_Export_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("BatchName,Channel,Status,Duration,Date,IntervalSec,Tr_Now,Tj_Now");
            foreach (var b in chosen)
                sb.AppendLine($"{b.Name},{b.ChannelName},{b.Status},{b.Duration},{b.Date},{ExportIntervalSec},{ActiveChannel.Tr:F2},{ActiveChannel.Tj:F2}");
            File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
            ShowToast($"已导出 {chosen.Count} 炉 CSV 到 Exports 目录");
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}");
        }
    }

    // ===== PID COMMANDS =====
    [RelayCommand]
    private void EditPidLookupTemp()
    {
        Keypad.Open("查看某温度的插值 PID", "℃", PidLookupTemp, -40, 180, ActiveChannel.TjSetpoint, v =>
        {
            PidLookupTemp = v;
            OnPropertyChanged(nameof(PidLookupResultText));
        });
    }

    [RelayCommand]
    private void EditPidBoostLimit()
    {
        Keypad.Open("快速升温加码上限", "K", PidBoostLimitK, 0, 20, PidBoostLimitK, v =>
        {
            PidBoostLimitK = v;
            OnPropertyChanged(nameof(PidOuterLoopLine2));
            ShowToast($"加码上限已改为 {v:F1} K（重启控制核心后生效）");
        });
    }

    [RelayCommand]
    private void AddPidRow()
    {
        double nextT = PidRows.Count > 0 ? PidRows[^1].Temp + 15 : 25;
        PidRows.Add(new PidGainRow { Temp = nextT, Kp = 12.0, Ki = 0.04, Kd = 220.0, SteadyBias = 1.5, HeatRatio = 2.0 });
        ShowToast($"已新增 {nextT:F0} ℃ 增益行");
    }

    [RelayCommand]
    private void ApplyPidTable() => ShowToast("PID 增益表已下发至控制核心");

    [RelayCommand]
    private void EditPidTemp(PidGainRow row)
        => Keypad.Open("PID 增益表 · 温度点", "℃", row.Temp, -40, 180, row.Temp, v => { row.Temp = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void EditPidKp(PidGainRow row)
        => Keypad.Open($"温度 {row.Temp:F0} ℃ · Kp", "", row.Kp, 0.1, 100, row.Kp, v => { row.Kp = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void EditPidKi(PidGainRow row)
        => Keypad.Open($"温度 {row.Temp:F0} ℃ · Ki", "", row.Ki, 0.0001, 5, row.Ki, v => { row.Ki = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void EditPidKd(PidGainRow row)
        => Keypad.Open($"温度 {row.Temp:F0} ℃ · Kd", "", row.Kd, 0, 2000, row.Kd, v => { row.Kd = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void EditPidSteadyBias(PidGainRow row)
        => Keypad.Open($"温度 {row.Temp:F0} ℃ · 稳态偏置", "K", row.SteadyBias ?? 0, -20, 20, row.SteadyBias ?? 0, v => { row.SteadyBias = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void EditPidHeatRatio(PidGainRow row)
        => Keypad.Open($"温度 {row.Temp:F0} ℃ · 加热比", "", row.HeatRatio, 0.1, 10, row.HeatRatio, v => { row.HeatRatio = v; OnPropertyChanged(nameof(PidLookupResultText)); });

    [RelayCommand]
    private void ReloadPidTable()
    {
        SeedPidTable();
        ShowToast("已从本机磁盘重新读取 PID 增益表");
    }

    // ===== RECIPE / FILES COMMANDS =====
    [RelayCommand]
    private void SelectRecipe(RecipeItem item)
    {
        foreach (var r in Recipes) r.IsSelected = (r == item);
        SelectedRecipe = item;
    }

    [RelayCommand]
    private void LoadRecipeToChannel(string chStr)
    {
        if (SelectedRecipe == null) return;
        var ch = chStr == "1" ? Channel1 : Channel2;
        if (ch.IsSequenceRunning)
        {
            ShowToast($"{ch.ChannelTitle} 正在运行中，无法装入新配方");
            return;
        }
        ch.SchemeName = SelectedRecipe.Name;
        ch.Steps.Clear();
        foreach (var st in SelectedRecipe.Steps)
            ch.Steps.Add(st.Clone());
        ch.RenumberSteps();
        ch.SelectStep(ch.Steps.FirstOrDefault());
        ch.NotifyAllDisplay();
        ShowToast($"已将「{SelectedRecipe.Name}」装入 {ch.ChannelTitle}");
    }

    [RelayCommand]
    private void SaveChannelAsRecipe(string chStr)
    {
        var ch = chStr == "1" ? Channel1 : Channel2;
        TextDialog.Open("给这个配方起个名字", ch.SchemeName, name =>
        {
            var r = new RecipeItem
            {
                Name = name,
                SavedAt = DateTime.Now.ToString("MM-dd HH:mm"),
                EstimatedDuration = $"{Math.Max(1, ch.Steps.Count * 35 / 60)} h {(ch.Steps.Count * 35) % 60:D2} min"
            };
            foreach (var st in ch.Steps) r.Steps.Add(st.Clone());
            Recipes.Insert(0, r);
            SelectRecipe(r);
            ShowToast($"已保存配方「{name}」至本机配方库");
        });
    }

    [RelayCommand]
    private void DeleteSelectedRecipe()
    {
        if (SelectedRecipe == null) return;
        var name = SelectedRecipe.Name;
        Recipes.Remove(SelectedRecipe);
        SelectRecipe(Recipes.FirstOrDefault()!);
        ShowToast($"已删除配方「{name}」");
    }

    // ===== COMM COMMANDS =====
    [RelayCommand]
    private void OpenCommDeviceDialog(CommDeviceConfig dev)
    {
        CommDialog.Open(dev, () => ShowToast($"已保存「{dev.Name}」串口参数（重启控制核心生效）"));
    }

    [RelayCommand]
    private void EditCommPort()
    {
        int cur = int.TryParse(CommDialog.Port.Replace("COM", "", StringComparison.OrdinalIgnoreCase), out int n) ? n : 1;
        Keypad.Open($"{CommDialog.DeviceName} · 串口号（COM 后数字）", "", cur, 1, 64, cur, v =>
        {
            CommDialog.Port = $"COM{(int)Math.Round(v)}";
        });
    }

    [RelayCommand]
    private void EditCommStation()
    {
        Keypad.Open($"{CommDialog.DeviceName} · Modbus 站号", "", CommDialog.StationId, 1, 247, CommDialog.StationId, v =>
        {
            CommDialog.StationId = (int)Math.Round(v);
        });
    }

    [RelayCommand]
    private void EditCommPoll()
    {
        Keypad.Open($"{CommDialog.DeviceName} · 轮询周期", "ms", CommDialog.PollMs, 50, 5000, CommDialog.PollMs, v =>
        {
            CommDialog.PollMs = (int)Math.Round(v);
        });
    }

    [RelayCommand]
    private void EditTcpPort()
    {
        Keypad.Open("对外通信 · 监听端口", "", TcpPort, 1024, 65535, TcpPort, v =>
        {
            TcpPort = (int)v;
            ShowToast($"TCP 端口已改为 {TcpPort}");
        });
    }

    [RelayCommand]
    private void EditTcpTimeout()
    {
        Keypad.Open("对外通信 · 断线超时", "s", TcpTimeoutSec, 3, 120, TcpTimeoutSec, v =>
        {
            TcpTimeoutSec = (int)v;
            ShowToast($"断线超时已改为 {TcpTimeoutSec} s");
        });
    }

    [RelayCommand]
    private void ToggleRemoteControl()
    {
        AllowRemoteControl = !AllowRemoteControl;
        ShowToast(AllowRemoteControl ? "已允许 PC 工作站远程控制" : "已关闭远程控制（PC 仅只读）");
    }

    // ===== ALARM & UTILITY COMMANDS =====
    [RelayCommand]
    private void OpenAlarms()
    {
        var unacked = Alarms.FirstOrDefault(a => !a.IsAcknowledged);
        if (unacked != null)
        {
            ActiveInterlockAlarm = unacked;
            IsAlarmInterlockOpen = true;
        }
        else
        {
            IsAlarmListOpen = true;
        }
    }

    [RelayCommand]
    private void OpenAlarmList() => IsAlarmListOpen = true;

    [RelayCommand]
    private void CloseAlarmList() => IsAlarmListOpen = false;

    [RelayCommand]
    private void AcknowledgeInterlock()
    {
        if (ActiveInterlockAlarm != null) ActiveInterlockAlarm.IsAcknowledged = true;
        IsAlarmInterlockOpen = false;
        NotifyAlarmsChanged();
        ShowToast("安全联锁已确认复位");
    }

    [RelayCommand]
    private void AcknowledgeAllAlarms()
    {
        foreach (var a in Alarms) a.IsAcknowledged = true;
        IsAlarmInterlockOpen = false;
        NotifyAlarmsChanged();
        ShowToast("全部报警已确认");
    }

    [RelayCommand]
    private void SimulateInterlockAlarm()
    {
        IsAlarmListOpen = false;
        TriggerInterlock("通道 1", "Tr 超过上限 180.0 ℃（实测 180.6 ℃）", "条件已恢复：实测 176.2 ℃");
    }

    [RelayCommand]
    private void ToggleWaterCooling()
    {
        WaterCoolingOn = !WaterCoolingOn;
        OnPropertyChanged(nameof(WaterCoolingText));
        OnPropertyChanged(nameof(SysCoolingText));
    }

    private void NotifyAlarmsChanged()
    {
        OnPropertyChanged(nameof(UnreadAlarmCount));
        OnPropertyChanged(nameof(HasUnreadAlarms));
        OnPropertyChanged(nameof(ActiveAlarmWhenText));
    }

    // ===== SEED DATA (Matches HTML designs) =====
    private void SeedChannel1Steps()
    {
        Channel1.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Tr, TargetTemp = 65.0, ArriveMode = ArriveMode.Duration, DurationMin = 12, ProgressPct = 100, IsCompleted = true });
        Channel1.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Wait, TargetTemp = 65.0, DurationMin = 45, ProgressPct = 100, IsCompleted = true });
        Channel1.Steps.Add(new SequenceStep { StepNumber = 3, Kind = StepKind.Tr, TargetTemp = 20.0, ArriveMode = ArriveMode.Rate, Rate = 0.50, ProgressPct = 45, IsRunning = true, IsSelected = true });
        Channel1.Steps.Add(new SequenceStep { StepNumber = 4, Kind = StepKind.Dose, DoseVolumeMl = 3.6, DoseFlowMlMin = 1.20, DoseMode = 1, ProgressPct = 0 });
        Channel1.Steps.Add(new SequenceStep { StepNumber = 5, Kind = StepKind.Wait, TargetTemp = 20.0, DurationMin = 60, ProgressPct = 0 });
        Channel1.SelectedStep = Channel1.Steps[2];
    }

    private void SeedChannel2Steps()
    {
        Channel2.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Tj, TargetTemp = -10.0, ArriveMode = ArriveMode.Rate, Rate = 1.00, StirRpm = 300, StirUnchanged = false, IsSelected = true });
        Channel2.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Wait, TargetTemp = -10.0, DurationMin = 60, StirRpm = 300, StirUnchanged = false });
        Channel2.Steps.Add(new SequenceStep { StepNumber = 3, Kind = StepKind.Tj, TargetTemp = 20.0, ArriveMode = ArriveMode.Duration, DurationMin = 35, StirRpm = 300, StirUnchanged = false });
        Channel2.SelectedStep = Channel2.Steps[0];
    }

    private void SeedRecipes()
    {
        var r1 = new RecipeItem { Name = "结晶工艺-0824", SavedAt = "11-23 20:14", EstimatedDuration = "3 h 30 min" };
        foreach (var s in Channel1.Steps) r1.Steps.Add(s.Clone());

        var r2 = new RecipeItem { Name = "低温筛选-B3", SavedAt = "11-22 16:40", EstimatedDuration = "2 h 10 min", IsSelected = true };
        foreach (var s in Channel2.Steps) r2.Steps.Add(s.Clone());

        var r3 = new RecipeItem { Name = "溶解度曲线-乙醇", SavedAt = "11-20 09:05", EstimatedDuration = "4 h 02 min" };
        r3.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Tr, TargetTemp = 50.0, ArriveMode = ArriveMode.Rate, Rate = 1.0 });
        r3.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Wait, TargetTemp = 50.0, DurationMin = 30 });
        r3.Steps.Add(new SequenceStep { StepNumber = 3, Kind = StepKind.Cry, DurationMin = 160 });
        r3.Steps.Add(new SequenceStep { StepNumber = 4, Kind = StepKind.Wait, TargetTemp = 5.0, DurationMin = 40 });

        var r4 = new RecipeItem { Name = "回流脱水-甲苯", SavedAt = "11-18 14:22", EstimatedDuration = "2 h 45 min" };
        r4.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Tr, TargetTemp = 110.0, ArriveMode = ArriveMode.Fast });
        r4.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Reflux, RefluxDeltaK = 8.0, DurationMin = 90 });
        r4.Steps.Add(new SequenceStep { StepNumber = 3, Kind = StepKind.Tr, TargetTemp = 25.0, ArriveMode = ArriveMode.Rate, Rate = 2.0 });

        var r5 = new RecipeItem { Name = "重结晶-乙醇水", SavedAt = "11-15 10:30", EstimatedDuration = "3 h 05 min" };
        r5.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Tr, TargetTemp = 72.0, ArriveMode = ArriveMode.Rate, Rate = 1.5 });
        r5.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Dose, DoseVolumeMl = 25.0, DoseFlowMlMin = 2.5 });
        r5.Steps.Add(new SequenceStep { StepNumber = 3, Kind = StepKind.Tr, TargetTemp = 10.0, ArriveMode = ArriveMode.Rate, Rate = 0.5 });

        var r6 = new RecipeItem { Name = "酯化回流-小试", SavedAt = "11-12 10:20", EstimatedDuration = "2 h 20 min" };
        r6.Steps.Add(new SequenceStep { StepNumber = 1, Kind = StepKind.Stir, StirRpm = 450, StirUnchanged = false });
        r6.Steps.Add(new SequenceStep { StepNumber = 2, Kind = StepKind.Reflux, RefluxDeltaK = 10.0, DurationMin = 120 });

        Recipes.Add(r1); Recipes.Add(r2); Recipes.Add(r3); Recipes.Add(r4); Recipes.Add(r5); Recipes.Add(r6);
        SelectedRecipe = r2;
    }

    private void SeedPidTable()
    {
        PidRows.Clear();
        PidRows.Add(new PidGainRow { Temp = -15, Kp = 10.20, Ki = 0.0190, Kd = 500.0, SteadyBias = 0.8, HeatRatio = 3.50 });
        PidRows.Add(new PidGainRow { Temp = -10, Kp = 10.00, Ki = 0.0220, Kd = 400.0, SteadyBias = 1.3, HeatRatio = 3.50 });
        PidRows.Add(new PidGainRow { Temp = 0, Kp = 9.40, Ki = 0.0300, Kd = 200.0, SteadyBias = 1.4, HeatRatio = 3.50 });
        PidRows.Add(new PidGainRow { Temp = 8, Kp = 9.40, Ki = 0.0320, Kd = 190.0, SteadyBias = 1.5, HeatRatio = 3.50 });
        PidRows.Add(new PidGainRow { Temp = 15, Kp = 9.40, Ki = 0.0340, Kd = 180.0, SteadyBias = 1.6, HeatRatio = 3.50 });
        PidRows.Add(new PidGainRow { Temp = 40, Kp = 9.40, Ki = 0.0370, Kd = 171.0, SteadyBias = 3.4, HeatRatio = 2.10, IsActiveRow = true });
        PidRows.Add(new PidGainRow { Temp = 50, Kp = 20.27, Ki = 0.0712, Kd = 418.6, SteadyBias = null, HeatRatio = 5.10 });
        PidRows.Add(new PidGainRow { Temp = 80, Kp = 19.31, Ki = 0.0624, Kd = 431.5, SteadyBias = null, HeatRatio = 0.25 });
    }

    private void SeedExportBatches()
    {
        ExportBatches.Add(new ExportBatchItem { Name = "结晶工艺-0824", ChannelName = "通道 1", Status = "运行中", Duration = "01:29:30", Date = "今天", IsSelected = true });
        ExportBatches.Add(new ExportBatchItem { Name = "结晶工艺-0823", ChannelName = "通道 1", Status = "已完成", Duration = "03:28:10", Date = "2026-11-22", IsSelected = true });
        ExportBatches.Add(new ExportBatchItem { Name = "晶型筛选-低温-B2", ChannelName = "通道 2", Status = "已完成", Duration = "02:10:45", Date = "2026-11-21", IsSelected = false });
        ExportBatches.Add(new ExportBatchItem { Name = "溶解度曲线-乙醇", ChannelName = "通道 2", Status = "已完成", Duration = "04:02:30", Date = "2026-11-20", IsSelected = false });
    }

    private void SeedCommDevices()
    {
        CommDevices.Add(new CommDeviceConfig { Name = "TEC 温控器 RD105", Port = "COM7", BaudRate = 38400, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "切换继电器 IO8R", Port = "COM8", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "冷水机", Port = "COM9", BaudRate = 9600, StationId = 1, Status = "未启用", Enabled = false });
        CommDevices.Add(new CommDeviceConfig { Name = "Tr 探头（两个通道共用）", Port = "COM3", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "pH 电极 · 通道 1", Port = "COM12", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "pH 电极 · 通道 2", Port = "COM13", BaudRate = 9600, StationId = 1, Status = "无应答", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "搅拌 · 通道 1", Port = "COM14", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "搅拌 · 通道 2", Port = "COM11", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });
        CommDevices.Add(new CommDeviceConfig { Name = "进料系统", Port = "COM15", BaudRate = 9600, StationId = 1, Status = "正常", Enabled = true });

        ConnectedPcs.Add(new ConnectedPcInfo { IpAddress = "192.168.1.20", StatsText = "收 12.4 k 条 · 发 12.6 k 条", ConnectedDuration = "已连 01:12:40" });
        ConnectedPcs.Add(new ConnectedPcInfo { IpAddress = "192.168.1.21", StatsText = "收 3.1 k 条 · 发 3.1 k 条", ConnectedDuration = "已连 00:20:05" });
        ConnectedPcs.Add(new ConnectedPcInfo { IpAddress = "192.168.1.22", StatsText = "收 0.4 k 条 · 发 0.4 k 条", ConnectedDuration = "已连 00:02:11" });
    }

    private void SeedAlarms()
    {
        Alarms.Add(new AlarmRecord
        {
            Timestamp = "11-23 21:57:42",
            ChannelName = "通道 1",
            Title = "安全联锁 · 停止控温",
            Message = "Tr 超过上限 180.0 ℃（实测 180.6 ℃）",
            RecoveredText = "条件已恢复：实测 176.2 ℃",
            ActionTaken = "安全层已执行：停止控温，搅拌保持",
            IsAcknowledged = false
        });
        Alarms.Add(new AlarmRecord
        {
            Timestamp = "11-23 16:20:05",
            ChannelName = "通道 2",
            Title = "安全联锁 · 冷媒超温",
            Message = "冷媒温度过高 46.2 ℃（上限 45.0 ℃，TEC 的热排不出去）",
            RecoveredText = "条件已恢复：实测 38.4 ℃",
            ActionTaken = "安全层已执行：停止制冷并报警",
            IsAcknowledged = true
        });
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
