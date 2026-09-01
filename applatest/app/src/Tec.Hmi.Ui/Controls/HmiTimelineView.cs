using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.Hmi.Ui.Controls;

/// <summary>方案总览时间轴的一段。W = 像素宽（视图模型按原型 segLay 算好）。</summary>
public sealed record HmiTlSeg(double W, bool Zip, bool Past, string Text);

public sealed class HmiTlModel
{
    public required IReadOnlyList<HmiTlSeg> TempRow { get; init; }
    public required IReadOnlyList<HmiTlSeg> StirRow { get; init; }
    public required IReadOnlyList<(double X, string Label)> Ticks { get; init; }
    /// <summary>运行游标位置（距轨道起点的像素）；没跑就 null。</summary>
    public double? CursorX { get; init; }
}

/// <summary>
/// 方案总览的双行时间轴（原型 .tl）：温度行 / 搅拌行按时长分段，
/// 短步骤压成 26px 斜纹块；下面一行时间刻度；运行中画绿游标。
/// </summary>
public sealed class HmiTimelineView : Control
{
    public static readonly StyledProperty<HmiTlModel?> ModelProperty =
        AvaloniaProperty.Register<HmiTimelineView, HmiTlModel?>(nameof(Model));

    public HmiTlModel? Model
    {
        get => GetValue(ModelProperty);
        set => SetValue(ModelProperty, value);
    }

    static HmiTimelineView() => AffectsRender<HmiTimelineView>(ModelProperty);

    // 原型常量：键 32 + 间距 8 + 内边距 13 → 轨道从 53 起；行高 26、行距 5
    public const double Pad = 13, KeyW = 32, Gap = 8, RowH = 26, RowGap = 5;

    private static readonly Typeface Face = new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    private static FormattedText T(string s, double size, IBrush b)
        => new(s, System.Globalization.CultureInfo.InvariantCulture,
               FlowDirection.LeftToRight, Face, size, b);

    public override void Render(DrawingContext ctx)
    {
        if (Model is not { } m) return;
        var mut = new SolidColorBrush(Color.Parse("#6F6F6F"));
        var x0 = Pad + KeyW + Gap;

        void Row(string keyText, IReadOnlyList<HmiTlSeg> segs, double y, bool temp)
        {
            var kf = T(keyText, 11, mut);
            ctx.DrawText(kf, new Point(Pad + KeyW - kf.Width, y + RowH / 2 - kf.Height / 2));
            var x = x0;
            foreach (var s in segs)
            {
                var rect = new Rect(x, y, Math.Max(0, s.W), RowH);
                var bg = new SolidColorBrush(Color.Parse(temp ? "#E4F4F4" : "#ECECEC"));
                var faded = s.Past ? (IDisposable?)ctx.PushOpacity(0.4) : null;
                ctx.DrawRectangle(bg, null, new RoundedRect(rect, 2));
                if (s.Zip)
                {
                    // 压缩段的斜纹（原型 repeating-linear-gradient 135°）
                    using var _ = ctx.PushClip(rect);
                    var pen = new Pen(new SolidColorBrush(Color.Parse("#528E8E8E")), 1);
                    for (var d = -RowH; d < s.W + RowH; d += 4)
                        ctx.DrawLine(pen, new Point(x + d, y + RowH), new Point(x + d + RowH, y));
                }
                else if (s.Text.Length > 0)
                {
                    var tf = T(s.Text, 11, new SolidColorBrush(Color.Parse(temp ? "#2F8189" : "#1A1A1A")));
                    if (tf.Width <= s.W - 10)
                        ctx.DrawText(tf, new Point(x + s.W - 6 - tf.Width, y + RowH / 2 - tf.Height / 2));
                }
                faded?.Dispose();
                x += s.W + 1;
            }
        }

        var y1 = 11.0;
        var y2 = y1 + RowH + RowGap;
        Row("温度", m.TempRow, y1, temp: true);
        Row("搅拌", m.StirRow, y2, temp: false);

        // 刻度行
        var ay = y2 + RowH + RowGap;
        var tick = new SolidColorBrush(Color.Parse("#C9C9C9"));
        foreach (var (tx, label) in m.Ticks)
        {
            ctx.FillRectangle(tick, new Rect(x0 + tx, ay, 1, 4));
            var tf = T(label, 10, mut);
            var lx = x0 + tx - tf.Width / 2;
            if (lx + tf.Width > Bounds.Width - 4) lx = x0 + tx - tf.Width * 0.92;
            ctx.DrawText(tf, new Point(lx, ay + 5));
        }

        // 运行游标（绿，贯穿两行）
        if (m.CursorX is { } cx)
            ctx.DrawRectangle(new SolidColorBrush(Color.Parse("#2F6B38")), null,
                new RoundedRect(new Rect(x0 + cx - 1, y1, 2, RowH * 2 + RowGap), 1));
    }
}
