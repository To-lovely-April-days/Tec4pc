using System.ComponentModel;
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

        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    private RunViewModel? _hooked;

    /// <summary>
    /// 三块 pane 的折叠状态一变就重算列宽。
    /// ColumnDefinition 不在可视树上，拿不到 DataContext，绑不了——只能在这儿算。
    /// </summary>
    private void Hook()
    {
        if (ReferenceEquals(_hooked, DataContext)) return;
        if (_hooked is not null)
        {
            _hooked.BenchPane.PropertyChanged -= OnPane;
            _hooked.TrendPane.PropertyChanged -= OnPane;
            _hooked.GanttPane.PropertyChanged -= OnPane;
        }
        _hooked = DataContext as RunViewModel;
        if (_hooked is null) return;
        _hooked.BenchPane.PropertyChanged += OnPane;
        _hooked.TrendPane.PropertyChanged += OnPane;
        _hooked.GanttPane.PropertyChanged += OnPane;
        Layout();
    }

    private void OnPane(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SectionViewModel.Open)) Layout();
    }

    /// <summary>
    /// 收起的那块只留 28px 竖标签，腾出来的宽度给还开着的那块。
    /// 优先给趋势曲线（它是唯一会随窗口伸缩的一块）；趋势也收了就给甘特，
    /// 再没有就给台面。三块都收起来时就三条竖标签靠左排着，右边留白——
    /// 那是操作人自己收的，不该硬撑出一块空面板。
    /// </summary>
    private void Layout()
    {
        if (_hooked is null) return;
        var bench = _hooked.BenchPane.Open;
        var trend = _hooked.TrendPane.Open;
        var gantt = _hooked.GanttPane.Open;

        var tab = GridLength.Auto;                       // 收起 = 只剩竖标签，宽度由内容定
        var star = new GridLength(1, GridUnitType.Star);

        Body.ColumnDefinitions[0].Width =
            !bench ? tab : (!trend && !gantt) ? star : new GridLength(560);
        Body.ColumnDefinitions[2].Width = trend ? star : tab;
        Body.ColumnDefinitions[4].Width =
            !gantt ? tab : !trend ? star : new GridLength(430);
    }

    private void OnTilePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RunViewModel vm && sender is Control { DataContext: StatTileViewModel tile })
        {
            vm.Selected = tile;
            e.Handled = true;
        }
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
