using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class ExportView : UserControl
{
    public ExportView() => InitializeComponent();

    private ExportViewModel? Vm => DataContext as ExportViewModel;

    private void OnRunPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: RunEntryViewModel r }) vm.PickRun.Execute(r);
    }

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: ExChipViewModel c })
        {
            vm.ToggleCh.Execute(c);
            e.Handled = true;
        }
    }

    private void OnAllChPressed(object? sender, PointerPressedEventArgs e) => Vm?.AllCh.Execute(null);

    private void OnFmtPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: FmtViewModel f }) vm.PickFmt.Execute(f);
    }

    private void OnDestLocal(object? sender, PointerPressedEventArgs e) { if (Vm is { } vm) vm.Dest = "local"; }
    private void OnDestUsb(object? sender, PointerPressedEventArgs e) { if (Vm is { } vm) vm.Dest = "usb"; }
}
