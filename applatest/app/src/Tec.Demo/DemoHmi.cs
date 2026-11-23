using Tec.Core.Persistence;

namespace Tec.Demo;

/// <summary>
/// 【本地改动】设备端 HMI 面板的演示文件：6 步任务序列（两路各一条）与面板配方库。
///
/// WHY：HMI 的「任务序列」「配方库」页读的是数据目录 HmiPanel/ 下的 JSON（seq-CHn.json / library.json，
/// 格式见 Tec.Hmi.Ui 的 HmiSeqStep / HmiSeqLibEntry）。演示启动前把文件写好，页面一开就有内容；
/// 序列也真能启动——演示启动时通道 1 的序列会被启动，装置总览 / 趋势页就有曲线。
/// 这里用匿名对象写同一套键名（小驼峰），不引用 Tec.Hmi.Ui（它是界面工程）。
/// </summary>
public static class DemoHmi
{
    public static void Write(string dataDir)
    {
        var dir = Path.Combine(dataDir, "HmiPanel");
        Directory.CreateDirectory(dir);

        // 工位 A：升温 → 保温 → 回流 → 降温结晶 → 养晶 → 停搅拌
        var seq1 = new object[]
        {
            new { type = "Tr", tgt = 60d, mode = "rate", val = 2d, rpm = 300d },
            new { type = "Wait", tgt = 0d, mode = "rate", val = 20d, rpm = 300d },
            new { type = "TrTj", tgt = 5d, mode = "rate", val = 30d, rpm = 300d },
            new { type = "Tr", tgt = 10d, mode = "rate", val = 0.5d, rpm = 250d },
            new { type = "Wait", tgt = 0d, mode = "rate", val = 15d, rpm = 150d },
            new { type = "R", tgt = 0d, mode = "time", val = 0.5d, rpm = 0d }
        };
        // 工位 B：温和升温 → 保温 → 加料 → 保温 → 回室温
        var seq2 = new object[]
        {
            new { type = "Tr", tgt = 35d, mode = "rate", val = 1d, rpm = 400d },
            new { type = "Wait", tgt = 0d, mode = "rate", val = 15d, rpm = 400d },
            new { type = "Dose", tgt = 20d, mode = "rate", val = 1d, rpm = 400d },
            new { type = "Wait", tgt = 0d, mode = "rate", val = 20d, rpm = 400d },
            new { type = "Tr", tgt = 25d, mode = "rate", val = 1d, rpm = 300d }
        };
        File.WriteAllText(Path.Combine(dir, "seq-CH1.json"), TecJson.Write(seq1));
        File.WriteAllText(Path.Combine(dir, "seq-CH2.json"), TecJson.Write(seq2));

        var lib = new object[]
        {
            new { name = "酯化回流 · 结晶", savedAt = new DateTime(2026, 11, 12, 10, 20, 0), steps = seq1 },
            new { name = "温和升温 · 加料 · 保温", savedAt = new DateTime(2026, 11, 15, 15, 42, 0), steps = seq2 },
            new
            {
                name = "阶梯升温 40 / 60 / 80", savedAt = new DateTime(2026, 11, 20, 9, 5, 0),
                steps = new object[]
                {
                    new { type = "Tr", tgt = 40d, mode = "rate", val = 2d, rpm = 300d },
                    new { type = "Wait", tgt = 0d, mode = "rate", val = 30d, rpm = 300d },
                    new { type = "Tr", tgt = 60d, mode = "rate", val = 2d, rpm = 300d },
                    new { type = "Wait", tgt = 0d, mode = "rate", val = 30d, rpm = 300d },
                    new { type = "Tr", tgt = 80d, mode = "rate", val = 2d, rpm = 300d },
                    new { type = "Wait", tgt = 0d, mode = "rate", val = 30d, rpm = 300d }
                }
            }
        };
        File.WriteAllText(Path.Combine(dir, "library.json"), TecJson.Write(lib));
    }
}
