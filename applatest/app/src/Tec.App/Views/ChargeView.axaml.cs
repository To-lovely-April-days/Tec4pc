using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class ChargeView : UserControl
{
    public ChargeView() => InitializeComponent();

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is ChargeViewModel vm && sender is Control { DataContext: ChargeRowViewModel r })
            vm.Selected = r;
    }
}
