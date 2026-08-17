using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class ExportView : UserControl
{
    public ExportView() => InitializeComponent();

    private ExportViewModel? Vm => DataContext as ExportViewModel;

    /// <summary>
    /// 点一行 = 勾 / 取消勾。**点在那个复选框上不再走这条**（否则勾一下走两遍，
    /// 等于没勾）；复选框自己双向绑到 IsChecked，这里补一句让预览跟过去。
    /// </summary>
    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Control { DataContext: RunRowViewModel row }) return;
        vm.Toggle(row, checkedOnly: InCheckBox(e.Source as Visual));
    }

    /// <summary>
    /// 点到的是不是那个复选框（含它模板里的那几层）。只看 e.Source is CheckBox
    /// 不够：点在勾勾上时 Source 是模板内部的 Border，于是走了「行点击」那条路，
    /// 一下勾两遍，等于没勾。
    /// </summary>
    private static bool InCheckBox(Visual? v)
    {
        for (; v is not null; v = v.GetVisualParent())
            if (v is CheckBox) return true;
        return false;
    }

    private void OnFmtPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: FmtViewModel f }) vm.PickFmt.Execute(f);
    }

    private void OnTemplatePressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is { } vm && sender is Control { DataContext: TemplateViewModel t }) vm.PickTemplate.Execute(t);
    }

    private void OnDestLocal(object? sender, PointerPressedEventArgs e) { if (Vm is { } vm) vm.Dest = "local"; }
    private void OnDestUsb(object? sender, PointerPressedEventArgs e) { if (Vm is { } vm) vm.Dest = "usb"; }

    // ── 预览区高度：那条 6px 的分隔条拖出来 ──────────────────────────

    private bool _dragging;
    private double _y0, _h0;

    private void OnSplitPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Vm is not { } vm || sender is not Border b) return;
        _dragging = true;
        _y0 = e.GetPosition(this).Y;
        _h0 = vm.PrevHeight;
        e.Pointer.Capture(b);
    }

    private void OnSplitMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Vm is not { } vm) return;
        // 预览贴在底部：往上拖是变高，所以是减
        vm.PrevHeight = _h0 - (e.GetPosition(this).Y - _y0);
    }

    private void OnSplitReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }
}
