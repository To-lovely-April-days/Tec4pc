using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView() => InitializeComponent();

    private void OnDevicePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BenchViewModel vm && sender is Control c && c.DataContext is DeviceNodeViewModel node)
            vm.Selected = node;
    }
}
