using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using Tec.Driver.Abi;

namespace Tec.Io8rTest;

public abstract class Bindable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>一路 DO 一行：名字、接线、读回的位置、点一下切。</summary>
public sealed class DoRow : Bindable
{
    private readonly MainViewModel _owner;
    private bool _closed;

    public DoRow(MainViewModel owner, DoPoint p)
    {
        _owner = owner;
        Point = p;
    }

    public DoPoint Point { get; }
    public string Tag => $"DO{Point.Index}";
    public string Name => Point.Name;
    public string Note => Point.Note;

    /// <summary>读回的位置（不是自己记的）：模块面板上人手扳了也照实显示。</summary>
    public bool Closed
    {
        get => _closed;
        set
        {
            Set(ref _closed, value);
            Raise(nameof(StateText));
            Raise(nameof(ButtonText));
        }
    }

    public string StateText => Closed ? "闭合" : "断开";
    public string ButtonText => Closed ? "断开" : "合上";

    public void Toggle() => _owner.Toggle(this);
}

public sealed class MainViewModel : Bindable
{
    private Io8rTester? _io;
    private readonly DispatcherTimer _poll;
    private bool _busy;
    private string _port = "COM10";
    private string _baud = "9600";
    private string _station = "1";
    private string _status = "未连接";
    private string _portsHint = "";
    private string? _warning;
    private string _inputs = "";

    public MainViewModel()
    {
        Rows = new ObservableCollection<DoRow>(Io8rTester.Points.Select(p => new DoRow(this, p)));
        RefreshPorts();
        // 每秒读回一次：模块面板上有按键，人手扳了这里照实变
        _poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _poll.Tick += async (_, _) => await PollAsync();
    }

    public string Heading => "IO8R 继电器测试";
    public ObservableCollection<DoRow> Rows { get; }
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>现场缺省 COM10 / 9600 / 站号 1（跟双工位主机的连接参数缺省一样）。</summary>
    public string Port { get => _port; set => Set(ref _port, value); }
    public string Baud { get => _baud; set => Set(ref _baud, value); }
    public string Station { get => _station; set => Set(ref _station, value); }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string PortsHint { get => _portsHint; private set => Set(ref _portsHint, value); }
    public string? Warning { get => _warning; private set { Set(ref _warning, value); Raise(nameof(HasWarning)); } }
    public bool HasWarning => Warning is not null;
    public string Inputs { get => _inputs; private set => Set(ref _inputs, value); }

    public bool Connected => _io is not null;
    public bool Disconnected => _io is null;

    public void RefreshPorts()
    {
        try
        {
            var ports = System.IO.Ports.SerialPort.GetPortNames().OrderBy(SerialPortChoices.NumberIn).ToArray();
            PortsHint = ports.Length == 0 ? "本机没检测到串口" : "本机串口：" + string.Join("、", ports);
        }
        catch (Exception ex) { PortsHint = "串口枚举失败：" + ex.Message; }
    }

    public async void Connect()
    {
        if (_io is not null || _busy) return;
        if (!int.TryParse(Baud.Trim(), out var baud) || baud <= 0) { Say("波特率填的不是数字：" + Baud); return; }
        if (!byte.TryParse(Station.Trim(), out var st) || st == 0) { Say("站号要填 1~247：" + Station); return; }
        var port = Port.Trim();
        _busy = true;
        Status = $"正在连接 {port} …";
        try
        {
            _io = await Io8rTester.OpenSerialAsync(port, baud, st, CancellationToken.None);
            Status = $"已连接 {port} · {baud} · 站号 {st}";
            Say($"已连接 {port}（{baud}，站号 {st}），读回当前位置；断开 / 关程序时八路全断");
            Show();
            _poll.Start();
        }
        catch (Exception ex)
        {
            Status = "未连接";
            Say($"连不上 {port}：{ex.Message}" +
                "——串口被占用的话，多半是 HMI / 工作站 / 极限测试工具连着这块 IO8R，先在那边断开设备");
        }
        finally
        {
            _busy = false;
            Raise(nameof(Connected));
            Raise(nameof(Disconnected));
        }
    }

    public async void Disconnect()
    {
        if (_io is not { } io || _busy) return;
        _busy = true;
        _poll.Stop();
        try
        {
            await io.DisposeAsync();
            Say("已断开：八路全断");
        }
        finally
        {
            _io = null;
            _busy = false;
            Status = "未连接";
            foreach (var r in Rows) r.Closed = false;
            Warning = null;
            Inputs = "";
            Raise(nameof(Connected));
            Raise(nameof(Disconnected));
        }
    }

    public async void Toggle(DoRow row)
    {
        if (_io is not { } io || _busy) return;
        var want = !row.Closed;
        _busy = true;
        try
        {
            await io.SetAsync(row.Point.Index, want, CancellationToken.None);
            Say($"{row.Tag}（{row.Name}）已{(want ? "合上" : "断开")}");
        }
        catch (Exception ex) { Say($"{row.Tag}（{row.Name}）{(want ? "合" : "断")}不了：{ex.Message}"); }
        finally
        {
            _busy = false;
            Show();
        }
    }

    public async void AllOff()
    {
        if (_io is not { } io || _busy) return;
        _busy = true;
        try
        {
            await io.AllOffAsync(CancellationToken.None);
            Say("八路全断");
        }
        catch (Exception ex) { Say("全断失败：" + ex.Message); }
        finally
        {
            _busy = false;
            Show();
        }
    }

    /// <summary>关程序：八路全断再关串口（在线程池上等，别在界面线程上死等异步）。</summary>
    public void Shutdown()
    {
        _poll.Stop();
        if (_io is { } io)
            Task.Run(() => io.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
        _io = null;
    }

    private async Task PollAsync()
    {
        if (_io is not { } io || _busy) return;
        _busy = true;
        try
        {
            await io.RefreshAsync(CancellationToken.None);
            Show();
        }
        catch (Exception ex) { Status = "读回失败：" + ex.Message; }
        finally { _busy = false; }
    }

    private void Show()
    {
        if (_io is not { } io) return;
        foreach (var r in Rows) r.Closed = io.Relays[r.Point.Index];
        Warning = io.Warning;
        Inputs = string.Join("   ", io.Inputs.Select((on, i) => $"DI{i} {(on ? "●" : "○")}"));
    }

    private void Say(string text)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
        while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
    }
}
