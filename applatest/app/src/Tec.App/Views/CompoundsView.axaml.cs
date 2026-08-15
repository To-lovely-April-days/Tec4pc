using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class CompoundsView : UserControl
{
    public CompoundsView() => InitializeComponent();

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is CompoundsViewModel vm && sender is Control { DataContext: CompoundViewModel c })
            vm.Selected = c;
    }
}
