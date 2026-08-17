using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tec.App.ViewModels;
using Tec.Core.Export;

namespace Tec.App.Views;

/// <summary>预览里的一页。缩放是每页各存一份——ItemsControl 的每一项各绑各的。</summary>
public sealed class PreviewPageViewModel : ViewModelBase
{
    private double _zoom = 1;

    public required ReportPage Page { get; init; }
    public required string FontName { get; init; }
    public required double W { get; init; }
    public required double H { get; init; }
    public double Zoom { get => _zoom; set => Set(ref _zoom, value); }
}

/// <summary>
/// 预览报告。
///
/// 画的是 <see cref="ReportLayout"/> 排出来的**那一批图元**，
/// 和导出的 PDF 是同一份排版：这里看到第 3 页在哪儿断，PDF 就在哪儿断。
/// 界面上另排一套的话，「预览」这两个字就是假的。
/// </summary>
public partial class ReportPreviewWindow : Window
{
    private readonly ObservableCollection<PreviewPageViewModel> _pages = new();
    private double _zoom = 1;

    public ReportPreviewWindow()
    {
        InitializeComponent();
        Pages.ItemsSource = _pages;
    }

    public static async Task Show(Window owner, ReportDoc doc, IReadOnlyList<ReportPage> pages,
                                  PageStyle style, string fontName, string note)
    {
        var w = new ReportPreviewWindow();
        w.Title = "预览报告 · " + doc.Title;
        w.TitleText.Text = "预览报告　·　" + doc.Title;
        w.TemplateText.Text = ReportTemplates.NameOf(doc.Template);
        w.CountText.Text = $"共 {pages.Count} 页 · A4 竖排";
        w.NoteText.Text = note;

        foreach (var p in pages)
            w._pages.Add(new PreviewPageViewModel
            { Page = p, FontName = fontName, W = style.Width, H = style.Height });

        w.Apply(1);
        await w.ShowDialog(owner);
    }

    private void Apply(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.4, 2.5);
        foreach (var p in _pages) p.Zoom = _zoom;
        ZoomText.Text = (_zoom * 100).ToString("0") + "%";
    }

    private void OnZoomIn(object? sender, RoutedEventArgs e) => Apply(_zoom + 0.15);
    private void OnZoomOut(object? sender, RoutedEventArgs e) => Apply(_zoom - 0.15);

    private void OnZoomFit(object? sender, RoutedEventArgs e)
    {
        if (_pages.Count == 0) return;
        // 减掉滚动条和左右留白，免得刚好「适合宽度」之后横向还要滚一点
        Apply((Scroll.Bounds.Width - 46) / _pages[0].W);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key is Key.OemPlus or Key.Add) Apply(_zoom + 0.15);
        else if (e.Key is Key.OemMinus or Key.Subtract) Apply(_zoom - 0.15);
    }

    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
