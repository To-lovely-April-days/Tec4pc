using CommunityToolkit.Mvvm.ComponentModel;
using HtlabHmi.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace HtlabHmi.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    public int ChannelIndex { get; }
    public string ChannelTitle => $"通道 {ChannelIndex}";

    // Telemetry
    [ObservableProperty] private double _tr;
    [ObservableProperty] private double _tj;
    [ObservableProperty] private double _tjSetpoint;
    [ObservableProperty] private double _tc = 20.5;
    [ObservableProperty] private double _ph = 6.98;
    [ObservableProperty] private int _rpm;
    [ObservableProperty] private int _targetRpm = 300;
    [ObservableProperty] private double _stirCurrent;
    [ObservableProperty] private double _dosedMl;
    [ObservableProperty] private double _rampRate;
    [ObservableProperty] private double _outputPowerPct;

    // Control states
    [ObservableProperty] private bool _lightOn;
    [ObservableProperty] private bool _stirRunning;
    [ObservableProperty] private bool _tempControlRunning;
    [ObservableProperty] private bool _trSensorEnabled = true;
    [ObservableProperty] private bool _phElectrodeEnabled;
    [ObservableProperty] private TempControlMode _controlMode = TempControlMode.Tr;
    [ObservableProperty] private ArriveMode _arriveMode = ArriveMode.Fast;
    [ObservableProperty] private double _targetTemp = 60.0;
    [ObservableProperty] private double _lastTargetTemp = 60.0;
    [ObservableProperty] private double _rateSetting = 1.0;
    [ObservableProperty] private int _durationSettingMin = 30;
    [ObservableProperty] private double _refluxDeltaK = 8.0;

    // Trend series toggles (ChTrend 6-tab bar)
    [ObservableProperty] private bool _showTr = true;
    [ObservableProperty] private bool _showTj = true;
    [ObservableProperty] private bool _showTjSp = true;
    [ObservableProperty] private bool _showDeltaT;
    [ObservableProperty] private bool _showPh;
    [ObservableProperty] private bool _showRpm;

    // Sequence state
    [ObservableProperty] private string _schemeName = "";
    [ObservableProperty] private bool _isSequenceRunning;
    [ObservableProperty] private int _currentStepIndex; // 0-based
    [ObservableProperty] private int _elapsedSeconds;
    [ObservableProperty] private int _remainingSeconds;
    [ObservableProperty] private int _stepRemainingSeconds;
    [ObservableProperty] private SequenceStep? _selectedStep;

    public ObservableCollection<SequenceStep> Steps { get; } = new();

    // Safety limits
    [ObservableProperty] private string _reactorType = "250 mL 玻璃釜";
    [ObservableProperty] private double _trMin = -20.0;
    [ObservableProperty] private double _trMax = 180.0;
    [ObservableProperty] private double _maxRampRate = 0.0; // 0 = off

    // Trend chart state
    public List<TrendSample> TrendHistory { get; } = new();
    public List<TrendMarker> TrendMarkers { get; } = new();
    [ObservableProperty] private int _chartRevision;
    [ObservableProperty] private bool _xAxisClock = true;
    [ObservableProperty] private bool _yAxisAll = true;

    // Crystallization curve 12 segments (CryViewPart 1:1)
    public ObservableCollection<CrySegment> CrySegmentsLeft { get; } = new()
    {
        new CrySegment { Index = 1, StartTemp = 60, EndTemp = 55, DurationMin = 10, RateLabel = "降 0.50" },
        new CrySegment { Index = 2, StartTemp = 55, EndTemp = 55, DurationMin = 15, RateLabel = "保温" },
        new CrySegment { Index = 3, StartTemp = 55, EndTemp = 50, DurationMin = 10, RateLabel = "降 0.50" },
        new CrySegment { Index = 4, StartTemp = 50, EndTemp = 50, DurationMin = 15, RateLabel = "保温" },
        new CrySegment { Index = 5, StartTemp = 50, EndTemp = 45, DurationMin = 10, RateLabel = "降 0.50" },
        new CrySegment { Index = 6, StartTemp = 45, EndTemp = 45, DurationMin = 15, RateLabel = "保温" },
    };

    public ObservableCollection<CrySegment> CrySegmentsRight { get; } = new()
    {
        new CrySegment { Index = 7, StartTemp = 45, EndTemp = 38, DurationMin = 14, RateLabel = "降 0.50", IsCurrent = true },
        new CrySegment { Index = 8, StartTemp = 38, EndTemp = 38, DurationMin = 20, RateLabel = "保温" },
        new CrySegment { Index = 9, StartTemp = 38, EndTemp = 30, DurationMin = 16, RateLabel = "降 0.50" },
        new CrySegment { Index = 10, StartTemp = 30, EndTemp = 20, DurationMin = 20, RateLabel = "降 0.50" },
        new CrySegment { Index = 11, StartTemp = 20, EndTemp = 10, DurationMin = 25, RateLabel = "降 0.40" },
        new CrySegment { Index = 12, StartTemp = 10, EndTemp = 5, DurationMin = 20, RateLabel = "降 0.25" },
    };

    public ChannelViewModel(int index)
    {
        ChannelIndex = index;
    }

    // Computed display helpers
    public double DeltaT => Tr - Tj;
    public string TrText => $"{Tr:F1}";
    public string TjText => $"{Tj:F1}";
    public string TjSpText => $"{TjSetpoint:F1}";
    public string TargetTempText => $"{TargetTemp:F1}";
    public string DeltaTText => DeltaT >= 0 ? $"+{DeltaT:F1}" : $"−{Math.Abs(DeltaT):F1}";
    public string TcText => $"{Tc:F1}";
    public string PhText => $"{Ph:F2}";
    public string DosedMlText => $"{DosedMl:F1}";
    public string RampRateText => RampRate >= 0 ? $"+{RampRate:F1}" : $"−{Math.Abs(RampRate):F1}";
    public string LightText => LightOn ? "开" : "关";
    public string StirButtonText => StirRunning ? $"{Rpm} rpm" : "关";
    public string StirStatusLine => StirRunning
        ? $"实测 {Rpm} rpm　·　电流 {StirCurrent:F2} A"
        : "搅拌已停止　·　电流 0.00 A";

    public string TopStateText => IsSequenceRunning
        ? $"运行中 · {CurrentStepIndex + 1} / {Math.Max(1, Steps.Count)}"
        : (TempControlRunning ? "手动控温中" : "待机");

    public bool IsActive => IsSequenceRunning || TempControlRunning;
    public double ReactorOpacity => IsActive ? 1.0 : 0.72;

    public string MainCardRightTop => IsSequenceRunning
        ? FormatHms(ElapsedSeconds)
        : (TempControlRunning ? "手动控温" : "待机");

    public string MainCardModeLine =>
        $"{(IsSequenceRunning ? "程序模式" : "手动模式")} · {ControlModeShortName}";

    public string MainCardRightBottom => IsSequenceRunning
        ? $"步骤 {CurrentStepIndex + 1} / {Steps.Count} · 剩余 {FormatHms(RemainingSeconds)}"
        : $"方案已加载 · 共 {Steps.Count} 步";

    public double OverallProgressPct => !IsSequenceRunning || Steps.Count == 0
        ? 0
        : Math.Clamp((CurrentStepIndex + (Steps.ElementAtOrDefault(CurrentStepIndex)?.ProgressPct ?? 0) / 100.0) / Steps.Count * 100.0, 0, 100);

    public string ControlModeShortName => ControlMode switch
    {
        TempControlMode.Tr => "釜内控温 Tr",
        TempControlMode.Tj => "夹套控温 Tj",
        TempControlMode.Reflux => "蒸回流 Tj − Tr",
        TempControlMode.Cry => "结晶曲线",
        _ => "釜内控温 Tr"
    };

    public string HeaderModeLine
    {
        get
        {
            var runPart = IsSequenceRunning ? "程序控制" : (TempControlRunning ? "手动控温" : "待机就绪");
            var heatPart = !TempControlRunning ? "未出力" : (TjSetpoint > Tr + 0.3 ? "加热中" : (TjSetpoint < Tr - 0.3 ? "制冷中" : "恒温保持"));
            return $"{runPart} · {heatPart} · {ControlModeShortName} · 热源 TEC";
        }
    }

    public string StepCounterText => Steps.Count == 0
        ? "无步骤"
        : (IsSequenceRunning ? $"步骤 {CurrentStepIndex + 1} / {Steps.Count}" : $"共 {Steps.Count} 步");

    public string StepRemainText => IsSequenceRunning ? FormatHms(StepRemainingSeconds) : "00:00:00";

    public string TargetLabel => ControlMode switch
    {
        TempControlMode.Tr => "目标 Tr",
        TempControlMode.Tj => "目标 Tj",
        TempControlMode.Reflux => "夹套高于釜内 ΔT",
        TempControlMode.Cry => "当前曲线 SP",
        _ => "目标 Tr"
    };

    public string TempStatusLine
    {
        get
        {
            var arriveTxt = ArriveMode switch
            {
                ArriveMode.Fast => "尽快：不限速，按最大能力到达",
                ArriveMode.Rate => $"按速率：{RateSetting:F2} ℃/min 平滑变温",
                ArriveMode.Duration => $"按时长：{DurationSettingMin} min 内到达目标",
                _ => ""
            };
            return $"{arriveTxt}　·　Tj {Tj:F1} ℃　·　Tr − Tj {DeltaTText} K　·　Tc {Tc:F1} ℃ · 冷媒正常";
        }
    }

    public string TempControlButtonTitle => TempControlRunning ? "控温中" : "开始控温";
    public string TempControlButtonSub => TempControlRunning ? "点按停止" : "启动 TEC";

    public string MaxRampRateText => MaxRampRate <= 0 ? "关（不检测）" : $"{MaxRampRate:F2} ℃/min";
    public string TrMinText => TrMin < 0 ? $"−{Math.Abs(TrMin):F1} ℃" : $"{TrMin:F1} ℃";
    public string TrMaxText => $"{TrMax:F1} ℃";

    public bool IsModeTr => ControlMode == TempControlMode.Tr;
    public bool IsModeTj => ControlMode == TempControlMode.Tj;
    public bool IsModeReflux => ControlMode == TempControlMode.Reflux;
    public bool IsModeCry => ControlMode == TempControlMode.Cry;

    public bool IsArriveFast => ArriveMode == ArriveMode.Fast;
    public bool IsArriveRate => ArriveMode == ArriveMode.Rate;
    public bool IsArriveDuration => ArriveMode == ArriveMode.Duration;

    public string SchemeOverviewMeta => IsSequenceRunning
        ? $"{Steps.Count} 步 · 共 3 h 30 min · 已走 {ElapsedSeconds / 3600} h {(ElapsedSeconds % 3600) / 60} min · 剩 {RemainingSeconds / 3600} h {(RemainingSeconds % 3600) / 60} min"
        : $"{Steps.Count} 步 · 共 2 h 10 min · 就绪待启动";

    public string SchemeOverviewBadge => IsSequenceRunning
        ? $"运行中 · 步骤 {CurrentStepIndex + 1} / {Steps.Count}"
        : $"待机 · 0 / {Steps.Count}";

    public string SeqHeaderMeta => IsSequenceRunning
        ? $"{Steps.Count} / 6 步　·　预计 3 h 30 min　·　已走 {ElapsedSeconds / 3600} h {(ElapsedSeconds % 3600) / 60} min"
        : $"{Steps.Count} / 6 步　·　编辑态（可增删改步骤）";

    public bool CanEditSequence => !IsSequenceRunning;

    public void SelectStep(SequenceStep? step)
    {
        foreach (var s in Steps) s.IsSelected = (s == step);
        SelectedStep = step;
    }

    public void RenumberSteps()
    {
        for (int i = 0; i < Steps.Count; i++)
        {
            Steps[i].StepNumber = i + 1;
            Steps[i].RefreshComputed();
        }
        OnPropertyChanged(nameof(SeqHeaderMeta));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(MainCardRightBottom));
    }

    public void SeedInitialTrend(double baseTr, double baseTj, bool isRampHistory)
    {
        TrendHistory.Clear();
        var now = DateTime.Now;
        var rng = new Random(ChannelIndex * 100 + 7);
        for (int i = 90; i >= 0; i--)
        {
            double elapsed = 90 - i;
            double tr, tj, sp;
            if (isRampHistory)
            {
                if (elapsed < 25)
                {
                    tr = 25.0 + (65.0 - 25.0) * (elapsed / 25.0);
                    tj = tr + 4.5;
                    sp = 65.0;
                }
                else if (elapsed < 65)
                {
                    tr = 65.0 + Math.Sin(elapsed * 0.2) * 0.12;
                    tj = 67.2 + Math.Sin(elapsed * 0.2) * 0.18;
                    sp = 65.0;
                }
                else
                {
                    double frac = (elapsed - 65) / 25.0;
                    tr = 65.0 - (65.0 - baseTr) * frac;
                    tj = baseTj + (67.2 - baseTj) * (1.0 - frac);
                    sp = baseTj;
                }
            }
            else
            {
                tr = baseTr + (rng.NextDouble() - 0.5) * 0.15;
                tj = baseTj + (rng.NextDouble() - 0.5) * 0.12;
                sp = baseTj;
            }
            TrendHistory.Add(new TrendSample
            {
                Time = now.AddMinutes(-i),
                ElapsedMin = elapsed,
                Tr = Math.Round(tr, 2),
                Tj = Math.Round(tj, 2),
                TjSp = Math.Round(sp, 2),
                Ph = Ph + (rng.NextDouble() - 0.5) * 0.02,
                Rpm = Rpm
            });
        }
        ChartRevision++;
    }

    public void TickSimulation(Random rng)
    {
        // Thermal physics simulation (1 Hz)
        if (TempControlRunning)
        {
            double sp = ControlMode switch
            {
                TempControlMode.Tr => TargetTemp,
                TempControlMode.Tj => TargetTemp,
                TempControlMode.Reflux => Tr + RefluxDeltaK,
                TempControlMode.Cry => 34.0,
                _ => TargetTemp
            };

            if (ControlMode == TempControlMode.Tr || ControlMode == TempControlMode.Cry)
            {
                double err = sp - Tr;
                TjSetpoint = Math.Clamp(sp + err * 0.45 + (sp > 25 ? 3.4 : -1.8), -40, 200);
                double prevTr = Tr;
                Tj += (TjSetpoint - Tj) * 0.08 + (rng.NextDouble() - 0.5) * 0.03;
                double maxStep = ArriveMode == ArriveMode.Rate ? RateSetting / 60.0 : 0.08;
                double dTr = Math.Clamp((Tj - 3.2 - Tr) * 0.05, -maxStep, maxStep);
                Tr += dTr + (rng.NextDouble() - 0.5) * 0.015;
                RampRate = (Tr - prevTr) * 60.0;
                OutputPowerPct = Math.Clamp(Math.Abs(TjSetpoint - 22.0) * 0.72 + Math.Abs(err) * 2.5, 5, 100);
            }
            else
            {
                TjSetpoint = Math.Clamp(sp, -40, 200);
                double prevTr = Tr;
                Tj += (TjSetpoint - Tj) * 0.10 + (rng.NextDouble() - 0.5) * 0.03;
                Tr += (Tj - Tr) * 0.04 + (rng.NextDouble() - 0.5) * 0.015;
                RampRate = (Tr - prevTr) * 60.0;
                OutputPowerPct = Math.Clamp(Math.Abs(TjSetpoint - 22.0) * 0.75, 5, 100);
            }
        }
        else
        {
            // Drift gently toward ambient 25 C
            Tj += (25.0 - Tj) * 0.005 + (rng.NextDouble() - 0.5) * 0.02;
            Tr += (25.2 - Tr) * 0.004 + (rng.NextDouble() - 0.5) * 0.02;
            RampRate = 0.0;
            OutputPowerPct = 0.0;
        }

        // Stirrer physics
        if (StirRunning)
        {
            Rpm = (int)Math.Round(Rpm + (TargetRpm - Rpm) * 0.35);
            if (Math.Abs(TargetRpm - Rpm) <= 2) Rpm = TargetRpm;
            StirCurrent = Math.Round(0.22 + (Rpm / 1000.0) * 1.95 + (rng.NextDouble() - 0.5) * 0.02, 2);
        }
        else
        {
            Rpm = 0;
            StirCurrent = 0.0;
        }

        // Sequence timer
        if (IsSequenceRunning && Steps.Count > 0)
        {
            ElapsedSeconds++;
            if (RemainingSeconds > 0) RemainingSeconds--;
            if (StepRemainingSeconds > 0) StepRemainingSeconds--;

            for (int i = 0; i < Steps.Count; i++)
            {
                Steps[i].IsCompleted = i < CurrentStepIndex;
                Steps[i].IsRunning = i == CurrentStepIndex;
                if (i < CurrentStepIndex) Steps[i].ProgressPct = 100;
                else if (i > CurrentStepIndex) Steps[i].ProgressPct = 0;
                else
                {
                    Steps[i].ProgressPct = Math.Min(99, Steps[i].ProgressPct + 0.05);
                }
            }
        }

        // Append trend sample
        double lastElapsed = TrendHistory.Count > 0 ? TrendHistory[^1].ElapsedMin : 0;
        TrendHistory.Add(new TrendSample
        {
            Time = DateTime.Now,
            ElapsedMin = lastElapsed + 1.0 / 60.0,
            Tr = Math.Round(Tr, 2),
            Tj = Math.Round(Tj, 2),
            TjSp = Math.Round(TjSetpoint, 2),
            Ph = Math.Round(Ph, 2),
            Rpm = Rpm
        });
        if (TrendHistory.Count > 360) TrendHistory.RemoveAt(0);
        ChartRevision++;

        NotifyAllDisplay();
    }

    public void NotifyAllDisplay()
    {
        OnPropertyChanged(nameof(DeltaT));
        OnPropertyChanged(nameof(TrText));
        OnPropertyChanged(nameof(TjText));
        OnPropertyChanged(nameof(TjSpText));
        OnPropertyChanged(nameof(TargetTempText));
        OnPropertyChanged(nameof(DeltaTText));
        OnPropertyChanged(nameof(TcText));
        OnPropertyChanged(nameof(PhText));
        OnPropertyChanged(nameof(DosedMlText));
        OnPropertyChanged(nameof(RampRateText));
        OnPropertyChanged(nameof(LightText));
        OnPropertyChanged(nameof(StirButtonText));
        OnPropertyChanged(nameof(StirStatusLine));
        OnPropertyChanged(nameof(TopStateText));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(ReactorOpacity));
        OnPropertyChanged(nameof(MainCardRightTop));
        OnPropertyChanged(nameof(MainCardModeLine));
        OnPropertyChanged(nameof(MainCardRightBottom));
        OnPropertyChanged(nameof(OverallProgressPct));
        OnPropertyChanged(nameof(ControlModeShortName));
        OnPropertyChanged(nameof(HeaderModeLine));
        OnPropertyChanged(nameof(StepCounterText));
        OnPropertyChanged(nameof(StepRemainText));
        OnPropertyChanged(nameof(TargetLabel));
        OnPropertyChanged(nameof(TempStatusLine));
        OnPropertyChanged(nameof(TempControlButtonTitle));
        OnPropertyChanged(nameof(TempControlButtonSub));
        OnPropertyChanged(nameof(MaxRampRateText));
        OnPropertyChanged(nameof(TrMinText));
        OnPropertyChanged(nameof(TrMaxText));
        OnPropertyChanged(nameof(IsModeTr));
        OnPropertyChanged(nameof(IsModeTj));
        OnPropertyChanged(nameof(IsModeReflux));
        OnPropertyChanged(nameof(IsModeCry));
        OnPropertyChanged(nameof(IsArriveFast));
        OnPropertyChanged(nameof(IsArriveRate));
        OnPropertyChanged(nameof(IsArriveDuration));
        OnPropertyChanged(nameof(SchemeOverviewMeta));
        OnPropertyChanged(nameof(SchemeOverviewBadge));
        OnPropertyChanged(nameof(SeqHeaderMeta));
        OnPropertyChanged(nameof(CanEditSequence));
    }

    public static string FormatHms(int sec)
    {
        if (sec < 0) sec = 0;
        int h = sec / 3600;
        int m = (sec % 3600) / 60;
        int s = sec % 60;
        return $"{h:D2}:{m:D2}:{s:D2}";
    }
}
