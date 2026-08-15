using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RunView : UserControl
{
    private RunViewModel? _vm;

    public RunView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (_vm is not null) _vm.GanttChanged -= OnGanttChanged;
        _vm = DataContext as RunViewModel;
        if (_vm is null) return;
        _vm.GanttChanged += OnGanttChanged;
        OnGanttChanged(this, EventArgs.Empty);
    }

    private void OnGanttChanged(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        Gantt.Model = GanttBuilder.Build(_vm.Workspace, _vm.WallClockAxis);
    }

    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RunViewModel vm && sender is Control { DataContext: ChannelTileViewModel tile })
            vm.Selected = tile;
    }
}
