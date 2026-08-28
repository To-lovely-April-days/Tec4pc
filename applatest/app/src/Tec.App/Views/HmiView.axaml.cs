using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tec.App.ViewModels;

namespace Tec.App.Views;

public partial class HmiView : UserControl
{
    // 侧栏状态条与状态点的两态色（原型 .rstate / .dot）
    public static readonly IValueConverter RailStateBg =
        new FuncValueConverter<bool, IBrush>(run => new SolidColorBrush(Color.Parse(run ? "#2F8189" : "#C9C9C9")));
    public static readonly IValueConverter RailStateFg =
        new FuncValueConverter<bool, IBrush>(run => run ? Brushes.White : new SolidColorBrush(Color.Parse("#1A1A1A")));
    public static readonly IValueConverter DotFill =
        new FuncValueConverter<bool, IBrush>(run => new SolidColorBrush(Color.Parse(run ? "#2F6B38" : "#C9C9C9")));
    public static readonly IValueConverter HexBrush =
        new FuncValueConverter<string?, IBrush>(s => new SolidColorBrush(Color.Parse(s ?? "#000000")));
    /// <summary>颜色弹窗里选中的色块描一圈黑（原型 outline: 2px solid var(--ink)）。</summary>
    public static readonly IValueConverter PickOutline =
        new FuncValueConverter<bool, IBrush>(on => new SolidColorBrush(Color.Parse(on ? "#1A1A1A" : "#DCDCDC")));

    private readonly DispatcherTimer _tick;
    private readonly DispatcherTimer _anim;

    public HmiView()
    {
        InitializeComponent();

        // 与台面页同一套拍子纪律：1 s 读数拍常驻（窗口可见才走），
        // 66 ms 动画拍只在有桨在转的时候走
        _tick = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            if (IsEffectivelyVisible && Vm is { } vm) vm.Refresh();
        });
        _anim = new DispatcherTimer(TimeSpan.FromMilliseconds(66), DispatcherPriority.Background, (_, _) =>
        {
            if (!IsEffectivelyVisible || Vm is not { } vm || !vm.AnimTick(0.066)) _anim!.Stop();
        });

        // 点图（原型 data-gchart）：换算成「秒」交给视图模型开 gtap 弹窗
        if (this.FindControl<Tec.App.Controls.HmiChartView>("ZChart") is { } zc)
            zc.Tapped = sec => Vm?.ChartTapped(sec);
        // 点序列卡（表头换类型 / 值框改数）
        if (this.FindControl<Tec.App.Controls.HmiSeqChartView>("ZSeqChart") is { } sc)
            sc.Tapped = (card, key) => Vm?.Cur?.SeqCardTapped(card, key);

        AttachedToVisualTree += (_, _) => { _tick.Start(); _anim.Start(); };
        DetachedFromVisualTree += (_, _) => { _tick.Stop(); _anim.Stop(); };
        // 转速从 0 变正时动画拍要重新点火：读数拍每秒顺手看一眼
        _tick.Tick += (_, _) => { if (Vm is { } vm && !_anim.IsEnabled && vm.Zones.Any(z => z.RpmVal > 0)) _anim.Start(); };
    }

    private HmiViewModel? Vm => DataContext as HmiViewModel;

    // ── 导航 ────────────────────────────────────────────────────────
    private void OnNavOv(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "ov"; }
    private void OnNavZ0(object? s, RoutedEventArgs e) { if (Vm is { } v) { v.Page = "z0"; v.ZTab = "ctl"; } }
    private void OnNavZ1(object? s, RoutedEventArgs e) { if (Vm is { } v) { v.Page = "z1"; v.ZTab = "ctl"; } }
    private void OnNavFiles(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "files"; }
    private void OnNavSys(object? s, RoutedEventArgs e) { if (Vm is { } v) v.Page = "sys"; }
    private void OnZoneCard(object? s, PointerPressedEventArgs e)
    {
        if (Vm is { } v && s is Control { DataContext: HmiZoneViewModel z })
        { v.Page = z.Index == 1 ? "z0" : "z1"; v.ZTab = "ctl"; }
    }

    private void OnTabOvApp(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "app"; }
    private void OnTabOvSeq(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "seq"; }
    private void OnTabOvGra(object? s, RoutedEventArgs e) { if (Vm is { } v) v.OvTab = "gra"; }
    private void OnTabZCtl(object? s, RoutedEventArgs e) { if (Vm is { } v) v.ZTab = "ctl"; }
    private void OnTabZStub(object? s, RoutedEventArgs e)
    { if (Vm is { } v && s is Control { Tag: string t }) v.ZTab = t; }

    // ── 控制页交互 ──────────────────────────────────────────────────
    private void OnToggleArt(object? s, PointerPressedEventArgs e)
    { if (Vm is { } v) v.ArtOpen = !v.ArtOpen; }

    private void OnModeTr(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("Tr");
    private void OnModeTj(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("Tj");
    private void OnModeTrTj(object? s, RoutedEventArgs e) => Vm?.Cur?.SwitchMode("TrTj");

    private void OnTogTr(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleTrSensor(); e.Handled = true; }
    private void OnTogPh(object? s, PointerPressedEventArgs e) { Vm?.Cur?.TogglePhSensor(); e.Handled = true; }
    private void OnTogTemp(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleTemp(); e.Handled = true; }
    private void OnTogStir(object? s, PointerPressedEventArgs e) { Vm?.Cur?.ToggleStir(); e.Handled = true; }

    private void OnRadRate(object? s, PointerPressedEventArgs e) { Vm?.Cur?.SetRampBy("rate"); e.Handled = true; }
    private void OnRadDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.SetRampBy("dur"); e.Handled = true; }

    private void OnVbTarget(object? s, PointerPressedEventArgs e)
    { if (Vm?.Cur is { } z) z.OpenKeypad(z.TargetKey); e.Handled = true; }
    private void OnVbRate(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rate"); e.Handled = true; }
    private void OnVbDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("dur"); e.Handled = true; }
    private void OnVbRpm(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rpm"); e.Handled = true; }
    private void OnVbREnd(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rEnd"); e.Handled = true; }
    private void OnVbRDur(object? s, PointerPressedEventArgs e) { Vm?.Cur?.OpenKeypad("rDur"); e.Handled = true; }

    private void OnCommit(object? s, RoutedEventArgs e) => Vm?.Cur?.Commit();
    private void OnDiscard(object? s, RoutedEventArgs e) => Vm?.Cur?.Discard();
    private void OnStopSeq(object? s, RoutedEventArgs e) => Vm?.Cur?.StopSequence();

    private void OnActDose(object? s, RoutedEventArgs e) => Vm?.OpenAct("dose");
    private void OnActSample(object? s, RoutedEventArgs e) => Vm?.OpenAct("sample");
    private void OnActMarker(object? s, RoutedEventArgs e) => Vm?.OpenAct("marker");
    private void OnActNote(object? s, RoutedEventArgs e) => Vm?.OpenAct("note");
    private void OnActOk(object? s, RoutedEventArgs e) => Vm?.ActConfirm();
    private void OnActCancel(object? s, RoutedEventArgs e) => Vm?.ActCancel();

    // ── 键盘弹窗 ────────────────────────────────────────────────────
    private void OnKp(object? s, RoutedEventArgs e)
    { if (Vm is { } v && s is Button { Content: string d }) v.KpPress(d); }
    private void OnKpBack(object? s, RoutedEventArgs e) => Vm?.KpBack();
    private void OnKpOk(object? s, RoutedEventArgs e) => Vm?.KpOk();
    private void OnKpCancel(object? s, RoutedEventArgs e) => Vm?.KpCancel();

    /// <summary>点遮罩空白处关弹窗；点弹窗本体不算（OnDlgBody 把事件吃掉）。</summary>
    private void OnMask(object? s, PointerPressedEventArgs e)
    { Vm?.KpCancel(); Vm?.ActCancel(); Vm?.GtCancel(); Vm?.TColClose(); Vm?.TyClose(); }
    private void OnDlgBody(object? s, PointerPressedEventArgs e) => e.Handled = true;

    // ── 趋势曲线页 ──────────────────────────────────────────────────

    private void OnTrendToggle(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string k }) Vm?.Cur?.ToggleTrend(k); }

    private void OnGraPanL(object? s, RoutedEventArgs e) => Vm?.Cur?.GraPan(-1);
    private void OnGraPanR(object? s, RoutedEventArgs e) => Vm?.Cur?.GraPan(+1);
    private void OnGraZoomIn(object? s, RoutedEventArgs e) => Vm?.Cur?.GraZoom(0.5);
    private void OnGraZoomOut(object? s, RoutedEventArgs e) => Vm?.Cur?.GraZoom(2);
    private void OnGraReset(object? s, RoutedEventArgs e) => Vm?.Cur?.GraReset();
    private void OnGraAxis(object? s, RoutedEventArgs e) => Vm?.Cur?.GraAxisToggle();

    private void OnTColOpen(object? s, RoutedEventArgs e) => Vm?.OpenTCol();
    private void OnTColClose(object? s, RoutedEventArgs e) => Vm?.TColClose();
    private void OnTColPick(object? s, PointerPressedEventArgs e)
    {
        if (Vm is { } v && s is Control { DataContext: HmiViewModel.TColCell c })
            v.TColPick(c.Key, c.Hex);
        e.Handled = true;
    }

    // ── 任务序列页 ──────────────────────────────────────────────────
    private void OnStartSeq(object? s, RoutedEventArgs e) => Vm?.Cur?.StartSeq();
    private void OnClearSeq(object? s, RoutedEventArgs e) => Vm?.Cur?.ClearSeq();
    private void OnSeqLib(object? s, RoutedEventArgs e) => Vm?.GoLibrary();
    private void OnSeqSlot(object? s, PointerPressedEventArgs e)
    {
        if (s is Control { Tag: int i }) Vm?.Cur?.SeqSlotTapped(i);
        e.Handled = true;
    }
    private void OnTyPick(object? s, RoutedEventArgs e)
    { if (Vm is { } v && s is Control { DataContext: HmiViewModel.TyRow r }) v.TyPick(r.Key); }
    private void OnTyClose(object? s, RoutedEventArgs e) => Vm?.TyClose();

    // ── 反应釜与安全页 + 报警层 ─────────────────────────────────────
    private void OnSafRow(object? s, PointerPressedEventArgs e)
    {
        if (s is Control { Tag: string k } && k.Length > 0) Vm?.Cur?.EditSafRow(k);
        e.Handled = true;
    }

    private void OnAlarmDrill(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string k }) Vm?.AlarmDrillOpen(k); }

    private void OnAlarmAck(object? s, RoutedEventArgs e) => Vm?.AlarmAck();
    private void OnAlarmAckAll(object? s, RoutedEventArgs e) => Vm?.AlarmAckAll();

    // ── 数据导出 / 文件 / 系统页 ────────────────────────────────────
    private void OnExpInt(object? s, PointerPressedEventArgs e) { Vm?.ExpCycleInt(); e.Handled = true; }
    private void OnExpRow(object? s, PointerPressedEventArgs e)
    { if (s is Control { Tag: string id }) Vm?.ExpSelect(id); e.Handled = true; }
    private void OnExpDo(object? s, RoutedEventArgs e) => Vm?.ExpDo();

    private void OnFileRow(object? s, PointerPressedEventArgs e)
    { if (s is Control { Tag: string id }) Vm?.FileSelect(id); e.Handled = true; }
    private void OnFileApply(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string n } && int.TryParse(n, out var i)) Vm?.FileApply(i); }
    private void OnFileSave(object? s, RoutedEventArgs e)
    { if (s is Control { Tag: string n } && int.TryParse(n, out var i)) Vm?.FileSave(i); }
    private void OnFileDelete(object? s, RoutedEventArgs e) => Vm?.FileDelete();

    private void OnGtCancel(object? s, RoutedEventArgs e) => Vm?.GtCancel();
    private void OnGtapMark(object? s, RoutedEventArgs e) => Vm?.GtapMark();
    private void OnGtapNote(object? s, RoutedEventArgs e) => Vm?.GtapNote();

    /// <summary>拍快照 / 导出快照：把图渲染成 PNG（导出再带一份窗口内原始 CSV）。</summary>
    private void OnSnap(object? s, RoutedEventArgs e) => Snapshot(withCsv: false);
    private void OnExpSnap(object? s, RoutedEventArgs e) => Snapshot(withCsv: true);

    private void Snapshot(bool withCsv)
    {
        if (Vm is not { } vm || vm.Cur is not { } z) return;
        var chart = this.FindControl<Tec.App.Controls.HmiChartView>("ZChart");
        if (chart is null || chart.Bounds.Width < 50 || z.GraModel is null)
        { vm.Toast("图上还没有内容可拍"); return; }
        try
        {
            var dir = System.IO.Path.Combine(Tec.App.Services.ExperimentStore.DataDir, "Snapshots");
            System.IO.Directory.CreateDirectory(dir);
            var baseName = $"HMI-CH{z.Number}-{DateTime.Now:yyyyMMdd-HHmmss}";
            // 2 倍渲染：这张图是要进报告/发人的，1280 屏上的 1 倍图放大就糊
            var px = new Avalonia.PixelSize((int)(chart.Bounds.Width * 2), (int)(chart.Bounds.Height * 2));
            using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(px, new Avalonia.Vector(192, 192));
            rtb.Render(chart);
            var png = System.IO.Path.Combine(dir, baseName + ".png");
            rtb.Save(png);
            var csv = withCsv ? z.ExportWindowCsv(dir, baseName) : null;
            vm.Toast(csv is null ? $"快照已保存：{png}" : $"已导出：{png} + 同名 .csv");
        }
        catch (Exception ex) { vm.Toast("保存失败：" + ex.Message); }
    }
}
