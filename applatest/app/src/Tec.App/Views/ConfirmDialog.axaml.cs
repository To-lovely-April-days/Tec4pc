using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Tec.App.Views;

/// <summary>操作人在确认框上选了什么。</summary>
public enum DialogChoice
{
    /// <summary>主按钮（绿色那个）。</summary>
    Primary,
    /// <summary>次按钮，通常是「不保存」这类放弃动作。</summary>
    Secondary,
    /// <summary>取消：什么都不做，回到原来的状态。关窗、Esc 都算这个。</summary>
    Cancel
}

/// <summary>
/// 确认框。不用系统 MessageBox——那东西长得跟程序完全两样，
/// 而且没法自绘圆角与紫色标题栏。样式跟主窗口一套。
/// </summary>
public partial class ConfirmDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    public ConfirmDialog() => InitializeComponent();

    /// <summary>
    /// 弹一个确认框并等操作人选。secondary 传 null 就只有「主按钮 + 取消」两个。
    /// 关窗或 Esc 一律当取消——问「要不要存」时，误关不该等于放弃数据。
    /// </summary>
    public static async Task<DialogChoice> Ask(Window owner, string title, string message,
                                               string? detail, string primary,
                                               string? secondary, string cancel = "取消")
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.Title = title;
        dlg.MessageText.Text = message;
        dlg.DetailText.Text = detail ?? "";
        dlg.DetailText.IsVisible = !string.IsNullOrWhiteSpace(detail);
        dlg.PrimaryBtn.Content = primary;
        dlg.SecondaryBtn.Content = secondary ?? "";
        dlg.SecondaryBtn.IsVisible = secondary is not null;
        dlg.CancelBtn.Content = cancel;
        await dlg.ShowDialog(owner);
        return dlg._choice;
    }

    private void Done(DialogChoice choice)
    {
        _choice = choice;
        Close();
    }

    private void OnPrimary(object? sender, RoutedEventArgs e) => Done(DialogChoice.Primary);
    private void OnSecondary(object? sender, RoutedEventArgs e) => Done(DialogChoice.Secondary);
    private void OnCancel(object? sender, RoutedEventArgs e) => Done(DialogChoice.Cancel);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // 回车 = 主按钮，Esc = 取消。手不用离开键盘
        if (e.Key == Key.Escape) Done(DialogChoice.Cancel);
        else if (e.Key is Key.Enter or Key.Return) Done(DialogChoice.Primary);
    }

    /// <summary>自绘标题栏要自己负责拖动。</summary>
    private void OnTitlebarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }
}
