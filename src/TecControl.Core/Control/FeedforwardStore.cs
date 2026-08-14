using System.Globalization;

namespace TecControl.Core.Control;

/// <summary>
/// 稳态前馈工作点的磁盘持久化（简单 CSV，便于人工查看与编辑）。
/// 学到的温度-输出关系跨程序重启保留，宽温域使用时无需每次重新学习。
/// </summary>
public static class FeedforwardStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TecStudio", "feedforward.csv");

    public static void Save(SteadyStateFeedforward ff, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var w = new StreamWriter(path, false, System.Text.Encoding.UTF8);
        w.WriteLine("设定温度(℃),稳态输出(%)");
        foreach (var p in ff.Points.OrderByDescending(p => p.SetpointC))
            w.WriteLine($"{p.SetpointC.ToString("F3", CultureInfo.InvariantCulture)}," +
                        $"{p.OutputPercent.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    public static void Load(SteadyStateFeedforward ff, string path)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path).Skip(1))
        {
            var parts = line.Split(',');
            if (parts.Length < 2) continue;
            if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sp) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var duty))
                ff.Learn(sp, duty);
        }
    }
}
