using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Tec.App.Services;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView()
    {
        InitializeComponent();
        HookLite();
    }

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

    /// <summary>
    /// 设备库的分类头：点一下收起 / 展开这一类。
    /// Handled 掉，不然这一下会顺着冒到画布上去（画布的 PointerPressed 是取消选中）。
    /// </summary>
    private void OnGroupPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: DeviceGroup g }) g.Open = !g.Open;
        e.Handled = true;
    }

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

    // ── 减负档：属性栏顶沿那层渐隐 ──────────────────────────────────
    //
    // 那 14px 渐隐是一层 OpacityMask，合成器每帧都要把整条属性栏先画进离屏图
    // 再混一遍。Ctrl+F12 当场摘掉它，好在现场量出它到底值多少毫秒
    // （为什么要这么问，见 Services/PerfProbe 里「减负档」那一段）。
    // 摘掉只是少一层渐隐，属性栏的内容一个字都不少。
    private void HookLite()
    {
        _fade = PropsScroll.OpacityMask;
        PerfProbe.LiteChanged += (_, _) => ApplyLite();
        ApplyLite();
    }

    private IBrush? _fade;

    private void ApplyLite() => PropsScroll.OpacityMask = PerfProbe.Lite ? null : _fade;
}
