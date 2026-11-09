using MK.Hq.Domain;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.Capture;

public sealed class CaptureUnitTests
{
    [Fact]
    public void Parser_accepts_fenced_json_and_Indian_grouping_and_flags_junk()
    {
        var x = GemmaExtractor.Parse("""
            Here you go:
            ```json
            {"documentType":"tax_invoice","seller":{"name":"SAI KUMAR VALVES","gstin":"37abdcs0279c1zh"},"buyer":null,
             "invoiceNumber":"3109","invoiceDate":"2026-11-06","priceBasis":"Inclusive","discountPercent":null,
             "lines":[{"sourceText":"25 MM ASHIRVAD ELBOW","name":"25 mm Ashirvad elbow","quantity":"150","unit":"Nos","rate":"27.90","amount":"4,185.00","gstRate":"18"},
                      {"sourceText":"P & F Amount","name":"Packing & forwarding","quantity":1,"rate":"220.00","amount":"259.60"},
                      {"sourceText":"smudge","name":"Something","quantity":"1O","rate":"2,00"}],
             "invoiceTotal":"30,410.00","warnings":["row 3 unclear"],}
            ```
            """);
        Assert.Equal("37ABDCS0279C1ZH", x.Seller?.Gstin);
        Assert.Equal(new DateOnly(2026, 11, 6), x.InvoiceDate);
        Assert.Equal("inclusive", x.PriceBasis); // case-normalised
        Assert.Equal(4185.00m, x.Lines[0].Amount);
        Assert.Equal(30410.00m, x.InvoiceTotal);
        Assert.Equal(1m, x.Lines[1].Quantity);
        Assert.Null(x.Lines[2].Quantity);   // "1O" is not a number
        Assert.Null(x.Lines[2].Rate);       // "2,00" is not valid grouping
        Assert.Contains(x.Warnings, w => w.Contains("quantity"));
        Assert.Contains("row 3 unclear", x.Warnings);
    }

    [Fact]
    public void Tax_and_round_off_rows_are_not_items()
    {
        var x = GemmaExtractor.Parse("""
            {"lines":[{"sourceText":"Modi 3/4\" Nylon Foam Tube","name":"Nylon foam tube","quantity":4,"rate":1,"amount":3.4},
                      {"sourceText":"CGST-9%","name":"CGST-9%","quantity":1,"amount":0.31},
                      {"sourceText":"SGST 9%","name":"SGST 9%","quantity":1,"amount":0.31},
                      {"sourceText":"Loss : Rounded Off","name":"Loss : Rounded Off","quantity":1,"amount":-0.02},
                      {"sourceText":"Sub Total","name":"Sub Total","amount":3.4}],"invoiceTotal":4}
            """);
        Assert.Single(x.Lines);
        Assert.Equal(4, x.Warnings.Count(w => w.Contains("left out")));
    }

    [Fact]
    public void Counter_shorthand_L_and_T_mean_elbow_and_tee_but_5_l_stays_litres()
    {
        Assert.Contains("elbow", ItemMatcher.Words("4' pvc door L"));
        Assert.Contains("tee", ItemMatcher.Words("1' T joint"));
        Assert.Contains("l", ItemMatcher.Words("primer 5 l"));
        Assert.Equal(ItemMatcher.Words("25 MM CPVC COUPLER"), ItemMatcher.Words("25 mm cpvc couplings"));
        Assert.Contains("upvc", ItemMatcher.Words("20*25 MM U PVC BUSH"));
    }

    [Fact]
    public async Task Material_and_counter_inch_notation_are_facts()
    {
        await using var s = await HqSession.OpenAsync(TestDatabase.ConnectionString("ISTMBM26"), new ProcedureCatalog(), 15);
        var matcher = new ItemMatcher(await Catalogue.ItemsAsync(s));
        var none = new Dictionary<string, (long, long)>();
        ExtractedLine L(string t) => new(t, t, null, 1, null, null, null, null, null);
        var tee = matcher.Match(L("1' upvc Tee"), none);
        Assert.Contains("UPVC", tee.Candidates[0].Name, StringComparison.OrdinalIgnoreCase);
        var pipe = matcher.Match(L("2\"1/2\" pvc pipe"), none);
        Assert.True(pipe.Candidates[0].Name.Contains("75 MM", StringComparison.OrdinalIgnoreCase),
            string.Join(" | ", pipe.Candidates.Select(c => $"{c.Name} {c.Score:0.00} [{string.Join("; ", c.Notes)}]")));
    }

    [Theory]
    [InlineData("1/4", "0.25")]
    [InlineData("1 1/2", "1.5")]
    [InlineData("12,34,567.50", "1234567.50")]
    [InlineData("2,00", null)]
    [InlineData("1O", null)]
    public void Numbers_and_fractions(string raw, string? expected) =>
        Assert.Equal(expected is null ? null : decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), GemmaExtractor.ParseNumber(raw));

    [Fact]
    public void Printed_amount_overrides_a_rate_that_cannot_produce_it()
    {
        var pf = new ExtractedLine("P & F Amount", "Packing", null, 1, null, 220m, null, 18, 259.60m);
        Assert.Equal(259.60m, CaptureService.RateFor(pf));
        var elbow = new ExtractedLine("25 MM ELBOW", "25 mm elbow", null, 150, null, 27.90m, null, 18, 4185m);
        Assert.Equal(27.90m, CaptureService.RateFor(elbow));
    }

    [Fact]
    public void Images_are_normalised_upright_jpeg_from_scanner_bmp()
    {
        var bmp = Directory.GetFiles(@"C:\MK_WORKSPACE\scans").FirstOrDefault(f => File.ReadAllBytes(f) is [0x42, 0x4D, ..]);
        if (bmp is null) return;
        var jpeg = ImageNormalizer.Normalize(File.ReadAllBytes(bmp), 90);
        Assert.True(jpeg is [0xFF, 0xD8, ..]);
        using var img = System.Drawing.Image.FromStream(new MemoryStream(jpeg));
        Assert.True(img.Width > img.Height, "a portrait scan turned 90° is landscape");
    }

    [Fact]
    public async Task Supplier_history_prior_ranks_the_supplier_s_usual_items()
    {
        await using var s = await HqSession.OpenAsync(TestDatabase.ConnectionString("ISTMBM26"), new ProcedureCatalog(), 15);
        // Adhithya entrprises (10093): its bills before 7-Nov-2026, as the app would have known when GSTPUR/302 arrived.
        var history = await CaptureService.HistoryAsync(s, purchase: true, 10093, null, new DateOnly(2026, 11, 7));
        Assert.NotEmpty(history);
        var sales = await CaptureService.HistoryAsync(s, purchase: false, null, null, new DateOnly(2026, 11, 7));
        Assert.NotEmpty(sales);
        var matcher = new ItemMatcher(await Catalogue.ItemsAsync(s));
        var cf = matcher.Match(new ExtractedLine("C&F", "C&F", null, 1, null, 65, null, null, 65), new Dictionary<string, (long, long)>(), history: history);
        Assert.Contains("PACKING", cf.Candidates[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>GSTPUR/302 exactly as Gemma read it, against what was saved in HQ (Adhithya entrprises, 7-Nov-2026).</summary>
    [Theory]
    [InlineData("PRI PRESTIGE HY AGR 110 4KG PIPE (6MTR) PRINCE", 1087, "110 MM PVC PIPE (PRINCE)")]
    [InlineData("PRI PRESTIGE HY AGR 75 4KG PIPE (6MTR)", 530, "75 MM PVC PIPE( PRINCE)")]
    [InlineData("PRI PRESTIGE HY AGR 140 4KG PIPE (6MTR)", 1808, "140 MM PVC PIPE ( PRINCE )")]
    [InlineData("PRI PRESTIGE HY AGR 160 4KG PIPE (6MTR)", 2425, "160 MM PVC PIPE (6\") ( PRINCE )")]
    [InlineData("PRI UPVC 1 PIPE SCHD-40 (3MTR) 2B", 262, "25 MM UPVC PIPE (1\")")]
    [InlineData("PRI UPVC 3/4 PIPE SCHD-40 (3MTR) 2B", 180, "20 MM UPVC PIPE (3/4\")")]
    // First-time items (never bought from this party, several 75 mm elbows/tees fit the words): the right size and part
    // type must lead, and nothing may be auto-picked; the user chooses which one.
    [InlineData("PRI AGRI 75 ELBOW HY 1B", 55.72, "75 MM|ELBOW", true)]
    [InlineData("PRI AGRI 75 TEE HY 1B", 76.54, "75 MM|TEE", true)]
    public async Task Supplier_bill_lines_rank_the_item_HQ_saved_first(string text, double rate, string hqItem, bool firstTime = false)
    {
        await using var s = await HqSession.OpenAsync(TestDatabase.ConnectionString("ISTMBM26"), new ProcedureCatalog(), 15);
        var history = await CaptureService.HistoryAsync(s, purchase: true, 10093, null, new DateOnly(2026, 11, 7));
        var matcher = new ItemMatcher(await Catalogue.ItemsAsync(s));
        var m = matcher.Match(new ExtractedLine(text, text, null, 1, null, (decimal)rate, null, 18, null), new Dictionary<string, (long, long)>(), history: history);
        var top = $"top: {string.Join(" | ", m.Candidates.Select(c => $"{c.Name} {c.Score:0.00} [{string.Join("; ", c.Notes)}]"))}";
        Assert.True(m.Candidates.Count > 0, "no candidates");
        if (!firstTime)
        {
            Assert.True(string.Equals(hqItem.Trim(), m.Candidates[0].Name.Trim(), StringComparison.OrdinalIgnoreCase), top);
            return;
        }
        Assert.True(hqItem.Split('|').All(part => m.Candidates[0].Name.Contains(part, StringComparison.OrdinalIgnoreCase)), top);
        Assert.True(m.Chosen is null, $"auto-picked {m.Chosen?.Name}; {top}");
    }

    [Fact]
    public async Task Matcher_respects_pack_sizes_and_learned_wording()
    {
        await using var s = await HqSession.OpenAsync(TestDatabase.ConnectionString("ISTMBM26"), new ProcedureCatalog(), 15);
        var items = await Catalogue.ItemsAsync(s);
        var matcher = new ItemMatcher(items);
        var none = new Dictionary<string, (long, long)>();

        var four = matcher.Match(new ExtractedLine("SH Zinc primer 4 ltr", "SH zinc primer 4 ltr", null, 1, "tin", null, null, null, null), none);
        Assert.Contains("4 ltr", four.Candidates[0].Name, StringComparison.OrdinalIgnoreCase);
        var oneLitre = four.Candidates.FirstOrDefault(c => c.Name.Contains("1Ltr", StringComparison.OrdinalIgnoreCase));
        Assert.True(oneLitre is null || oneLitre.Notes.Any(n => n.StartsWith("size differs")), "the 1 ltr tin must be flagged as a different size");

        var leo = new ExtractedLine("25MM LEO TANK FILTER", "25mm Leo tank filter", null, 5, null, 850, null, 18, null);
        var pillar = items.First(i => i.Name.Contains("LEO ROYAL PILLER TAP"));
        var learned = new Dictionary<string, (long, long)> { [ItemMatcher.AliasKey(leo.SourceText)] = (pillar.ItemId, pillar.UnitId) };
        var m = matcher.Match(leo, learned);
        Assert.Equal(pillar.ItemId, m.Chosen?.ItemId);
        Assert.Equal("learned", m.Reason);
    }
}
