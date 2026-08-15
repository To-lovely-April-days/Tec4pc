using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RecipeLibView : UserControl
{
    public RecipeLibView() => InitializeComponent();

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RecipeLibViewModel vm && sender is Control { DataContext: LibRowViewModel r })
            vm.Selected = r;
    }
}
