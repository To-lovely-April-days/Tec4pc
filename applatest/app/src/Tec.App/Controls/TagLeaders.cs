using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Tec.App.ViewModels;

namespace Tec.App.Controls;

/// <summary>
/// 读数框的引线：从探头头顶到框沿，一根 #4A4A4A 细线，探头那端一颗小圆点——
/// 提示框式的「从传感器往外延伸」。几何都在 ReadTagViewModel 上，
/// 每次插拔 / 移机重建标签集合，这里只管照着画。
/// </summary>
public sealed class TagLeaders : Control
{
    public static readonly StyledProperty<IEnumerable?> TagsProperty =
        AvaloniaProperty.Register<TagLeaders, IEnumerable?>(nameof(Tags));

    static TagLeaders() => AffectsRender<TagLeaders>(TagsProperty);

    public IEnumerable? Tags
    {
        get => GetValue(TagsProperty);
        set => SetValue(TagsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TagsProperty) return;
        if (change.OldValue is INotifyCollectionChanged o) o.CollectionChanged -= OnListChanged;
        if (change.NewValue is INotifyCollectionChanged n) n.CollectionChanged += OnListChanged;
        InvalidateVisual();
    }

    private void OnListChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        if (Tags is null) return;
        var pen = new Pen(new SolidColorBrush(Color.Parse("#4A4A4A")), 1.4)
        { LineCap = PenLineCap.Round };
        var dot = new SolidColorBrush(Color.Parse("#4A4A4A"));

        foreach (var t in Tags.OfType<ReadTagViewModel>())
        {
            ctx.DrawLine(pen, new Point(t.AnchorX, t.AnchorY), new Point(t.AttachX, t.AttachY));
            ctx.DrawEllipse(dot, null, new Point(t.AnchorX, t.AnchorY), 2.6, 2.6);
        }
    }
}
