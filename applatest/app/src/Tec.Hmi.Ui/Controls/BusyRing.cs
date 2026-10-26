using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tec.Hmi.Ui.Controls;

/// <summary>
/// 连接中 / 在途操作的小转环（菊花）。Avalonia 11.2 主包里没有 ProgressRing，自己画一个：
/// 一段 100° 的弧按拍子转。只在挂着且可见时走定时器，不可见不耗拍子
/// （BenchView 的动画拍子同款规矩）。放在 Hmi.Ui：台面（Tec.App）与 HMI 面板共用。
/// </summary>
public sealed class BusyRing : Control
{
    /// <summary>弧的颜色（默认靛——外壳色，不是信号色）。</summary>
    public static readonly StyledProperty<IBrush?> BrushProperty =
        AvaloniaProperty.Register<BusyRing, IBrush?>(nameof(Brush), new SolidColorBrush(Color.Parse("#352d6a")));

    /// <summary>弧的线宽（默认 2.5）。</summary>
    public static readonly StyledProperty<double> StrokeProperty =
        AvaloniaProperty.Register<BusyRing, double>(nameof(Stroke), 2.5);

    public IBrush? Brush { get => GetValue(BrushProperty); set => SetValue(BrushProperty, value); }
    public double Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private readonly DispatcherTimer _timer;
    private double _angle;

    public BusyRing()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Background,
            (_, _) => { _angle = (_angle + 14) % 360; InvalidateVisual(); });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext ctx)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size < 4 || Brush is null) return;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = (size - Stroke) / 2;
        if (radius <= 0) return;

        using (ctx.PushTransform(
                   Matrix.CreateTranslation(-center.X, -center.Y) *
                   Matrix.CreateRotation(_angle * Math.PI / 180) *
                   Matrix.CreateTranslation(center.X, center.Y)))
        {
            // 从 12 点起画一段 100° 的弧，整段跟着角度转
            const double start = -90, sweep = 100;
            var a0 = start * Math.PI / 180;
            var a1 = (start + sweep) * Math.PI / 180;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(center.X + radius * Math.Cos(a0), center.Y + radius * Math.Sin(a0)), false);
                g.ArcTo(new Point(center.X + radius * Math.Cos(a1), center.Y + radius * Math.Sin(a1)),
                        new Size(radius, radius), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(Brush, Stroke, lineCap: PenLineCap.Round), geo);
        }
    }
}
