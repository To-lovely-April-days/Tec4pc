using System.Collections.ObjectModel;
using System.Globalization;
using Tec.App.Services;
using Tec.Core.Chemistry;
using Tec.Core.Compounds;
using Tec.Core.Recipes;
using Tec.Driver.Abi;

namespace Tec.App.ViewModels;

/// <summary>
/// 表里的一行。**输入在左、算出来的在右**——
/// 界面上这两类必须分得出来：左边那几格是人填的，右边那几格是算的，
/// 混在一起人会以为算出来的数也能直接改。
/// </summary>
public sealed class ChargeRowViewModel : ViewModelBase
{
    private readonly ChargeItem _m;
    private readonly Action _changed;
    private ChargeLine? _line;
    private bool _sel;

    public ChargeRowViewModel(ChargeItem model, Action changed)
    {
        _m = model;
        _changed = changed;
    }

    public ChargeItem Model => _m;

    /// <summary>算完的那一行。整表重算之后由视图模型灌进来。</summary>
    public ChargeLine? Line
    {
        get => _line;
        set
        {
            _line = value;
            RaiseAll(nameof(MolesText), nameof(MassText), nameof(VolumeText), nameof(EqText),
                     nameof(Hint), nameof(HasHint), nameof(HintColorHex), nameof(RowNote),
                     nameof(DeviationText), nameof(HasDeviation), nameof(DeviationColorHex),
                     nameof(ActualCalcText), nameof(HasActualCalc));
        }
    }

    public bool IsSelected { get => _sel; set => Set(ref _sel, value); }

    /// <summary>
    /// 整行重读一遍。**不经过属性设置器改了模型之后必须调它**——
    /// 「连到化合物库」是直接往模型上写名字和键的，不叫一声的话，
    /// 表格里那一格还是「新组分」，而右栏已经显示连上了（实测踩到过）。
    /// </summary>
    public void Refresh() => RaiseAll(nameof(Name), nameof(Cas), nameof(RoleText), nameof(BasisText),
                                      nameof(UnitText), nameof(AmountEdit), nameof(AmountText),
                                      nameof(AmountUnitText), nameof(AmountLabel), nameof(ShowUnit),
                                      nameof(IsProduct), nameof(MwText), nameof(DensityText),
                                      nameof(PurityText), nameof(PhaseText),
                                      nameof(Batch), nameof(Supplier), nameof(Note),
                                      nameof(ActualMassEdit), nameof(ActualVolumeEdit), nameof(ActualMassText));

    // ── 人填的 ──────────────────────────────────────────────────────

    public string Name
    {
        get => _m.Name;
        set => Edit(_m.Name, value ?? "", v => _m.Name = v);
    }

    public string RoleText
    {
        get => ChargeWords.Of(_m.Role);
        set
        {
            var role = ChargeWords.Roles.FirstOrDefault(r => ChargeWords.Of(r) == value);
            Edit(_m.Role, role, v => _m.Role = v, new[] { nameof(IsProduct) });
        }
    }

    public string BasisText
    {
        get => ChargeWords.Of(_m.Basis);
        set
        {
            var basis = ChargeWords.Bases.FirstOrDefault(b => ChargeWords.Of(b) == value);
            Edit(_m.Basis, basis, v => _m.Basis = v,
                 new[] { nameof(AmountUnitText), nameof(ShowUnit), nameof(AmountLabel) });
        }
    }

    public string UnitText
    {
        get => ChargeWords.Of(_m.Unit);
        set
        {
            var unit = ChargeWords.Units.FirstOrDefault(u => ChargeWords.Of(u) == value);
            Edit(_m.Unit, unit, v => _m.Unit = v, new[] { nameof(AmountUnitText) });
        }
    }

    public string AmountEdit
    {
        get => Raw(_m.Amount);
        set => Edit(_m.Amount, Parse(value), v => _m.Amount = v, new[] { nameof(AmountText) });
    }

    /// <summary>表格里那一格：数量 + 单位。基准不同单位就不同，得连着显示。</summary>
    public string AmountText => _m.Amount is null ? "—" : Raw(_m.Amount) + " " + AmountUnitText;

    /// <summary>这一行的数量是以什么计的。</summary>
    public string AmountUnitText => _m.Basis switch
    {
        ChargeBasis.Equivalents => "eq",
        ChargeBasis.Volumes => "mL/g",
        _ => ChargeWords.Of(_m.Unit)
    };

    public string AmountLabel => _m.Basis switch
    {
        ChargeBasis.Equivalents => "当量",
        ChargeBasis.Volumes => "倍量",
        _ => "给定量"
    };

    /// <summary>只有「直接给量」才要选单位；按当量 / 按倍量的单位是定死的。</summary>
    public bool ShowUnit => _m.Basis == ChargeBasis.Quantity;

    public bool IsProduct => _m.Role == ChargeRole.Product;

    public string MwText { get => Raw(_m.Mw); set => Edit(_m.Mw, Parse(value), v => _m.Mw = v); }

    /// <summary>投料形态。「未填」是合法状态——不知道就不填，校验对没填的不拦也不猜。</summary>
    public string PhaseText
    {
        get => _m.Phase.Length > 0 ? _m.Phase : "未填";
        set
        {
            var v = value == "固" || value == "液" ? value : "";
            Edit(_m.Phase, v, x => _m.Phase = x);
        }
    }
    public static IReadOnlyList<string> PhaseOptions { get; } = new[] { "未填", "固", "液" };
    public string DensityText { get => Raw(_m.Density); set => Edit(_m.Density, Parse(value), v => _m.Density = v); }
    public string PurityText { get => Raw(_m.Purity); set => Edit(_m.Purity, Parse(value), v => _m.Purity = v); }

    public string Batch { get => _m.Batch; set => Edit(_m.Batch, value ?? "", v => _m.Batch = v); }
    public string Supplier { get => _m.Supplier; set => Edit(_m.Supplier, value ?? "", v => _m.Supplier = v); }
    public string Note { get => _m.Note; set => Edit(_m.Note, value ?? "", v => _m.Note = v); }

    public string ActualMassEdit
    {
        get => Raw(_m.ActualMass);
        set => Edit(_m.ActualMass, Parse(value), v => _m.ActualMass = v, new[] { nameof(ActualMassText) });
    }

    public string ActualVolumeEdit
    {
        get => Raw(_m.ActualVolume);
        set => Edit(_m.ActualVolume, Parse(value), v => _m.ActualVolume = v);
    }

    public string ActualMassText => _m.ActualMass is null ? "—" : Raw(_m.ActualMass);

    /// <summary>实投折算一句话（CH-3.5）：折合多少 mmol、实际当量几何、比计划过量还是欠量。</summary>
    public string ActualCalcText
    {
        get
        {
            if (_line?.ActualMoles is not { } an) return "";
            var parts = new List<string> { $"实投折合 {Show(an)} mmol" };
            if (_line.ActualEquivalents is { } ae) parts.Add($"实际当量 {Show(ae)}");
            if (_line.ExcessPercent is { } ex)
                parts.Add((ex >= 0 ? "较计划过量 +" : "较计划欠量 −")
                          + Math.Abs(ex).ToString("0.#", CultureInfo.InvariantCulture) + " %");
            return string.Join("　·　", parts);
        }
    }
    public bool HasActualCalc => ActualCalcText.Length > 0;

    /// <summary>连库的键。连上了就显示那条化合物的名字，好让人知道物性是从哪来的。</summary>
    public string Cas => _m.Cas;

    // ── 算出来的 ────────────────────────────────────────────────────

    public string EqText => Show(_line?.Equivalents);
    public string MolesText => Show(_line?.Moles);
    public string MassText => Show(IsProduct ? _line?.TheoreticalMass : _line?.Mass);
    public string VolumeText => Show(_line?.Volume);

    /// <summary>实投跟应称量差多少。没称过就没有这个数，不是 0 %。</summary>
    public string DeviationText => _line?.MassDeviation is { } d
        ? (d >= 0 ? "+" : "") + d.ToString("0.#", CultureInfo.InvariantCulture) + " %" : "";
    public bool HasDeviation => _line?.MassDeviation is not null;
    /// <summary>差得超过 2 % 标红：称量误差到这个量级就该被看见了。</summary>
    public string DeviationColorHex => Math.Abs(_line?.MassDeviation ?? 0) > 2 ? "#c0392b" : "#7a7a7a";

    /// <summary>缺什么 / 按什么假设算的。表格最后一列。</summary>
    public string Hint => string.Join("；", (_line?.Missing ?? new List<string>())
                                     .Concat(_line?.Assumptions ?? new List<string>()));
    public bool HasHint => Hint.Length > 0;
    /// <summary>缺东西是红的，只是「按 100 % 计」这种假设是灰的——两者严重程度不同。</summary>
    public string HintColorHex => _line?.Missing.Count > 0 ? "#c0392b" : "#8a7a4a";

    /// <summary>右栏顶上那句：这一行的物性是从哪来的。</summary>
    public string RowNote
    {
        get
        {
            if (_line?.Reference is { } c)
            {
                if (_m.SnapshotAt is { } t)
                    return $"物性于 {t:MM-dd HH:mm} 从「{c.Name}」快照（库第 {_m.LibraryVersion} 版）。"
                         + "之后改库不影响本行，要同步请按「刷新库值」。";
                // 连着库却没盖章：快照机制之前的行，值还在跟着库漂——这必须让人看见
                return $"连着「{c.Name}」但还没做物性快照，空着的项在跟着库变。"
                     + "按「刷新库值」可落下快照。";
            }
            return _m.Cas.Length > 0
                ? "化合物库里找不到这个键，物性只能靠下面自己填。"
                : "这一行不连库，物性靠下面自己填。";
        }
    }

    // ── 小工具 ──────────────────────────────────────────────────────

    /// <summary>算不出来就是一条短横，不是 0——0 的意思是「不用加」。</summary>
    private static string Show(double? v)
    {
        if (v is not { } x) return "—";
        var a = Math.Abs(x);
        var fmt = a >= 1000 ? "0.#" : a >= 10 ? "0.##" : a >= 1 ? "0.###" : "0.####";
        return x.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private static string Raw(double? v)
        => v?.ToString("0.##########", CultureInfo.InvariantCulture) ?? "";

    private static double? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private void Edit<T>(T old, T value, Action<T> set, string[]? also = null,
                         [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(old, value)) return;
        set(value);
        Raise(name);
        if (also is not null) RaiseAll(also);
        _changed();
    }
}

/// <summary>
/// 配料表页。
///
/// **原型里没有这一页**——它是照着「化学表里的计算引擎直接连着化合物库，
/// 算出来的量再联到加料表」这条链补的。观感照台面属性那一套：
/// 白底面板 + 浅紫内容块，跟其余几页一致。
///
/// 一页管一个通道：四路各跑各的量，混在一张表里没法看。
/// </summary>
public sealed class ChargeViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private ChargeRowViewModel? _selected;
    private int _channel = 1;
    private ChargeResult? _result;
    private bool _loading;

    public ChargeViewModel(Workspace ws)
    {
        _ws = ws;

        AddRow = new RelayCommand(DoAddRow);
        DeleteRow = new RelayCommand(DoDeleteRow);
        ApplyToRecipe = new RelayCommand(DoApply);
        ReadBack = new RelayCommand(DoReadBack);
        WriteSaturation = new RelayCommand(DoWriteSaturation);
        RefreshLibrary = new RelayCommand(DoRefreshLibrary);

        foreach (var r in ChargeWords.Roles) Roles.Add(ChargeWords.Of(r));
        foreach (var b in ChargeWords.Bases) Bases.Add(ChargeWords.Of(b));
        foreach (var u in ChargeWords.Units) Units.Add(ChargeWords.Of(u));

        ws.BenchChanged += (_, _) => ReloadChannels();
        ws.Store.Changed += (_, _) => Reload();
        // 应用配方库 / 导入配方捎来的配料表整张换掉了通道那份——
        // Store.Changed 只在第一次标脏时发，这里必须自己听
        ws.ChargeReplaced += _ => Reload();
        ReloadChannels();
    }

    public RelayCommand AddRow { get; }
    public RelayCommand DeleteRow { get; }
    public RelayCommand ApplyToRecipe { get; }
    /// <summary>从这一炉的运行记录把「实取」填回来。</summary>
    public RelayCommand ReadBack { get; }
    /// <summary>把饱和温度写进选中的那条控温步骤。</summary>
    public RelayCommand WriteSaturation { get; }
    /// <summary>把连库行的物性改回库里的当前值（会覆盖行上手改的数，改了什么逐条报）。</summary>
    public RelayCommand RefreshLibrary { get; }

    /// <summary>
    /// 改这一路配方的入口，外壳接到配方页上。
    /// 参数是（通道、这次改动的名义、要做的事）——快照、改、刷新由配方页那边包住。
    /// </summary>
    public Action<int, string, Action>? EditRecipe { get; set; }

    public ObservableCollection<ChargeRowViewModel> Rows { get; } = new();
    public ObservableCollection<string> Channels { get; } = new();
    public ObservableCollection<string> Roles { get; } = new();
    public ObservableCollection<string> Bases { get; } = new();
    public ObservableCollection<string> Units { get; } = new();

    /// <summary>能挑的化合物：右栏那个「连到化合物库」下拉。</summary>
    public ObservableCollection<string> LibraryNames { get; } = new();

    public string ChannelText
    {
        get => "CH" + _channel;
        set
        {
            if (value is null || !value.StartsWith("CH", StringComparison.Ordinal)) return;
            if (!int.TryParse(value[2..], out var n) || n == _channel) return;
            _channel = n;
            Raise(nameof(ChannelText));
            Reload();
        }
    }

    public int Channel => _channel;

    private ChargeTable Table => _ws.ChargeOf(_channel);

    /// <summary>新加那一行的占位名。连库时按它判断「这个名字是我们填的、可以顶掉」。</summary>
    private const string NewRowName = "新组分";

    public ChargeRowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (value is null) return;
            var old = _selected;
            if (!Set(ref _selected, value)) return;
            if (old is not null) old.IsSelected = false;
            value.IsSelected = true;
            RaiseAll(nameof(HasSelection), nameof(LibraryPick));
        }
    }

    // ── 饱和温度 ────────────────────────────────────────────────────

    private IReadOnlyList<SaturationCase> _sat = Array.Empty<SaturationCase>();
    private string? _tempStepPick;
    private double _margin;

    /// <summary>能算饱和温度的那几条。一条都没有时这一块整个不出现。</summary>
    public bool HasSaturation => _sat.Count > 0;

    /// <summary>算出来的（或者算不出来的原因）。一行一句。</summary>
    public ObservableCollection<string> SaturationLines { get; } = new();

    /// <summary>算成了的第一条的温度。写进控温步骤用它。</summary>
    private SaturationCase? Solved => _sat.FirstOrDefault(c => c.Ok);

    public bool CanWriteSaturation => Solved is not null && TempSteps.Count > 0;

    /// <summary>这一路配方里有没有控温步骤。没有就别摆一个空下拉，直接说没有。</summary>
    public bool HasTempSteps => TempSteps.Count > 0;
    public bool NoTempSteps => TempSteps.Count == 0;

    /// <summary>这一路配方里的控温步骤。恒温保持没有目标温度，写不进去，不列。</summary>
    public ObservableCollection<string> TempSteps { get; } = new();

    public string? TempStepPick
    {
        get => _tempStepPick;
        set { if (Set(ref _tempStepPick, value)) Raise(nameof(CanWriteSaturation)); }
    }

    /// <summary>写进去的是饱和温度加这个余量。溶清常 +5 ~ +10，结晶终点是负的。</summary>
    public string MarginEdit
    {
        get => _margin.ToString("0.##", CultureInfo.InvariantCulture);
        set
        {
            var v = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0;
            if (Math.Abs(v - _margin) < 1e-9) return;
            _margin = v;
            RaiseAll(nameof(MarginEdit), nameof(TargetText));
        }
    }

    /// <summary>要写进去的那个数，先摆出来给人看。</summary>
    public string TargetText => Solved?.Result.Temperature is { } t
        ? (t + _margin).ToString("0.#", CultureInfo.InvariantCulture) + " ℃" : "—";

    /// <summary>每 1 g 产物需要多少限制试剂。只是个换算，不是「放大设计」。</summary>
    public string ScaleHint => _result?.LimitingPerProductGram is { } r
        ? $"每 1 g 目标产物需限制试剂 {r.ToString("0.###", CultureInfo.InvariantCulture)} g"
          + "（理论收率 100 % 计）" : "";

    public bool HasScaleHint => ScaleHint.Length > 0;

    public bool HasSelection => _selected is not null;
    /// <summary>表是空的。**台面上没通道的时候不算**——那时候该说的是「先去摆设备」，
    /// 两句一起摆出来，人会以为这是两个各自独立的问题。</summary>
    public bool IsEmpty => Rows.Count == 0 && HasChannels;

    /// <summary>右栏「连到化合物库」。选一条就把名字和键接过来，物性跟着库走。</summary>
    public string? LibraryPick
    {
        get => _selected?.Line?.Reference?.Name;
        set
        {
            if (_selected is null || value is null) return;
            var c = _ws.Compounds.FirstOrDefault(x => x.Name == value);
            if (c is null) return;

            _selected.Model.Cas = c.Cas;
            // 名字也接过来：加料步骤是按名字对上配料表的，两边得叫同一个名
            if (_selected.Model.Name.Length == 0 || _selected.Model.Name == NewRowName)
                _selected.Model.Name = c.Name;
            // 连库即快照：库值拷进行里（空才填）并盖上版本与时刻，
            // 之后库里怎么改都不影响这一行（CH-D1）
            ChargeSnapshot.Link(_selected.Model, c, _ws.Store.CompoundVersion, DateTimeOffset.Now);
            _selected.Refresh();          // 绕过设置器改的模型，得叫一声
            Recompute();
            _ws.Store.MarkDirty();
        }
    }

    /// <summary>釜容 mL。填了就拿总体积跟它对一下；空 = 不检查。</summary>
    public string VesselEdit
    {
        get => Table.VesselVolume?.ToString("0.##", CultureInfo.InvariantCulture) ?? "";
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? (double?)null
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
            if (Table.VesselVolume == v) return;
            Table.VesselVolume = v;
            Raise(nameof(VesselEdit));
            Recompute();
            _ws.Store.MarkDirty();
        }
    }

    // ── 合计 ────────────────────────────────────────────────────────

    public string LimitingText
    {
        get
        {
            if (_result?.Limiting is not { } l) return "未指定";
            var text = l.Item.Name.Length > 0 ? l.Item.Name : "（未命名）";
            if (l.Moles is { } n) text += $"　{n.ToString("0.###", CultureInfo.InvariantCulture)} mmol";
            return text;
        }
    }

    public string TotalMassText => _result?.TotalMass is { } m
        ? m.ToString("0.##", CultureInfo.InvariantCulture) + " g" : "—";

    public string TotalVolumeText => _result?.TotalVolume is { } v
        ? v.ToString("0.##", CultureInfo.InvariantCulture) + " mL" : "—";

    public string VesselColorHex => _result?.OverVessel == true ? "#c0392b" : "#3d3d3d";

    public string YieldText
    {
        get
        {
            var p = _result?.Lines.FirstOrDefault(l => l.Item.Role == ChargeRole.Product
                                                       && l.TheoreticalMass is not null);
            if (p is null) return "";
            var text = $"理论产量 {p.TheoreticalMass!.Value.ToString("0.###", CultureInfo.InvariantCulture)} g";
            if (p.Yield is { } y)
            {
                text += $"　实际 {p.Item.ActualMass!.Value.ToString("0.###", CultureInfo.InvariantCulture)} g"
                        + $"　收率 {y.ToString("0.#", CultureInfo.InvariantCulture)} %";
                // 收率的分母按限制试剂实投还是计划，两个数能差好几个点——必须连着说
                if (_result?.YieldBasis is { } basis) text += $"（{basis}）";
            }
            else text += "　实际产量未回填";
            return text;
        }
    }

    public bool HasYield => YieldText.Length > 0;

    /// <summary>摩尔浓度（CH-C7）。溶剂行有一个算不出体积就没有这个数——引擎不给就不显示。</summary>
    public string ConcentrationText => _result?.Concentration is { } c
        ? c.ToString(c >= 10 ? "0.#" : c >= 1 ? "0.##" : "0.###", CultureInfo.InvariantCulture) + " mol/L"
        : "";
    public bool HasConcentration => ConcentrationText.Length > 0;

    /// <summary>整张表的毛病 + 跟加料步骤对不上的地方。有一条就显示一条，不汇总成「有问题」。</summary>
    public ObservableCollection<string> Problems { get; } = new();
    public bool HasProblems => Problems.Count > 0;

    /// <summary>工具条那行状态。成了失败都写这儿，不弹框打断正在跑的实验。</summary>
    public string Status { get; private set; } = "";
    public bool HasStatus => Status.Length > 0;
    public bool StatusBad { get; private set; }
    public string StatusColorHex => StatusBad ? "#c0392b" : "#2f8f49";

    private void Say(string text, bool bad = false)
    {
        Status = text;
        StatusBad = bad;
        RaiseAll(nameof(Status), nameof(HasStatus), nameof(StatusBad), nameof(StatusColorHex));
    }

    // ── 动作 ────────────────────────────────────────────────────────

    /// <summary>加一行**空白的**。不预填任何物性——预填等于替人编数据。</summary>
    private void DoAddRow()
    {
        // 台面上没有这一路的话，存盘时泳道压根不存在，加进去的行会被悄悄丢掉。
        // 工具条上那几颗钮也是灰的，这里再兜一道
        if (NoChannels) { Say("台面上还没有通道，先去「台面」把反应器拖进来", bad: true); return; }

        var item = new ChargeItem
        {
            Name = NewRowName,
            // 头一行默认当限制试剂：整张表都是相对它算的，先把基准立起来
            Role = Table.Items.Any(i => i.Role == ChargeRole.Limiting)
                ? ChargeRole.Reagent : ChargeRole.Limiting,
            Basis = Table.Items.Any(i => i.Role == ChargeRole.Limiting)
                ? ChargeBasis.Equivalents : ChargeBasis.Quantity
        };
        Table.Items.Add(item);
        _ws.Store.MarkDirty();
        Reload();
        Selected = Rows.FirstOrDefault(r => ReferenceEquals(r.Model, item));
        Say("已加一行，请在右侧填名称与用量");
    }

    private void DoDeleteRow()
    {
        if (_selected is not { } row) { Say("先在表里选中一行", bad: true); return; }
        Table.Items.Remove(row.Model);
        _ws.Store.MarkDirty();
        Reload();
        Say($"已删除 {(row.Name.Length > 0 ? row.Name : "那一行")}");
    }

    /// <summary>
    /// 把算出来的体积填进配方里对得上的加料步骤，并给它们**建立引用**：
    /// 从此这些步骤的体积跟随配料表（自动同步走快照 + 审计，撤销接得住），
    /// 手改体积即脱离。「明确的动作」收缩成建立引用这一次——之后的跟随
    /// 是引用语义本身，不再是「有人在操作人没看的时候改了工艺」。
    /// </summary>
    private void DoApply()
    {
        if (!_ws.ChannelRecipes.TryGetValue(_channel, out var recipe))
        {
            Say($"CH{_channel} 还没有配方", bad: true);
            return;
        }
        if (_result is null) { Say("配料表还没算出结果", bad: true); return; }

        var links = ChargeLink.Match(recipe, _ws.Catalog, _result);
        if (links.Count == 0)
        {
            Say($"CH{_channel} 的配方里没有加料步骤", bad: true);
            return;
        }

        ChargeApplyResult? done = null;
        if (EditRecipe is { } edit) edit(_channel, "charge-apply", () => done = ChargeLink.Apply(links));
        else done = ChargeLink.Apply(links);
        if (done is null || done.IsEmpty)
        {
            var why = links.All(l => !l.Matched)
                ? "没有一步的料液名跟配料表里的组分对得上"
                : "对得上的那几步早已连着配料表，体积也一致";
            Say("没有改动任何步骤：" + why);
            return;
        }

        _ws.Store.MarkDirty();
        var parts = new List<string>();
        if (done.Volumes.Count > 0)
        {
            var what = string.Join("、", done.Volumes.Take(3).Select(d =>
                $"第 {d.Entry.StepIndex + 1} 步 {d.Entry.Liquid} {Ml(d.Before)} → {Ml(d.After)} mL"));
            if (done.Volumes.Count > 3) what += $" 等 {done.Volumes.Count} 处";
            parts.Add($"体积 {done.Volumes.Count} 处：{what}");
        }
        if (done.NewLinks.Count > 0)
            parts.Add($"建立引用 {done.NewLinks.Count} 步（此后体积跟随配料表，手改即脱离）");
        var said = string.Join("；", parts);
        Say("已应用到加料步骤：" + said);
        _ws.Log?.Write("配料表", $"CH{_channel} 应用到加料步骤：{said}", _ws.Operator);
        Recompute();
    }

    /// <summary>体积、质量在提示语里的写法。两位小数，泵的刻度到这儿就够。</summary>
    private static string Ml(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    /// <summary>
    /// 从这一炉的运行记录把「实取」填回来。
    ///
    /// 泵的累计加料量一直在采，实投从前却只能人手填——计划与实际在物料这一侧
    /// 本来就能闭环，缺的只是接上。读不出来的那几步照实说，不拿计划量顶。
    /// </summary>
    /// <summary>
    /// 「刷新库值」：把本通道全部连库行的物性改回化合物库的**当前**值并重新盖章。
    /// 这是快照机制里唯一一条「跟上库」的路，而且是人显式按出来的——
    /// 改了什么逐条列在状态条上，盖掉了行上手改的数也看得见。
    /// </summary>
    private void DoRefreshLibrary()
    {
        var linked = Table.Items.Count(i => i.Cas.Length > 0);
        if (linked == 0) { Say("这一路没有连库的行，没什么可刷新的", bad: true); return; }

        var changes = ChargeSnapshot.Refresh(Table, _ws.Compounds.ToList(),
                                             _ws.Store.CompoundVersion, DateTimeOffset.Now);
        foreach (var r in Rows) r.Refresh();
        Recompute();
        _ws.Store.MarkDirty();

        if (changes.Count == 0) { Say($"{linked} 个连库行与库一致，只更新了快照时刻"); return; }
        // 状态条一行放不下十几条改动；报前几条，剩下的归个总数，别把话吞掉
        var shown = string.Join("；", changes.Take(4));
        Say(changes.Count <= 4 ? "已按库更新：" + shown
                               : $"已按库更新：{shown}；等共 {changes.Count} 处");
    }

    private void DoReadBack()
    {
        var run = _ws.Engine.Record.Channels.LastOrDefault(c => c.Channel == _channel);
        if (run is null) { Say($"CH{_channel} 这一炉还没有运行记录", bad: true); return; }
        if (_result is null || Table.IsEmpty) { Say("配料表是空的", bad: true); return; }

        var read = DoseReadback.Of(run, _ws.Pipeline, _ws.Catalog);
        if (read.Count == 0) { Say($"CH{_channel} 这一炉没有加料步骤", bad: true); return; }

        var done = DoseReadback.Apply(Table, read, _result);
        var stuck = read.Where(r => r.Problem is not null).ToList();

        if (done.Count == 0)
        {
            var why = stuck.Count > 0
                ? stuck[0].Problem!
                : "加料步骤的料液名跟配料表里的组分对不上";
            Say("没有回填任何一行：" + why, bad: true);
            return;
        }

        Reload();
        _ws.Store.MarkDirty();
        var what = string.Join("、", done.Take(3).Select(d =>
            $"{d.Name} {Ml(d.Volume)} mL" + (d.Mass is { } m ? $"（{Ml(m)} g）" : "")));
        if (done.Count > 3) what += $" 等 {done.Count} 项";
        var msg = $"已从运行记录回填 {done.Count} 行实取：{what}";
        if (stuck.Count > 0) msg += $"；另有 {stuck.Count} 步读不出来（{stuck[0].Problem}）";
        Say(msg, bad: stuck.Count > 0);
        _ws.Log?.Write("配料表", $"CH{_channel} 从运行记录回填实取 {done.Count} 行：{what}", _ws.Operator);
    }

    /// <summary>
    /// 把饱和温度写进选中的那条控温步骤。
    ///
    /// 跟「应用到加料步骤」同一个道理：**是一次明确的动作**，改了哪一步、
    /// 从多少改成多少都说出来，配方页撤销得回去。
    /// </summary>
    private void DoWriteSaturation()
    {
        if (Solved is not { } sat || sat.Result.Temperature is not { } t)
        { Say("现在没有算得出来的饱和温度", bad: true); return; }
        if (!_ws.ChannelRecipes.TryGetValue(_channel, out var recipe))
        { Say($"CH{_channel} 还没有配方", bad: true); return; }

        var index = StepIndexOf(_tempStepPick);
        if (index is not { } i || i >= recipe.Steps.Count)
        { Say("先选一条控温步骤", bad: true); return; }

        var target = Math.Round(t + _margin, 1);
        var before = recipe.Steps[i].Parameters.Num("target");
        if (Math.Abs(before - target) < 0.05) { Say("这一步的目标温度已经是这个数了"); return; }

        if (EditRecipe is { } edit)
            edit(_channel, "charge-saturation", () => recipe.Steps[i].Parameters["target"] = target);
        else recipe.Steps[i].Parameters["target"] = target;

        _ws.Store.MarkDirty();
        var margin = _margin == 0 ? "" : $"（饱和温度 {F1(t)} ℃ {(_margin > 0 ? "+" : "−")} {F1(Math.Abs(_margin))}）";
        Say($"第 {i + 1} 步的目标温度 {F1(before)} → {F1(target)} ℃{margin}");
        _ws.Log?.Write("配料表",
            $"CH{_channel} 第 {i + 1} 步目标温度按饱和温度改为 {F1(target)} ℃"
            + $"（{sat.Basis}）", _ws.Operator);
        RefreshTempSteps();
    }

    private static string F1(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>下拉里那一条对应配方里第几步。列表项形如「第 3 步 · 控温 至 60 ℃」。</summary>
    private static int? StepIndexOf(string? label)
    {
        if (label is null) return null;
        var a = label.IndexOf('第');
        var b = label.IndexOf('步');
        if (a < 0 || b <= a) return null;
        return int.TryParse(label[(a + 1)..b].Trim(), out var n) ? n - 1 : null;
    }

    /// <summary>重建控温步骤下拉。恒温保持没有目标温度，写不进去，所以不列。</summary>
    private void RefreshTempSteps()
    {
        var keep = _tempStepPick;
        TempSteps.Clear();
        if (_ws.ChannelRecipes.TryGetValue(_channel, out var recipe))
            for (var i = 0; i < recipe.Steps.Count; i++)
            {
                var step = recipe.Steps[i];
                if (step.CommandId != CommandSpecs.Control) continue;
                TempSteps.Add($"第 {i + 1} 步 · 控温 至 "
                              + F1(step.Parameters.Num("target")) + " ℃");
            }
        _tempStepPick = TempSteps.Contains(keep ?? "") ? keep : TempSteps.FirstOrDefault();
        RaiseAll(nameof(TempStepPick), nameof(CanWriteSaturation),
                 nameof(HasTempSteps), nameof(NoTempSteps));
    }

    // ── 重建 ────────────────────────────────────────────────────────

    private void ReloadChannels()
    {
        var live = _ws.Channels.Where(c => c.Enabled).Select(c => c.Number).OrderBy(x => x).ToList();
        Channels.Clear();
        foreach (var n in live) Channels.Add("CH" + n);

        if (live.Count > 0 && !live.Contains(_channel)) _channel = live[0];
        Raise(nameof(ChannelText));
        RaiseAll(nameof(NoChannels), nameof(HasChannels), nameof(IsEmpty));
        Reload();
    }

    /// <summary>台面上一个通道都没有。这一页就没得配——照实说，不摆一张空表。</summary>
    public bool NoChannels => Channels.Count == 0;
    public bool HasChannels => Channels.Count > 0;

    public void Reload()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var keep = _selected?.Model.Id;
            Rows.Clear();
            foreach (var item in Table.Items) Rows.Add(new ChargeRowViewModel(item, OnRowChanged));
            _selected = null;
            Recompute();
            Selected = Rows.FirstOrDefault(r => r.Model.Id == keep) ?? Rows.FirstOrDefault();
            RaiseAll(nameof(IsEmpty), nameof(VesselEdit));
        }
        finally { _loading = false; }
    }

    private void OnRowChanged()
    {
        Recompute();
        _ws.Store.MarkDirty();
    }

    /// <summary>整表重算。算一遍很便宜，改一个字就重算，界面上永远是当前的数。</summary>
    private void Recompute()
    {
        _result = Stoichiometry.Solve(Table, _ws.Compounds.ToList());
        for (var i = 0; i < Rows.Count && i < _result.Lines.Count; i++)
            Rows[i].Line = _result.Lines[i];

        Problems.Clear();
        foreach (var p in _result.Problems) Problems.Add(p);

        // 跟配方里的加料步骤对不对得上，也算这一页要回答的问题
        if (_ws.ChannelRecipes.TryGetValue(_channel, out var recipe) && Table.Items.Count > 0)
            foreach (var e in ChargeLink.Match(recipe, _ws.Catalog, _result))
            {
                if (!e.Matched)
                    Problems.Add($"第 {e.StepIndex + 1} 步的料液「{Show(e.Liquid)}」"
                                 + "在这张表里没有同名组分，它的体积不会跟着配料表走");
                else if (e.Differs)
                    Problems.Add($"第 {e.StepIndex + 1} 步加「{Show(e.Liquid)}」{Ml(e.StepVolume)} mL，"
                                 + $"配料表算出来是 {Ml(e.PlannedVolume!.Value)} mL");
            }

        // 名单没变就不动它：每次重算都全清全建的话，ComboBox 的 ItemsSource 一抖，
        // 「连到化合物库」的选中项就被打成空白（实测：随便改个给定量下拉就空了）
        var names = _ws.Compounds.OrderBy(c => c.Name, StringComparer.Ordinal)
                                 .Select(c => c.Name).ToList();
        if (!names.SequenceEqual(LibraryNames))
        {
            LibraryNames.Clear();
            foreach (var n in names) LibraryNames.Add(n);
        }

        // 饱和温度：能算的算，算不了的把原因摆出来——那句原因本身就是要给人看的
        _sat = ChargeSaturation.Of(_result);
        SaturationLines.Clear();
        foreach (var c in _sat)
            SaturationLines.Add(c.Ok
                ? $"{c.Solute.Item.Name}：饱和温度 {F1(c.Result.Temperature!.Value)} ℃　·　{c.Basis}"
                : $"{c.Solute.Item.Name}：{c.Result.Problem}");
        RefreshTempSteps();

        RaiseAll(nameof(LimitingText), nameof(TotalMassText), nameof(TotalVolumeText),
                 nameof(VesselColorHex), nameof(YieldText), nameof(HasYield),
                 nameof(ConcentrationText), nameof(HasConcentration),
                 nameof(HasProblems), nameof(LibraryPick),
                 nameof(HasSaturation), nameof(CanWriteSaturation), nameof(TargetText),
                 nameof(ScaleHint), nameof(HasScaleHint));

        // 跟随改了配方的话，问题条里「体积不一致」那几条已经过时——整个再算一遍。
        // _following 挡住递归：第二遍里跟随无事可做，必然收敛
        if (FollowAll()) { Recompute(); return; }
    }

    private bool _following;

    /// <summary>
    /// 跟随（CH-4.2）：配料表一变，把 linked 加料步骤的体积同步成算出来的值。
    /// 走 EditRecipe（快照 → 改 → 刷新），配方页撤销拉得回来；改了什么写系统日志。
    /// **全部通道都查**，不只当前通道——只查当前的话，切到别的通道之前
    /// 它的引用一直停在旧值上。只动建立过引用且在跟随的步骤；
    /// 按名字对的、已脱离的、算不出体积的一概不碰。
    /// </summary>
    private bool FollowAll()
    {
        if (_following) return false;
        _following = true;
        try
        {
            var lib = _ws.Compounds.ToList();
            var said = new List<string>();
            foreach (var (ch, table) in _ws.ChannelCharges.ToList())
            {
                if (table.IsEmpty) continue;
                if (!_ws.ChannelRecipes.TryGetValue(ch, out var recipe)) continue;
                var solved = ch == _channel && _result is not null
                    ? _result : Stoichiometry.Solve(table, lib);
                var links = ChargeLink.Match(recipe, _ws.Catalog, solved);
                if (!links.Any(ChargeLink.NeedsFollow)) continue;

                IReadOnlyList<(ChargeLinkEntry Entry, double Before, double After)> done =
                    Array.Empty<(ChargeLinkEntry, double, double)>();
                if (EditRecipe is { } edit) edit(ch, "charge-follow", () => done = ChargeLink.Follow(links));
                else done = ChargeLink.Follow(links);
                if (done.Count == 0) continue;

                var what = string.Join("、", done.Take(3).Select(d =>
                    $"第 {d.Entry.StepIndex + 1} 步 {d.Entry.Liquid} {Ml(d.Before)} → {Ml(d.After)} mL"));
                if (done.Count > 3) what += $" 等 {done.Count} 处";
                said.Add($"CH{ch} {what}");
                _ws.Log?.Write("配料表",
                    $"CH{ch} 配料表变更，跟随更新加料体积 {done.Count} 处：{what}", _ws.Operator);
            }
            if (said.Count > 0)
            {
                _ws.Store.MarkDirty();
                Say("已跟随配料表更新加料体积：" + string.Join("；", said));
            }
            return said.Count > 0;
        }
        finally { _following = false; }
    }

    private static string Show(string s) => s.Length > 0 ? s : "（没填）";
}
