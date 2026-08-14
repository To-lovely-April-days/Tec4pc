using CommunityToolkit.Mvvm.ComponentModel;
using TecControl.Core.Control;

namespace TecControl.App.ViewModels;

/// <summary>增益表中的一行，供界面表格直接编辑。</summary>
public partial class GainPointRow : ObservableObject
{
    [ObservableProperty] private double temperatureC;
    [ObservableProperty] private double kp;
    [ObservableProperty] private double ki;
    [ObservableProperty] private double kd;
    [ObservableProperty] private double ku;
    [ObservableProperty] private double tu;

    // 串级外环参数：外环快慢取决于该温度下的传热速度，同样需要随温度变化
    [ObservableProperty] private double outerKp;
    [ObservableProperty] private double outerKi;
    [ObservableProperty] private double outerKd;
    [ObservableProperty] private double outerMaxBiasC;

    /// <summary>该温度实测的稳态偏置（腔体 − 内部，℃）；空表示尚未学到。</summary>
    [ObservableProperty] private double? steadyBiasC;

    /// <summary>该温度的加热有效度比值；空表示沿用全局值（>100℃ 段是 PWM 电加热器，比值另填）。</summary>
    [ObservableProperty] private double? heatRatio;

    /// <summary>来源标注：自整定得到的点带 Ku/Tu，手工添加的没有。</summary>
    public string Source => Ku > 0 ? "自整定" : "手工";

    /// <summary>外环三项齐备才算登记（否则插值会给出半套参数）。</summary>
    public PidGains? ToOuterGains() => OuterKp > 0 ? new PidGains(OuterKp, OuterKi, OuterKd) : null;

    public static GainPointRow From(GainPoint p) => new()
    {
        TemperatureC = p.TemperatureC,
        Kp = p.Gains.Kp,
        Ki = p.Gains.Ki,
        Kd = p.Gains.Kd,
        Ku = p.UltimateGainKu,
        Tu = p.UltimatePeriodTuSeconds,
        OuterKp = p.OuterGains?.Kp ?? 0,
        OuterKi = p.OuterGains?.Ki ?? 0,
        OuterKd = p.OuterGains?.Kd ?? 0,
        OuterMaxBiasC = p.OuterMaxBiasC,
        SteadyBiasC = p.SteadyBiasC,
        HeatRatio = p.HeatRatio,
    };

    public GainPoint ToPoint() => new(
        TemperatureC, new PidGains(Kp, Ki, Kd), Ku, Tu,
        ToOuterGains(), OuterMaxBiasC, SteadyBiasC,
        HeatRatio is > 0 ? HeatRatio : null);
}
