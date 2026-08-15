using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView() => InitializeComponent();

    private BenchViewModel? Vm => DataContext as BenchViewModel;

    // 按下先记着，指针挪开一段距离才算拖拽——单纯点一下只是选中，
    // 不该在画布上冒出一个跟手的幽灵设备
    private const double DragSlop = 4;
    private LibraryItemViewModel? _pendingLib;
    private DeviceNodeViewModel? _pendingDev;
    private Point _pressAt;

    /// <summary>
    /// 拖拽坐标一律换算到 World（缩放平移之内的那层），这样放大以后
    /// 落点仍然对得上设备的实际坐标；设备库与画布两套坐标系也统一了。
    /// </summary>
    private Point OnStage(PointerEventArgs e) => e.GetPosition(World);

    // ── 从设备库拖出来 ──────────────────────────────────────────────
    private void OnLibraryPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: LibraryItemViewModel item }) return;
        vm.PickedFromLibrary = item;
        if (!item.Usable) return;                       // 驱动不可用的不让拖
        _pendingLib = item;
        _pendingDev = null;
        _pressAt = OnStage(e);
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    // ── 拖动台面上已有的设备 ────────────────────────────────────────
    private void OnDevicePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: DeviceNodeViewModel node }) return;
        vm.Selected = node;                             // 按下就选中，拖不拖另说
        _pendingDev = node;
        _pendingLib = null;
        _pressAt = OnStage(e);
        e.Pointer.Capture(Stage);
        e.Handled = true;
    }

    private void OnStageMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is not { } vm) return;
        var at = OnStage(e);

        if (!vm.Dragging && (_pendingLib is not null || _pendingDev is not null))
        {
            if (Math.Abs(at.X - _pressAt.X) < DragSlop && Math.Abs(at.Y - _pressAt.Y) < DragSlop) return;
            if (_pendingLib is { } lib) vm.BeginDragFromLibrary(lib, _pressAt);
            else if (_pendingDev is { } dev) vm.BeginDragDevice(dev, _pressAt);
        }

        if (vm.Dragging) vm.DragTo(at);
    }

    private void OnStageReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pendingLib = null;
        _pendingDev = null;
        if (Vm is { Dragging: true } vm) vm.EndDrag(OnStage(e));
        e.Pointer.Capture(null);
    }

    /// <summary>Esc 取消拖拽；Delete 删掉选中的设备。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (e.Key == Key.Escape && vm.Dragging) { vm.CancelDrag(); e.Handled = true; }
        else if (e.Key == Key.Delete && vm.HasSelection) { vm.DeleteSelected.Execute(null); e.Handled = true; }
    }

    /// <summary>「适应窗口」要知道可视区多大。</summary>
    private void OnStageSize(object? sender, SizeChangedEventArgs e) => Vm?.StageSize(e.NewSize);
}
