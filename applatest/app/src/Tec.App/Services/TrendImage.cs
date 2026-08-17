using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Tec.App.Controls;

namespace Tec.App.Services;

/// <summary>
/// 导出图上要写清楚的那些字。**图离开程序之后只剩这些字能解释它**——
/// 一张没有实验名、没有通道号、没有时间范围的曲线贴进报告里，
/// 谁也说不出它是哪一次实验的哪一路。
/// </summary>
public sealed class TrendCaption
{
    public required string Experiment { get; init; }
    public required string Channel { get; init; }
    /// <summary>「机A · A 孔」这种孔位说法，和台面 / 运行页共用一套。</summary>
    public string Well { get; init; } = "";
    public required string Window { get; init; }
    public required string Range { get; init; }
    public required string Points { get; init; }
    public required string ExportedAt { get; init; }
    public string Operator { get; init; } = "";
    /// <summary>
    /// 仿真跑出来的数据必须标出来，绝不能和真实数据混进一份报告（§8.1）。
    /// 这是整张图上最要紧的一行字，所以它单独占一块红底。
    /// </summary>
    public bool Simulated { get; init; }
    public bool HasPh { get; init; }
}

/// <summary>
/// 把趋势图存成 PNG。
///
/// 不是截屏：另建一份离屏的可视树，把说明文字和图例一起排进去，2× 渲染。
/// 截屏会把窗口的缩放、遮挡、当时的滚动位置一起带走，而且拿不到高分辨率。
/// </summary>
public static class TrendImage
{
    private const double W = 1040, H = 520;

    public static void Save(string path, TrendModel model, TrendCaption cap, double scale = 2)
    {
        var root = Build(model, cap);
        var size = new Size(W, H);
        root.Measure(size);
        root.Arrange(new Rect(size));
        root.UpdateLayout();

        var px = new PixelSize((int)Math.Round(W * scale), (int)Math.Round(H * scale));
        using var bmp = new RenderTargetBitmap(px, new Vector(96 * scale, 96 * scale));
        bmp.Render(root);
        bmp.Save(path);
    }

    private static Control Build(TrendModel model, TrendCaption cap)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Background = Brushes.White,
            Width = W,
            Height = H
        };

        grid.Children.Add(Head(cap));
        var legend = Legend(cap);
        Grid.SetRow(legend, 1);
        grid.Children.Add(legend);

        // 图本身用的就是界面上那个控件，一笔一画完全一致——
        // 另写一份导出专用的画法，迟早和屏幕上看到的对不上
        var chart = new Border
        {
            Margin = new Thickness(18, 2, 18, 6),
            BorderBrush = Ink("#dcdcdc"),
            BorderThickness = new Thickness(1),
            Background = Brushes.White,
            ClipToBounds = true,
            Child = new TrendView { Model = model }
        };
        Grid.SetRow(chart, 2);
        grid.Children.Add(chart);

        var foot = Foot(cap);
        Grid.SetRow(foot, 3);
        grid.Children.Add(foot);
        return grid;
    }

    private static Control Head(TrendCaption cap)
    {
        var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 3 };
        rows.Children.Add(Line(cap.Experiment, 17, "#2b2b2b", FontWeight.SemiBold));

        var sub = new WrapPanel { Orientation = Orientation.Horizontal };
        void Chip(string text, string ink, string fill, string line)
            => sub.Children.Add(new Border
            {
                Background = Ink(fill),
                BorderBrush = Ink(line),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 1, 9, 2),
                Margin = new Thickness(0, 0, 7, 0),
                Child = Line(text, 11.5, ink)
            });

        Chip(cap.Channel + (cap.Well.Length > 0 ? $" · {cap.Well}" : ""), "#1f4e79", "#e8f2fb", "#bcd7ec");
        Chip("窗口 " + cap.Window, "#444444", "#f4f4f4", "#e0e0e0");
        Chip(cap.Range, "#444444", "#f4f4f4", "#e0e0e0");
        Chip(cap.Points, "#444444", "#f4f4f4", "#e0e0e0");
        if (cap.Simulated) Chip("仿真运行 · 非真实实验数据", "#c0392b", "#fbe6e3", "#e0b4ad");
        rows.Children.Add(sub);

        return new Border { Padding = new Thickness(18, 14, 18, 8), Child = rows };
    }

    private static Control Legend(TrendCaption cap)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            Margin = new Thickness(18, 0, 18, 6)
        };
        void Item(string color, string text, bool dash = false)
        {
            var one = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            one.Children.Add(new Rectangle
            {
                Width = 16,
                Height = 3,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = Ink(color),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = dash ? 0.75 : 1
            });
            one.Children.Add(Line(text, 11, "#444444"));
            row.Children.Add(one);
        }
        Item("#e02020", "Tr 釜内");
        Item("#3f6fd8", "Tj 夹套");
        Item("#b56cc9", "Tr−Tj");
        if (cap.HasPh) Item("#2f8f49", "pH", dash: true);
        return row;
    }

    private static Control Foot(TrendCaption cap)
    {
        var text = $"导出于 {cap.ExportedAt}"
                   + (cap.Operator.Length > 0 ? $" · 操作人 {cap.Operator}" : "")
                   + " · TecStudio";
        return new Border
        {
            BorderBrush = Ink("#ececec"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(18, 0, 18, 0),
            Padding = new Thickness(0, 6, 0, 10),
            Child = Line(text, 10.5, "#8d8d8d")
        };
    }

    /// <summary>
    /// 离屏的可视树拿不到 App.axaml 里那些样式（它不在窗口上），
    /// 所以字体、字号、颜色一律显式写死——少写一个就退回 Fluent 的默认值。
    /// </summary>
    private static TextBlock Line(string text, double size, string color,
                                  FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei, system-ui, Arial"),
        Foreground = Ink(color),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static IBrush Ink(string hex) => new SolidColorBrush(Color.Parse(hex));
}
