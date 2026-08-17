using System.Security.Cryptography;
using System.Text;
using Tec.Core.Catalog;
using Tec.Core.Data;
using Tec.Core.Export;
using Tec.Core.Records;

namespace Tec.App.Services;

/// <summary>一条记录导出成什么、写到哪儿。界面把选项装进来，写文件的活在 <see cref="ExportRunner"/>。</summary>
public sealed class ExportJob
{
    public required RunRecord Record { get; init; }
    public required ISampleSource Samples { get; init; }
    public required CommandCatalog Catalog { get; init; }
    public required string Dir { get; init; }
    /// <summary>csv / xlsx / docx / pdf。</summary>
    public required string Format { get; init; }
    public required ExportOptions Options { get; init; }
    public required ReportOptions Report { get; init; }
    public required ExportMeta Meta { get; init; }
    /// <summary>勾了「步骤执行记录」这个数据项。</summary>
    public bool Steps { get; init; } = true;
    public bool Audit { get; init; } = true;
    public bool Checksum { get; init; } = true;
    public bool Signature { get; init; } = true;
    public string Signer { get; init; } = "";
}

/// <summary>
/// 真正写文件的那一步。
///
/// **顺序是有讲究的**：先写数据文件与 GLP 记录，算出各自的校验码，
/// 再把校验码填进报告——反过来的话，报告里的校验码只能是空的，
/// 或者是一个「等会儿会写成这样」的假值。
/// </summary>
public static class ExportRunner
{
    public static IReadOnlyList<string> Run(ExportJob job)
    {
        Directory.CreateDirectory(job.Dir);
        var written = new List<string>();

        void Write(string name, Action<string> body)
        {
            var path = Path.Combine(job.Dir, name);
            body(path);
            written.Add(name);
        }

        // ── 数据文件 ────────────────────────────────────────────────
        switch (job.Format)
        {
            case "csv":
                Write("data.csv", p => File.WriteAllText(p,
                    RecordExporter.SamplesLongCsv(job.Samples, job.Record, job.Options), Utf8Bom));
                if (job.Steps)
                    Write("steps.csv", p => File.WriteAllText(p,
                        RecordExporter.ExecutionCsv(job.Record, job.Options.TimeBase, job.Catalog), Utf8Bom));
                break;

            case "xlsx":
            {
                var opt = job.Options;
                var sheets = WorkbookExporter.Build(job.Record, job.Samples, opt, job.Catalog, job.Meta);
                Write("记录.xlsx", p => XlsxWriter.Save(p, sheets, job.Meta.Experiment, job.Meta.Operator));
                break;
            }
        }

        // ── GLP 记录 ────────────────────────────────────────────────
        if (job.Audit || job.Checksum || job.Signature)
        {
            Write("run.glp", p =>
            {
                using var store = new RecordStore(p);
                foreach (var ch in job.Record.Channels)
                {
                    store.Write(ch);
                    foreach (var s in ch.Steps) store.Write(ch.Channel, s);
                    if (job.Audit) foreach (var e in ch.Events) store.Write(e);
                }
                if (job.Signature)
                    store.Sign(job.Signer.Length > 0 ? job.Signer : job.Record.Operator ?? job.Meta.Operator,
                               "导出时签名");
            });
        }

        // ── 报告 ────────────────────────────────────────────────────
        if (job.Format is "docx" or "pdf")
        {
            // 报告里的校验码表列的是**已经写出来的那些文件**，先算再写
            if (job.Checksum)
                foreach (var name in written)
                    job.Report.Files.Add((name, Sha256(Path.Combine(job.Dir, name))));

            var doc = ReportBuilder.Build(job.Record, job.Samples, job.Catalog, job.Meta, job.Report);

            if (job.Format == "docx")
                Write("报告.docx", p => DocxWriter.Save(p, doc, job.Meta.Operator));
            else
            {
                var font = FontFinder.Find() ?? throw new PdfWriter.NoFontException(FontFinder.SearchedPaths);
                var style = new PageStyle();
                var pages = ReportLayout.Paginate(doc, new TextMetrics(font), style);
                Write("报告.pdf", p => PdfWriter.Save(p, pages, font, style, doc.Title, job.Meta.Operator));
            }
        }

        // ── 校验码清单 ──────────────────────────────────────────────
        if (job.Checksum && written.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# TecStudio 导出完整性校验码（SHA-256）");
            sb.AppendLine($"# 记录编号 {job.Record.RunId}　导出于 {job.Meta.ExportedAt:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("# 核对：对文件重新计算 SHA-256，与此处比对。");
            foreach (var name in written.ToList())
                sb.AppendLine(Sha256(Path.Combine(job.Dir, name)) + "  " + name);
            Write("checksums.txt", p => File.WriteAllText(p, sb.ToString(), Utf8Bom));
        }

        return written;
    }

    /// <summary>Excel 认 UTF-8 BOM，不带 BOM 的中文 CSV 双击打开是乱码。</summary>
    private static readonly UTF8Encoding Utf8Bom = new(true);

    private static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
