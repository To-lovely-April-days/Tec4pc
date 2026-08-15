using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();

    /// <summary>单击选中，双击打开——跟资源管理器一个习惯。</summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not StartViewModel vm ||
            sender is not Control { DataContext: RecentCardViewModel card }) return;

        vm.Pick.Execute(card);
        if (e.ClickCount >= 2) vm.OpenRecent.Execute(card);
    }
}
