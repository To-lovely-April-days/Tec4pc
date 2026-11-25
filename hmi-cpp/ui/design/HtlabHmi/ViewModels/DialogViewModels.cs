using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HtlabHmi.Models;
using System;
using System.Collections.ObjectModel;
using System.Globalization;

namespace HtlabHmi.ViewModels;

public partial class KeypadDialogViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _unit = "℃";
    [ObservableProperty] private double _min = -40;
    [ObservableProperty] private double _max = 180;
    [ObservableProperty] private double _previousValue;
    [ObservableProperty] private double _currentMeasured;
    [ObservableProperty] private string _inputText = "0";
    [ObservableProperty] private double _sliderValue;
    [ObservableProperty] private string _errorText = "";
    private bool _syncingSlider;
    private Action<double>? _onApply;

    partial void OnSliderValueChanged(double value)
    {
        if (_syncingSlider) return;
        _syncingSlider = true;
        InputText = Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);
        ErrorText = "";
        _syncingSlider = false;
    }

    partial void OnInputTextChanged(string value)
    {
        if (_syncingSlider) return;
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            _syncingSlider = true;
            SliderValue = Math.Clamp(d, Min, Max);
            _syncingSlider = false;
        }
    }

    public string MinText => Min < 0 ? $"−{Math.Abs(Min):0.##}" : $"{Min:0.##}";
    public string MaxText => $"{Max:0.##}";
    public string PreviousText => (Unit is "℃" or "K") ? $"{PreviousValue:0.0} {Unit}" : $"{PreviousValue:0.##} {Unit}";
    public string MeasuredText => (Unit is "℃" or "K") ? $"{CurrentMeasured:0.0} {Unit}" : $"{CurrentMeasured:0.##} {Unit}";
    public string CurrentBadgeText => (Unit is "℃" or "K") ? $"当前 {CurrentMeasured:0.0}" : $"当前 {CurrentMeasured:0.#}";
    public double MeasuredMarkerOffset => Max > Min ? Math.Clamp((CurrentMeasured - Min) / (Max - Min), 0, 1) * 332.0 : 0;

    public void Open(string title, string unit, double initialValue, double min, double max, double measured, Action<double> onApply)
    {
        Title = title;
        Unit = unit;
        Min = min;
        Max = max;
        PreviousValue = initialValue;
        CurrentMeasured = measured;
        _syncingSlider = true;
        SliderValue = Math.Clamp(initialValue, min, max);
        InputText = (unit is "℃" or "K")
            ? initialValue.ToString("0.0", CultureInfo.InvariantCulture)
            : initialValue.ToString("0.##", CultureInfo.InvariantCulture);
        _syncingSlider = false;
        ErrorText = "";
        _onApply = onApply;
        OnPropertyChanged(nameof(MinText));
        OnPropertyChanged(nameof(MaxText));
        OnPropertyChanged(nameof(PreviousText));
        OnPropertyChanged(nameof(MeasuredText));
        OnPropertyChanged(nameof(CurrentBadgeText));
        OnPropertyChanged(nameof(MeasuredMarkerOffset));
        IsOpen = true;
    }

    [RelayCommand]
    private void PressKey(string key)
    {
        ErrorText = "";
        switch (key)
        {
            case "±":
                if (InputText.StartsWith("-")) InputText = InputText[1..];
                else if (InputText != "0" && InputText.Length > 0) InputText = "-" + InputText;
                break;
            case ".":
                if (!InputText.Contains('.'))
                    InputText = string.IsNullOrEmpty(InputText) ? "0." : InputText + ".";
                break;
            case "BS":
                InputText = InputText.Length <= 1 ? "0" : InputText[..^1];
                if (InputText == "-") InputText = "0";
                break;
            case "CLR":
                InputText = "0";
                break;
            default:
                if (InputText == "0") InputText = key;
                else if (InputText.Length < 10) InputText += key;
                break;
        }
    }

    [RelayCommand]
    private void FillPrevious()
    {
        InputText = PreviousValue.ToString("0.##", CultureInfo.InvariantCulture);
        ErrorText = "";
    }

    [RelayCommand]
    private void FillMeasured()
    {
        InputText = CurrentMeasured.ToString("0.##", CultureInfo.InvariantCulture);
        ErrorText = "";
    }

    [RelayCommand]
    private void Apply()
    {
        if (!double.TryParse(InputText, NumberStyles.Float, CultureInfo.InvariantCulture, out double val))
        {
            ErrorText = "数值格式不正确";
            return;
        }
        if (val < Min || val > Max)
        {
            ErrorText = $"超出允许范围（{Min:0.##} ~ {Max:0.##} {Unit}）";
            return;
        }
        IsOpen = false;
        _onApply?.Invoke(val);
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;
}

public partial class TextDialogViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "给这个配方起个名字";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private int _maxLen = 30;
    [ObservableProperty] private bool _isUpper;
    [ObservableProperty] private bool _isNumeric;
    [ObservableProperty] private bool _isChinese = true;
    [ObservableProperty] private string _pinyin = "jie";
    private Action<string>? _onConfirm;

    public ObservableCollection<string> Candidates { get; } = new() { "结", "界", "接", "节", "解", "姐", "借", "介" };
    public string LengthCounter => $"{Text.Length} / {MaxLen}";
    public string LangLabel => IsChinese ? "中" : "EN";

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(LengthCounter));
    partial void OnIsChineseChanged(bool value) => OnPropertyChanged(nameof(LangLabel));

    public void Open(string title, string initialText, Action<string> onConfirm)
    {
        Title = title;
        Text = initialText;
        _onConfirm = onConfirm;
        IsOpen = true;
    }

    [RelayCommand]
    private void TypeChar(string ch)
    {
        if (Text.Length >= MaxLen) return;
        Text += IsUpper ? ch.ToUpperInvariant() : ch;
    }

    [RelayCommand]
    private void InsertSpace()
    {
        if (Text.Length >= MaxLen) return;
        Text += " ";
    }

    [RelayCommand]
    private void ToggleNumeric() => IsNumeric = !IsNumeric;

    [RelayCommand]
    private void ToggleChinese() => IsChinese = !IsChinese;

    [RelayCommand]
    private void PickCandidate(string cand)
    {
        if (Text.Length >= MaxLen) return;
        Text += cand;
    }

    [RelayCommand]
    private void ToggleUpper() => IsUpper = !IsUpper;

    [RelayCommand]
    private void Backspace()
    {
        if (Text.Length > 0) Text = Text[..^1];
    }

    [RelayCommand]
    private void Confirm()
    {
        if (string.IsNullOrWhiteSpace(Text)) return;
        IsOpen = false;
        _onConfirm?.Invoke(Text.Trim());
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;
}

public partial class StepTypeDialogViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "第 6 步做什么？";
    private Action<StepKind>? _onPick;

    public void Open(int stepNumber, Action<StepKind> onPick)
    {
        Title = $"第 {stepNumber} 步做什么？";
        _onPick = onPick;
        IsOpen = true;
    }

    [RelayCommand]
    private void Pick(string kindStr)
    {
        if (Enum.TryParse<StepKind>(kindStr, out var k))
        {
            IsOpen = false;
            _onPick?.Invoke(k);
        }
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;
}

public partial class CommDeviceDialogViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _deviceName = "";
    [ObservableProperty] private string _port = "COM14";
    [ObservableProperty] private int _baudRate = 9600;
    [ObservableProperty] private string _parity = "无";
    [ObservableProperty] private int _stopBits = 1;
    [ObservableProperty] private int _stationId = 1;
    [ObservableProperty] private int _pollMs = 500;
    [ObservableProperty] private bool _enabled = true;

    private CommDeviceConfig? _target;
    private Action? _onSaved;

    public void Open(CommDeviceConfig dev, Action onSaved)
    {
        _target = dev;
        _onSaved = onSaved;
        DeviceName = dev.Name;
        Port = dev.Port;
        BaudRate = dev.BaudRate;
        Parity = dev.Parity;
        StopBits = dev.StopBits;
        StationId = dev.StationId;
        PollMs = dev.PollMs;
        Enabled = dev.Enabled;
        IsOpen = true;
    }

    [RelayCommand] private void SetBaud(string b) { if (int.TryParse(b, out int v)) BaudRate = v; }
    [RelayCommand] private void SetParity(string p) => Parity = p;
    [RelayCommand] private void SetStopBits(string s) { if (int.TryParse(s, out int v)) StopBits = v; }
    [RelayCommand] private void ToggleEnabled() => Enabled = !Enabled;

    [RelayCommand]
    private void Save()
    {
        if (_target != null)
        {
            _target.Port = Port;
            _target.BaudRate = BaudRate;
            _target.Parity = Parity;
            _target.StopBits = StopBits;
            _target.StationId = StationId;
            _target.PollMs = PollMs;
            _target.Enabled = Enabled;
            _target.Status = Enabled ? "正常" : "未启用";
            _target.RefreshStatusFlags();
        }
        IsOpen = false;
        _onSaved?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => IsOpen = false;
}
