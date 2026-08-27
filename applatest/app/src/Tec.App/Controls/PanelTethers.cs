using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.ViewModels;

namespace Tec.App.Controls;

/// <summary>
/// 泵控制小窗到泵之间的肘形引线，1:1 照交互演示的 chrome()：
/// 从窗的上/下沿靠泵那一侧 34px 处出发，竖直走一段，半径 14 的圆角
/// 拐成水平，落在泵顶；落点一颗 r4 的圆点。#B4B4B4 · 1.6。
/// 窗挪、泵挪、开关窗都要跟着重画，所以订每个面板的属性变化。
/// </summary>
public sealed class PanelTethers : Control
{
    public static readonly StyledProperty<IEnumerable?> PanelsProperty =
        AvaloniaProperty.Register<PanelTethers, IEnumerable?>(nameof(Panels));

    static PanelTethers() => AffectsRender<PanelTethers>(PanelsProperty);

    public IEnumerable? Panels
    {
        get => GetValue(PanelsProperty);
        set => SetValue(PanelsProperty, value);
    }

    private readonly List<INotifyPropertyChanged> _watched = new();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != PanelsProperty) return;
        if (change.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= OnListChanged;
        if (change.NewValue is INotifyCollectionChanged n) n.CollectionChanged += OnListChanged;
        Rewatch();
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rewatch();

    private void Rewatch()
    {
        foreach (var w in _watched) w.PropertyChanged -= OnItemChanged;
        _watched.Clear();
        if (Panels is not null)
            foreach (var p in Panels.OfType<INotifyPropertyChanged>())
            {
                p.PropertyChanged += OnItemChanged;
                _watched.Add(p);
            }
        InvalidateVisual();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PumpPanelViewModel.X) or nameof(PumpPanelViewModel.Y)
            or nameof(PumpPanelViewModel.TargetX) or nameof(PumpPanelViewModel.TargetY)
            or nameof(PumpPanelViewModel.Closed))
            InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        if (Panels is null) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#B4B4B4")), 1.6)
        { LineCap = PenLineCap.Round };
        var dot = new SolidColorBrush(Color.Parse("#B4B4B4"));

        foreach (var p in Panels.OfType<PumpPanelViewModel>())
        {
            if (p.Closed) continue;
            double tx = p.TargetX, ty = p.TargetY;

            const double r = 14;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                // 落点跟窗差不多一样高（演示没有这种摆法——它的窗永远在泵上方）：
                // 从朝着泵那一侧的边上水平引一根直线过去，别按演示的上下沿出线——
                // 那样线会从窗顶绕回来，穿过窗身
                if (ty >= p.Y + 44 && ty <= p.Y + p.H - 12)
                {
                    var ex = tx < p.X + p.W / 2 ? p.X : p.X + p.W;
                    g.BeginFigure(new Point(ex, ty), false);
                    g.LineTo(new Point(tx, ty));
                    g.EndFigure(false);
                }
                else
                {
                    // 演示 elbow()：上/下沿靠泵那侧 34px 出发，竖直 → 圆角 → 水平
                    var ax = tx < p.X + p.W / 2 ? p.X + 34 : p.X + p.W - 34;
                    var ay = p.Y + p.H / 2 < ty ? p.Y + p.H : p.Y;
                    g.BeginFigure(new Point(ax, ay), false);
                    if (Math.Abs(tx - ax) < r + 3)
                    {
                        g.LineTo(new Point(ax, ty));
                    }
                    else
                    {
                        var dy = ty > ay ? 1 : -1;
                        var dx = tx > ax ? 1 : -1;
                        g.LineTo(new Point(ax, ty - dy * r));
                        g.QuadraticBezierTo(new Point(ax, ty), new Point(ax + dx * r, ty));
                        g.LineTo(new Point(tx, ty));
                    }
                    g.EndFigure(false);
                }
            }
            ctx.DrawGeometry(null, pen, geo);
            ctx.DrawEllipse(dot, null, new Point(tx, ty), 4, 4);
        }
    }
}
