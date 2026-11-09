using Dapper;
using Microsoft.Data.SqlClient;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Configuration;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Capture;

/// <summary>
/// Calls Google's Gemma 4 with the shop's real scans (C:\MK_WORKSPACE\scans). Needs the API key; costs a few API calls.
/// The printed invoice is checked against what was actually entered in HQ for it (GSTPUR/300, Sai Kumar Valves bill 3109).
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveExtractionTests(ITestOutputHelper output)
{
    private const string Scans = @"C:\MK_WORKSPACE\scans";

    private static GemmaExtractor Extractor(out AiSettings settings)
    {
        settings = new AiSettings(new HqOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "mkhq-ai-tests"),
            ImportAiSettingsFrom = @"C:\MK_WORKSPACE\mk-api\var\capture\ai-settings.json",
        });
        return new GemmaExtractor(settings, new HttpClient { Timeout = TimeSpan.FromMinutes(3) });
    }

    [Theory]
    [InlineData("scan-rot.png", 0)]
    public async Task Printed_invoice_matches_what_was_entered_in_HQ(string file, int rotate)
    {
        var extractor = Extractor(out var settings);
        if (settings.ApiKey is null) { output.WriteLine("No API key; skipped"); return; }
        var page = ImageNormalizer.Normalize(await File.ReadAllBytesAsync(Path.Combine(Scans, file)), rotate);
        var r = await extractor.ExtractAsync([page], "purchase");
        var x = r.Data;
        output.WriteLine($"{file}: {r.Model} {r.ElapsedMs} ms, {r.PromptTokens}+{r.OutputTokens} tokens, attempts {r.Attempts}");
        output.WriteLine($"seller {x.Seller?.Name} {x.Seller?.Gstin} | buyer {x.Buyer?.Name} {x.Buyer?.Gstin} | no {x.InvoiceNumber} date {x.InvoiceDate} total {x.InvoiceTotal} basis {x.PriceBasis}");
        foreach (var l in x.Lines) output.WriteLine($"  {l.Name} | qty {l.Quantity} {l.Unit} | rate {l.Rate} | gst {l.GstRate} | amt {l.Amount} | hsn {l.Hsn}");
        foreach (var w in x.Warnings) output.WriteLine("  ! " + w);

        await using var cn = new SqlConnection(TestDatabase.ConnectionString("ISTMBM26"));
        var hq = (await cn.QueryAsync<(decimal Qty, decimal Rate)>(
            "SELECT CAST(Qty AS decimal(19,2)), CAST(Rate AS decimal(19,2)) FROM dbo.IQDoc_Recd WHERE Doc_ID=10339 ORDER BY Ln_No")).ToList();
        Assert.Equal("37ABDCS0279C1ZH", x.Seller?.Gstin);
        Assert.Equal("3109", x.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 11, 6), x.InvoiceDate);
        var items = x.Lines.Take(11).ToList();
        Assert.Equal(11, items.Count);
        var qtyHits = items.Zip(hq).Count(p => p.First.Quantity == p.Second.Qty);
        var rateHits = items.Zip(hq).Count(p => p.First.Rate == p.Second.Rate);
        output.WriteLine($"Quantities matching HQ: {qtyHits}/11, rates: {rateHits}/11");
        Assert.True(qtyHits >= 10 && rateHits >= 10, "Too many digits misread");
        Assert.Contains(x.Lines, l => l.Name.Contains("pack", StringComparison.OrdinalIgnoreCase) || l.Name.Contains("P & F", StringComparison.OrdinalIgnoreCase) || l.SourceText.Contains("P & F"));
    }

    [Fact]
    public async Task Handwritten_sideways_list_is_read()
    {
        var extractor = Extractor(out var settings);
        if (settings.ApiKey is null) { output.WriteLine("No API key; skipped"); return; }
        var raw = await File.ReadAllBytesAsync(Path.Combine(Scans, "scan-0f5e404055ea45b7b0bb5e6349dea306.png"));
        var turn = await extractor.OrientationAsync(ImageNormalizer.Normalize(raw));
        output.WriteLine($"Orientation check says rotate {turn}°");
        // Ground truth: the user's own upright copy of this page (scan_upright.png) is this scan turned 270° clockwise.
        Assert.Equal(270, turn);
        var r = await extractor.ExtractAsync([ImageNormalizer.Normalize(raw, turn)], "salesOrder");
        output.WriteLine($"{r.Model} {r.ElapsedMs} ms, {r.Data.Lines.Count} lines");
        foreach (var l in r.Data.Lines) output.WriteLine($"  [{l.SourceText}] → {l.Name} | qty {l.Quantity} {l.Unit}");
        foreach (var w in r.Data.Warnings) output.WriteLine("  ! " + w);
        Assert.True(r.Data.Lines.Count >= 20, "The list has about 28 rows");
    }
}
