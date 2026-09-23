using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Tec.Hmi.Ui.ViewModels;

namespace Tec.Hmi.Ui.Views;

/// <summary>「PID 整定」页（HmiView 的一页，DataContext = 当前通道的 HmiPidViewModel）。</summary>
public partial class HmiPidView : UserControl
{
    /// <summary>稳定度那一行：最近 5 min 全在 ±0.01 以内印绿，否则常色。</summary>
    public static readonly IValueConverter GoodBrush =
        new FuncValueConverter<bool, IBrush>(good => new SolidColorBrush(Color.Parse(good ? "#2F6B38" : "#3A3A3A")));

    public HmiPidView() => AvaloniaXamlLoader.Load(this);

    private HmiPidViewModel? Vm => DataContext as HmiPidViewModel;

    private void OnTable(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string t }) Vm?.SelectTable(t); }

    private void OnAddRow(object? s, RoutedEventArgs e) => Vm?.AddRow();
    private void OnApply(object? s, RoutedEventArgs e) => Vm?.ApplyTable();
    private void OnRevert(object? s, RoutedEventArgs e) => Vm?.RevertTable();
    private void OnReload(object? s, RoutedEventArgs e) => Vm?.ReloadDisk();
    private void OnClear(object? s, RoutedEventArgs e) => Vm?.ClearTable();
    private void OnCopy(object? s, RoutedEventArgs e) => Vm?.CopyToOther();

    private void OnRemoveRow(object? s, RoutedEventArgs e)
    { if (s is Control { DataContext: HmiPidRow r }) r.Remove(); }

    private void OnSched(object? s, PointerPressedEventArgs e) { Vm?.ToggleScheduling(); e.Handled = true; }

    private void OnManualApply(object? s, RoutedEventArgs e) => Vm?.ApplyManual();
    private void OnManualRevert(object? s, RoutedEventArgs e) => Vm?.RevertManual();

    private void OnPoint(object? s, RoutedEventArgs e)
    { if (s is Control { DataContext: HmiPidPoint p }) Vm?.PickPoint(p); }

    private void OnAct(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string t }) Vm?.PickActuator(t); }

    private void OnTuneStart(object? s, RoutedEventArgs e) => Vm?.StartTune();
    private void OnTuneCancel(object? s, RoutedEventArgs e) => Vm?.CancelTune();
}
