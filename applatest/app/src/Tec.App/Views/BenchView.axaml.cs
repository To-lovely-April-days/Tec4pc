using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView() => InitializeComponent();

    private void OnDevicePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BenchViewModel vm && sender is Control { DataContext: DeviceNodeViewModel node })
            vm.Selected = node;
    }

    /// <summary>设备库里点一下先选中；拖拽落台面留到下一轮做。</summary>
    private void OnLibraryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is BenchViewModel vm && sender is Control { DataContext: LibraryItemViewModel item })
            vm.PickedFromLibrary = item;
    }
}
