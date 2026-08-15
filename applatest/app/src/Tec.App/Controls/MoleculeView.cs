using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 结构式（骨架式）渲染：键画线，双键画平行两条，带标签的顶点画原子符号并把键让开一段。
/// 离子化合物没有骨架，用 IonText 直接排离子对。
/// </summary>
public sealed class MoleculeView : Control
{
    public static readonly StyledProperty<Molecule?> StructureProperty =
        AvaloniaProperty.Register<MoleculeView, Molecule?>(nameof(Structure));

    public static readonly StyledProperty<string?> IonTextProperty =
        AvaloniaProperty.Register<MoleculeView, string?>(nameof(IonText));

    private const double Pad = 16, LabelGap = 7.5, FontSize = 11.5;

    static MoleculeView() => AffectsRender<MoleculeView>(StructureProperty, IonTextProperty);

    public Molecule? Structure
    {
        get => GetValue(StructureProperty);
        set => SetValue(StructureProperty, value);
    }

    public string? IonText
    {
        get => GetValue(IonTextProperty);
        set => SetValue(IonTextProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        var w = double.IsInfinity(available.Width) ? 280 : available.Width;
        return new Size(w, 150);
    }

    public override void Render(DrawingContext ctx)
    {
        var ink = new SolidColorBrush(Color.Parse("#3a3a3a"));

        if (Structure is not { } m || m.Atoms.Count == 0)
        {
            if (IonText is { Length: > 0 } t)
                Draw(ctx, t, new Point(Bounds.Width / 2, Bounds.Height / 2 - 9), ink, 15, center: true);
            return;
        }

        // 等比缩放到控件内并居中
        double minX = m.Atoms.Min(a => a.X), maxX = m.Atoms.Max(a => a.X);
        double minY = m.Atoms.Min(a => a.Y), maxY = m.Atoms.Max(a => a.Y);
        var sx = (Bounds.Width - 2 * Pad) / Math.Max(maxX - minX, 0.001);
        var sy = (Bounds.Height - 2 * Pad) / Math.Max(maxY - minY, 0.001);
        var s = Math.Min(Math.Min(sx, sy), 34);
        var ox = (Bounds.Width - (maxX - minX) * s) / 2 - minX * s;
        var oy = (Bounds.Height - (maxY - minY) * s) / 2 - minY * s;
        Point P(int i) => new(m.Atoms[i].X * s + ox, m.Atoms[i].Y * s + oy);

        var pen = new Pen(ink, 1.3) { LineCap = PenLineCap.Round };
        foreach (var (ai, bi, order) in m.Bonds)
        {
            var a = P(ai);
            var b = P(bi);
            // 有标签的一端把线缩回去，免得压住原子符号
            if (m.Atoms[ai].Label is not null) a = Shorten(a, b, LabelGap);
            if (m.Atoms[bi].Label is not null) b = Shorten(b, a, LabelGap);

            if (order < 2) { ctx.DrawLine(pen, a, b); continue; }

            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            var len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.001);
            var nx = -dy / len * 2.1;
            var ny = dx / len * 2.1;
            ctx.DrawLine(pen, new Point(a.X + nx, a.Y + ny), new Point(b.X + nx, b.Y + ny));
            ctx.DrawLine(pen, new Point(a.X - nx, a.Y - ny), new Point(b.X - nx, b.Y - ny));
        }

        for (var i = 0; i < m.Atoms.Count; i++)
            if (m.Atoms[i].Label is { } label)
                Draw(ctx, label, P(i), ink, FontSize, center: true);
    }

    private static Point Shorten(Point from, Point to, double by)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Max(Math.Sqrt(dx * dx + dy * dy), 0.001);
        return new Point(from.X + dx / len * by, from.Y + dy / len * by);
    }

    private static void Draw(DrawingContext ctx, string s, Point at, IBrush brush, double size, bool center)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, brush);
        ctx.DrawText(ft, center ? new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2) : at);
    }
}
