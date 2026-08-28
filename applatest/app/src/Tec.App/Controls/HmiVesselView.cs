using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// HMI 手动控制面板上的反应釜示意图，1:1 复刻原型 HTLAB_HMI v54 的 vessel()：
/// viewBox −26 −14 271 318，电机 + 联轴节 + 法兰 + 夹套/釜体 + 液体 + 搅拌桨 +
/// Tr / pH 插入件 + 读数气泡 + 仪表位号（HT/TT/AT）+ 台面深色横带。
/// 几何坐标逐条照抄原型 SVG，别"顺手改好看"——1:1 是这一张图的全部要求。
/// 桨叶动画是 scaleX 1→0.2→1 的往复（原型 @keyframes sp，1.2 s ease-in-out），
/// 相位由外面的动画拍子推进（PaddlePhase 0..1），不转就静止。
/// </summary>
public sealed class HmiVesselView : Control
{
    // ── 设计坐标（原型 viewBox）──────────────────────────────────────
    private const double Vx = -26, Vy = -14, Vw = 271, Vh = 318;

    // ── 原型色板 ────────────────────────────────────────────────────
    private const string Ln = "#4A4A4A";      // --v-ln
    private const string Liq = "#16EDFF";     // --v-liq
    private const string Shell = "#ECECEC";   // --v-shell
    private const string Hi = "#F9F9F9";      // --v-hi
    private const string Surf = "#0C8E9E";    // --v-surf
    private const string DarkBand = "#3A3A3A";// --dark
    private const string Ink = "#1A1A1A";
    private const string Mut = "#6F6F6F";
    private const string Mut2 = "#C9C9C9";
    private const string Blue = "#4FB1B8";

    public static readonly StyledProperty<bool> RunningProperty =
        AvaloniaProperty.Register<HmiVesselView, bool>(nameof(Running));
    /// <summary>液面高低跟运行状态走（原型 s=190/203），是示意不是液位计。</summary>
    public bool Running { get => GetValue(RunningProperty); set => SetValue(RunningProperty, value); }

    public static readonly StyledProperty<double> RpmProperty =
        AvaloniaProperty.Register<HmiVesselView, double>(nameof(Rpm));
    /// <summary>实测转速：决定液面波幅（vt）与桨叶是否摆动。</summary>
    public double Rpm { get => GetValue(RpmProperty); set => SetValue(RpmProperty, value); }

    public static readonly StyledProperty<double> PaddlePhaseProperty =
        AvaloniaProperty.Register<HmiVesselView, double>(nameof(PaddlePhase));
    /// <summary>桨叶动画相位 0..1，由 66 ms 拍子推进；转速为 0 时无人推，图自然静止。</summary>
    public double PaddlePhase { get => GetValue(PaddlePhaseProperty); set => SetValue(PaddlePhaseProperty, value); }

    public static readonly StyledProperty<bool> ShowTrProperty =
        AvaloniaProperty.Register<HmiVesselView, bool>(nameof(ShowTr), true);
    public bool ShowTr { get => GetValue(ShowTrProperty); set => SetValue(ShowTrProperty, value); }

    public static readonly StyledProperty<bool> ShowPhProperty =
        AvaloniaProperty.Register<HmiVesselView, bool>(nameof(ShowPh));
    public bool ShowPh { get => GetValue(ShowPhProperty); set => SetValue(ShowPhProperty, value); }

    public static readonly StyledProperty<string> TrTextProperty =
        AvaloniaProperty.Register<HmiVesselView, string>(nameof(TrText), "—");
    public string TrText { get => GetValue(TrTextProperty); set => SetValue(TrTextProperty, value); }

    public static readonly StyledProperty<string?> TrSetTextProperty =
        AvaloniaProperty.Register<HmiVesselView, string?>(nameof(TrSetText));
    /// <summary>「设定 65.0 ℃」那行；null = Tr 不是控温对象，气泡矮一档、描边灰。</summary>
    public string? TrSetText { get => GetValue(TrSetTextProperty); set => SetValue(TrSetTextProperty, value); }

    public static readonly StyledProperty<string> PhTextProperty =
        AvaloniaProperty.Register<HmiVesselView, string>(nameof(PhText), "—");
    public string PhText { get => GetValue(PhTextProperty); set => SetValue(PhTextProperty, value); }

    public static readonly StyledProperty<int> ThermProperty =
        AvaloniaProperty.Register<HmiVesselView, int>(nameof(Therm));
    /// <summary>升温/降温状态（原型 v60）：1 = 升温夹套染暖色，-1 = 降温染冷色，其余原色。</summary>
    public int Therm { get => GetValue(ThermProperty); set => SetValue(ThermProperty, value); }

    static HmiVesselView()
    {
        AffectsRender<HmiVesselView>(RunningProperty, RpmProperty, PaddlePhaseProperty,
            ShowTrProperty, ShowPhProperty, TrTextProperty, TrSetTextProperty, PhTextProperty,
            ThermProperty);
    }

    public HmiVesselView() => ClipToBounds = true;

    private static readonly Typeface Face = new("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");

    private static IBrush B(string hex) => new SolidColorBrush(Color.Parse(hex));
    private static Pen P(string hex, double w) => new(new SolidColorBrush(Color.Parse(hex)), w)
    { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

    // Geometry.Parse 的结果按 d 串缓存（SvgArt 同款惯例）：Render 每帧都来，不缓存就是每帧重解析
    private static readonly Dictionary<string, Geometry> Geo = new(StringComparer.Ordinal);
    private static Geometry G(string d)
    {
        if (!Geo.TryGetValue(d, out var g)) Geo[d] = g = Geometry.Parse(d);
        return g;
    }

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 10 || h < 10) return;

        // preserveAspectRatio meet：等比缩放、居中（原型 svg 的默认行为）
        var sc = Math.Min(w / Vw, h / Vh);
        var ox = (w - Vw * sc) / 2 - Vx * sc;
        var oy = (h - Vh * sc) / 2 - Vy * sc;

        // 台面深色横带横贯整个控件宽度（原型 rect x=-700 w=1620，溢出 viewBox 画满容器）
        var bandTop = oy + 221 * sc;
        ctx.FillRectangle(B(DarkBand), new Rect(0, bandTop, w, 66 * sc));

        using var _ = ctx.PushTransform(Matrix.CreateScale(sc, sc) * Matrix.CreateTranslation(ox, oy));

        var ln24 = P(Ln, 2.4); var ln19 = P(Ln, 1.9); var ln18 = P(Ln, 1.8);
        var ln17 = P(Ln, 1.7); var ln16 = P(Ln, 1.6); var ln15 = P(Ln, 1.5); var ln11 = P(Ln, 1.1);

        // ── 搅拌电机 + 联轴节（与法兰之间 45 的裸轴）──
        ctx.DrawRectangle(B(Hi), ln24, new RoundedRect(new Rect(92.5, 2, 34, 45), 4.5));
        ctx.DrawRectangle(Brushes.White, ln11, new RoundedRect(new Rect(98, 7.5, 23, 34), 2.5));
        ctx.DrawRectangle(B(Shell), ln16, new RoundedRect(new Rect(101, 47, 17, 11), 2));

        // ── 釜盖法兰 + 两端接管 ──
        ctx.DrawRectangle(B(Shell), ln16, new Rect(40, 92, 16, 11));
        ctx.DrawLine(ln18, new Point(37, 92), new Point(59, 92));
        ctx.DrawRectangle(B(Shell), ln16, new Rect(164, 92, 16, 11));
        ctx.DrawLine(ln18, new Point(161, 92), new Point(183, 92));
        ctx.DrawRectangle(B(Shell), ln18, new RoundedRect(new Rect(33, 103, 153, 20), 2));

        // ── 夹套与釜体 ──（升温夹套染暖、降温染冷，原型 v60 的 th 三态）
        const string outer = "M41 123 V236 Q41 273 109.5 273 Q178 273 178 236 V123 Z";
        const string inner = "M49 123 V233 Q49 266 109.5 266 Q170 266 170 233 V123 Z";
        var jacket = Therm switch { 1 => "#EFDCCB", -1 => "#D8E7F2", _ => Shell };
        ctx.DrawGeometry(B(jacket), ln19, G(outer));
        ctx.DrawGeometry(Brushes.White, ln17, G(inner));

        // ── 液体（釜体内裁剪；液面高度与波幅照原型公式）──
        var s = Running ? 190.0 : 203.0;
        var vt = Rpm > 0 ? Math.Min(26, 10 + Rpm / 28) : 3;
        var liq = $"M49 {F(s)} Q109.5 {F(s + vt)} 170 {F(s)} V278 H49 Z";
        var surf = $"M49 {F(s)} Q109.5 {F(s + vt)} 170 {F(s)}";
        using (ctx.PushGeometryClip(G(inner)))
        {
            ctx.DrawGeometry(B(Liq), null, Geometry.Parse(liq));      // 液面动，不进缓存
            ctx.DrawGeometry(null, P(Surf, 2), Geometry.Parse(surf));
        }

        // ── 搅拌轴 + 桨（scaleX 往复摆，原点 109.5,233.5）──
        ctx.DrawLine(P(Ln, 3), new Point(109.5, 58), new Point(109.5, 239));
        var run = Rpm > 0;
        var sx = run ? 0.6 + 0.4 * Math.Cos(PaddlePhase * Math.PI * 2) : 1.0;
        var paddle = Matrix.CreateTranslation(-109.5, -233.5)
                     * Matrix.CreateScale(sx, 1)
                     * Matrix.CreateTranslation(109.5, 233.5);
        using (ctx.PushTransform(paddle))
        {
            ctx.DrawRectangle(Brushes.White, ln18, new RoundedRect(new Rect(73, 228, 34, 11), 5.5));
            ctx.DrawRectangle(Brushes.White, ln18, new RoundedRect(new Rect(112, 228, 34, 11), 5.5));
        }

        // ── Tr 温度探头（插入件，绕 84,216 转 −9.2°）──
        if (ShowTr)
        {
            using (ctx.PushTransform(About(-9.2, 84, 216)))
            {
                ctx.DrawRectangle(B(Hi), P(Ln, 2), new RoundedRect(new Rect(77, 16, 14, 36), 3.5));
                ctx.DrawRectangle(Brushes.White, ln11, new RoundedRect(new Rect(80.5, 21, 7, 26), 1.5));
                ctx.DrawRectangle(B(Shell), ln16, new RoundedRect(new Rect(78.5, 52, 11, 11), 1.5));
                ctx.DrawLine(P(Ln, 2.5), new Point(84, 63), new Point(84, 213));
                ctx.DrawEllipse(B(Ln), null, new Point(84, 215), 2.8, 2.8);
            }
        }

        // ── pH 玻璃电极（绕 138,216 转 +9.2°）──
        if (ShowPh)
        {
            using (ctx.PushTransform(About(9.2, 138, 216)))
            {
                ctx.DrawRectangle(B(Hi), P(Ln, 2), new RoundedRect(new Rect(131, 16, 14, 36), 3.5));
                ctx.DrawRectangle(Brushes.White, ln11, new RoundedRect(new Rect(134.5, 21, 7, 26), 1.5));
                ctx.DrawRectangle(B(Shell), ln16, new RoundedRect(new Rect(132.5, 52, 11, 11), 1.5));
                ctx.DrawLine(P(Ln, 3.5), new Point(138, 63), new Point(138, 209));
                ctx.DrawEllipse(Brushes.White, ln18, new Point(138, 213), 4.5, 4.5);
            }
        }

        // ── 读数气泡：锚在各自探头上 ──
        if (ShowTr)
        {
            var tgt = TrSetText is not null;
            ctx.DrawLine(ln15, new Point(42, 36), new Point(47.5, 36));
            ctx.DrawRectangle(Brushes.White, P(tgt ? Blue : Mut2, 2),
                new RoundedRect(new Rect(-20, tgt ? 16 : 22, 62, tgt ? 40 : 28), 3));
            Text(ctx, TrText, 11, tgt ? 30 : 36, 15, Ink);
            if (tgt) Text(ctx, TrSetText!, 11, 47, 10, Mut);
        }
        if (ShowPh)
        {
            ctx.DrawLine(ln15, new Point(174.5, 36), new Point(180, 36));
            ctx.DrawRectangle(Brushes.White, P(Mut2, 2), new RoundedRect(new Rect(180, 22, 52, 28), 3));
            Text(ctx, PhText, 206, 36, 15, Ink);
        }

        // ── 仪表位号 ──
        Text(ctx, "HT", 56, 180, 9, Mut, center: false);
        if (ShowPh)
        {
            ctx.DrawLine(ln15, new Point(161, 85), new Point(198, 85));
            ctx.DrawEllipse(Brushes.White, ln15, new Point(206, 85), 8, 8);
            Text(ctx, "AT", 206, 85, 7.5, Mut);
        }
        ctx.DrawRectangle(B(Shell), ln15, new Rect(29, 200, 12, 11));
        ctx.DrawLine(ln15, new Point(29, 205.5), new Point(23, 205.5));
        ctx.DrawEllipse(Brushes.White, ln15, new Point(15, 205.5), 8, 8);
        Text(ctx, "TT", 15, 205.5, 7.5, Mut);
    }

    private static string F(double v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static Matrix About(double deg, double cx, double cy)
        => Matrix.CreateTranslation(-cx, -cy)
           * Matrix.CreateRotation(deg * Math.PI / 180)
           * Matrix.CreateTranslation(cx, cy);

    /// <summary>SVG 的 text-anchor=middle + dominant-baseline=central：按量出来的尺寸手动居中。</summary>
    private static void Text(DrawingContext ctx, string s, double x, double y, double size,
                             string color, bool center = true)
    {
        var ft = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Face, size, B(color));
        ctx.DrawText(ft, new Point(center ? x - ft.Width / 2 : x, y - ft.Height / 2));
    }
}
