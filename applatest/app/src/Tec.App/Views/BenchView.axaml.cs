using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView() => InitializeComponent();

    private BenchViewModel? Vm => DataContext as BenchViewModel;

    /// <summary>拖拽的坐标一律换算到画布，不然设备库与画布两套坐标系对不上。</summary>
    private Point OnStage(PointerEventArgs e) => e.GetPosition(Stage);

    // ── 从设备库拖出来 ──────────────────────────────────────────────
    private void OnLibraryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: LibraryItemViewModel item }) return;
        vm.PickedFromLibrary = item;
        if (!item.Usable) return;                       // 驱动不可用的不让拖
        vm.BeginDragFromLibrary(item, OnStage(e));
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    // ── 拖动台面上已有的设备 ────────────────────────────────────────
    private void OnDevicePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: DeviceNodeViewModel node }) return;
        vm.BeginDragDevice(node, OnStage(e));
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is { Dragging: true } vm) vm.DragTo(OnStage(e));
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (Vm is not { Dragging: true } vm) return;
        vm.EndDrag(OnStage(e));
        e.Pointer.Capture(null);
    }

    /// <summary>拖到一半按 Esc 取消。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { Dragging: true } vm) vm.CancelDrag();
    }
}
