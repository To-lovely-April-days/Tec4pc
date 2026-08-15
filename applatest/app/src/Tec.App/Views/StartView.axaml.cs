using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class StartView : UserControl
{
    public StartView() => InitializeComponent();

    private void OnCardPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is StartViewModel vm && sender is Control { DataContext: RecentCardViewModel card })
            vm.Pick.Execute(card);
    }
}
