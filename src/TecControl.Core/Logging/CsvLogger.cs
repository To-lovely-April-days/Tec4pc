using System.Globalization;
using TecControl.Core.Control;
using TecControl.Core.Protocol;

namespace TecControl.Core.Logging;

/// <summary>把控制周期数据追加写入 CSV 文件（线程安全）。</summary>
public sealed class CsvLogger : IDisposable
{
    private readonly object _lock = new();
    private StreamWriter? _writer;

    public string? CurrentPath { get; private set; }
    public bool IsRunning => _writer is not null;

    public void Start(string path)
    {
        lock (_lock)
        {
            StopCore();
            var writeHeader = !File.Exists(path) || new FileInfo(path).Length == 0;
            _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8);
            if (writeHeader)
                _writer.WriteLine(
                    "时间,内部温度TC1(℃),腔体温度TC2(℃),主环设定(℃),内环设定(℃),占空比(%)," +
                    "通道1电流(A),通道2电流(A),通道1输出电压(V),通道2输出电压(V),机内温度(℃),曲线进度,告警码");
            _writer.Flush();
            CurrentPath = path;
        }
    }

    public void Append(ControlCycleResult cycle, TecErrorCode lastError)
    {
        lock (_lock)
        {
            if (_writer is null) return;
            var s = cycle.Snapshot;
            var c = cycle.Ch1;
            var running = c.Active || c.Tuning;

            _writer.WriteLine(string.Join(',',
                s.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                Num(s.Temp1C, "F5"),
                Num(s.Temp2C, "F5"),
                running ? Num(c.SetpointC, "F5") : "",
                running ? Num(c.InnerSetpointC, "F5") : "",
                running ? Num(c.DutyPercent, "F2") : "",
                cycle.Current1A is double i1 ? Num(i1, "F3") : "",
                cycle.Current2A is double i2 ? Num(i2, "F3") : "",
                Num(s.OutVolts1, "F3"),
                Num(s.OutVolts2, "F3"),
                Num(s.InternalTempC, "F0"),
                c.ProfileProgress is double p ? Num(p, "F4") : "",
                ((ushort)lastError).ToString(CultureInfo.InvariantCulture)));
            _writer.Flush();
        }
    }

    public void Stop()
    {
        lock (_lock) StopCore();
    }

    private void StopCore()
    {
        _writer?.Dispose();
        _writer = null;
        CurrentPath = null;
    }

    private static string Num(double value, string format) =>
        double.IsNaN(value) ? "" : value.ToString(format, CultureInfo.InvariantCulture);

    public void Dispose() => Stop();
}
