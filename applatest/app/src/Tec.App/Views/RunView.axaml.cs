using Avalonia.Controls;
using Avalonia.Input;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class RunView : UserControl
{
    public RunView()
    {
        InitializeComponent();
        // 台面总览随周期刷新重画（数据在管线里，控件只管画）
        var timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        timer.Tick += (_, _) => Deck.Refresh();
        timer.Start();
    }

    private void OnDrawHeadPressed(object? sender, PointerPressedEventArgs e)
        => (DataContext as RunViewModel)?.ToggleDraw.Execute(null);

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RunViewModel vm && sender is Control { DataContext: DrawChipViewModel chip })
        {
            vm.ToggleChip.Execute(chip);
            e.Handled = true;
        }
    }
}
