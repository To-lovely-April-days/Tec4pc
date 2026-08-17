using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 记录表「趋势」那一列的小曲线（原型 spark()）。
///
/// 原型里是四条写死的形状（drop / flat / saw / stair）——这里画的是**这一炉
/// 真实的釜温**，降采样到几十个点。一眼看出「这炉是降温结晶还是恒温保持」，
/// 靠的是它真的长那样，不是靠随机挑一条好看的折线。
///
/// 没有采样就什么都不画：一条凭空捏出来的曲线比空着危险得多。
/// </summary>
public sealed class SparkView : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> PointsProperty =
        AvaloniaProperty.Register<SparkView, IReadOnlyList<double>?>(nameof(Points));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<SparkView, IBrush?>(nameof(Stroke));

    static SparkView() => AffectsRender<SparkView>(PointsProperty, StrokeProperty);

    public SparkView()
    {
        Width = 66;
        Height = 16;
        ClipToBounds = true;
    }

    public IReadOnlyList<double>? Points
    {
        get => GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var p = Points;
        if (p is null || p.Count < 2) return;

        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w < 4 || h < 4) return;

        var lo = double.MaxValue;
        var hi = double.MinValue;
        foreach (var v in p) { if (v < lo) lo = v; if (v > hi) hi = v; }
        // 恒温那一炉上下限相等，除下去就是 0/0。平线画在正中间才是它真实的样子
        var span = hi - lo;
        var flat = span < 1e-9;

        var pen = new Pen(Stroke ?? Brushes.Gray, 1.4)
        { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            Point At(int i)
            {
                var x = p.Count == 1 ? 0 : i * (w - 2) / (p.Count - 1.0) + 1;
                var y = flat ? h / 2 : 1 + (1 - (p[i] - lo) / span) * (h - 2);
                return new Point(x, y);
            }
            g.BeginFigure(At(0), false);
            for (var i = 1; i < p.Count; i++) g.LineTo(At(i));
            g.EndFigure(false);
        }
        ctx.DrawGeometry(null, pen, geo);
    }
}
