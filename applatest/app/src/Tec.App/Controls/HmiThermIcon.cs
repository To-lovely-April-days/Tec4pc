using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Tec.App.Controls;

/// <summary>
/// 升温/降温/恒温小图标（原型 v60 的 TH_G，逐条照抄）：升温橙热浪、
/// 降温蓝雪花、恒温绿等号。原型按 24 格 viewBox 设计再整体缩放——
/// 这里同样整体缩（连线宽一起），别用 Path 的 Stretch 按包围盒归一，
/// 三个图标长宽比不同会各缩各的。
/// </summary>
public sealed class HmiThermIcon : Control
{
    public static readonly StyledProperty<int> StateProperty =
        AvaloniaProperty.Register<HmiThermIcon, int>(nameof(State));
    /// <summary>0 = 不画；1 = 升温；-1 = 降温；2 = 恒温。</summary>
    public int State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }

    static HmiThermIcon() => AffectsRender<HmiThermIcon>(StateProperty);

    private static readonly Geometry Heat = Geometry.Parse(
        "M7 20.6c-2.3-2.8 2.3-4.9 0-7.7s2.3-4.9 0-7.7" +
        "M12 20.6c-2.3-2.8 2.3-4.9 0-7.7s2.3-4.9 0-7.7" +
        "M17 20.6c-2.3-2.8 2.3-4.9 0-7.7s2.3-4.9 0-7.7");
    private static readonly Geometry Cool = Geometry.Parse(
        "M12 2.8 V21.2 M4 7.4 L20 16.6 M4 16.6 L20 7.4" +
        "M9.6 5.2 L12 7.4 L14.4 5.2 M9.6 18.8 L12 16.6 L14.4 18.8");
    private static readonly Geometry Hold = Geometry.Parse("M6.5 10.4 H17.5 M6.5 13.6 H17.5");

    public override void Render(DrawingContext ctx)
    {
        var t = State;
        if (t == 0 || Bounds.Width < 4) return;
        var g = t switch { 1 => Heat, -1 => Cool, _ => Hold };
        var col = t switch { 1 => "#D9552B", -1 => "#2F7FD4", _ => "#2F6B38" };
        var k = Math.Min(Bounds.Width, Bounds.Height) / 24;
        using var _ = ctx.PushTransform(Matrix.CreateScale(k, k));
        ctx.DrawGeometry(null,
            new Pen(new SolidColorBrush(Color.Parse(col)), t == -1 ? 1.9 : 2.2)
            { LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, g);
    }
}
