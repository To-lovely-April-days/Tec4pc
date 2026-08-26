using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Rendering;
using Avalonia.Threading;
using System.Diagnostics;
using System.Reflection;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class MainWindow : Window
{
    /// <summary>已经问过「要不要保存」了，这次关窗直接放行，别再问一遍。</summary>
    private bool _confirmed;

    public MainWindow()
    {
        InitializeComponent();
        // 隧道阶段挂：F12 在输入框里按下时也要收得到，冒泡上来之前就可能被吃掉
        AddHandler(KeyDownEvent, OnDiagKey, RoutingStrategies.Tunnel);
    }

    // ── 性能自检（F12 / Shift+F12）─────────────────────────────────────
    //
    // 「界面卡」这件事，卡在哪一层不是看出来的，是量出来的，而这两个数只有
    // 在**出问题的那台机器上**才有意义：
    //
    //   · 走的哪条渲染路。Avalonia 拿得到显卡就交给显卡合成，拿不到就退回
    //     软件逐帧重画整窗。后者一帧的代价跟窗口面积成正比、跟脏了多大一块无关
    //     ——鼠标扫过任何有悬停反应的东西都会摊上一整窗。开发这边在没有显卡的
    //     环境里实测：一帧 27–34 ms，八成的时间花在「清空整窗 + 整窗拷出去」上，
    //     真正画东西只占 3%。这条路上再怎么省画法都是白省，得先知道是不是它。
    //   · 每帧实际用了多少毫秒（Avalonia 自带的 Fps / RenderTimeGraph 浮层）。
    //     16.7 ms 是 60 Hz 的一格，Avg 越过它就是人能感觉到的顿。
    //
    // Shift+F12 是给「窗口透明」单独留的开关。这扇窗为了 8px 圆角整个是
    // 带透明通道的（TransparencyLevelHint=Transparent + 自绘标题栏），
    // 在 Windows 上这意味着 DWM 每帧都要把整窗混合一遍。值不值这个圆角，
    // 按一下就知道——切成不透明，圆角同时没了，这是同一件事的两面。
    //
    // **那三个浮层量的都是渲染线程。**现场回来的数是 GPU 合成、Render Avg 3.68 ms
    // ——16.7 ms 的预算只用了两成，画这一头是干净的。可人还是觉得卡，
    // 那就只剩界面线程：命中测试、悬停换样式、布局、绑定、还有 GC 的停顿，
    // 全挤在这一根线程上，它一堵，鼠标就跟不上手。渲染浮层看不见这些。
    //
    // 所以自检行自己带一支 16 ms 的表来量这根线程堵不堵：
    // 表该什么时候响是定死的，实际什么时候轮到它，差出来的那一截就是
    // 「界面线程当时正忙着，顾不上」——**人手上感觉到的那一下顿，就是这个数**。
    // 一起报 GC 次数与已分配总量：二代 GC 会把界面线程整个停住，
    // 分配量大就说明有一路在per帧造垃圾，那是另一种卡法。
    private bool _diag;
    private bool _opaque;
    private DispatcherTimer? _lagTimer;
    private long _lastTick;
    private double _lagMax, _lagSum;
    private int _lagN, _refresh;

    private void OnDiagKey(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12) return;
        e.Handled = true;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _opaque = !_opaque;
            TransparencyLevelHint = new[]
            {
                _opaque ? WindowTransparencyLevel.None : WindowTransparencyLevel.Transparent
            };
            Background = _opaque ? Brushes.White : Brushes.Transparent;
        }
        else
        {
            _diag = !_diag;
            RendererDiagnostics.DebugOverlays = _diag
                ? RendererDebugOverlays.Fps
                  | RendererDebugOverlays.RenderTimeGraph
                  | RendererDebugOverlays.LayoutTimeGraph
                : RendererDebugOverlays.None;
            if (_diag) StartLagMeter(); else StopLagMeter();
        }

        DiagLine.IsVisible = _diag || _opaque;
        if (DiagLine.IsVisible) DiagLine.Text = DiagText();
    }

    /// <summary>
    /// 界面线程堵不堵：一支 16 ms 的表，量它每次「迟到」多少。
    ///
    /// 用普通优先级——输入、布局、渲染提交都排在它前面，正好让它替人
    /// 站在队尾感受一下要等多久。迟到 2 ms 是正常抖动；迟到几十上百毫秒，
    /// 就是那一刻界面线程被谁占死了，鼠标在那一下就是跟不上手的。
    /// </summary>
    private void StartLagMeter()
    {
        _lagMax = _lagSum = 0;
        _lagN = _refresh = 0;
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
        _alloc0 = GC.GetTotalAllocatedBytes(false);
        _lastTick = Stopwatch.GetTimestamp();

        _lagTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _lagTimer.Tick += (_, _) =>
        {
            var now = Stopwatch.GetTimestamp();
            var late = (now - _lastTick) * 1000.0 / Stopwatch.Frequency - 16;
            _lastTick = now;
            if (late > 0) { _lagSum += late; if (late > _lagMax) _lagMax = late; }
            _lagN++;
            // 一秒刷一次字，不然自检行自己就成了每帧重画的那一路
            if (++_refresh >= 60) { _refresh = 0; DiagLine.Text = DiagText(); }
        };
        _lagTimer.Start();
    }

    private void StopLagMeter()
    {
        _lagTimer?.Stop();
        _lagTimer = null;
    }

    private int _gc0, _gc1, _gc2;
    private long _alloc0;

    /// <summary>自检行写什么。全是当场问出来的，没有一个是写死的。</summary>
    private string DiagText()
    {
        var head = $"F12 自检 · 渲染 {GraphicsBackend()} · 透明 {ActualTransparencyLevel} · "
                 + $"窗口 {Bounds.Width:0}×{Bounds.Height:0} @ {RenderScaling:0.##}×";
        if (!_diag) return head + " · 浮层 关（Shift+F12 切透明）";

        var avg = _lagN > 0 ? _lagSum / _lagN : 0;
        var mb = (GC.GetTotalAllocatedBytes(false) - _alloc0) / 1024.0 / 1024.0;
        return head
             + $" · 界面线程迟到 平均 {avg:0.0} ms / 最大 {_lagMax:0} ms"
             + $" · GC {GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2}"
             + $" · 已分配 {mb:0} MB";
    }

    /// <summary>
    /// 走的是显卡还是软件。
    ///
    /// **只能靠反射问**：Avalonia 11.2 把 AvaloniaLocator 收成了内部类型，
    /// 公开 API 里没有一处说得出「这次用的是哪个图形后端」。拿不到
    /// IPlatformGraphics 就说明根本没有显卡后端，整窗由 CPU 逐帧重画。
    ///
    /// 反射失败一律写「未知」——这一行是拿来看的，不参与任何判断，
    /// 换 Avalonia 版本以后就算问不出来了，也只是少一行字，不会影响程序。
    /// </summary>
    private static string GraphicsBackend()
    {
        try
        {
            var locator = Type.GetType("Avalonia.AvaloniaLocator, Avalonia.Base");
            var current = locator?
                .GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?
                .GetValue(null);
            var iface = Type.GetType("Avalonia.Platform.IPlatformGraphics, Avalonia.Base");
            if (current is null || iface is null) return "未知";

            var svc = current.GetType()
                .GetMethod("GetService", new[] { typeof(Type) })?
                .Invoke(current, new object?[] { iface });
            return svc is null ? "软件整窗重画" : "GPU 合成 · " + svc.GetType().Name;
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>自绘标题栏要自己负责拖动。</summary>
    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // ── 用户区下拉 ───────────────────────────────────────────────────

    /// <summary>
    /// 选完一项就把账户菜单收起来。Avalonia 的 Flyout 只认「点到别处」这一种关法，
    /// 点里面的按钮它自己不关——不收的话，弹出来的窗前面还浮着一块菜单。
    /// </summary>
    private void CloseUserMenu() => (UserZone.Flyout as Flyout)?.Hide();

    private void OnChangePassword(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm) return;
        _ = new ChangePasswordWindow(vm.Workspace).ShowDialog(this);
    }

    private void OnManageUsers(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm || !vm.IsAdmin) return;
        _ = new UserAdminWindow(vm.Workspace).ShowDialog(this);
    }

    private void OnLogout(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is MainViewModel vm && vm.Logout.CanExecute(null)) vm.Logout.Execute(null);
    }

    /// <summary>锁屏。通道不动——锁的是人，不是台面（见 LockWindow 顶上那段）。</summary>
    private async void OnLockScreen(object? sender, RoutedEventArgs e)
    {
        CloseUserMenu();
        if (DataContext is not MainViewModel vm || vm.Workspace.CurrentUser is null) return;
        vm.Workspace.Log.Write("登录", $"锁定屏幕（{vm.Workspace.CurrentUser.Name}）", vm.Workspace.Operator);

        var win = new LockWindow(vm.Workspace);
        await win.ShowDialog(this);
        if (win.SwitchUser && vm.Logout.CanExecute(null)) vm.Logout.Execute(null);
    }

    /// <summary>
    /// 「下次登录必须修改密码」的账号，进了工作站先把这件事办了。
    /// 初始密码是别人定的，本人没改过之前，记录上那个署名站不住脚——
    /// 所以这扇对话框关不掉（见 ChangePasswordWindow 的 forced）。
    /// </summary>
    public async Task ForceChangePasswordIfNeeded()
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.Workspace.CurrentUser is not { MustChangePassword: true }) return;
        await new ChangePasswordWindow(vm.Workspace, forced: true).ShowDialog(this);
    }

    /// <summary>
    /// 有未保存的改动就先问一句。Closing 不能等异步结果，所以先把这次关窗拦下来，
    /// 问完再自己调一次 Close()——标准做法。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_confirmed || DataContext is not MainViewModel vm) return;

        var store = vm.Workspace.Store;
        if (!store.Dirty) return;

        e.Cancel = true;
        _ = AskThenClose(vm);
    }

    private async Task AskThenClose(MainViewModel vm)
    {
        if (!await ConfirmLeaveAsync(this, vm)) return;
        _confirmed = true;
        Close();
    }

    /// <summary>
    /// 有未保存的改动就问一句，返回「可以走了吗」。
    ///
    /// 拎成静态的是因为**退出的口子不止一个**：主窗口的 ✕ 是一个，注销之后
    /// 关掉那扇登录窗又是一个——两处得问同一句话、走同一套判断，
    /// 不然从其中一个口子出去就能把没存的实验悄悄丢掉。
    /// </summary>
    public static async Task<bool> ConfirmLeaveAsync(Window owner, MainViewModel vm)
    {
        var store = vm.Workspace.Store;
        if (!store.Dirty) return true;

        var name = vm.Workspace.ExperimentName;
        var detail = store.CurrentPath is { } p
            ? $"上次保存的位置：{p}"
            : "这份实验还没有保存过。选「保存」会让你挑一个位置。";

        var choice = await ConfirmDialog.Ask(owner,
            "未保存的改动",
            $"「{name}」有改动还没有保存。关掉程序这些改动就没了。",
            detail, "保存并退出", "不保存，直接退出");

        return choice switch
        {
            // 存不成（挑位置时取消了、或者写盘失败）就留在程序里，不能闷头关掉
            DialogChoice.Primary => await vm.Start.SaveForExit(),
            DialogChoice.Secondary => true,
            _ => false
        };
    }
}
