using Avalonia.Controls;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RecipeView : UserControl
{
    public RecipeView() => InitializeComponent();

    private void OnCommandSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is RecipeViewModel vm && sender is ListBox { SelectedItem: CommandItemViewModel item })
            vm.SelectedCommand = item;
    }
}
