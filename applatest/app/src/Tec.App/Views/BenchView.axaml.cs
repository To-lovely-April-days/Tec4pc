using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class BenchView : UserControl
{
    public BenchView()
    {
        InitializeComponent();
        // 插上工位的读数标签、泵小窗的累计加料，每秒刷一次真值。
        // 页面不可见或没东西可刷时一拍只花一次布尔判断——
        // 别学从前运行页那个常驻 700ms 心跳的教训
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsEffectivelyVisible && Vm is { } vm && (vm.Tags.Count > 0 || vm.Panels.Count > 0))
                vm.RefreshTagValues();
        });

        // 动画拍子（转子 + 管内流动）**只在有泵在跑的时候走**：
        // 66ms 一拍推角度和流动时钟，没有泵在跑 AnimTick 返回 false，拍子自己停。
        // 重新启动的信号是 AnyPumpRunning 变 true（见 OnVmProp）
        _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsEffectivelyVisible || Vm is not { } vm || !vm.AnimTick(0.066)) _anim!.Stop();
        });

        DataContextChanged += (_, _) =>
        {
            if (_hookedVm is { } old) old.PropertyChanged -= OnVmProp;
            _hookedVm = DataContext as BenchViewModel;
            if (_hookedVm is { } vm) vm.PropertyChanged += OnVmProp;
        };
        AttachedToVisualTree += (_, _) =>
        {
            _tick.Start();
            if (Vm is { AnyPumpRunning: true }) _anim.Start();
        };
        DetachedFromVisualTree += (_, _) => { _tick.Stop(); _anim.Stop(); };
    }

    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _anim;
    private BenchViewModel? _hookedVm;

    private void OnVmProp(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BenchViewModel.AnyPumpRunning)
            && Vm is { AnyPumpRunning: true } && IsEffectivelyVisible)
            _anim.Start();
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

    // ── 泵控制小窗：标题栏拖动 / 滑杆 / 速率编辑 ────────────────────
    private PumpPanelViewModel? _panelDrag;
    private Point _panelGrab;
    private PumpPanelViewModel? _slide;

    private void OnPanelHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PumpPanelViewModel p } c) return;
        _panelDrag = p;
        var at = e.GetPosition(World);
        _panelGrab = new Point(at.X - p.X, at.Y - p.Y);
        e.Pointer.Capture(c);
        e.Handled = true;
    }

    private void OnPanelHeaderMoved(object? sender, PointerEventArgs e)
    {
        if (_panelDrag is not { } p) return;
        var at = e.GetPosition(World);
        p.X = at.X - _panelGrab.X;
        p.Y = at.Y - _panelGrab.Y;
        e.Handled = true;
    }

    private void OnPanelHeaderReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panelDrag = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void OnSlidePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: PumpPanelViewModel p } b) return;
        _slide = p;
        e.Pointer.Capture(b);
        p.SlideTo(e.GetPosition(b).X);
        e.Handled = true;
    }

    private void OnSlideMoved(object? sender, PointerEventArgs e)
    {
        if (_slide is not { } p || sender is not Border b) return;
        p.SlideTo(e.GetPosition(b).X);
        e.Handled = true;
    }

    private void OnSlideReleased(object? sender, PointerReleasedEventArgs e)
    {
        _slide = null;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>
    /// 点速率数字进入编辑：**直接物理键盘输入**（用户定的，不做屏幕键盘）。
    /// IsVisible 翻开不重进视觉树，AttachedToVisualTree 不会再响，
    /// 所以焦点在这儿排一拍后手动给。
    /// </summary>
    private void OnRateBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: PumpPanelViewModel p } c) return;
        if (!p.Editing)
        {
            p.BeginEdit();
            var tb = c.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (tb is not null)
                Dispatcher.UIThread.Post(() => { tb.Focus(); tb.SelectAll(); },
                                         DispatcherPriority.Background);
        }
        e.Handled = true;
    }

    private void OnRateKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: PumpPanelViewModel p }) return;
        if (e.Key == Key.Enter) { p.CommitEdit(); e.Handled = true; }
        else if (e.Key == Key.Escape) { p.CancelEdit(); e.Handled = true; }
    }

    /// <summary>点到别处：能解析就按确定收，解析不了就当取消——别把人卡在编辑态里。</summary>
    private void OnRateLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: PumpPanelViewModel { Editing: true } p }) p.CommitEdit();
    }
}
