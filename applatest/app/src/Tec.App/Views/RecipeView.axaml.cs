using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RecipeView : UserControl
{
    public RecipeView() => InitializeComponent();

    private void OnLanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RecipeViewModel vm && sender is Control { DataContext: LaneViewModel lane })
            vm.CurCh = lane.Channel;
    }

    private void OnStepPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not RecipeViewModel vm) return;
        if (sender is not Control { DataContext: StepViewModel step }) return;
        // 原型行为：点步骤先切到它所在的通道，再选中
        var lane = vm.Lanes.FirstOrDefault(l => l.Steps.Contains(step));
        if (lane is not null) vm.CurCh = lane.Channel;
        vm.SelectedStep = vm.Lanes.First(l => l.Channel == vm.CurCh)
                            .Steps.FirstOrDefault(s => s.Step.StepId == step.Step.StepId);
        e.Handled = true;
    }

    private void OnGroupBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: ModuleGroup g }) g.Open = !g.Open;
    }
}
