using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace HtlabHmi.Models;

public enum TopSection
{
    Overview,
    Ch1,
    Ch2,
    Files,
    Settings,
    Users
}

public enum TempControlMode
{
    Tr,
    Tj,
    Reflux,
    Cry
}

public enum ArriveMode
{
    Fast,
    Rate,
    Duration
}

public enum StepKind
{
    Tr,
    Tj,
    Reflux,
    Cry,
    Stir,
    Wait,
    Dose,
    Pause
}

public partial class SecondaryTabItem : ObservableObject
{
    public int Index { get; init; }
    public string Title { get; init; } = "";
    public string IconKey { get; init; } = "IconReactor";
    [ObservableProperty] private bool _isSelected;
}

public partial class SequenceStep : ObservableObject
{
    [ObservableProperty] private int _stepNumber;
    [ObservableProperty] private StepKind _kind;
    [ObservableProperty] private double _targetTemp = 60.0;
    [ObservableProperty] private ArriveMode _arriveMode = ArriveMode.Rate;
    [ObservableProperty] private double _rate = 0.5;
    [ObservableProperty] private int _durationMin = 30;
    [ObservableProperty] private int _stirRpm = 300;
    [ObservableProperty] private bool _stirUnchanged = true;

    // Reflux
    [ObservableProperty] private double _refluxDeltaK = 8.0;

    // Dose / pH
    [ObservableProperty] private int _pumpIndex = 1;
    [ObservableProperty] private int _doseMode = 1; // 0:一次加入, 1:按速率, 2:pH反馈
    [ObservableProperty] private string _liquidName = "未填";
    [ObservableProperty] private double _doseVolumeMl = 3.6;
    [ObservableProperty] private double _doseFlowMlMin = 1.20;
    [ObservableProperty] private double _phTarget = 7.00;
    [ObservableProperty] private double _phDeadband = 0.20;
    [ObservableProperty] private double _phMaxVolume = 50.0;

    // Pause
    [ObservableProperty] private string _pausePrompt = "请确认后继续";

    // Runtime progress (0..100)
    [ObservableProperty] private double _progressPct;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private bool _isSelected;

    public string KindBadge => Kind switch
    {
        StepKind.Tr => "Tr",
        StepKind.Tj => "Tj",
        StepKind.Reflux => "TrTj",
        StepKind.Cry => "结晶",
        StepKind.Stir => "R",
        StepKind.Wait => "Wait",
        StepKind.Dose => "加料",
        StepKind.Pause => "暂停",
        _ => "Tr"
    };

    public string KindFullTitle => Kind switch
    {
        StepKind.Tr => "Tr 釜内温度",
        StepKind.Tj => "Tj 夹套温度",
        StepKind.Reflux => "蒸回流 Tj − Tr",
        StepKind.Cry => "结晶分段曲线",
        StepKind.Stir => "R 搅拌转速",
        StepKind.Wait => "Wait 等待保持",
        StepKind.Dose => "Dose 加料控制",
        StepKind.Pause => "Pause 暂停确认",
        _ => "步骤"
    };

    public string ShortLabel => Kind switch
    {
        StepKind.Tr => $"{StepNumber} Tr {TargetTemp:F1}",
        StepKind.Tj => $"{StepNumber} Tj {TargetTemp:F1}",
        StepKind.Reflux => $"{StepNumber} 回流",
        StepKind.Cry => $"{StepNumber} 结晶",
        StepKind.Stir => $"{StepNumber} R {StirRpm}",
        StepKind.Wait => $"{StepNumber} Wait",
        StepKind.Dose => $"{StepNumber} 加料",
        StepKind.Pause => $"{StepNumber} 暂停",
        _ => $"{StepNumber}"
    };

    public string MainActionText => Kind switch
    {
        StepKind.Tr => TargetTemp >= 40 ? $"升到 {TargetTemp:F1} ℃" : $"降到 {TargetTemp:F1} ℃",
        StepKind.Tj => TargetTemp >= 20 ? $"升到 {TargetTemp:F1} ℃" : $"降到 {TargetTemp:F1} ℃",
        StepKind.Reflux => $"夹套高 {RefluxDeltaK:F1} K",
        StepKind.Cry => "4 段曲线 · 60→5 ℃",
        StepKind.Stir => $"设定 {StirRpm} rpm",
        StepKind.Wait => $"保持 {TargetTemp:F1} ℃",
        StepKind.Dose => DoseMode == 2 ? $"pH 反馈 {PhTarget:F2}" : $"{DoseVolumeMl:F1} mL",
        StepKind.Pause => PausePrompt,
        _ => ""
    };

    public string SubDetailText => Kind switch
    {
        StepKind.Tr or StepKind.Tj => ArriveMode switch
        {
            ArriveMode.Fast => "尽快到达",
            ArriveMode.Rate => $"{Rate:F2} ℃/min",
            ArriveMode.Duration => $"按时长 {DurationMin} min",
            _ => ""
        },
        StepKind.Reflux => $"维持 {DurationMin} min",
        StepKind.Cry => "2 h 40 min",
        StepKind.Stir => "立即生效",
        StepKind.Wait => $"{DurationMin} min",
        StepKind.Dose => DoseMode == 2 ? $"最大 {PhMaxVolume:F0} mL" : $"{DoseFlowMlMin:F2} mL/min",
        StepKind.Pause => "等待人工确认",
        _ => ""
    };

    public string StirSummary => StirUnchanged ? "搅拌不变" : $"搅拌 {StirRpm} rpm";
    public bool IsArriveFast => ArriveMode == ArriveMode.Fast;
    public bool IsArriveRate => ArriveMode == ArriveMode.Rate;
    public bool IsArriveDuration => ArriveMode == ArriveMode.Duration;
    public bool IsTodo => !IsCompleted && !IsRunning;
    public string StatusBadgeText => IsRunning ? "进行中" : (IsCompleted ? "已完成" : "待执行");
    public string StepNumberText => StepNumber.ToString();

    public void RefreshComputed()
    {
        OnPropertyChanged(nameof(KindBadge));
        OnPropertyChanged(nameof(KindFullTitle));
        OnPropertyChanged(nameof(ShortLabel));
        OnPropertyChanged(nameof(MainActionText));
        OnPropertyChanged(nameof(SubDetailText));
        OnPropertyChanged(nameof(StirSummary));
        OnPropertyChanged(nameof(IsArriveFast));
        OnPropertyChanged(nameof(IsArriveRate));
        OnPropertyChanged(nameof(IsArriveDuration));
        OnPropertyChanged(nameof(IsTodo));
        OnPropertyChanged(nameof(StatusBadgeText));
    }

    public SequenceStep Clone()
    {
        return new SequenceStep
        {
            StepNumber = StepNumber,
            Kind = Kind,
            TargetTemp = TargetTemp,
            ArriveMode = ArriveMode,
            Rate = Rate,
            DurationMin = DurationMin,
            StirRpm = StirRpm,
            StirUnchanged = StirUnchanged,
            RefluxDeltaK = RefluxDeltaK,
            PumpIndex = PumpIndex,
            DoseMode = DoseMode,
            LiquidName = LiquidName,
            DoseVolumeMl = DoseVolumeMl,
            DoseFlowMlMin = DoseFlowMlMin,
            PhTarget = PhTarget,
            PhDeadband = PhDeadband,
            PhMaxVolume = PhMaxVolume,
            PausePrompt = PausePrompt
        };
    }
}

public partial class RecipeItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _savedAt = "";
    [ObservableProperty] private string _estimatedDuration = "2 h 10 min";
    [ObservableProperty] private bool _isSelected;
    public ObservableCollection<SequenceStep> Steps { get; } = new();

    public string MetaText => $"{Steps.Count} 步 · {EstimatedDuration}";
    public string DetailSubText => $"{Steps.Count} 步 · 预计 {EstimatedDuration} · 保存于 {SavedAt}";
}

public partial class PidGainRow : ObservableObject
{
    [ObservableProperty] private double _temp;
    [ObservableProperty] private double _kp;
    [ObservableProperty] private double _ki;
    [ObservableProperty] private double _kd;
    [ObservableProperty] private double? _steadyBias;
    [ObservableProperty] private double _heatRatio;
    [ObservableProperty] private bool _isActiveRow;

    public string SteadyBiasText => SteadyBias.HasValue ? $"{SteadyBias.Value:F1}" : "—";
    partial void OnSteadyBiasChanged(double? value) => OnPropertyChanged(nameof(SteadyBiasText));
}

public partial class ExportBatchItem : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _channelName = "通道 1";
    [ObservableProperty] private string _status = "已完成";
    [ObservableProperty] private string _duration = "02:10:00";
    [ObservableProperty] private string _date = "2026-11-24";
    [ObservableProperty] private bool _isSelected;
    public bool IsRunningStatus => Status == "运行中";
}

public partial class AlarmRecord : ObservableObject
{
    [ObservableProperty] private string _timestamp = "";
    [ObservableProperty] private string _channelName = "通道 1";
    [ObservableProperty] private string _title = "安全联锁 · 停止控温";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _recoveredText = "";
    [ObservableProperty] private string _actionTaken = "安全层已执行：停止控温，搅拌保持";
    [ObservableProperty] private bool _isAcknowledged;
    public string StatusText => IsAcknowledged ? "已确认" : "未确认";
}

public partial class CommDeviceConfig : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _port = "COM1";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private string _parity = "无";
    [ObservableProperty] private int _stopBits = 1;
    [ObservableProperty] private int _stationId = 1;
    [ObservableProperty] private int _pollMs = 500;
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private string _status = "正常";

    public bool IsNormal => Status == "正常";
    public bool IsError => Status == "无应答";
    public bool IsDisabled => Status == "未启用";

    public void RefreshStatusFlags()
    {
        OnPropertyChanged(nameof(IsNormal));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsDisabled));
    }
}

public class ConnectedPcInfo
{
    public string IpAddress { get; init; } = "";
    public string StatsText { get; init; } = "";
    public string ConnectedDuration { get; init; } = "";
}

public class TrendSample
{
    public DateTime Time { get; init; }
    public double ElapsedMin { get; init; }
    public double Tr { get; init; }
    public double Tj { get; init; }
    public double TjSp { get; init; }
    public double Ph { get; init; }
    public double Rpm { get; init; }
}

public class TrendMarker
{
    public double ElapsedMin { get; init; }
    public string Label { get; init; } = "";
}

public class CrySegment
{
    public int Index { get; set; }
    public double StartTemp { get; set; }
    public double EndTemp { get; set; }
    public int DurationMin { get; set; }
    public string RateLabel { get; set; } = "降 0.50";
    public bool IsCurrent { get; set; }
    public string TargetLabel => $"{EndTemp:F0} ℃";
    public string DurationLabel => $"{DurationMin} min";
    public string Desc => $"第 {Index} 段：{StartTemp:F1} → {EndTemp:F1} ℃ · {DurationMin} min";
}
