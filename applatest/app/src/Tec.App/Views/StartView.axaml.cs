using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();

    /// <summary>
    /// 点一下就打开。这是开始页不是文件管理器——摆在这儿的卡片只有一个用途，
    /// 还要求双击等于多一步。图钉是独立的按钮，它自己吃掉点击，不会连带打开。
    /// </summary>
    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not StartViewModel vm ||
            sender is not Control { DataContext: RecentCardViewModel card }) return;

        vm.Pick.Execute(card);
        vm.OpenRecent.Execute(card);
    }
}
