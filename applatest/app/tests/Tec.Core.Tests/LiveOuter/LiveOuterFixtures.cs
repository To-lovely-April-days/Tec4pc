using System.Globalization;
using System.IO.Compression;
using System.Text;
using TecControl.Core.Control;

namespace Tec.Core.Tests;

/// <summary>
/// LiveOuter 对拍 / 性质测试共用的夹具读法。
/// 夹具不进输出目录（测试工程没有拷贝项）：从 bin 往上找到 Tec.Core.Tests.csproj，直接读仓库里 LiveOuter/golden/ 下的文件。
/// 真机场是 .csv.gz（原件 gzip，省体积），合成场是 .csv；两种这里一个读法。怎么来的、怎么重生成见 LiveOuter/reference/README.md。
/// </summary>
internal static class LiveOuterFixtures
{
    /// <summary>进仓库的 6 场真机对拍（Python 定稿在现场逐拍记录上开环重放）。拍数钉在这里：夹具少了 / 换了测试要红。</summary>
    public static readonly (string Name, int Beats)[] Real =
    [
        ("20261110-104725", 2074),   // 42 → 60 ℃ 升温，读数带锯齿；拍长最齐
        ("20261109-095506", 1869),   // 19.3 → 50 ℃：出厂三个数就是照它拟的；有等人答复开水
        ("20261106-130619", 1877),   // 66 → 70 ℃ 小台阶 + 长保温（75 min）；τ̂ 认到 1838
        ("20261104-085350", 2241),   // 21.6 → −10 ℃ 大降温；拍长最长 12.8 s
        ("20261104-104405", 641),    // −2 → 20 ℃ 过零区；有「开不开冷却水」的询问
        ("20261103-104547", 905),    // 19 → 15 ℃ 小降温；τ̂ 认到 568（最小）
    ];

    /// <summary>12 场合成对拍（Python 定稿自己造输入，覆盖真机场走不到的分支：斜坡、无读数、坏点、改目标、τ̂ 顶边、报警、拍长不齐、占位目标）。</summary>
    public static readonly (string Name, int Beats)[] Synth =
    [
        ("s01_step_plain", 200), ("s02_ramp", 231), ("s03_retarget", 280), ("s04_tau_hi", 430), ("s05_tau_lo_reseed", 370),
        ("s06_glitch", 150), ("s07_loss", 170), ("s08_far", 180), ("s09_alarm_combo", 444), ("s10_exo_dt", 274), ("s11_dt_mix", 220),
        ("s12_idle_pin", 300),
    ];

    private static readonly Lazy<string> DirLazy = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Tec.Core.Tests.csproj")))
            dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("从 " + AppContext.BaseDirectory + " 往上没找到 Tec.Core.Tests.csproj");
        return Path.Combine(dir.FullName, "LiveOuter");
    });

    /// <summary>仓库里的 tests/Tec.Core.Tests/LiveOuter 目录。</summary>
    public static string Dir => DirLazy.Value;

    public static string RealPath(string name) => Path.Combine(Dir, "golden", "real", name + ".csv.gz");

    public static string SynthPath(string name) => Path.Combine(Dir, "golden", "synth", name + ".csv");

    public static string WhiteboxPath => Path.Combine(Dir, "golden", "whitebox", "whitebox-steps.json");

    public static Table Load(string path)
    {
        string text;
        using (var fs = File.OpenRead(path))
        {
            if (path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            {
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var rd = new StreamReader(gz, Encoding.UTF8);
                text = rd.ReadToEnd();
            }
            else
            {
                using var rd = new StreamReader(fs, Encoding.UTF8);
                text = rd.ReadToEnd();
            }
        }
        // 这些 csv 全是数字和单个字母，没有带引号的格子；行尾 \n 或 \r\n 都认（检出时可能被换成 CRLF）
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
        return new Table(Path.GetFileName(path), lines[0].Split(','), lines.Skip(1).Select(l => l.Split(',')).ToArray());
    }

    public static double Num(string s) => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>一份对拍 csv：表头 + 逐拍的格子（都还是字符串）。按列名取，不按列号。</summary>
    public sealed class Table(string file, string[] header, string[][] rows)
    {
        public string File { get; } = file;
        public string[] Header { get; } = header;
        public string[][] Rows { get; } = rows;

        public bool Has(string name) => Array.IndexOf(Header, name) >= 0;

        public int Col(string name)
        {
            int i = Array.IndexOf(Header, name);
            if (i < 0) throw new InvalidDataException(File + " 没有列 " + name);
            return i;
        }
    }

    /// <summary>一拍：喂给 LiveOuter 的输入、是不是 first = 1 的行（先 Reset 再 Step）、这一行原始的格子，
    /// 和是不是 pin = 1 的行（这一拍的目标是占位的：Step 之前先 PinTarget；只有 s12 有这一列）。</summary>
    public readonly record struct Beat(LiveOuterInput Input, bool First, string[] Cells, bool Pin = false);

    /// <summary>
    /// 把一份对拍 csv 变成逐拍输入。真机场只有 t_s、dt_s、sp、tr、tj 五列输入，照 golden.py 的喂法补：
    /// 第 0 行 first；热源在加热棒侧、输出开着、不贴下限、不挂起；不给终点和坡速。
    /// 合成场 12 列输入全有：tr 空 = 没读数（NaN）；sp_final / sp_rate 空 = 没给（null）；side 是 H / C。
    /// s12 在行尾多一列 pin：1 = 这一拍喂的目标是占位的（不在控），Python 那边 step 之前做了 LiveOuter.PinTarget 的等价写法。
    /// </summary>
    public static IEnumerable<Beat> Beats(Table t)
    {
        int cT = t.Col("t_s"), cDt = t.Col("dt_s"), cSp = t.Col("sp"), cTr = t.Col("tr"), cTj = t.Col("tj");
        bool synth = t.Has("first");
        int cSpf = synth ? t.Col("sp_final") : -1, cRate = synth ? t.Col("sp_rate") : -1, cSide = synth ? t.Col("side") : -1;
        int cSat = synth ? t.Col("inner_sat_low") : -1, cWh = synth ? t.Col("tec_withheld") : -1, cOn = synth ? t.Col("output_on") : -1;
        int cFirst = synth ? t.Col("first") : -1;
        int cPin = t.Has("pin") ? t.Col("pin") : -1;
        for (int k = 0; k < t.Rows.Length; k++)
        {
            var f = t.Rows[k];
            var input = new LiveOuterInput
            {
                T = Num(f[cT]),
                Dt = Num(f[cDt]),
                Sp = Num(f[cSp]),
                Tr = f[cTr].Length == 0 ? double.NaN : Num(f[cTr]),
                Tj = Num(f[cTj]),
                TecSide = synth && f[cSide] == "C",
                InnerSatLow = synth && f[cSat] == "1",
                TecWithheld = synth && f[cWh] == "1",
                OutputOn = !synth || f[cOn] == "1",
                SpFinal = synth && f[cSpf].Length > 0 ? Num(f[cSpf]) : null,
                SpRate = synth && f[cRate].Length > 0 ? Num(f[cRate]) : null,
            };
            yield return new Beat(input, synth ? f[cFirst] == "1" : k == 0, f, cPin >= 0 && f[cPin] == "1");
        }
    }

    /// <summary>照对拍 csv 的喂法走一拍：first = 1 的行先 Reset 再 Step(first: true)，其余只 Step；pin = 1 的行在 Step 之前先 PinTarget（Reset 之后）。</summary>
    public static LiveOuterOutput Feed(LiveOuter lo, in Beat b)
    {
        if (b.First) lo.Reset(b.Input);
        if (b.Pin) lo.PinTarget(b.Input.Sp);
        return lo.Step(b.Input, b.First);
    }
}
