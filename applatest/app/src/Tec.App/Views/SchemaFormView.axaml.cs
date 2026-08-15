using Avalonia.Controls;
using Avalonia.Interactivity;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class SchemaFormView : UserControl
{
    public SchemaFormView() => InitializeComponent();

    private void OnAddRow(object? sender, RoutedEventArgs e)
        => (DataContext as SchemaFormViewModel)?.AddRow();
}
