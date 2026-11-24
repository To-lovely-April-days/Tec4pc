using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Tec.App.Controls;

/// <summary>
/// 触摸屏那套界面的本体，装在工作站的窗口里。
///
/// 它不是把屏上的界面「照着再写一遍」——**就是同一份代码**：hmi-cpp 编出来的 tec_hmi_core
/// 既是触摸屏上跑的那个程序，也是这里加载的这个库。每帧问它要一张画好的画面贴上来，
/// 把鼠标动作当手指喂回去。所以 PC 上和屏上长得一模一样，改设计稿两边一起变
/// （用户 2026-11-24：「必经我们自己开发的产品，要能做到控制面板还原会比较好」）。
///
/// 库和它的 assets 放在工作站程序旁边的 hmi 文件夹里（开发时可用环境变量 TEC_HMI_DIR 指到别处）。
/// </summary>
public static class NativePanel
{
    private const string Lib = "tec_hmi_core";

    [DllImport(Lib)] private static extern int tec_hmi_init(string assetsDir, string host, int port, int channel);
    [DllImport(Lib)] private static extern int tec_hmi_tick();
    [DllImport(Lib)] private static extern IntPtr tec_hmi_pixels();
    [DllImport(Lib)] private static extern void tec_hmi_size(out int w, out int h);
    [DllImport(Lib)] private static extern int tec_hmi_take_dirty();
    [DllImport(Lib)] private static extern void tec_hmi_pointer(int x, int y, int down);
    [DllImport(Lib)] private static extern void tec_hmi_connect(string host, int port, int channel);
    [DllImport(Lib)] private static extern void tec_hmi_show(string page, int channel);

    /// <summary>库和 assets 放哪。开发时用环境变量 TEC_HMI_DIR 指到 hmi-cpp 的编译输出目录。</summary>
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("TEC_HMI_DIR");
            return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(AppContext.BaseDirectory, "hmi");
        }
    }

    private static bool _resolverSet, _started;
    /// <summary>起不来的原因；空串就是好的。界面上照实显示这句话，不装作面板是空的。</summary>
    public static string Problem { get; private set; } = "";

    /// <summary>现在连的是哪台机器，用来判断要不要换过去。</summary>
    private static (string Host, int Port, int Channel) _at = ("", 0, 0);

    /// <summary>
    /// 让面板连上这台机器。第一次调会把库起起来，之后再调只是换一台看——
    /// 整个程序里只有一套界面（那边 LVGL 是全局的），所以同时只能看一台机器。
    /// 回空串是好的，否则是起不来的原因。
    /// </summary>
    public static string Attach(string host, int port, int channel)
    {
        if (!_resolverSet)
        {
            _resolverSet = true;
            // 库不在程序根目录，而在旁边的 hmi 文件夹里，所以自己告诉运行时去哪儿找
            NativeLibrary.SetDllImportResolver(typeof(NativePanel).Assembly, (name, asm, path) =>
            {
                if (name != Lib) return IntPtr.Zero;
                foreach (var f in new[] { Lib + ".dll", "lib" + Lib + ".dylib", "lib" + Lib + ".so" })
                {
                    var full = Path.Combine(Dir, f);
                    if (File.Exists(full) && NativeLibrary.TryLoad(full, out var h)) return h;
                }
                return IntPtr.Zero;
            });
        }
        try
        {
            if (!_started)
            {
                var rc = tec_hmi_init(Dir, host, port, channel);
                if (rc != 0) return Problem = $"面板起不来（错误码 {rc}）：检查 {Dir} 里有没有 assets 文件夹";
                _started = true;
            }
            else if (_at != (host, port, channel))
            {
                tec_hmi_connect(host, port, channel);
            }
            _at = (host, port, channel);
            return Problem = "";
        }
        catch (DllNotFoundException)
        {
            return Problem = $"没找到面板程序库：把 hmi-cpp 编出来的 tec_hmi_core 和 assets 文件夹放进 {Dir}";
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or BadImageFormatException)
        {
            return Problem = $"面板程序库对不上（可能是 32/64 位不符或版本旧了）：{ex.Message}";
        }
    }

    internal static bool Started => _started;
    internal static int Tick() => tec_hmi_tick();
    internal static IntPtr Pixels() => tec_hmi_pixels();
    internal static void Size(out int w, out int h) => tec_hmi_size(out w, out h);
    internal static bool TakeDirty() => tec_hmi_take_dirty() != 0;
    internal static void Pointer(int x, int y, bool down) => tec_hmi_pointer(x, y, down ? 1 : 0);
    /// <summary>跳到某一页（页面名和触摸屏那边一致，比如 Ctl、ChTrend、Seq）。</summary>
    public static void Show(string page, int channel) { if (_started) tec_hmi_show(page, channel); }
}

/// <summary>
/// 把上面那套画面画到窗口里的控件。画面是固定比例的（和触摸屏一样 16:9），
/// 控件比它大就居中留边，不拉变形。鼠标按下、拖动、松开都转成「手指」喂过去，
/// 所以点按、滑动、长按在 PC 上和在屏上是一个行为。
/// </summary>
public sealed class NativePanelView : Control
{
    private WriteableBitmap? _bmp;
    private int _w, _h;
    private DispatcherTimer? _timer;
    private Rect _dest;                 // 画面贴在控件的哪一块（居中、保持比例）

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!NativePanel.Started) return;
        NativePanel.Size(out _w, out _h);
        _bmp = new WriteableBitmap(new PixelSize(_w, _h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        // 30 毫秒一帧：界面本身半秒才换一次读数，再快没意义，但点下去要跟手，所以也不能慢
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += OnFrame;
        _timer.Start();
        Pull();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_timer is not null) { _timer.Stop(); _timer.Tick -= OnFrame; _timer = null; }
        // 库不卸：它在进程里只起一次，面板关掉再开是接着用同一个
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        NativePanel.Tick();
        if (NativePanel.TakeDirty()) Pull();
    }

    /// <summary>把那边画好的画面拷过来。只在它说「画面变了」时拷，没变不动。</summary>
    private void Pull()
    {
        if (_bmp is null) return;
        var src = NativePanel.Pixels();
        if (src == IntPtr.Zero) return;
        using (var fb = _bmp.Lock())
        {
            unsafe
            {
                var bytes = (long)_w * _h * 4;
                Buffer.MemoryCopy(src.ToPointer(), fb.Address.ToPointer(), bytes, bytes);
            }
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (_bmp is null || _w == 0)
        {
            ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1F, 0x2A, 0x35)), null, new Rect(Bounds.Size));
            return;
        }
        // 保持比例居中；四周填深色，和界面本身的底色接得上
        var cw = Bounds.Width; var ch = Bounds.Height;
        var scale = Math.Min(cw / _w, ch / _h);
        var dw = _w * scale; var dh = _h * scale;
        _dest = new Rect((cw - dw) / 2, (ch - dh) / 2, dw, dh);
        ctx.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0A, 0x2A, 0x55)), null, new Rect(Bounds.Size));
        ctx.DrawImage(_bmp, new Rect(0, 0, _w, _h), _dest);
    }

    /// <summary>控件上的坐标换成画面里的坐标；点在画面外面就回 null。</summary>
    private (int X, int Y)? Map(Point p)
    {
        if (_dest.Width <= 0 || !_dest.Contains(p)) return null;
        return ((int)((p.X - _dest.X) / _dest.Width * _w), (int)((p.Y - _dest.Y) / _dest.Height * _h));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Map(e.GetPosition(this)) is not { } p) return;
        NativePanel.Pointer(p.X, p.Y, true);
        e.Pointer.Capture(this);        // 按住往外拖也还算这一次按（滑块要这个）
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var pt = e.GetCurrentPoint(this);
        if (Map(e.GetPosition(this)) is { } p) NativePanel.Pointer(p.X, p.Y, pt.Properties.IsLeftButtonPressed);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Map(e.GetPosition(this)) is { } p) NativePanel.Pointer(p.X, p.Y, false);
        else NativePanel.Pointer(0, 0, false);
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
