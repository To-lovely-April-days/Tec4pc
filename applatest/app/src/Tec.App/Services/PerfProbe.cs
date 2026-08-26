using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;

namespace Tec.App.Services;

/// <summary>
/// 性能自检探针。挂在任意一扇窗上，F12 开，再按一下关。
///
/// **为什么要这么一件东西：**「界面卡」报上来的时候，卡在哪一层是看不出来的，
/// 而且这台机器上的数在另一台上不成立——开发机没有显卡、时钟粒度也不一样，
/// 拿那边的数推这边只会推错（已经推错过两次，见下面各处注释）。
/// 所以量表得跟着程序走到现场去。
///
/// **量四件事，每件都对着一种卡法：**
///
///   帧距 —— 两帧之间隔多久。60 Hz 一格是 16.7 ms。**「有时候顺有时候卡」
///     这种断续的顿，只有这个数看得见**：平均值可以很好看，中间夹着几帧
///     30、50 ms 的，手上就是一跳一跳的。所以除了平均，还报近 1 秒里
///     超过 32 ms（掉了一格）的帧有几个。
///   队列排队 —— 往界面线程塞一件最低优先级的小事，量它多久才轮上。
///     输入、布局、渲染提交都排在它前面，这个数就是「界面线程手上压着多少活」。
///   GC —— 二代回收会把界面线程整个停住，那是另一种卡法。
///   渲染后端 —— 走显卡还是软件。软件那条路一帧的代价只跟窗口面积有关，
///     跟脏了多小一块无关，鼠标扫过任何有悬停反应的东西都摊上一整窗。
///
/// 开着的时候每秒往 perf.log 追一行（跟程序放在一起）。**断续的毛病靠截图抓不住**
/// ——顺的时候截图没用，卡的那一下手忙脚乱也截不到；让它自己记，
/// 跑上几分钟把文件发回来，哪一秒出的事一目了然。
///
/// **一个已知的副作用：**帧距是靠一帧接一帧地请求下一帧量出来的，
/// 所以开着自检的时候程序会一直出帧，不像平时那样闲下来就停。
/// 量到的帧距是「连续出帧时」的帧距——要找的正是这种情况下的抖动，
/// 但别拿开着自检时的 CPU 占用去推平时的。
/// </summary>
public sealed class PerfProbe
{
    private readonly Window _win;
    private readonly TextBlock _line;

    private bool _on;
    private DispatcherTimer? _timer;
    private StreamWriter? _log;

    // 队列排队
    private double _qSum, _qMax, _qWin;
    private int _qN;
    // 帧距
    private double _fSum, _fMax, _fWin;
    private int _fN, _fSlowWin, _fSlowAll;
    private TimeSpan _fLast;
    private bool _fFirst = true;
    // GC
    private int _gc0, _gc1, _gc2;
    private long _alloc0;
    private int _refresh, _seconds;

    private PerfProbe(Window win, TextBlock line)
    {
        _win = win;
        _line = line;
    }

    /// <summary>挂上去。<paramref name="line"/> 是显示自检行的那块文字，平时藏着。</summary>
    public static PerfProbe Attach(Window win, TextBlock line) => new(win, line);

    public bool On => _on;

    /// <summary>开 / 关。返回开关之后的状态。</summary>
    public bool Toggle()
    {
        _on = !_on;
        if (_on) Start(); else Stop();
        return _on;
    }

    /// <summary>把当前这一行文字重新算一遍（窗口透明档之类在外面改了，也走这儿刷新）。</summary>
    public void Refresh() => _line.Text = Text();

    private void Start()
    {
        _qSum = _qMax = _qWin = 0;
        _fSum = _fMax = _fWin = 0;
        _qN = _fN = _fSlowWin = _fSlowAll = _refresh = _seconds = 0;
        _fFirst = true;
        _gc0 = GC.CollectionCount(0);
        _gc1 = GC.CollectionCount(1);
        _gc2 = GC.CollectionCount(2);
        _alloc0 = GC.GetTotalAllocatedBytes(false);

        OpenLog();
        _win.RequestAnimationFrame(OnFrame);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) =>
        {
            // 塞一件最低优先级的小事，量它排多久
            var t0 = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(() =>
            {
                var wait = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _qSum += wait;
                _qN++;
                if (wait > _qMax) _qMax = wait;
                if (wait > _qWin) _qWin = wait;
            }, DispatcherPriority.Background);

            // 一秒结一次账：刷字、记一行日志、把「近 1 秒」那几个清零
            if (++_refresh < 60) return;
            _refresh = 0;
            _seconds++;
            var text = Text();
            _line.Text = text;
            WriteLog(text);
            _qWin = _fWin = 0;
            _fSlowWin = 0;
        };
        _timer.Start();
        _line.Text = Text();
    }

    private void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _log?.Dispose();
        _log = null;
    }

    /// <summary>
    /// 一帧回来一次，量跟上一帧隔了多久，然后接着请求下一帧。
    /// 时间戳是合成器自己的钟，不是我们这边的表——量的就是它出帧的节奏。
    /// </summary>
    private void OnFrame(TimeSpan now)
    {
        if (!_on) return;
        if (_fFirst) { _fFirst = false; }
        else
        {
            var ms = (now - _fLast).TotalMilliseconds;
            // 窗口最小化 / 被挡住时帧会稀到几百毫秒，那不是卡，是根本没在画。
            // 一秒以上的间隔当作「中间停过」，不计进统计
            if (ms is > 0 and < 1000)
            {
                _fSum += ms;
                _fN++;
                if (ms > _fMax) _fMax = ms;
                if (ms > _fWin) _fWin = ms;
                if (ms > 32) { _fSlowWin++; _fSlowAll++; }   // 掉了一格（60 Hz）
            }
        }
        _fLast = now;
        _win.RequestAnimationFrame(OnFrame);
    }

    private string Text()
    {
        var fAvg = _fN > 0 ? _fSum / _fN : 0;
        var qAvg = _qN > 0 ? _qSum / _qN : 0;
        var mb = (GC.GetTotalAllocatedBytes(false) - _alloc0) / 1024.0 / 1024.0;
        return new StringBuilder()
            .Append("F12 自检 · 渲染 ").Append(Backend())
            .Append(" · 透明 ").Append(_win.ActualTransparencyLevel)
            .Append(" · 窗口 ").Append($"{_win.Bounds.Width:0}×{_win.Bounds.Height:0}")
            .Append(" @ ").Append($"{_win.RenderScaling:0.##}×")
            .Append(" · 帧距 平均 ").Append($"{fAvg:0.0}")
            .Append(" / 近 1 秒最大 ").Append($"{_fWin:0}")
            .Append(" ms · 慢帧 近 1 秒 ").Append(_fSlowWin)
            .Append(" 全程 ").Append(_fSlowAll)
            .Append(" · 排队 平均 ").Append($"{qAvg:0.0}")
            .Append(" / 近 1 秒最大 ").Append($"{_qWin:0}")
            .Append(" / 全程最大 ").Append($"{_qMax:0}")
            .Append(" ms · GC ").Append(GC.CollectionCount(0) - _gc0)
            .Append('/').Append(GC.CollectionCount(1) - _gc1)
            .Append('/').Append(GC.CollectionCount(2) - _gc2)
            .Append(" · 已分配 ").Append($"{mb:0}").Append(" MB")
            .ToString();
    }

    // ── 日志 ──────────────────────────────────────────────────────────

    /// <summary>
    /// perf.log 放在程序旁边（跟 exports 一个地方），好找、好发回来。
    /// 每开一次自检重开一次文件，不往上叠——一次自检就是一份记录。
    /// 写不出来（目录只读之类）就算了，自检行照常显示。
    /// </summary>
    private void OpenLog()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "perf.log");
            _log = new StreamWriter(path, append: false) { AutoFlush = true };
            _log.WriteLine($"# TecStudio 性能自检 · {DateTime.Now:yyyy-MM-dd HH:mm:ss} · 窗口「{_win.Title}」");
            _log.WriteLine("# 每秒一行。帧距 32 ms 以上算慢帧（60 Hz 掉一格）");
        }
        catch
        {
            _log = null;
        }
    }

    private void WriteLog(string text)
    {
        try { _log?.WriteLine($"[{_seconds,5}s] {text}"); }
        catch { /* 写不动就不写，别把自检本身变成故障 */ }
    }

    // ── 渲染后端 ──────────────────────────────────────────────────────

    /// <summary>
    /// 走的是显卡还是软件。
    ///
    /// **只能靠反射问**：Avalonia 11.2 把 AvaloniaLocator 收成了内部类型，
    /// 公开 API 里没有一处说得出用的是哪个图形后端。拿不到 IPlatformGraphics
    /// 就说明根本没有显卡后端，整窗由 CPU 逐帧重画。
    ///
    /// 问不出来就写「未知」：这一行是拿来看的，不参与任何判断，
    /// 换 Avalonia 版本以后失灵也只是少一行字。
    /// </summary>
    private static string Backend()
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

    /// <summary>Avalonia 自带的那几个浮层（帧率、渲染耗时、布局耗时）跟着一起开关。</summary>
    public static void Overlays(Window win, bool on)
        => win.RendererDiagnostics.DebugOverlays = on
            ? RendererDebugOverlays.Fps
              | RendererDebugOverlays.RenderTimeGraph
              | RendererDebugOverlays.LayoutTimeGraph
            : RendererDebugOverlays.None;
}
