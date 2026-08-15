using System.Collections.ObjectModel;
using System.Text;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Export;
using Tec.Core.Records;

namespace Tec.App.ViewModels;

/// <summary>实验记录列表的一行（原型 RUNS + .rrow.one）。第 0 条是本次正在运行的批次。</summary>
public sealed class RunEntryViewModel : ViewModelBase
{
    private bool _sel;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Start { get; init; }
    public required int DurSec { get; init; }
    public required IReadOnlyList<int> Chs { get; init; }
    public required string RecipeName { get; init; }
    public required string User { get; init; }
    public required string State { get; init; }
    public IReadOnlyList<string> Probes { get; init; } = Array.Empty<string>();
    public int Alarms { get; init; }
    public int Marks { get; init; }
    /// <summary>各通道 [启动偏移, 时长]；null = 该通道本次未启动。</summary>
    public required IReadOnlyDictionary<int, (int Off, int Dur)?> Spans { get; init; }
    public bool Live { get; init; }

    public bool IsSel { get => _sel; set => Set(ref _sel, value); }
    public string Tip => $"{Start} · {Chs.Count} 通道 · {Hms(DurSec)} · {State}";
    public static string Hms(int s) => $"{s / 3600:D2}:{s % 3600 / 60:D2}:{s % 60:D2}";
}

public sealed class MetaPair
{
    public required string K { get; init; }
    public required string V { get; init; }
}

public sealed class ExChipViewModel : ViewModelBase
{
    private bool _on;
    public required int Ch { get; init; }
    public required bool Available { get; init; }
    public string? Tip { get; init; }
    public string ColorHex => Ch switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };
    public string Label => Available ? $"通道 {Ch}" : $"通道 {Ch}（未启动）";
    public bool On { get => _on; set => Set(ref _on, value); }
}

public sealed class DataItemViewModel : ViewModelBase
{
    private bool _on;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Unit { get; init; } = "";
    /// <summary>依赖的探头名（原型 it[3]）；空 = 总是可用。</summary>
    public string Needs { get; init; } = "";
    public required bool Available { get; init; }
    public bool HasUnit => Unit.Length > 0;
    public string Tip => Available ? "" : $"台面未配置{Needs}探头";
    public bool On { get => _on; set => Set(ref _on, value); }
}

public sealed class DataGroupViewModel
{
    public required string Name { get; init; }
    public ObservableCollection<DataItemViewModel> Items { get; } = new();
}

public sealed class FmtViewModel : ViewModelBase
{
    private bool _on;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Desc { get; init; }
    public string IconKey => "ui-fmt-" + Key;
    public bool On { get => _on; set => Set(ref _on, value); }
}

public sealed class PrevRow
{
    public required IReadOnlyList<string> Cells { get; init; }
}

/// <summary>
/// 数据导出（原型 view-export 的 1:1）：
/// 左 实验记录列表 ｜ 中 exhead + 通道 / 数据项 / 时间范围 / 预览 ｜ 右 342px 导出设置。
/// 第 0 条实验 = 本次运行的批次（实时数据）；归档实验的记录由记录文件读取，原型不伪造，
/// 这里同样给占位说明。
/// </summary>
public sealed class ExportViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private int _curRun;
    private string _range = "全程";
    private string _interval = "10 s";
    private string _baseLabel = "绝对时间";
    private bool _prevData = true;
    private string _dest = "local";
    private string _from = "00:00:00";
    private string _to = "06:00:12";

    private static readonly string[] DefOn =
        { "Tr", "Tj", "dT", "Tset", "pH", "rpm", "flow", "vol", "turb", "steps" };

    public ExportViewModel(Workspace ws)
    {
        _ws = ws;

        // 原型 RUNS：第 0 条为正在运行的批次，其余三条为归档演示数据
        Runs.Add(new RunEntryViewModel
        {
            Id = "EXP-20260814-001", Name = "降温结晶_梯度筛选_0814", Start = "2026-08-14 09:12",
            DurSec = 29915, Chs = new[] { 1, 2, 3, 4 }, RecipeName = "降温结晶_梯度筛选",
            User = "管理员", State = "运行中", Probes = new[] { "pH", "拉曼", "浊度" },
            Alarms = 1, Marks = 6, Live = true,
            Spans = new Dictionary<int, (int, int)?>
            { [1] = (0, 28655), [2] = (360, 10655), [3] = (1260, 28655), [4] = null }
        });
        Runs.Add(new RunEntryViewModel
        {
            Id = "EXP-20260812-003", Name = "硝化_控温加料_0812", Start = "2026-08-12 17:40",
            DurSec = 14400, Chs = new[] { 1, 2 }, RecipeName = "硝化_控温加料",
            User = "管理员", State = "已完成", Probes = new[] { "pH" }, Alarms = 2, Marks = 3,
            Spans = new Dictionary<int, (int, int)?> { [1] = (0, 14400), [2] = (900, 13200) }
        });
        Runs.Add(new RunEntryViewModel
        {
            Id = "EXP-20260810-002", Name = "溶解度曲线_自动测定", Start = "2026-08-10 14:03",
            DurSec = 28800, Chs = new[] { 1 }, RecipeName = "溶解度曲线_自动",
            User = "张工", State = "已完成", Probes = new[] { "浊度" }, Alarms = 0, Marks = 12,
            Spans = new Dictionary<int, (int, int)?> { [1] = (0, 28800) }
        });
        Runs.Add(new RunEntryViewModel
        {
            Id = "EXP-20260806-001", Name = "介稳区_自动测定", Start = "2026-08-06 08:55",
            DurSec = 36000, Chs = new[] { 1, 2, 3, 4 }, RecipeName = "介稳区_自动测定",
            User = "张工", State = "已完成", Probes = new[] { "浊度", "拉曼" }, Alarms = 1, Marks = 20,
            Spans = new Dictionary<int, (int, int)?>
            { [1] = (0, 34200), [2] = (600, 33600), [3] = (600, 33600), [4] = (1800, 30600) }
        });

        foreach (var f in new (string, string, string)[]
        {
            ("csv", "CSV", "逐点数据表"), ("xlsx", "Excel", "每通道一页"),
            ("docx", "Word", "排版报告"), ("pdf", "PDF", "含曲线与步骤")
        })
            Fmts.Add(new FmtViewModel { Key = f.Item1, Name = f.Item2, Desc = f.Item3, On = f.Item1 == "csv" });

        PickRun = new RelayCommand(p => { if (p is RunEntryViewModel r) SelectRun(r); });
        ToggleCh = new RelayCommand(p =>
        {
            if (p is ExChipViewModel c && c.Available) { c.On = !c.On; Refresh(); }
        });
        AllCh = new RelayCommand(() =>
        {
            var chips = Chips.Where(c => c.Available).ToList();
            var target = !chips.All(c => c.On);
            foreach (var c in chips) c.On = target;
            Refresh();
        });
        PickFmt = new RelayCommand(p =>
        {
            if (p is not FmtViewModel f) return;
            foreach (var x in Fmts) x.On = x == f;
            RaiseAll(nameof(FileNote), nameof(ReportEnabled));
            Refresh();
        });
        SetPrev = new RelayCommand(p => { PrevData = Convert.ToString(p) == "data"; });
        DoExport = new RelayCommand(Export);
        SelectRun(Runs[0]);
    }

    public ObservableCollection<RunEntryViewModel> Runs { get; } = new();
    public ObservableCollection<MetaPair> Meta { get; } = new();
    public ObservableCollection<ExChipViewModel> Chips { get; } = new();
    public ObservableCollection<DataGroupViewModel> Groups { get; } = new();
    public ObservableCollection<FmtViewModel> Fmts { get; } = new();
    public ObservableCollection<PrevRow> PrevRows { get; } = new();
    public ObservableCollection<string> PrevHeader { get; } = new();
    public ObservableCollection<MetaPair> Sum { get; } = new();

    public RelayCommand PickRun { get; }
    public RelayCommand ToggleCh { get; }
    public RelayCommand AllCh { get; }
    public RelayCommand PickFmt { get; }
    public RelayCommand SetPrev { get; }
    public RelayCommand DoExport { get; }

    public RunEntryViewModel Cur => Runs[_curRun];
    public string ExName => Cur.Name;

    public ObservableCollection<string> RangeOptions { get; } = new() { "全程", "自定义时段" };
    public ObservableCollection<string> IntervalOptions { get; } = new() { "原始（1 s）", "5 s", "10 s", "1 min" };
    public ObservableCollection<string> BaseOptions { get; } = new() { "绝对时间", "相对实验开始", "相对通道启动" };

    public string Range { get => _range; set { if (Set(ref _range, value)) Refresh(); } }
    public string Interval { get => _interval; set { if (Set(ref _interval, value)) Refresh(); } }
    public string FromText { get => _from; set { if (Set(ref _from, value)) Refresh(); } }
    public string ToText { get => _to; set { if (Set(ref _to, value)) Refresh(); } }

    public string BaseLabel
    {
        get => _baseLabel;
        set { if (Set(ref _baseLabel, value)) { Raise(nameof(BaseTip)); Refresh(); } }
    }

    /// <summary>原型 BASES.tip 逐字。</summary>
    public string BaseTip => _baseLabel switch
    {
        "相对实验开始" => "以批次最早启动的通道为 0 点。晚启动的通道在起始时段没有数据，对应单元格留空。",
        "相对通道启动" => "每个通道以自己的启动时刻为 0 点，各通道原点不同，无法共用一列时间——预览与导出自动切换为长表（一行一个通道），不产生空洞。",
        _ => "绝对时间为准。CSV / Excel 中同时写入绝对时间与相对本通道启动的时间两列，事后可直接复算。"
    };

    public bool PrevData
    {
        get => _prevData;
        set { if (Set(ref _prevData, value)) { Raise(nameof(PrevStep)); Refresh(); } }
    }
    public bool PrevStep => !_prevData;

    public string CurFmt => Fmts.First(f => f.On).Key;
    public string CurFmtName => Fmts.First(f => f.On).Name;
    public bool ReportEnabled => CurFmt is "docx" or "pdf";

    /// <summary>原型 fileNote 逐字。</summary>
    public string FileNote
    {
        get
        {
            var rec = StepsOn;
            if (CurFmt == "csv")
                return rec
                    ? "将生成 2 个文件：…_data.csv（采样数据）与 …_steps.csv（步骤执行记录）。"
                    : "将生成 1 个文件：…_data.csv（采样数据）。";
            if (CurFmt == "xlsx")
                return $"工作簿含 {Chips.Count(c => c.On)} 个通道页{(rec ? " + 「步骤执行记录」页" : "")} + 「实验信息」页（台面配置、配方参数、基线、签名）。";
            return "按「报告内容」勾选的章节排版；步骤执行记录表随「步骤执行记录表」一项输出。";
        }
    }

    public string Dest { get => _dest; set { Set(ref _dest, value); RaiseAll(nameof(DestLocal), nameof(DestUsb)); } }
    public bool DestLocal => _dest == "local";
    public bool DestUsb => _dest == "usb";
    public RelayCommand SetDestLocal => new(() => Dest = "local");
    public RelayCommand SetDestUsb => new(() => Dest = "usb");

    public string Status { get; private set; } = "";

    private bool StepsOn => Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == "steps")?.On ?? false;

    public void Reload() => SelectRun(Cur);

    private void SelectRun(RunEntryViewModel run)
    {
        foreach (var r in Runs) r.IsSel = r == run;
        _curRun = Runs.IndexOf(run);
        _to = RunEntryViewModel.Hms(run.DurSec);
        Raise(nameof(ToText));

        Meta.Clear();
        foreach (var (k, v) in new[]
        {
            ("记录编号", run.Id), ("开始时间", run.Start), ("时长", RunEntryViewModel.Hms(run.DurSec)),
            ("配方", run.RecipeName), ("操作人", run.User), ("状态", run.State),
            ("在线探头", run.Probes.Count > 0 ? string.Join(" / ", run.Probes) : "无"),
            ("报警", $"{run.Alarms} 次"), ("标记", $"{run.Marks} 处")
        })
            Meta.Add(new MetaPair { K = k, V = v });

        Chips.Clear();
        foreach (var c in run.Chs)
        {
            var sp = run.Spans.TryGetValue(c, out var v) ? v : null;
            Chips.Add(new ExChipViewModel
            {
                Ch = c,
                Available = sp is not null,
                On = sp is not null,
                Tip = sp is { } s ? $"+{RunEntryViewModel.Hms(s.Off)} 启动 · 时长 {RunEntryViewModel.Hms(s.Dur)}"
                                  : "该通道本次未启动"
            });
        }

        // 数据项（原型 DITEMS）：可用性依赖该实验的探头
        Groups.Clear();
        var groups = new (string G, (string K, string N, string U, string Needs)[] Items)[]
        {
            ("温度", new[] { ("Tr", "釜内温度", "℃", ""), ("Tj", "夹套温度", "℃", ""),
                             ("dT", "Tr−Tj 温差", "℃", ""), ("Tset", "设定温度", "℃", "") }),
            ("过程", new[] { ("pH", "pH 值", "", "pH"), ("rpm", "搅拌转速", "rpm", ""),
                             ("flow", "加料流量", "mL/min", ""), ("vol", "累计加料量", "mL", "") }),
            ("在线分析", new[] { ("raman", "拉曼特征峰", "a.u.", "拉曼"), ("ir", "红外特征峰", "a.u.", "红外"),
                                 ("turb", "浊度", "NTU", "浊度") }),
            ("执行记录", new[] { ("steps", "步骤执行记录（含事件与偏差）", "", "") })
        };
        foreach (var (g, items) in groups)
        {
            var group = new DataGroupViewModel { Name = g };
            foreach (var (k, n, u, needs) in items)
            {
                var avail = needs.Length == 0 || run.Probes.Contains(needs);
                group.Items.Add(new DataItemViewModel
                {
                    Key = k, Name = n, Unit = u, Needs = needs,
                    Available = avail, On = avail && DefOn.Contains(k)
                });
            }
            Groups.Add(group);
        }

        RaiseAll(nameof(ExName), nameof(FileNote));
        Refresh();
    }

    /// <summary>重建预览与汇总（原型 renderExportPreview 的思路，数据取自真实管线）。</summary>
    public void Refresh()
    {
        PrevHeader.Clear();
        PrevRows.Clear();
        Sum.Clear();

        var run = Cur;
        var chs = Chips.Where(c => c.On).Select(c => c.Ch).OrderBy(x => x).ToList();
        var items = Groups.SelectMany(g => g.Items)
                          .Where(i => i.Key != "steps" && i.On && i.Available).ToList();

        if (PrevStep)
        {
            BuildStepPreview(run, chs);
            return;
        }

        if (chs.Count == 0 || items.Count == 0)
        {
            PrevRows.Add(new PrevRow { Cells = new[]
                { chs.Count == 0 ? "请至少选择一个导出通道" : "请至少选择一个数值数据项" } });
            return;
        }

        var iv = _interval switch { "原始（1 s）" => 1, "5 s" => 5, "1 min" => 60, _ => 10 };
        var longTable = _baseLabel == "相对通道启动";

        var head = new List<string> { _baseLabel == "绝对时间" ? "时间" : _baseLabel == "相对实验开始" ? "实验时间" : "通道时间" };
        if (longTable)
        {
            head.Add("通道");
            head.AddRange(items.Select(i => i.HasUnit ? $"{i.Name} {i.Unit}" : i.Name));
        }
        else
            foreach (var c in chs)
                head.AddRange(items.Select(i => $"CH{c} {i.Name}"));
        foreach (var h in head) PrevHeader.Add(h);

        if (!run.Live)
        {
            PrevRows.Add(new PrevRow { Cells = new[] { "归档实验的采样数据由记录文件读取，原型未内置。" } });
        }
        else
        {
            var rec = _ws.Engine.Record;
            var t0 = rec.FirstStart ?? _ws.Clock.Now;
            var total = (_ws.Clock.Now - t0).TotalSeconds;
            var rows = 0;
            for (double t = 0; t <= total && rows < 8; t += iv, rows++)
            {
                var at = t0 + TimeSpan.FromSeconds(t);
                if (longTable)
                {
                    foreach (var c in chs)
                    {
                        if (rows >= 8) break;
                        var chRun = rec.Of(c);
                        if (chRun is null || at < chRun.StartedAt) continue;
                        var cells = new List<string>
                        { RunEntryViewModel.Hms((int)(at - chRun.StartedAt).TotalSeconds), $"CH{c}" };
                        cells.AddRange(items.Select(i => Sample(c, i.Key, at)));
                        PrevRows.Add(new PrevRow { Cells = cells });
                        rows++;
                    }
                }
                else
                {
                    var cells = new List<string> { _baseLabel == "绝对时间" ? at.ToString("HH:mm:ss") : RunEntryViewModel.Hms((int)t) };
                    foreach (var c in chs)
                    {
                        var chRun = rec.Of(c);
                        cells.AddRange(items.Select(i =>
                            chRun is null || at < chRun.StartedAt ? "" : Sample(c, i.Key, at)));
                    }
                    PrevRows.Add(new PrevRow { Cells = cells });
                }
            }
        }

        var nTicks = Math.Max(1, run.DurSec / iv);
        var nCols = longTable ? items.Count + 2 : chs.Count * items.Count + 1;
        var nRows = longTable ? nTicks * chs.Count : nTicks;
        var bytes = (long)nRows * nCols * 9;
        Sum.Add(new MetaPair { K = "数据点", V = (nTicks * chs.Count * items.Count).ToString("N0") });
        Sum.Add(new MetaPair { K = "行 × 列", V = $"{nRows:N0} × {nCols}{(longTable ? " 长表" : "")}" });
        Sum.Add(new MetaPair { K = "时间跨度", V = RunEntryViewModel.Hms(run.DurSec) });
        if (StepsOn) Sum.Add(new MetaPair { K = "步骤记录", V = "随导出写出" });
        Sum.Add(new MetaPair { K = "预计体积", V = bytes > 1048576 ? $"{bytes / 1048576.0:F1} MB" : $"{bytes / 1024} KB" });
        Sum.Add(new MetaPair { K = "格式", V = CurFmtName });
    }

    /// <summary>
    /// 数据项的键沿用原型 DITEMS，采集标签由驱动给定，两边只有累计加料量不同名。
    /// 预览与真正写文件必须走同一份映射，否则勾了什么和导出什么会对不上。
    /// </summary>
    private static string TagOf(string key) => key switch { "vol" => "volume", _ => key };

    /// <summary>当前勾选的数值数据项对应的采集标签（不含「步骤执行记录」）。</summary>
    private List<string> SelectedTags()
        => Groups.SelectMany(g => g.Items)
                 .Where(i => i.Key != "steps" && i.On && i.Available)
                 .Select(i => TagOf(i.Key)).Distinct().ToList();

    private string Sample(int ch, string key, DateTimeOffset at)
    {
        var series = _ws.Pipeline.Series(ch, TagOf(key));
        if (series is null) return "";
        var snap = series.Snapshot();
        var best = snap.LastOrDefault(s => s.WallClock <= at);
        return best.WallClock == default ? "" : best.Value.ToString("F2");
    }

    private void BuildStepPreview(RunEntryViewModel run, List<int> chs)
    {
        foreach (var h in new[]
        { "通道", "#", "步骤 / 事件", "计划开始", "实际开始", "开始偏差", "计划时长", "实际时长", "时长偏差", "结束原因", "操作人" })
            PrevHeader.Add(h);

        if (chs.Count == 0)
        {
            PrevRows.Add(new PrevRow { Cells = new[] { "请至少选择一个导出通道" } });
            return;
        }
        if (!StepsOn)
        {
            PrevRows.Add(new PrevRow { Cells = new[] { "请在「数据项」中勾选「步骤执行记录」" } });
            return;
        }
        if (!run.Live)
        {
            // 原型原话：归档实验不伪造记录
            PrevRows.Add(new PrevRow { Cells = new[]
                { $"归档实验的执行记录由记录文件读取，原型未内置。本次记录含 {run.Alarms} 次报警、{run.Marks} 处标记，导出时随步骤行一并写出。" } });
            Sum.Add(new MetaPair { K = "报警", V = $"{run.Alarms} 次" });
            Sum.Add(new MetaPair { K = "标记", V = $"{run.Marks} 处" });
            Sum.Add(new MetaPair { K = "列", V = "11" });
            Sum.Add(new MetaPair { K = "格式", V = CurFmtName });
            return;
        }

        var all = new List<(DateTimeOffset At, string[] Cells)>();
        foreach (var chRun in _ws.Engine.Record.Channels.Where(c => chs.Contains(c.Channel)))
            foreach (var s in chRun.Steps)
                all.Add((s.ActualStart ?? chRun.StartedAt, new[]
                {
                    $"CH{chRun.Channel}", (s.Index + 1).ToString(), s.Title,
                    (chRun.StartedAt + s.PlanStart).ToString("HH:mm:ss"),
                    s.ActualStart?.ToString("HH:mm:ss") ?? "—",
                    s.StartDeviation is { } sd ? Fmt.Signed(sd) : "—",
                    s.PlanDuration > TimeSpan.Zero ? Fmt.Hms(s.PlanDuration) : "—",
                    s.ActualDuration is { } ad ? Fmt.Hms(ad) : "—",
                    s.DurationDeviation is { } dd ? Fmt.Signed(dd) : "—",
                    EndBy.Text(s, _ws.Catalog),
                    chRun.Operator ?? "管理员"
                }));

        var stepsN = all.Count;
        foreach (var (_, cells) in all.OrderBy(x => x.At).Take(8))
            PrevRows.Add(new PrevRow { Cells = cells });

        Sum.Add(new MetaPair { K = "步骤行", V = stepsN.ToString() });
        Sum.Add(new MetaPair { K = "列", V = "11" });
        Sum.Add(new MetaPair { K = "预计体积", V = $"{Math.Max(1, stepsN * 110 / 1024)} KB" });
        Sum.Add(new MetaPair { K = "格式", V = CurFmtName });
    }

    private void Export()
    {
        var chs = Chips.Where(c => c.On).Select(c => c.Ch).ToList();
        if (chs.Count == 0) { Status = "请至少选择一个导出通道"; Raise(nameof(Status)); return; }
        if (!Cur.Live) { Status = "归档实验由记录文件导出，原型未内置"; Raise(nameof(Status)); return; }

        var rec = _ws.Engine.Record;
        var dir = Path.Combine(AppContext.BaseDirectory, "exports", rec.RunId);
        Directory.CreateDirectory(dir);
        try
        {
            var opt = new ExportOptions
            {
                TimeBase = _baseLabel == "相对通道启动" ? TimeBase.Channel : TimeBase.Wall,
                Shape = _baseLabel == "相对通道启动" ? TableShape.Long : TableShape.Wide,
                Grid = TimeSpan.FromSeconds(_interval switch { "原始（1 s）" => 1, "5 s" => 5, "1 min" => 60, _ => 10 })
            };
            opt.Channels.AddRange(chs);
            // 数据项的勾选此前只作用于预览，写出的文件仍是全量——导出的内容
            // 必须与界面上勾的一致，否则这一栏等于摆设
            opt.Tags.AddRange(SelectedTags());

            File.WriteAllText(Path.Combine(dir, "data.csv"),
                RecordExporter.SamplesLongCsv(_ws.Pipeline, rec, opt), new UTF8Encoding(true));
            if (StepsOn)
                File.WriteAllText(Path.Combine(dir, "steps.csv"),
                    RecordExporter.ExecutionCsv(rec, opt.TimeBase, _ws.Catalog), new UTF8Encoding(true));

            using var store = new RecordStore(Path.Combine(dir, "run.glp"));
            foreach (var ch in rec.Channels)
            {
                store.Write(ch);
                foreach (var s in ch.Steps) store.Write(ch.Channel, s);
                foreach (var e in ch.Events) store.Write(e);
            }
            store.Sign(rec.Operator ?? "管理员", "导出时签名");
            Status = $"已导出到 {dir}";
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
        Raise(nameof(Status));
    }
}
