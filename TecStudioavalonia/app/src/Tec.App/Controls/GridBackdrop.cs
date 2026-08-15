using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>台面底纹：28 px 网格，线色 #f4f4f4（原型 .stage 的 background-image）。</summary>
public sealed class GridBackdrop : Control
{
    public static readonly StyledProperty<double> CellProperty =
        AvaloniaProperty.Register<GridBackdrop, double>(nameof(Cell), 28);

    public static readonly StyledProperty<IBrush> LineBrushProperty =
        AvaloniaProperty.Register<GridBackdrop, IBrush>(nameof(LineBrush),
            new SolidColorBrush(Color.Parse("#f4f4f4")));

    static GridBackdrop() => AffectsRender<GridBackdrop>(CellProperty, LineBrushProperty);

    public double Cell
    {
        get => GetValue(CellProperty);
        set => SetValue(CellProperty, value);
    }

    public IBrush LineBrush
    {
        get => GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public override void Render(DrawingContext ctx)
    {
        var cell = Cell <= 0 ? 28 : Cell;
        var pen = new Pen(LineBrush, 1);
        for (var x = 0.5; x < Bounds.Width; x += cell)
            ctx.DrawLine(pen, new Point(x, 0), new Point(x, Bounds.Height));
        for (var y = 0.5; y < Bounds.Height; y += cell)
            ctx.DrawLine(pen, new Point(0, y), new Point(Bounds.Width, y));
    }
}
