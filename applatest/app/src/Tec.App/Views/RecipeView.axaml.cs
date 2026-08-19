using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RecipeView : UserControl
{
    public RecipeView()
    {
        InitializeComponent();

        // 走隧道阶段：左边的步骤库是 Button，它会把冒泡的 PointerPressed 吃掉，
        // 冒泡阶段再挂就收不到按下的位置了
        AddHandler(PointerPressedEvent, OnRootPressed, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, OnRootMoved, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnRootReleased, RoutingStrategies.Tunnel);
    }

    private RecipeViewModel? Vm => DataContext as RecipeViewModel;

    /// <summary>按下先记着，挪开一段距离才算拖拽——单纯点一下仍然是「点一下加到当前通道」。</summary>
    private const double DragSlop = 5;

    private CommandItemViewModel? _pendingCmd;
    private StepViewModel? _pendingStep;
    private int _pendingFromCh;
    private Point _pressAt;

    private void OnLanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: LaneViewModel lane })
            vm.CurCh = lane.Channel;
    }

    private void OnStepPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not Control { DataContext: StepViewModel step }) return;
        // 原型行为：点步骤先切到它所在的通道，再选中
        var lane = vm.Lanes.FirstOrDefault(l => l.Steps.Contains(step));
        if (lane is not null) vm.CurCh = lane.Channel;
        vm.SelectedStep = vm.Lanes.First(l => l.Channel == vm.CurCh)
                            .Steps.FirstOrDefault(s => s.Step.StepId == step.Step.StepId);
        e.Handled = true;
    }

    private void OnGroupBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ModuleGroup g }) g.Open = !g.Open;
    }

    /// <summary>左缘「变量」竖栏：收起态点整条展开，展开态点圆圈收起。</summary>
    private void OnVarsBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm) vm.VarsOpen = !vm.VarsOpen;
        e.Handled = true;
    }

    /// <summary>泳道头的垃圾桶：清空该通道的配方（原型 removeChannelTab）。</summary>
    private void OnLaneRemove(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: LaneViewModel lane })
            vm.RemoveLane(lane.Channel);
    }

    // ── 拖拽 ─────────────────────────────────────────────────────────

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        _pendingCmd = null;
        _pendingStep = null;
        if (Vm is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        _pressAt = e.GetPosition(this);
        switch (DataOf(e.Source as Visual))
        {
            case CommandItemViewModel c:
                _pendingCmd = c;
                break;
            case StepViewModel s:
                _pendingStep = s;
                _pendingFromCh = Vm.Lanes.FirstOrDefault(l => l.Steps.Contains(s))?.Channel ?? -1;
                break;
        }
    }

    private void OnRootMoved(object? sender, PointerEventArgs e)
    {
        if (Vm is not { } vm) return;
        var at = e.GetPosition(this);

        if (!vm.Dragging)
        {
            if (_pendingCmd is null && _pendingStep is null) return;
            if (Math.Abs(at.X - _pressAt.X) < DragSlop && Math.Abs(at.Y - _pressAt.Y) < DragSlop) return;

            if (_pendingCmd is { } cmd) vm.BeginDragCommand(cmd);
            else if (_pendingStep is { } step && _pendingFromCh > 0) vm.BeginDragStep(step, _pendingFromCh);
            _pendingCmd = null;
            _pendingStep = null;
            if (!vm.Dragging) return;                  // 没有通道时不起拖
            e.Pointer.Capture(this);                   // 起拖之后才抓指针，不然点一下会丢 Click
        }

        var (lane, index) = DropAt(at);
        // 幽灵卡画在指针右下一点，别盖住落点指示条
        vm.DragTo(at.X + 12, at.Y + 10, lane, index);
    }

    private void OnRootReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pendingCmd = null;
        _pendingStep = null;
        if (Vm is not { Dragging: true } vm) return;
        vm.EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;                              // 别让这一下再被当成按钮的 Click
    }

    /// <summary>Esc 撤销这次拖拽。手滑了要有退路。</summary>
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Vm is { Dragging: true } vm)
        {
            vm.CancelDrag();
            e.Handled = true;
        }
    }

    /// <summary>顺着视觉树往上找第一个认识的 DataContext。</summary>
    private static object? DataOf(Visual? from)
    {
        for (var v = from; v is not null; v = v.GetVisualParent())
            if (v is Control { DataContext: CommandItemViewModel or StepViewModel } c)
                return c.DataContext;
        return null;
    }

    /// <summary>
    /// 指针在哪条泳道的第几个位置。返回的 index 是「插在第几张卡之前」，
    /// 等于卡片数就是插到末尾。
    ///
    /// 用卡片自己的高度中线判断，而不是按整条泳道均分：卡片高度并不一致
    /// （描述有一行的也有三行的），均分会让落点跟看到的对不上。
    /// </summary>
    private (int? Lane, int Index) DropAt(Point at)
    {
        if (Vm is null) return (null, 0);

        // 整棵树只走一遍：指针每动一下都要算落点，走两遍就是白走
        var boxes = this.GetVisualDescendants().OfType<Control>()
                        .Where(c => c.Tag is "lane" or "stepcard").ToList();

        var laneBox = boxes.FirstOrDefault(
            c => c.Tag is "lane" && c.DataContext is LaneViewModel && Inside(c, at));
        if (laneBox?.DataContext is not LaneViewModel lane) return (null, 0);

        var index = lane.Steps.Count;
        foreach (var card in boxes.Where(c => c.Tag is "stepcard"))
        {
            if (card.DataContext is not StepViewModel s) continue;
            var ord = lane.Steps.IndexOf(s);
            if (ord < 0) continue;                              // 别条泳道的卡
            if (this.TranslatePoint(at, card) is not { } p) continue;
            // 指针过了这张卡的中线就落在它之后；取满足条件的最小序号 =
            // 第一张「中线在指针下方」的卡，插在它前面
            if (p.Y < card.Bounds.Height / 2) index = Math.Min(index, ord);
        }
        return (lane.Channel, index);
    }

    private bool Inside(Visual v, Point at)
        => this.TranslatePoint(at, v) is { } p && new Rect(v.Bounds.Size).Contains(p);
}
