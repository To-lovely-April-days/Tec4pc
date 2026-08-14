using System.ComponentModel;
using System.Windows;
using ScottPlot;
using ScottPlot.Plottables;
using TecControl.App.ViewModels;
using TecControl.Core.Control;

namespace TecControl.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private DataLogger _t1 = null!, _t2 = null!, _d1 = null!, _sp = null!, _inner = null!;
    private DateTime _chartStart = DateTime.Now;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        SetupPlots();
        _vm.CycleArrived += OnCycle;
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void SetupPlots()
    {
        foreach (var plot in new[] { TempPlot, OutPlot })
        {
            plot.Plot.Clear();
            plot.Plot.Font.Automatic(); // 自动选择支持中文的字体
            plot.Plot.Axes.Bottom.Label.Text = "时间 (s)";
            plot.Plot.Legend.IsVisible = true;
            plot.Plot.Legend.Alignment = Alignment.UpperRight;
        }

        TempPlot.Plot.Axes.Left.Label.Text = "温度 (℃)";
        _t1 = TempPlot.Plot.Add.DataLogger();
        _t1.LegendText = "TC1 内部";
        _t1.ManageAxisLimits = false;
        _t2 = TempPlot.Plot.Add.DataLogger();
        _t2.LegendText = "TC2 腔体";
        _t2.ManageAxisLimits = false;
        _sp = TempPlot.Plot.Add.DataLogger();
        _sp.LegendText = "主设定值";
        _sp.ManageAxisLimits = false;
        _sp.LineStyle.Pattern = LinePattern.Dashed;
        _inner = TempPlot.Plot.Add.DataLogger();
        _inner.LegendText = "内环设定（串级）";
        _inner.ManageAxisLimits = false;
        _inner.LineStyle.Pattern = LinePattern.Dotted;

        OutPlot.Plot.Axes.Left.Label.Text = "输出占空比 (%)";
        _d1 = OutPlot.Plot.Add.DataLogger();
        _d1.LegendText = "主环占空比（两路同步）";
        _d1.ManageAxisLimits = false;

        TempPlot.Refresh();
        OutPlot.Refresh();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.IsConnected) || !_vm.IsConnected) return;

        // 重新连接后清空曲线，时间轴归零
        _chartStart = DateTime.Now;
        foreach (var logger in new[] { _t1, _t2, _d1, _sp, _inner })
            logger.Data.Clear();
        TempPlot.Refresh();
        OutPlot.Refresh();
    }

    private void OnCycle(ControlCycleResult cycle)
    {
        var s = cycle.Snapshot;
        var t = (s.Timestamp - _chartStart).TotalSeconds;

        if (!double.IsNaN(s.Temp1C)) _t1.Add(t, s.Temp1C);
        if (!double.IsNaN(s.Temp2C)) _t2.Add(t, s.Temp2C);
        _d1.Add(t, cycle.Ch1.DutyPercent);

        // 控温运行时画出设定值轨迹（曲线模式下可直观看到降温曲线是否被跟上）
        if (cycle.Ch1.Active)
        {
            _sp.Add(t, cycle.Ch1.SetpointC);
            if (cycle.Ch1.Cascade && !double.IsNaN(cycle.Ch1.InnerSetpointC))
                _inner.Add(t, cycle.Ch1.InnerSetpointC);
        }

        TempPlot.Plot.Axes.AutoScale();
        OutPlot.Plot.Axes.AutoScale();
        TempPlot.Refresh();
        OutPlot.Refresh();
    }

    private void Window_Closing(object sender, CancelEventArgs e) => _vm.OnWindowClosing();
}
