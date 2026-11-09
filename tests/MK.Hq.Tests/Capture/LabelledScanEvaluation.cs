using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Capture;

/// <summary>
/// Accuracy report on the shop's own labelled scans (C:\MK_WORKSPACE\scans\GSTPUR-300.png, SO-428.png …): each photo is read
/// by Gemma 4 and matched to the catalogue, then compared with the bill the shop actually saved in HQ under that number.
/// Writes docs/scan-evaluation.md. It measures; it does not fail on misreads (the user reviews every draft anyway).
/// </summary>
[Trait("Category", "Live")]
public sealed partial class LabelledScanEvaluation(ITestOutputHelper output)
{
    private const string Scans = @"C:\MK_WORKSPACE\scans";
    private const string Database = "ISTMBM26";

    private sealed record HqHeader(long Doc_Id, int Tt_Id, DateTime Doc_Date, string? Supp_Billno, DateTime? Supp_Billdate, decimal Bill_Amt, int Exclusive, int Mode, string Party, string? Gstin);
    private sealed record HqLine(int Ln_No, long Item_ID, string Item, decimal Qty, decimal Rate, decimal Disc, decimal Tax, decimal Net);

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>The repository's docs folder, wherever the test binaries were copied to.</summary>
    private static string DocsFolder([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "..", "docs"));

    /// <summary>
    /// MKHQ_EVAL_CACHED=1 re-matches the readings saved by the last live run (docs/scan-cache) without calling Google,
    /// so the matcher can be tuned for free.
    /// </summary>
    [Fact]
    public async Task Evaluate_labelled_scans()
    {
        var settings = new AiSettings(new HqOptions { DataDirectory = Path.Combine(Path.GetTempPath(), "mkhq-ai-tests"), ImportAiSettingsFrom = @"C:\MK_WORKSPACE\mk-api\var\capture\ai-settings.json" });
        if (settings.ApiKey is null) { output.WriteLine("No API key; skipped"); return; }
        var model = Environment.GetEnvironmentVariable("MKHQ_EVAL_MODEL") ?? settings.Model;
        var extractor = new GemmaExtractor(settings, new HttpClient { Timeout = TimeSpan.FromMinutes(3) });
        var only = Environment.GetEnvironmentVariable("MKHQ_EVAL_ONLY");
        var cached = Environment.GetEnvironmentVariable("MKHQ_EVAL_CACHED") == "1";
        var cache = Path.Combine(DocsFolder(), "scan-cache");
        Directory.CreateDirectory(cache);

        await using var session = await HqSession.OpenAsync(TestDatabase.ConnectionString(Database), new ProcedureCatalog(), 15);
        var matcher = new ItemMatcher(await Catalogue.ItemsAsync(session));
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(Database));

        var files = Directory.GetFiles(Scans).Select(f => (Path: f, M: Label().Match(Path.GetFileName(f)))).Where(x => x.M.Success)
            .Where(x => only is null || Path.GetFileNameWithoutExtension(x.Path) == only)
            .OrderBy(x => x.M.Groups[1].Value).ThenBy(x => int.Parse(x.M.Groups[2].Value, CultureInfo.InvariantCulture)).ToList();
        var report = new StringBuilder();
        var details = new StringBuilder();
        report.AppendLine($"# Scan evaluation — {model}, {DateTime.Now:dd-MM-yyyy HH:mm}{(cached ? " (saved readings, matcher re-run)" : "")}\n");
        report.AppendLine("Each labelled scan was read by Gemma 4 and matched to HQ's catalogue, then compared with the bill saved in HQ under that number.\n");
        report.AppendLine("| Scan | Secs | Supplier GSTIN | Bill no. | Date | Printed total vs HQ | Lines read / HQ | Qty ok | Rate ok | Item top-1 | Item in top-5 |");
        report.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        int tq = 0, tr = 0, trl = 0, t1 = 0, t5 = 0, tl = 0;
        foreach (var (path, m) in files)
        {
            var series = m.Groups[1].Value;
            var number = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var tt = series == "GSTPUR" ? 500 : 249;
            var kind = tt == 500 ? "purchase" : "salesOrder";
            var name = Path.GetFileName(path);
            var head = await cn.QueryFirstOrDefaultAsync<HqHeader>("""
                SELECT CAST(h.Doc_Id AS bigint) Doc_Id, CAST(h.Tt_Id AS int) Tt_Id, h.Doc_Date, h.Supp_Billno, h.Supp_Billdate, CAST(h.Bill_Amt AS decimal(19,2)) Bill_Amt,
                  CAST(ISNULL(h.Exclusive,0) AS int) Exclusive, CAST(h.Mode AS int) Mode,
                  LTRIM(RTRIM(CASE WHEN h.Mode=1 THEN a.Act_Name ELSE c.Act_Name END)) Party, UPPER(LTRIM(RTRIM(CASE WHEN h.Mode=1 THEN ad.GSTIN ELSE c.GSTIN END))) Gstin
                FROM dbo.IQDoc_Header h LEFT JOIN dbo.IQAccmst a ON a.Act_Id=h.HeadAct_ID LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id=h.HeadAct_ID LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id
                WHERE h.Tt_Id=@tt AND h.TNo=@number
                """, new { tt, number });
            if (head is null) { report.AppendLine($"| {name} | | not in HQ | | | | | | | | |"); continue; }
            var hq = (await cn.QueryAsync<HqLine>($"""
                SELECT CAST(l.Ln_No AS int) Ln_No, CAST(l.Item_ID AS bigint) Item_ID, LTRIM(RTRIM(im.Item_Name)) Item, CAST(l.Qty AS decimal(19,4)) Qty,
                  CAST(l.Rate AS decimal(19,4)) Rate, CAST(ISNULL(l.Disc,0) AS decimal(19,4)) Disc, CAST(ISNULL(l.Tax,0) AS decimal(9,2)) Tax, CAST(l.NetValue AS decimal(19,2)) Net
                FROM dbo.{(tt == 500 ? "IQDoc_Recd" : "IQOrd_Detail")} l JOIN dbo.IQItems_Mast im ON im.Item_ID=l.Item_ID WHERE l.Doc_ID=@id ORDER BY l.Ln_No
                """, new { id = head.Doc_Id })).ToList();

            ExtractionResult r;
            var cacheFile = Path.Combine(cache, name + ".json");
            if (cached && File.Exists(cacheFile))
                r = System.Text.Json.JsonSerializer.Deserialize<ExtractionResult>(await File.ReadAllTextAsync(cacheFile), Json)!;
            else
            {
                await Task.Delay(TimeSpan.FromSeconds(4)); // be gentle with the free Gemma endpoint
                try
                {
                    var raw = await File.ReadAllBytesAsync(path);
                    var turn = await extractor.OrientationAsync(ImageNormalizer.Normalize(raw));
                    r = await extractor.ExtractAsync([ImageNormalizer.Normalize(raw, turn)], kind, model);
                    if (turn != 0) output.WriteLine($"{name}: turned {turn}°");
                    await File.WriteAllTextAsync(cacheFile, System.Text.Json.JsonSerializer.Serialize(r, Json));
                }
                catch (HqException e)
                {
                    report.AppendLine($"| {name} | | **failed: {e.Code}** {e.Message} | | | | | | | | |");
                    continue;
                }
            }
            var x = r.Data;
            var lines = x.Lines.ToList();
            // What the app would know at the time: this supplier's (or the shop's) bills BEFORE this one.
            var partyId = await cn.ExecuteScalarAsync<long?>("SELECT CAST(NULLIF(HeadAct_ID,6) AS bigint) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id = head.Doc_Id });
            var history = await CaptureService.HistoryAsync(session, tt == 500, partyId, x.Seller?.Gstin, DateOnly.FromDateTime(head.Doc_Date));
            details.AppendLine($"\n## {name} → HQ {series}/{number} ({head.Party}, ₹{Money.Text(head.Bill_Amt)}, prices {(head.Exclusive == 1 ? "incl." : "excl.")} GST)\n");
            details.AppendLine($"Read: seller {x.Seller?.Name} {x.Seller?.Gstin} · bill {x.InvoiceNumber} · {x.InvoiceDate} · total {x.InvoiceTotal} · basis {x.PriceBasis ?? "?"}\n");
            details.AppendLine("| # | Written (Gemma) | Qty | Rate | Amount | Matcher's item (score) | HQ item | HQ qty | HQ rate | |");
            details.AppendLine("|---|---|---|---|---|---|---|---|---|---|");

            // Pair read lines with HQ lines. Handwritten lists are not in HQ's order and repeat quantities, so the pairing uses
            // everything known: the matcher's suggestions, quantity, rate and position; best pairs first.
            var matches = lines.Select(l => matcher.Match(l, new Dictionary<string, (long, long)>(), history: history)).ToList();
            var pairs = new List<(int I, int K, double S)>();
            for (var i = 0; i < hq.Count; i++)
                for (var k = 0; k < lines.Count; k++)
                {
                    var rank = matches[k].Candidates.ToList().FindIndex(c => c.ItemId == hq[i].Item_ID);
                    var rateClose = lines[k].Rate is not null && Math.Abs(CaptureService.RateFor(lines[k]) - hq[i].Rate) <= Math.Max(0.02m, hq[i].Rate * 0.2m);
                    pairs.Add((i, k, (rank >= 0 ? 3 - 0.3 * rank : 0) + (lines[k].Quantity == hq[i].Qty ? 1 : 0) + (rateClose ? 1 : 0)
                        - 0.6 * Math.Abs((double)k / Math.Max(1, lines.Count) - (double)i / Math.Max(1, hq.Count))));
                }
            var pairOf = new Dictionary<int, int>();
            var used = new HashSet<int>();
            foreach (var (pi, pk, _) in pairs.OrderByDescending(p => p.S))
                if (!pairOf.ContainsKey(pi) && !used.Contains(pk)) { pairOf[pi] = pk; used.Add(pk); }
            int qok = 0, rok = 0, top1 = 0, top5 = 0;
            for (var i = 0; i < hq.Count; i++)
            {
                var h = hq[i];
                if (!pairOf.TryGetValue(i, out var j)) { details.AppendLine($"| {i + 1} | *(not read)* | | | | | {h.Item} | {Money.Plain(h.Qty)} | {Money.Plain(h.Rate)} | ✗ |"); continue; }
                var l = lines[j];
                var match = matches[j];
                var rate = CaptureService.RateFor(l);
                var withTax = Money.Round(rate * (1 + h.Tax / 100m), 2);
                var qtyOk = l.Quantity == h.Qty;
                var rateOk = tt != 500 || Math.Abs(rate - h.Rate) <= 0.01m || Math.Abs(withTax - h.Rate) <= 0.02m || (l.Amount is { } a && h.Qty > 0 && Math.Abs(a / h.Qty - h.Rate) <= 0.02m);
                var is1 = match.Candidates.FirstOrDefault()?.ItemId == h.Item_ID;
                var is5 = match.Candidates.Any(c => c.ItemId == h.Item_ID);
                qok += qtyOk ? 1 : 0; rok += rateOk ? 1 : 0; top1 += is1 ? 1 : 0; top5 += is5 ? 1 : 0;
                var best = match.Candidates.FirstOrDefault();
                details.AppendLine($"| {i + 1} | {Esc(l.SourceText)} | {l.Quantity} {l.Unit} | {l.Rate} | {l.Amount} | {Esc(best?.Name ?? "—")} ({best?.Score:0.00}) | {Esc(h.Item)} | {Money.Plain(h.Qty)} | {Money.Plain(h.Rate)} | {(qtyOk ? "" : "qty ")}{(rateOk ? "" : "rate ")}{(match.Chosen is { } ch ? ch.ItemId == h.Item_ID ? "auto✓ " : "AUTO-WRONG " : "")}{(is1 ? "" : is5 ? "item#2-5" : "item✗")} |");
            }
            foreach (var k in Enumerable.Range(0, lines.Count).Where(k => !used.Contains(k)))
                details.AppendLine($"| + | {Esc(lines[k].SourceText)} | {lines[k].Quantity} | {lines[k].Rate} | {lines[k].Amount} | | *(not in HQ bill)* | | | extra |");
            foreach (var w in x.Warnings) details.AppendLine($"\n> ⚠ {Esc(w)}");

            string Mark(bool ok) => ok ? "✓" : "✗";
            var gstOk = head.Gstin is { Length: > 0 } g ? Mark(g == x.Seller?.Gstin) : "–";
            var noOk = tt == 500 ? Mark(Norm(head.Supp_Billno) == Norm(x.InvoiceNumber)) : "–";
            var dateOk = tt == 500 ? Mark(head.Supp_Billdate is { } sd && x.InvoiceDate == DateOnly.FromDateTime(sd)) : "–";
            var total = x.InvoiceTotal is { } it ? $"{Money.Text(it)} vs {Money.Text(head.Bill_Amt)} {Mark(Math.Abs(it - head.Bill_Amt) <= 1)}" : $"– vs {Money.Text(head.Bill_Amt)}";
            report.AppendLine($"| {name} | {r.ElapsedMs / 1000.0:0} | {gstOk} | {noOk} | {dateOk} | {total} | {lines.Count}/{hq.Count} | {qok}/{hq.Count} | {(tt == 500 ? $"{rok}/{hq.Count}" : "–")} | {top1}/{hq.Count} | {top5}/{hq.Count} |");
            if (tt == 500) { tr += rok; trl += hq.Count; }
            tq += qok; t1 += top1; t5 += top5; tl += hq.Count;
            output.WriteLine($"{name}: qty {qok}/{hq.Count} rate {rok}/{hq.Count} item1 {top1} item5 {top5} ({r.ElapsedMs} ms)");
        }
        if (tl > 0)
            report.AppendLine($"\n**All lines: quantity {tq}/{tl} ({100.0 * tq / tl:0}%), rate (printed bills) {tr}/{trl} ({100.0 * tr / Math.Max(1, trl):0}%), same HQ item as top suggestion {t1}/{tl} ({100.0 * t1 / tl:0}%), among top 5 {t5}/{tl} ({100.0 * t5 / tl:0}%).**\n");
        report.AppendLine("\nItem matching here has no memory yet. In the app every confirmed choice is remembered for that wording, so repeat suppliers improve with use.\n");
        var docs = DocsFolder();
        var file = Path.GetFullPath(Path.Combine(docs, only is null ? "scan-evaluation.md" : $"scan-evaluation-{only}.md"));
        await File.WriteAllTextAsync(file, report + "\n# Line-by-line\n" + details);
        output.WriteLine("Report: " + file);
    }

    private static string Norm(string? s) => Regex.Replace(s ?? "", @"\s+", "").ToUpperInvariant();
    private static string Esc(string? s) => (s ?? "").Replace("|", "/").Replace("\n", " ");

    [GeneratedRegex(@"^(GSTPUR|SO)-(\d+)\.", RegexOptions.IgnoreCase)] private static partial Regex Label();
}
