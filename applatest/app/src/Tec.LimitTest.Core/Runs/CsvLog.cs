using System.Globalization;
using System.Text;

namespace Tec.LimitTest.Runs;

/// <summary>
/// 一次测试的每秒全量记录（表里只放记录点那一行，这里什么都留）。
/// UTF-8 带 BOM，Excel 直接双击能看中文；每行写完就 Flush——中途断电到那一秒为止都在。
/// </summary>
public sealed class CsvLog : IDisposable
{
    private readonly StreamWriter _w;

    public CsvLog(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? ".");
        _w = new StreamWriter(path, false, new UTF8Encoding(true));
        _w.WriteLine("时间 s,墙钟,A 夹套 ℃,A 釜内 ℃,A 输出 %,A 电流 A,A 热源,A 功率线,B 夹套 ℃,B 釜内 ℃,B 输出 %,B 电流 A,B 热源,B 功率线,阶段");
        _w.Flush();
        Path = path;
    }

    public string Path { get; }

    public void Write(DateTimeOffset wall, TimeSpan elapsed, WellReading a, WellReading b, string phase)
    {
        _w.Write(elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture));
        _w.Write(',');
        _w.Write(wall.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        foreach (var r in new[] { a, b })
        {
            _w.Write(',');
            _w.Write(N(r.Tj, "0.00"));
            _w.Write(',');
            _w.Write(N(r.Tr, "0.00"));
            _w.Write(',');
            _w.Write(N(r.Out, "0.0"));
            _w.Write(',');
            _w.Write(N(r.Cur, "0.000"));
            _w.Write(',');
            _w.Write(r.Source ?? "");
            _w.Write(',');
            _w.Write(r.TecPower is { } p ? (p ? "通" : "断") : "");
        }
        _w.Write(',');
        _w.WriteLine(phase);
        _w.Flush();
    }

    private static string N(double? v, string fmt)
        => v is { } d && !double.IsNaN(d) ? d.ToString(fmt, CultureInfo.InvariantCulture) : "";

    public void Dispose() => _w.Dispose();
}
