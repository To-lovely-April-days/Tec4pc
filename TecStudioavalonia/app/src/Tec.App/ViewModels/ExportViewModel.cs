using System.Collections.ObjectModel;
using System.Text;
using Tec.App.Services;
using Tec.Core;
using Tec.Core.Export;
using Tec.Core.Records;

namespace Tec.App.ViewModels;

public sealed class ChannelPickViewModel : ViewModelBase
{
    private bool _selected = true;
    public ChannelPickViewModel(int number, bool started)
    {
        Number = number;
        Started = started;
        _selected = started;
    }
    public int Number { get; }
    public bool Started { get; }
    public string Name => $"CH{Number}" + (Started ? "" : "（未启动）");
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

public sealed class TagPickViewModel : ViewModelBase
{
    private bool _selected = true;
    public TagPickViewModel(string tag, string display)
    {
        Tag = tag;
        Display = display;
    }
    public string Tag { get; }
    public string Display { get; }
    public bool Selected
    {
        get => _selected;
        set => Set(ref _selected, value);
    }
}

/// <summary>
/// 导出。执行记录是这里最值钱的一份——它回答的是
/// "这一步到底跑了多久、跟计划差多少"，而且每个通道各有各的答案。
/// </summary>
public sealed class ExportViewModel : ViewModelBase
{
    private readonly Workspace _ws;
    private bool _wallBase = true;
    private bool _wideTable;
    private bool _includeExecution = true;
    private bool _includeEvents = true;
    private bool _includeSamples = true;
    private string _preview = "";
    private string _status = "";

    public ExportViewModel(Workspace ws)
    {
        _ws = ws;
        Refresh = new RelayCommand(() => BuildPreview());
        Save = new RelayCommand(() => SaveFiles());
        Reload();
    }

    public ObservableCollection<ChannelPickViewModel> Channels { get; } = new();
    public ObservableCollection<TagPickViewModel> Tags { get; } = new();

    public RelayCommand Refresh { get; }
    public RelayCommand Save { get; }

    public bool WallBase
    {
        get => _wallBase;
        set { if (Set(ref _wallBase, value)) { Raise(nameof(BaseNote)); BuildPreview(); } }
    }

    public bool WideTable
    {
        get => _wideTable;
        set { if (Set(ref _wideTable, value)) { Raise(nameof(ShapeNote)); BuildPreview(); } }
    }

    public bool IncludeExecution
    {
        get => _includeExecution;
        set { if (Set(ref _includeExecution, value)) BuildPreview(); }
    }

    public bool IncludeEvents
    {
        get => _includeEvents;
        set { if (Set(ref _includeEvents, value)) BuildPreview(); }
    }

    public bool IncludeSamples
    {
        get => _includeSamples;
        set { if (Set(ref _includeSamples, value)) BuildPreview(); }
    }

    public string BaseNote => _wallBase
        ? "墙钟：跨通道对齐，看的是谁跟谁在同时跑"
        : "各通道基准：以自己的启动时刻为 0，宽表会按通道分块出——通道各自启动，硬塞一张表必然一半空格";

    public string ShapeNote => _wideTable
        ? "宽表：一行一个时刻，一列一个通道-标签"
        : "长表：一行一个点，通道 × 标签任意多，不会有空列";

    public string Preview
    {
        get => _preview;
        private set => Set(ref _preview, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string Summary
    {
        get
        {
            var rec = _ws.Engine.Record;
            if (rec.Channels.Count == 0) return "尚无运行记录可导出";
            var steps = rec.Channels.Sum(c => c.Steps.Count);
            var events = rec.Channels.Sum(c => c.Events.Count);
            var sim = rec.Channels.Any(c => c.Simulated) ? "（含仿真数据，已在每行标出来源）" : "";
            return $"批次 {rec.RunId} · {rec.Channels.Count} 个通道 · {steps} 条步骤记录 · {events} 条事件 {sim}";
        }
    }

    public void Reload()
    {
        var rec = _ws.Engine.Record;
        var started = rec.StartedChannels;
        Channels.Clear();
        foreach (var ch in _ws.Channels)
            Channels.Add(new ChannelPickViewModel(ch.Number, started.Contains(ch.Number)));

        Tags.Clear();
        foreach (var t in _ws.Pipeline.Tags.OrderBy(t => t.Tag, StringComparer.Ordinal))
            Tags.Add(new TagPickViewModel(t.Tag, string.IsNullOrEmpty(t.Unit) ? t.DisplayName : $"{t.DisplayName}（{t.Unit}）"));

        Raise(nameof(Summary));
        BuildPreview();
    }

    private ExportOptions Options()
    {
        var opt = new ExportOptions
        {
            TimeBase = _wallBase ? TimeBase.Wall : TimeBase.Channel,
            Shape = _wideTable ? TableShape.Wide : TableShape.Long,
            IncludeExecution = _includeExecution,
            IncludeEvents = _includeEvents,
            IncludeSamples = _includeSamples,
            Grid = TimeSpan.FromSeconds(5)
        };
        opt.Channels.AddRange(Channels.Where(c => c.Selected).Select(c => c.Number));
        opt.Tags.AddRange(Tags.Where(t => t.Selected).Select(t => t.Tag));
        return opt;
    }

    private void BuildPreview()
    {
        var rec = _ws.Engine.Record;
        if (rec.Channels.Count == 0)
        {
            Preview = "尚无运行记录。\n\n归档实验的执行记录由记录文件读取——没有就是没有，不拿运行中的数据顶替。";
            Raise(nameof(Summary));
            return;
        }

        var opt = Options();
        var sb = new StringBuilder();
        if (opt.IncludeExecution) sb.AppendLine(Head(RecordExporter.ExecutionCsv(rec, opt.TimeBase), 24));
        if (opt.IncludeEvents) sb.AppendLine(Head(RecordExporter.EventsCsv(rec, opt.TimeBase), 12));
        if (opt.IncludeSamples)
            sb.AppendLine(Head(opt.Shape == TableShape.Wide
                ? RecordExporter.SamplesWideCsv(_ws.Pipeline, rec, opt)
                : RecordExporter.SamplesLongCsv(_ws.Pipeline, rec, opt), 16));
        Preview = sb.ToString();
        Raise(nameof(Summary));
    }

    private static string Head(string text, int lines)
    {
        var all = text.Split('\n');
        var take = Math.Min(lines, all.Length);
        var body = string.Join("\n", all.Take(take)).TrimEnd();
        return all.Length > take ? body + $"\n… 共 {all.Length} 行" : body;
    }

    private void SaveFiles()
    {
        var rec = _ws.Engine.Record;
        if (rec.Channels.Count == 0) { Status = "没有可导出的记录。"; return; }

        var opt = Options();
        var dir = Path.Combine(AppContext.BaseDirectory, "exports", rec.RunId);
        Directory.CreateDirectory(dir);

        try
        {
            if (opt.IncludeExecution)
                File.WriteAllText(Path.Combine(dir, "执行记录.csv"),
                    RecordExporter.ExecutionCsv(rec, opt.TimeBase), new UTF8Encoding(true));
            if (opt.IncludeEvents)
                File.WriteAllText(Path.Combine(dir, "事件记录.csv"),
                    RecordExporter.EventsCsv(rec, opt.TimeBase), new UTF8Encoding(true));
            if (opt.IncludeSamples)
                File.WriteAllText(Path.Combine(dir, opt.Shape == TableShape.Wide ? "采样宽表.csv" : "采样长表.csv"),
                    opt.Shape == TableShape.Wide
                        ? RecordExporter.SamplesWideCsv(_ws.Pipeline, rec, opt)
                        : RecordExporter.SamplesLongCsv(_ws.Pipeline, rec, opt), new UTF8Encoding(true));

            // GLP：同时落一份只追加、带链式摘要的记录文件
            using (var store = new RecordStore(Path.Combine(dir, "run.glp")))
            {
                foreach (var ch in rec.Channels)
                {
                    store.Write(ch);
                    foreach (var s in ch.Steps) store.Write(ch.Channel, s);
                    foreach (var e in ch.Events) store.Write(e);
                }
                store.Sign(rec.Operator ?? "操作员", "导出时签名");
            }

            Status = $"已导出到 {dir}";
        }
        catch (Exception ex)
        {
            Status = "导出失败：" + ex.Message;
        }
    }
}
