using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Export;
using Tec.Core.Records;

namespace Tec.App.ViewModels;

/// <summary>
/// 记录表里的一行。**整行都由真实批次换算而来**：时长、通道、数据点、报警数、
/// 体积、连那条小趋势曲线都是这一炉的釜温降采样出来的，没有一个数是编的。
/// </summary>
public sealed class RunRowViewModel : ViewModelBase
{
    private bool _checked;
    private bool _preview;

    public required RunRecord Rec { get; init; }
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int DurSec { get; init; }
    public required string RecipeName { get; init; }
    public required string User { get; init; }
    /// <summary>已完成 / 运行中 / 已中止。</summary>
    public required string State { get; init; }
    public required IReadOnlyList<int> Chs { get; init; }
    /// <summary>各通道 [启动偏移, 时长]；缺 = 该通道本次未启动。</summary>
    public required IReadOnlyDictionary<int, (int Off, int Dur)> Spans { get; init; }
    public required IReadOnlyList<string> Probes { get; init; }
    public required int Alarms { get; init; }
    public required int Marks { get; init; }
    /// <summary>这一炉在管线里**实际还留着**的采样点数。见 ExportViewModel.CountPoints。</summary>
    public required long Points { get; init; }
    /// <summary>釜温降采样，画那条小曲线用。空 = 没采到，什么都不画。</summary>
    public required IReadOnlyList<double> Spark { get; init; }
    /// <summary>还在跑。导出的是截至此刻的数据。</summary>
    public required bool Live { get; init; }

    /// <summary>
    /// 勾没勾上。**变了就叫一声**：复选框是双向绑定，它自己会改这个值，
    /// 而行点击那条路走的是 PointerPressed——它比复选框的翻转**早**一步。
    /// 靠点击事件去重算计数的话，页脚永远慢一拍（勾了两条还写着「已选 1 条」）。
    /// </summary>
    public bool IsChecked
    {
        get => _checked;
        set { if (Set(ref _checked, value)) { Raise(nameof(SparkColorHex)); Changed?.Invoke(); } }
    }
    public bool IsPreview { get => _preview; set => Set(ref _preview, value); }

    /// <summary>勾选变了通知谁。由 ExportViewModel 挂上。</summary>
    public Action? Changed { get; set; }

    public string StartText => StartedAt.ToString("yyyy-MM-dd HH:mm");
    public string DurText => Fmt.Hms(TimeSpan.FromSeconds(DurSec));
    public string PointsText => Points.ToString("N0", CultureInfo.InvariantCulture);
    public string AlarmText => Alarms > 0 ? $"{Alarms} 次" : "—";
    public bool HasAlarm => Alarms > 0;

    /// <summary>整炉原始数据大概多大。页脚那个「预计体积」是按当前设置算的，两码事。</summary>
    public double Mb => Points * 9 / 1048576.0;
    public string MbText => Points * 9 >= 1048576 ? $"{Mb:F2} MB"
        : Points * 9 >= 1024 ? $"{Points * 9 / 1024.0:F1} KB"
        : $"{Points * 9} B";

    /// <summary>状态点：跑着的绿、中止红、跑完了灰。</summary>
    public string StateColorHex => State switch
    { "运行中" => "#2f8f49", "已中止" => "#c0392b", _ => "#b6b6b6" };

    public string SparkColorHex => IsChecked ? "#1a7fc4" : "#9aa4ab";

    /// <summary>四格通道条。没启动的那一格是灰的（原型 .chn i）。</summary>
    public IReadOnlyList<string> ChBars =>
        Enumerable.Range(1, 4).Select(k => Chs.Contains(k) ? ChColor(k) : "#e6e6e6").ToList();

    public static string ChColor(int ch) => ch switch
    { 1 => "#2f7ed8", 2 => "#2aa87a", 3 => "#c9772b", _ => "#8a63d2" };

    public string Tip
    {
        get
        {
            var lines = new List<string> { $"{Id} · {StartText} · {DurText} · {State}" };
            foreach (var c in Chs)
                lines.Add(Spans.TryGetValue(c, out var s)
                    ? $"CH{c} +{Fmt.Hms(TimeSpan.FromSeconds(s.Off))} 启动 · 跑了 {Fmt.Hms(TimeSpan.FromSeconds(s.Dur))}"
                    : $"CH{c} 未启动");
            if (Probes.Count > 0) lines.Add("在线探头 " + string.Join(" / ", Probes));
            lines.Add($"报警 {Alarms} 次 · 标记 {Marks} 处");
            return string.Join("\n", lines);
        }
    }

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
    public string ColorHex => Available ? RunRowViewModel.ChColor(Ch) : "#d8d8d8";
    public string Label => Available ? $"通道 {Ch}" : $"通道 {Ch}（未启动）";
    public bool On { get => _on; set => Set(ref _on, value); }
}

public sealed class DataItemViewModel : ViewModelBase
{
    private bool _on;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string Unit { get; init; } = "";
    /// <summary>依赖的探头名；空 = 总是可用。</summary>
    public string Needs { get; init; } = "";
    public required bool Available { get; init; }
    public bool HasUnit => Unit.Length > 0;
    public string Tip => Available ? "" : $"所选记录的台面上没有{Needs}探头";
    public bool On { get => _on; set { if (Set(ref _on, value)) Changed?.Invoke(); } }
    public Action? Changed { get; set; }
}

public sealed class DataGroupViewModel
{
    public required string Name { get; init; }
    public ObservableCollection<DataItemViewModel> Items { get; } = new();
}

/// <summary>报告章节 / GLP 选项这种「一行一个勾」的东西。</summary>
public sealed class ToggleViewModel : ViewModelBase
{
    private bool _on;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public bool On { get => _on; set { if (Set(ref _on, value)) Changed?.Invoke(); } }
    public Action? Changed { get; set; }
}

public sealed class FmtViewModel : ViewModelBase
{
    private bool _on;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required string Desc { get; init; }
    /// <summary>真的能写出这种文件吗。写不出就摆在那儿但按不下去，不假装能导。</summary>
    public required bool Ready { get; init; }
    public string IconKey => "ui-fmt-" + Key;
    public string Tip => Ready ? Desc : $"{Name} 导出尚未实现";
    public bool On { get => _on; set => Set(ref _on, value); }
}

/// <summary>
/// 预览表的一格。**数字右对齐、文字左对齐**——一列「到达目标温度」右对齐着
/// 排下来，读起来像是数字栏里混进了字。对齐方式跟着格子走，模板才管不着列号。
/// </summary>
public sealed class PrevCell
{
    public required string Text { get; init; }
    public bool Num { get; init; }
    public string Align => Num ? "Right" : "Left";
    public string Font => Num ? "Consolas, Menlo, monospace" : "Segoe UI, Microsoft YaHei UI, Microsoft YaHei";
}

public sealed class PrevRow
{
    public required IReadOnlyList<PrevCell> Cells { get; init; }
}

/// <summary>
/// 数据导出（原型 tecstudio-export-v2 的 1:1）：
/// 页头 ｜ 左主区（工具条 → 记录表 → 可拖拽的数据预览）｜ 右 342px 导出设置 ｜ 52px 页脚。
///
/// 与上一版的区别是**记录从"一次选一条"变成"一张可搜索、可筛选、可多选的表"**。
/// 表里只有这一开机真跑过的批次（<see cref="Tec.Core.Execution.RunEngine.Batches"/>）——
/// 跨次开机的归档记录要等记录文件读取做出来，这里绝不摆样例行。
/// </summary>
public sealed class ExportViewModel : ViewModelBase
{
    private readonly Workspace _ws;

    // 右栏七个小节
    public SectionViewModel FormatSection { get; } = new();
    public SectionViewModel ChannelsSection { get; } = new();
    public SectionViewModel ItemsSection { get; } = new();
    public SectionViewModel RangeSection { get; } = new();
    public SectionViewModel ReportSection { get; } = new(false);
    public SectionViewModel TargetSection { get; } = new(false);
    public SectionViewModel GlpSection { get; } = new();

    private string _q = "";
    private string _days = "all";
    // 三个筛选下拉的「不筛」项就是列表里的第一行。存空串的话下拉框选不中任何一项，
    // 界面上是三个空白框——看着像没加载出来
    private string _fRecipe = AllRecipes;
    private string _fOp = AllOps;
    private string _fState = AllStates;
    private bool _onlyAlarm;
    private string _sort = "ts";
    private int _dir = -1;

    private string _range = "全程";
    private string _interval = "10 s";
    private string _baseLabel = "绝对时间";
    private bool _prevData = true;
    private string _dest = "local";
    private string _from = "00:00:00";
    private string _to = "00:00:00";
    private string _signer = "管理员";
    private double _prevHeight = 250;
    private RunRowViewModel? _preview;

    private static readonly string[] DefOn =
        { "Tr", "Tj", "dT", "Tset", "pH", "rpm", "flow", "vol", "turb", "steps" };

    /// <summary>探头驱动 → 数据项依赖的探头名。数据项能不能勾，看那一炉的台面上装了什么。</summary>
    private static readonly Dictionary<string, string> ProbeNames = new(StringComparer.Ordinal)
    {
        ["tec.probe.ph"] = "pH",
        ["tec.probe.turbidity"] = "浊度",
        ["tec.probe.raman"] = "拉曼",
        ["tec.probe.ir"] = "红外"
    };

    public ExportViewModel(Workspace ws)
    {
        _ws = ws;

        // 四种格式照原型摆齐，但只有 CSV 真的写得出来。摆着能点、点了给你一个
        // 改了扩展名的 CSV，比按不下去糟得多——那是拿假文件糊弄审计
        foreach (var (k, n, d, ready) in new (string, string, string, bool)[]
        {
            ("csv", "CSV", "逐点数据表", true), ("xlsx", "Excel", "每通道一页", false),
            ("docx", "Word", "排版报告", false), ("pdf", "PDF", "含曲线与步骤", false)
        })
            Fmts.Add(new FmtViewModel { Key = k, Name = n, Desc = d, Ready = ready, On = k == "csv" });

        foreach (var (k, n, on) in new (string, string, bool)[]
        {
            ("trend", "趋势曲线（Tr / Tj / Tr−Tj / pH）", true), ("steps", "步骤执行记录表", true),
            ("alarm", "报警与偏差记录", true), ("recipe", "配方参数与台面配置", true),
            ("chem", "化合物物性数据", false)
        })
            ReportItems.Add(new ToggleViewModel { Key = k, Name = n, On = on });

        foreach (var (k, n, on) in new (string, string, bool)[]
        {
            ("audit", "附带审计追踪（操作与修改记录）", true),
            ("sha", "生成完整性校验码（SHA-256）", true),
            ("sign", "电子签名", true)
        })
            GlpItems.Add(new ToggleViewModel { Key = k, Name = n, On = on, Changed = () => Raise(nameof(GlpHint)) });

        PickRow = new RelayCommand(p => { if (p is RunRowViewModel r) Toggle(r, false); });
        ToggleAll = new RelayCommand(SelectAllOrNone);
        ClearSel = new RelayCommand(() =>
        {
            _bulk = true;
            foreach (var r in _all) r.IsChecked = false;
            _bulk = false;
            AfterSelection();
        });
        SortBy = new RelayCommand(p =>
        {
            var k = Convert.ToString(p) ?? "ts";
            if (_sort == k) _dir = -_dir; else { _sort = k; _dir = k == "ts" ? -1 : 1; }
            Rebuild(keepSel: true);
        });
        SetDays = new RelayCommand(p => { _days = Convert.ToString(p) ?? "all"; Rebuild(keepSel: true); });
        ToggleCh = new RelayCommand(p =>
        {
            if (p is ExChipViewModel c && c.Available) { c.On = !c.On; Refresh(); }
        });
        PickFmt = new RelayCommand(p =>
        {
            if (p is not FmtViewModel { Ready: true } f) { Say(p is FmtViewModel x ? x.Tip : ""); return; }
            foreach (var y in Fmts) y.On = y == f;
            RaiseAll(nameof(FileNote), nameof(ReportEnabled), nameof(FormatHint), nameof(CanPreviewReport));
            Refresh();
        });
        SetPrev = new RelayCommand(p => PrevData = Convert.ToString(p) == "data");
        SetDestLocal = new RelayCommand(() => Dest = "local");
        SetDestUsb = new RelayCommand(() => Dest = "usb");
        DoExport = new RelayCommand(Export);
        PreviewReport = new RelayCommand(() =>
            Say("报告排版（Word / PDF）尚未实现 —— 需要先做报告模板引擎"));

        Reload();
    }

    // ── 集合 ────────────────────────────────────────────────────────

    /// <summary>过滤 + 排序之后摆在表里的那些行。</summary>
    public ObservableCollection<RunRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> RecipeOptions { get; } = new();
    public ObservableCollection<string> OpOptions { get; } = new();
    public const string AllRecipes = "全部配方", AllOps = "全部操作人", AllStates = "全部状态";
    public ObservableCollection<string> StateOptions { get; } =
        new() { AllStates, "已完成", "运行中", "已中止" };
    public ObservableCollection<ExChipViewModel> Chips { get; } = new();
    public ObservableCollection<DataGroupViewModel> Groups { get; } = new();
    public ObservableCollection<FmtViewModel> Fmts { get; } = new();
    public ObservableCollection<ToggleViewModel> ReportItems { get; } = new();
    public ObservableCollection<ToggleViewModel> GlpItems { get; } = new();
    public ObservableCollection<PrevRow> PrevRows { get; } = new();
    public ObservableCollection<PrevCell> PrevHeader { get; } = new();
    public ObservableCollection<MetaPair> Stat { get; } = new();

    public RelayCommand PickRow { get; }
    public RelayCommand ToggleAll { get; }
    public RelayCommand ClearSel { get; }
    public RelayCommand SortBy { get; }
    public RelayCommand SetDays { get; }
    public RelayCommand ToggleCh { get; }
    public RelayCommand PickFmt { get; }
    public RelayCommand SetPrev { get; }
    public RelayCommand SetDestLocal { get; }
    public RelayCommand SetDestUsb { get; }
    public RelayCommand DoExport { get; }
    public RelayCommand PreviewReport { get; }

    /// <summary>全部批次（未过滤）。计数与筛选下拉都从它来。</summary>
    private readonly List<RunRowViewModel> _all = new();

    // ── 工具条 ──────────────────────────────────────────────────────

    public string Query { get => _q; set { if (Set(ref _q, value)) Rebuild(keepSel: true); } }
    public bool OnlyAlarm { get => _onlyAlarm; set { if (Set(ref _onlyAlarm, value)) Rebuild(keepSel: true); } }

    public string FilterRecipe { get => _fRecipe; set { if (Set(ref _fRecipe, value)) Rebuild(keepSel: true); } }
    public string FilterOp { get => _fOp; set { if (Set(ref _fOp, value)) Rebuild(keepSel: true); } }
    public string FilterState { get => _fState; set { if (Set(ref _fState, value)) Rebuild(keepSel: true); } }

    public bool DaysAll => _days == "all";
    public bool Days7 => _days == "7";
    public bool Days30 => _days == "30";
    public bool Days90 => _days == "90";

    public string SortArrowId => Arrow("id");
    public string SortArrowTs => Arrow("ts");
    public string SortArrowDur => Arrow("dur");
    public string SortArrowPts => Arrow("pts");
    public string SortArrowMb => Arrow("mb");
    private string Arrow(string k) => _sort == k ? (_dir > 0 ? "▲" : "▼") : "";

    public string CountText => $"共 {Rows.Count} 条 · 已选 {SelCount} 条";
    public int SelCount => _all.Count(r => r.IsChecked);
    public bool AllChecked => Rows.Count > 0 && Rows.All(r => r.IsChecked);

    /// <summary>一条记录都没有。整张表换成一句说明，不画空表头。</summary>
    public bool NoRows => Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;

    public string EmptyText => _all.Count == 0
        ? "还没有跑过实验"
        : "没有符合条件的实验记录";

    public string EmptySub => _all.Count == 0
        ? "导出的是执行引擎记下来的真实数据。先在「台面」摆好设备、在「配方」写好步骤，"
          + "启动任一通道跑一趟，这一炉就会出现在这里。"
        : "调整搜索词或放宽筛选条件";

    // ── 预览 ────────────────────────────────────────────────────────

    public RunRowViewModel? Preview => _preview;
    public bool HasPreview => _preview is not null;

    /// <summary>预览区高度，那条 6px 的分隔条拖出来的。</summary>
    public double PrevHeight
    {
        get => _prevHeight;
        set => Set(ref _prevHeight, Math.Clamp(value, 90, 560));
    }

    public string PrevWho
    {
        get
        {
            if (_preview is not { } r) return "";
            var n = SelCount;
            return n > 1 ? $"以 {r.Id} 为样例 · 其余 {n - 1} 条结构相同" : $"{r.Id} · {r.Name}";
        }
    }

    public bool PrevData
    {
        get => _prevData;
        set { if (Set(ref _prevData, value)) { Raise(nameof(PrevStep)); Refresh(); } }
    }
    public bool PrevStep => !_prevData;

    // ── 右栏 ────────────────────────────────────────────────────────

    public ObservableCollection<string> RangeOptions { get; } = new() { "全程", "自定义时段" };
    public ObservableCollection<string> IntervalOptions { get; } = new() { "原始（1 s）", "5 s", "10 s", "1 min" };
    public ObservableCollection<string> BaseOptions { get; } = new() { "绝对时间", "相对实验开始", "相对通道启动" };

    public string Range { get => _range; set { if (Set(ref _range, value)) { Raise(nameof(CustomRange)); Refresh(); } } }
    public bool CustomRange => _range == "自定义时段";
    public string Interval { get => _interval; set { if (Set(ref _interval, value)) { Raise(nameof(RangeHint)); Refresh(); } } }
    public string FromText { get => _from; set { if (Set(ref _from, value)) Refresh(); } }
    public string ToText { get => _to; set { if (Set(ref _to, value)) Refresh(); } }

    public string BaseLabel
    {
        get => _baseLabel;
        set { if (Set(ref _baseLabel, value)) { Raise(nameof(BaseTip)); Refresh(); } }
    }

    public string BaseTip => _baseLabel switch
    {
        "相对实验开始" => "以批次最早启动的通道为 0 点。晚启动的通道在起始时段没有数据，对应单元格留空。",
        "相对通道启动" => "每个通道以自己的启动时刻为 0 点，各通道原点不同，无法共用一列时间——预览与导出自动切换为长表（一行一个通道），不产生空洞。",
        _ => "CSV / Excel 中同时写入绝对时间与相对本通道启动的时间两列，事后可直接复算。"
    };

    public FmtViewModel CurFmtItem => Fmts.First(f => f.On);
    public string CurFmt => CurFmtItem.Key;
    public string CurFmtName => CurFmtItem.Name;
    public bool ReportEnabled => CurFmt is "docx" or "pdf";
    public bool CanPreviewReport => ReportEnabled && SelCount > 0;

    public string FormatHint => CurFmtName;
    public string ChannelHint
    {
        get
        {
            var avail = Chips.Count(c => c.Available);
            return avail == 0 ? "—" : $"{Chips.Count(c => c.On && c.Available)} / {avail}";
        }
    }
    public string ItemHint => $"{Groups.SelectMany(g => g.Items).Count(i => i.On && i.Available)} 项";
    public string RangeHint => _interval;
    public string TargetHint => _dest == "usb" ? "USB" : "本地";
    public string GlpHint => $"{GlpItems.Count(g => g.On)} / {GlpItems.Count}";

    public string FileNote
    {
        get
        {
            if (!CurFmtItem.Ready)
                return $"{CurFmtName} 导出尚未实现——需要先做{(CurFmt == "xlsx" ? "工作簿写出" : "报告模板引擎")}。"
                       + "现在只有 CSV 能真的写出文件。";
            var rec = StepsOn;
            return rec
                ? "将生成 2 个文件：…_data.csv（采样数据）与 …_steps.csv（步骤执行记录）。"
                : "将生成 1 个文件：…_data.csv（采样数据）。";
        }
    }

    /// <summary>选中的记录通道数不一致时说一句，别让人以为空列是 bug。</summary>
    public string ChannelTip
    {
        get
        {
            var mixed = Selected.Select(r => r.Chs.Count).Distinct().OrderBy(x => x).ToList();
            return mixed.Count > 1
                ? $"所选记录的通道数不一致（{string.Join(" / ", mixed)}），缺的通道在文件里留空列。"
                : "";
        }
    }
    public bool HasChannelTip => ChannelTip.Length > 0;

    public string LocalDir => Path.Combine(AppContext.BaseDirectory, "exports");

    /// <summary>可移动磁盘按实际枚举。没插盘就照实说没插。</summary>
    public string UsbNote
    {
        get
        {
            try
            {
                var d = DriveInfo.GetDrives().FirstOrDefault(x => x.DriveType == DriveType.Removable && x.IsReady);
                if (d is null) return "未检测到可移动磁盘";
                var name = d.VolumeLabel.Length > 0 ? d.VolumeLabel : "可移动磁盘";
                return $"{name} ({d.Name.TrimEnd('\\')}) · 剩余 {d.AvailableFreeSpace / 1073741824.0:F1} GB";
            }
            catch { return "未检测到可移动磁盘"; }
        }
    }

    public bool UsbReady => !UsbNote.StartsWith("未检测", StringComparison.Ordinal);

    public string Dest
    {
        get => _dest;
        set { if (Set(ref _dest, value)) RaiseAll(nameof(DestLocal), nameof(DestUsb), nameof(TargetHint)); }
    }
    public bool DestLocal => _dest == "local";
    public bool DestUsb => _dest == "usb";

    public string Signer { get => _signer; set => Set(ref _signer, value); }

    // ── 页脚 ────────────────────────────────────────────────────────

    public string Status { get; private set; } = "";
    public bool HasStatus => Status.Length > 0;

    public string Warn { get; private set; } = "";
    public bool HasWarn => Warn.Length > 0;

    public bool CanExport => SelCount > 0 && CurFmtItem.Ready;

    public string ExportTip => SelCount == 0 ? "先在表里勾选一条或多条记录"
        : !CurFmtItem.Ready ? $"{CurFmtName} 导出尚未实现，先选 CSV"
        : $"导出 {SelCount} 条记录到{(DestUsb ? " USB 记忆盘" : "本地文件夹")}";

    private IEnumerable<RunRowViewModel> Selected => _all.Where(r => r.IsChecked);
    private bool StepsOn => Groups.SelectMany(g => g.Items).FirstOrDefault(i => i.Key == "steps")?.On ?? false;

    private void Say(string text)
    {
        Status = text;
        RaiseAll(nameof(Status), nameof(HasStatus));
    }

    // ── 重建 ────────────────────────────────────────────────────────

    /// <summary>每次切到这一页都重来一遍：中途可能又跑了一炉。</summary>
    public void Reload()
    {
        var keep = _all.Where(r => r.IsChecked).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var keepPrev = _preview?.Id;

        _all.Clear();
        foreach (var rec in _ws.Engine.Batches)
        {
            if (rec.Channels.Count == 0) continue;      // 空壳批次不摆
            var row = Row(rec);
            row.IsChecked = keep.Contains(row.Id);
            row.Changed = OnRowChecked;
            _all.Add(row);
        }

        // 头一回进来（还没人勾过）就替他勾上最近那一炉——十有八九就是要导它
        if (keep.Count == 0 && _all.Count > 0) _all[^1].IsChecked = true;

        var recipes = _all.Select(r => r.RecipeName).Distinct().OrderBy(x => x).ToList();
        Fill(RecipeOptions, AllRecipes, recipes);
        Fill(OpOptions, AllOps, _all.Select(r => r.User).Distinct().OrderBy(x => x).ToList());
        if (!RecipeOptions.Contains(_fRecipe)) _fRecipe = AllRecipes;
        if (!OpOptions.Contains(_fOp)) _fOp = AllOps;
        RaiseAll(nameof(FilterRecipe), nameof(FilterOp));

        _preview = _all.FirstOrDefault(r => r.Id == keepPrev)
                   ?? _all.LastOrDefault(r => r.IsChecked)
                   ?? _all.LastOrDefault();
        Rebuild(keepSel: true);
    }

    private static void Fill(ObservableCollection<string> target, string head, List<string> items)
    {
        var want = new List<string> { head };
        want.AddRange(items);
        if (target.SequenceEqual(want)) return;
        target.Clear();
        foreach (var x in want) target.Add(x);
    }

    /// <summary>过滤 + 排序。</summary>
    private void Rebuild(bool keepSel)
    {
        if (!keepSel) foreach (var r in _all) r.IsChecked = false;

        var now = _ws.Clock.Now;
        IEnumerable<RunRowViewModel> q = _all;

        if (_q.Trim().Length > 0)
        {
            var s = _q.Trim();
            q = q.Where(r => r.Name.Contains(s, StringComparison.OrdinalIgnoreCase)
                          || r.Id.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        if (_days != "all" && int.TryParse(_days, out var days))
            q = q.Where(r => (now - r.StartedAt).TotalDays <= days);
        if (_fRecipe != AllRecipes && _fRecipe.Length > 0)
            q = q.Where(r => r.RecipeName == _fRecipe);
        if (_fOp != AllOps && _fOp.Length > 0)
            q = q.Where(r => r.User == _fOp);
        if (_fState != AllStates && _fState.Length > 0)
            q = q.Where(r => r.State == _fState);
        if (_onlyAlarm) q = q.Where(r => r.Alarms > 0);

        var list = q.ToList();
        list.Sort((a, b) => _dir * (_sort switch
        {
            "id" => string.CompareOrdinal(a.Id, b.Id),
            "dur" => a.DurSec.CompareTo(b.DurSec),
            "pts" => a.Points.CompareTo(b.Points),
            "mb" => a.Mb.CompareTo(b.Mb),
            _ => a.StartedAt.CompareTo(b.StartedAt)
        }));

        Rows.Clear();
        foreach (var r in list) Rows.Add(r);

        // 预览的那一条被筛掉了就换一条，不留一个指向表外的预览
        if (_preview is null || !Rows.Contains(_preview))
            _preview = Rows.LastOrDefault(r => r.IsChecked) ?? Rows.LastOrDefault();
        foreach (var r in _all) r.IsPreview = ReferenceEquals(r, _preview);

        AfterSelection();
        RaiseAll(nameof(NoRows), nameof(HasRows), nameof(EmptyText), nameof(EmptySub),
                 nameof(DaysAll), nameof(Days7), nameof(Days30), nameof(Days90),
                 nameof(SortArrowId), nameof(SortArrowTs), nameof(SortArrowDur),
                 nameof(SortArrowPts), nameof(SortArrowMb));
    }

    /// <summary>
    /// 点一行 = 勾 / 取消勾，并把预览挪过去。点在复选框上时它自己会翻，
    /// 这里只负责挪预览（<paramref name="checkedOnly"/>）。
    /// </summary>
    public void Toggle(RunRowViewModel row, bool checkedOnly)
    {
        _preview = row;
        foreach (var r in _all) r.IsPreview = ReferenceEquals(r, row);
        if (!checkedOnly) row.IsChecked = !row.IsChecked;   // setter 里会重算
        else AfterSelection();
    }

    private bool _bulk;

    private void OnRowChecked()
    {
        if (_bulk) return;
        AfterSelection();
    }

    private void SelectAllOrNone()
    {
        var target = !AllChecked;
        // 一次勾几十行时每行都重算一遍通道 / 数据项 / 预览是白费——收口了再算一次
        _bulk = true;
        foreach (var r in Rows) r.IsChecked = target;
        _bulk = false;
        AfterSelection();
    }

    /// <summary>选择变了：通道 / 数据项要跟着所选记录重算，页脚重算，预览重画。</summary>
    private void AfterSelection()
    {
        BuildChipsAndItems();
        Refresh();
        RaiseAll(nameof(CountText), nameof(SelCount), nameof(AllChecked), nameof(CanExport),
                 nameof(ExportTip), nameof(CanPreviewReport), nameof(PrevWho), nameof(HasPreview),
                 nameof(Preview), nameof(ChannelTip), nameof(HasChannelTip));
    }

    // ── 由批次记录换算出一行 ────────────────────────────────────────

    private RunRowViewModel Row(RunRecord rec)
    {
        var now = _ws.Clock.Now;
        var t0 = rec.FirstStart ?? rec.CreatedAt;
        var live = rec.Channels.Any(c => c.State is ChannelRunState.Running or ChannelRunState.Paused);
        var end = live ? now : rec.Channels.Max(c => c.FinishedAt ?? now);

        var spans = new Dictionary<int, (int Off, int Dur)>();
        foreach (var ch in rec.StartedChannels)
            if (rec.Of(ch) is { } r)
                spans[ch] = ((int)(r.StartedAt - t0).TotalSeconds,
                             (int)((r.FinishedAt ?? now) - r.StartedAt).TotalSeconds);

        var probes = _ws.Bench.Devices
            .Select(d => ProbeNames.TryGetValue(d.DriverId, out var n) ? n : null)
            .Where(n => n is not null).Select(n => n!).Distinct().ToList();

        var recipes = rec.Channels.Select(c => c.Baseline.Recipe.Name).Distinct().ToList();
        var aborted = !live && rec.Channels.Any(c => c.State is ChannelRunState.Aborted or ChannelRunState.Faulted);

        return new RunRowViewModel
        {
            Rec = rec,
            Id = rec.RunId,
            Name = rec.Name,
            StartedAt = t0,
            DurSec = Math.Max(0, (int)(end - t0).TotalSeconds),
            RecipeName = recipes.Count switch { 0 => "—", 1 => recipes[0], _ => string.Join(" / ", recipes) },
            User = rec.Operator ?? "管理员",
            State = live ? "运行中" : aborted ? "已中止" : "已完成",
            Chs = rec.StartedChannels,
            Spans = spans,
            Probes = probes,
            Alarms = rec.Channels.Sum(c => c.Events.Count(e => e.Kind == EventKind.Alarm)),
            Marks = rec.Channels.Sum(c => c.Events.Count(e => e.Kind == EventKind.OperatorMark)),
            Points = CountPoints(rec, t0, end),
            Spark = Spark(rec, t0, end),
            Live = live
        };
    }

    /// <summary>
    /// 这一炉**现在还能导出多少个点**。
    ///
    /// 不按「时长 ÷ 采样周期」估：采样在定长环形缓冲里，跑了新的一炉之后老的
    /// 会被顶掉。估出来的数字会让人以为导得出一整炉，实际写出来只有零头。
    /// 数管线里真正还在的那些。
    /// </summary>
    private long CountPoints(RunRecord rec, DateTimeOffset from, DateTimeOffset to)
    {
        long n = 0;
        foreach (var key in _ws.Pipeline.Keys)
        {
            if (!rec.StartedChannels.Contains(key.Channel)) continue;
            if (_ws.Pipeline.Series(key.Channel, key.Tag) is not { } s) continue;
            foreach (var sample in s.Snapshot())
                if (sample.WallClock >= from && sample.WallClock <= to) n++;
        }
        return n;
    }

    /// <summary>那条小趋势曲线：第一个启动的通道的釜温，降采样到 ~28 个点。</summary>
    private IReadOnlyList<double> Spark(RunRecord rec, DateTimeOffset from, DateTimeOffset to)
    {
        var ch = rec.StartedChannels.FirstOrDefault();
        if (ch == 0 || _ws.Pipeline.Series(ch, "Tr") is not { } s) return Array.Empty<double>();
        var pts = s.Snapshot().Where(x => x.WallClock >= from && x.WallClock <= to)
                   .Select(x => x.Value).ToList();
        if (pts.Count < 2) return Array.Empty<double>();
        const int N = 28;
        if (pts.Count <= N) return pts;
        var step = pts.Count / (double)N;
        return Enumerable.Range(0, N).Select(i => pts[Math.Min(pts.Count - 1, (int)(i * step))]).ToList();
    }

    // ── 通道 / 数据项跟着所选记录走 ─────────────────────────────────

    private void BuildChipsAndItems()
    {
        var sel = Selected.ToList();
        var chs = sel.SelectMany(r => r.Chs).Distinct().OrderBy(x => x).ToList();
        var probes = sel.SelectMany(r => r.Probes).Distinct().ToList();

        // 通道格子固定四个（台面最多四路），选中的记录里没启动过的那几个是灰的
        var wantOn = Chips.Where(c => c.On).Select(c => c.Ch).ToHashSet();
        var fresh = Chips.Count == 0;
        Chips.Clear();
        for (var c = 1; c <= 4; c++)
        {
            var avail = chs.Contains(c);
            Chips.Add(new ExChipViewModel
            {
                Ch = c,
                Available = avail,
                On = avail && (fresh || wantOn.Contains(c) || wantOn.Count == 0),
                Tip = avail ? null : "所选记录里这一路没有启动过"
            });
        }

        var keepOff = Groups.SelectMany(g => g.Items).Where(i => !i.On).Select(i => i.Key).ToHashSet();
        var first = Groups.Count == 0;
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
                var avail = needs.Length == 0 || probes.Contains(needs);
                group.Items.Add(new DataItemViewModel
                {
                    Key = k, Name = n, Unit = u, Needs = needs, Available = avail,
                    On = avail && (first ? DefOn.Contains(k) : !keepOff.Contains(k) && DefOn.Contains(k)),
                    Changed = OnItemChanged
                });
            }
            Groups.Add(group);
        }
        RaiseAll(nameof(ChannelHint), nameof(ItemHint));
    }

    private void OnItemChanged()
    {
        RaiseAll(nameof(ItemHint), nameof(FileNote));
        Refresh();
    }

    // ── 预览 + 页脚 ─────────────────────────────────────────────────

    public void Refresh()
    {
        PrevHeader.Clear();
        PrevRows.Clear();
        BuildFooter();
        RaiseAll(nameof(PrevWho), nameof(ChannelHint), nameof(ItemHint), nameof(GlpHint),
                 nameof(RangeHint), nameof(FormatHint), nameof(TargetHint));

        if (_preview is not { } run) return;
        var chs = Chips.Where(c => c.On && c.Available).Select(c => c.Ch)
                       .Where(c => run.Chs.Contains(c)).OrderBy(x => x).ToList();
        var items = Groups.SelectMany(g => g.Items)
                          .Where(i => i.Key != "steps" && i.On && i.Available).ToList();

        if (PrevStep) { BuildStepPreview(run, chs); return; }

        if (chs.Count == 0 || items.Count == 0)
        {
            PrevRows.Add(Note(chs.Count == 0 ? "请至少选择一个导出通道" : "请至少选择一个数值数据项"));
            return;
        }

        var iv = IntervalSeconds;
        var longTable = _baseLabel == "相对通道启动";

        var head = new List<string>
        { _baseLabel == "绝对时间" ? "时间 hh:mm:ss（绝对）" : _baseLabel == "相对实验开始" ? "实验时间" : "通道时间" };
        if (longTable)
        {
            head.Add("通道");
            head.AddRange(items.Select(i => i.HasUnit ? $"{i.Name} {i.Unit}" : i.Name));
        }
        else
            foreach (var c in chs)
                head.AddRange(items.Select(i => i.HasUnit ? $"CH{c} {i.Name} {i.Unit}" : $"CH{c} {i.Name}"));
        for (var i = 0; i < head.Count; i++)
            PrevHeader.Add(new PrevCell { Text = head[i], Num = longTable ? i >= 2 : i >= 1 });

        var rec = run.Rec;
        var t0 = rec.FirstStart ?? run.StartedAt;
        var total = run.DurSec;
        var rows = 0;
        for (double t = 0; t <= total && rows < 14; t += iv)
        {
            var at = t0 + TimeSpan.FromSeconds(t);
            if (longTable)
            {
                foreach (var c in chs)
                {
                    if (rows >= 14) break;
                    var chRun = rec.Of(c);
                    if (chRun is null || at < chRun.StartedAt) continue;
                    var cells = new List<PrevCell>
                    {
                        new() { Text = Fmt.Hms(at - chRun.StartedAt), Num = true },
                        new() { Text = $"CH{c}" }
                    };
                    cells.AddRange(items.Select(i => new PrevCell { Text = Sample(c, i.Key, at), Num = true }));
                    PrevRows.Add(new PrevRow { Cells = cells });
                    rows++;
                }
            }
            else
            {
                var cells = new List<PrevCell>
                {
                    new() { Text = _baseLabel == "绝对时间" ? at.ToString("HH:mm:ss")
                                                          : Fmt.Hms(TimeSpan.FromSeconds(t)), Num = true }
                };
                foreach (var c in chs)
                {
                    var chRun = rec.Of(c);
                    cells.AddRange(items.Select(i => new PrevCell
                    { Text = chRun is null || at < chRun.StartedAt ? "" : Sample(c, i.Key, at), Num = true }));
                }
                PrevRows.Add(new PrevRow { Cells = cells });
                rows++;
            }
        }

        if (PrevRows.Count == 0)
            PrevRows.Add(Note("这一炉的采样已经被后面几炉顶出缓冲区，导出会是空的"));
    }

    /// <summary>预览区里的一句话（「请先勾一个通道」这种），占一整行，左对齐。</summary>
    private static PrevRow Note(string text)
        => new() { Cells = new[] { new PrevCell { Text = text } } };

    private int IntervalSeconds => _interval switch { "原始（1 s）" => 1, "5 s" => 5, "1 min" => 60, _ => 10 };

    private static string TagOf(string key) => key switch { "vol" => "volume", _ => key };

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

    private void BuildStepPreview(RunRowViewModel run, List<int> chs)
    {
        foreach (var (h, num) in new (string, bool)[]
        {
            ("通道", false), ("#", true), ("步骤 / 事件", false), ("计划开始", true), ("实际开始", true),
            ("开始偏差", true), ("计划时长", true), ("实际时长", true), ("时长偏差", true),
            ("结束原因", false), ("操作人", false)
        })
            PrevHeader.Add(new PrevCell { Text = h, Num = num });

        if (chs.Count == 0)
        {
            PrevRows.Add(Note("请至少选择一个导出通道"));
            return;
        }
        if (!StepsOn)
        {
            PrevRows.Add(Note("请在「数据项」中勾选「步骤执行记录」"));
            return;
        }

        var all = new List<(DateTimeOffset At, string[] Cells)>();
        foreach (var chRun in run.Rec.Channels.Where(c => chs.Contains(c.Channel)))
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

        // 与上面那排表头一一对应：序号与几列时刻 / 偏差是数字，其余是文字
        var isNum = new[] { false, true, false, true, true, true, true, true, true, false, false };
        foreach (var (_, cells) in all.OrderBy(x => x.At).Take(14))
            PrevRows.Add(new PrevRow
            { Cells = cells.Select((t, i) => new PrevCell { Text = t, Num = isNum[i] }).ToList() });
        if (all.Count == 0) PrevRows.Add(Note("这一炉没有步骤记录"));
    }

    /// <summary>页脚那一行统计：全部按当前设置算，不是拍脑袋的常数。</summary>
    private void BuildFooter()
    {
        Stat.Clear();
        var sel = Selected.ToList();
        var nItem = Groups.SelectMany(g => g.Items).Count(i => i.Key != "steps" && i.On && i.Available);
        var iv = IntervalSeconds;

        if (sel.Count == 0)
        {
            Warn = "";
            RaiseAll(nameof(Warn), nameof(HasWarn), nameof(CanExport), nameof(ExportTip));
            return;
        }

        long pts = 0;
        foreach (var r in sel)
        {
            var chs = Chips.Count(c => c.On && c.Available && r.Chs.Contains(c.Ch));
            pts += (long)Math.Max(1, r.DurSec / iv) * chs * nItem;
        }
        var factor = CurFmt switch { "pdf" => 1.9, "docx" => 1.6, "xlsx" => 1.3, _ => 1.05 };
        var bytes = pts * 9 * factor;

        Stat.Add(new MetaPair { K = "已选", V = $"{sel.Count} 条记录" });
        Stat.Add(new MetaPair { K = "数据点", V = pts.ToString("N0", CultureInfo.InvariantCulture) });
        Stat.Add(new MetaPair { K = "数据项", V = $"{nItem} × 通道" });
        Stat.Add(new MetaPair { K = "预计体积", V = Size(bytes) });
        Stat.Add(new MetaPair { K = "格式", V = CurFmtName });
        if (StepsOn) Stat.Add(new MetaPair { K = "步骤记录", V = "随导出写出" });

        var msgs = new List<string>();
        var run = sel.Count(r => r.Live);
        var abt = sel.Count(r => r.State == "已中止");
        var thin = sel.Count(r => r.Points == 0);
        if (run > 0) msgs.Add($"{run} 条仍在运行，导出的是截至此刻的数据");
        if (abt > 0) msgs.Add($"{abt} 条为已中止实验，步骤记录不完整");
        // 采样环被后面几炉顶掉了是真会发生的事，别让人导出一个空文件才发现
        if (thin > 0) msgs.Add($"{thin} 条的采样已不在缓冲区内，只能导出步骤记录");
        Warn = string.Join("；", msgs);
        RaiseAll(nameof(Warn), nameof(HasWarn), nameof(CanExport), nameof(ExportTip));
    }

    // ── 导出 ────────────────────────────────────────────────────────

    private void Export()
    {
        var sel = Selected.ToList();
        if (sel.Count == 0) { Say("请先在表里勾选一条或多条记录"); return; }
        if (!CurFmtItem.Ready) { Say($"{CurFmtName} 导出尚未实现，先选 CSV"); return; }

        var chs = Chips.Where(c => c.On && c.Available).Select(c => c.Ch).ToList();
        if (chs.Count == 0) { Say("请至少选择一个导出通道"); return; }

        var root = DestUsb ? UsbRoot() : LocalDir;
        if (root is null) { Say("没有可写的可移动磁盘，改选本地文件夹"); return; }

        var done = new List<string>();
        try
        {
            foreach (var row in sel)
            {
                var dir = Path.Combine(root, row.Id);
                Directory.CreateDirectory(dir);

                var opt = new ExportOptions
                {
                    TimeBase = _baseLabel == "相对通道启动" ? TimeBase.Channel : TimeBase.Wall,
                    Shape = _baseLabel == "相对通道启动" ? TableShape.Long : TableShape.Wide,
                    Grid = TimeSpan.FromSeconds(IntervalSeconds)
                };
                opt.Channels.AddRange(chs.Where(c => row.Chs.Contains(c)));
                opt.Tags.AddRange(SelectedTags());

                File.WriteAllText(Path.Combine(dir, "data.csv"),
                    RecordExporter.SamplesLongCsv(_ws.Pipeline, row.Rec, opt), new UTF8Encoding(true));
                if (StepsOn)
                    File.WriteAllText(Path.Combine(dir, "steps.csv"),
                        RecordExporter.ExecutionCsv(row.Rec, opt.TimeBase, _ws.Catalog), new UTF8Encoding(true));

                // 审计追踪与校验码是 GLP 那一节的两个勾。链式摘要本来就在 RecordStore 里，
                // 勾掉「校验码」就不写这份文件，而不是写一份没有摘要的假 GLP 文件
                if (GlpOn("audit") || GlpOn("sha") || GlpOn("sign"))
                {
                    using var store = new RecordStore(Path.Combine(dir, "run.glp"));
                    foreach (var ch in row.Rec.Channels)
                    {
                        store.Write(ch);
                        foreach (var s in ch.Steps) store.Write(ch.Channel, s);
                        if (GlpOn("audit")) foreach (var e in ch.Events) store.Write(e);
                    }
                    if (GlpOn("sign"))
                        store.Sign(_signer.Length > 0 ? _signer : row.User, "导出时签名");
                }
                done.Add(row.Id);
            }
            Say($"已导出 {done.Count} 条到 {root}：{string.Join("、", done)}");
        }
        catch (Exception ex)
        {
            Say("导出失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 体积的说法。一律按 KB 印的话，几百字节会写成「0 KB」——
    /// 一个明明要导出东西的批次显示成 0，看着像哪儿算错了。
    /// </summary>
    private static string Size(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576.0:F2} MB"
        : bytes >= 1024 ? $"{bytes / 1024.0:F1} KB"
        : $"{bytes:F0} B";

    private bool GlpOn(string key) => GlpItems.FirstOrDefault(g => g.Key == key)?.On ?? false;

    private static string? UsbRoot()
    {
        try
        {
            var d = DriveInfo.GetDrives().FirstOrDefault(x => x.DriveType == DriveType.Removable && x.IsReady);
            return d is null ? null : Path.Combine(d.RootDirectory.FullName, "TecStudio");
        }
        catch { return null; }
    }
}
