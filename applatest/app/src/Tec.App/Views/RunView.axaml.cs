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
        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    private RunViewModel? _hooked;

    /// <summary>
    /// 三块 pane 的折叠状态一变就重算列宽。
    /// ColumnDefinition 不在可视树上，拿不到 DataContext，绑不了——只能在这儿算。
    ///
    /// 台面总览的重画也挂在这儿：搭 RunViewModel 那趟表的车，不另起一个。
    /// 从前这里自己起了一个 700ms 的 DispatcherTimer——一进过运行页就永远跳下去，
    /// 翻到别的页也照跳，Deck 明明藏着还在一遍遍作废重画。
    /// 那正是那个视图模型自己的注释里写着「没必要」的第二个表。
    /// </summary>
    private void Hook()
    {
        if (ReferenceEquals(_hooked, DataContext)) return;
        if (_hooked is not null)
        {
            _hooked.BenchPane.PropertyChanged -= OnPane;
            _hooked.TrendPane.PropertyChanged -= OnPane;
            _hooked.GanttPane.PropertyChanged -= OnPane;
            _hooked.Ticked -= OnTicked;
        }
        _hooked = DataContext as RunViewModel;
        if (_hooked is null) return;
        _hooked.BenchPane.PropertyChanged += OnPane;
        _hooked.TrendPane.PropertyChanged += OnPane;
        _hooked.GanttPane.PropertyChanged += OnPane;
        _hooked.Ticked += OnTicked;
        Layout();
    }

    /// <summary>台面总览随节拍重画（数据在管线里，控件只管画）。藏着的时候不画。</summary>
    private void OnTicked(object? sender, EventArgs e)
    {
        if (IsEffectivelyVisible) Deck.Refresh();
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
            !bench ? tab : (!trend && !gantt) ? star : new GridLength(600);
        Body.ColumnDefinitions[2].Width = trend ? star : tab;
        Body.ColumnDefinitions[4].Width =
            !gantt ? tab : !trend ? star : new GridLength(430);
        // 第 5 列是改参数面板，Auto：关着的时候它整个 IsVisible=false，
        // 不参与测量，这一列自然收成 0，不用另算
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

    private void OnHotStepPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RunViewModel vm && sender is Control { DataContext: HotStepViewModel step })
        {
            vm.Edit.Selected = step;
            e.Handled = true;
        }
    }

    private void OnChipPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is RunViewModel vm && sender is Control { DataContext: DrawChipViewModel chip })
        {
            vm.ToggleChip.Execute(chip);
            e.Handled = true;
        }
    }
}
