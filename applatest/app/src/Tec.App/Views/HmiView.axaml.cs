using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class HmiView : UserControl
{
    // 侧栏状态条与状态点的两态色（原型 .rstate / .dot）
    public static readonly IValueConverter RailStateBg =
        new FuncValueConverter<bool, IBrush>(run => new SolidColorBrush(Color.Parse(run ? "#2F8189" : "#C9C9C9")));
    public static readonly IValueConverter RailStateFg =
        new FuncValueConverter<bool, IBrush>(run => run ? Brushes.White : new SolidColorBrush(Color.Parse("#1A1A1A")));
    public static readonly IValueConverter DotFill =
        new FuncValueConverter<bool, IBrush>(run => new SolidColorBrush(Color.Parse(run ? "#2F6B38" : "#C9C9C9")));

    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _anim;

    public HmiView()
    {
        InitializeComponent();

        // 与台面页同一套拍子纪律：1 s 读数拍常驻（窗口可见才走），
        // 66 ms 动画拍只在有桨在转的时候走
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsEffectivelyVisible && Vm is { } vm) vm.Refresh();
        });
        _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsEffectivelyVisible || Vm is not { } vm || !vm.AnimTick(0.066)) _anim!.Stop();
        });

        AttachedToVisualTree += (_, _) => { _tick.Start(); _anim.Start(); };
        DetachedFromVisualTree += (_, _) => { _tick.Stop(); _anim.Stop(); };
        // 转速从 0 变正时动画拍要重新点火：读数拍每秒顺手看一眼
        _tick.Tick += (_, _) => { if (Vm is { } vm && !_anim.IsEnabled && vm.Zones.Any(z => z.RpmVal > 0)) _anim.Start(); };
    }

    private HmiViewModel? Vm => DataContext as HmiViewModel;

    // ── 导航 ────────────────────────────────────────────────────────
    private void OnNavOv(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "ov"; }
    private void OnNavZ0(object? s, RoutedEventArgs e) { if (Vm is { } v) { v.Page = "z0"; v.ZTab = "ctl"; } }
    private void OnNavZ1(object? s, RoutedEventArgs e) { if (Vm is { } v) { v.Page = "z1"; v.ZTab = "ctl"; } }
    private void OnNavFiles(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "files"; }
    private void OnNavSys(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "sys"; }
    private void OnZoneCard(object? s, PointerPressedEventArgs e)
    {
        if (Vm is { } v && s is Control { DataContext: HmiZoneViewModel z })
        { v.Page = z.Index == 1 ? "z0" : "z1"; v.ZTab = "ctl"; }
    }

    private void OnTabOvApp(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "app"; }
    private void OnTabOvSeq(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "seq"; }
    private void OnTabOvGra(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "gra"; }
    private void OnTabZCtl(object? s, RoutedEventArgs e) { if (Vm is { } v) v.ZTab = "ctl"; }
    private void OnTabZStub(object? s, RoutedEventArgs e)
    { if (Vm is { } v && s is Control { Tag: string t }) v.ZTab = t; }

    // ── 控制页交互 ──────────────────────────────────────────────────
    private void OnToggleArt(object? s, PointerPressedEventArgs e)
    { if (Vm is { } v) v.ArtOpen = !v.ArtOpen; }

    private void OnModeTr(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("Tr");
    private void OnModeTj(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("Tj");
    private void OnModeTrTj(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("TrTj");

    private void OnTogTr(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleTrSensor(); e.Handled = true; }
    private void OnTogPh(object? s, PointerPressedEventArgs e) { Vm?.Cur?.TogglePhSensor(); e.Handled = true; }
    private void OnTogTemp(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleTemp(); e.Handled = true; }
    private void OnTogStir(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleStir(); e.Handled = true; }

    private void OnRadRate(object? s, PointerPressedEventArgs e) { Vm?.Cur?.SetRampBy("rate"); e.Handled = true; }
    private void OnRadDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.SetRampBy("dur"); e.Handled = true; }

    private void OnVbTarget(object? s, PointerPressedEventArgs e)
    { if (Vm?.Cur is { } z) z.OpenKeypad(z.TargetKey); e.Handled = true; }
    private void OnVbRate(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rate"); e.Handled = true; }
    private void OnVbDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("dur"); e.Handled = true; }
    private void OnVbRpm(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rpm"); e.Handled = true; }
    private void OnVbREnd(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rEnd"); e.Handled = true; }
    private void OnVbRDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rDur"); e.Handled = true; }

    private void OnCommit(object? s, RoutedEventArgs e) => Vm?.Cur?.Commit();
    private void OnDiscard(object? s, RoutedEventArgs e) => Vm?.Cur?.Discard();
    private void OnStopSeq(object? s, RoutedEventArgs e) => Vm?.Cur?.StopSequence();

    private void OnActDose(object? s, RoutedEventArgs e) => Vm?.OpenAct("dose");
    private void OnActSample(object? s, RoutedEventArgs e) => Vm?.OpenAct("sample");
    private void OnActMarker(object? s, RoutedEventArgs e) => Vm?.OpenAct("marker");
    private void OnActNote(object? s, RoutedEventArgs e) => Vm?.OpenAct("note");
    private void OnActOk(object? s, RoutedEventArgs e) => Vm?.ActConfirm();
    private void OnActCancel(object? s, RoutedEventArgs e) => Vm?.ActCancel();

    // ── 键盘弹窗 ────────────────────────────────────────────────────
    private void OnKp(object? s, RoutedEventArgs e)
    { if (Vm is { } v && s is Button { Content: string d }) v.KpPress(d); }
    private void OnKpBack(object? s, RoutedEventArgs e) => Vm?.KpBack();
    private void OnKpOk(object? s, RoutedEventArgs e) => Vm?.KpOk();
    private void OnKpCancel(object? s, RoutedEventArgs e) => Vm?.KpCancel();

    /// <summary>点遮罩空白处关弹窗；点弹窗本体不算（OnDlgBody 把事件吃掉）。</summary>
    private void OnMask(object? s, PointerPressedEventArgs e)
    { Vm?.KpCancel(); Vm?.ActCancel(); }
    private void OnDlgBody(object? s, PointerPressedEventArgs e) => e.Handled = true;
}
