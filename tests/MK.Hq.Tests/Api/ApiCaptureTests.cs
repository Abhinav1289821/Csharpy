using System.Drawing;
using System.Drawing.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using MK.Hq.Domain;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Tests.Api;

/// <summary>Bill-photo capture without HQ or Google: the matcher on a small made-up catalogue, the reply parsers, the
/// image normaliser, the capture store's state machine and the service's claim/retry/failure paths.</summary>
public sealed class ApiCaptureTests
{
    // ------------------------------------------------------------------ item matcher (shop facts)
    private static long _nextId = 100;
    private static CatalogueItem Item(string name, decimal purchase = 10, decimal sale = 12) =>
        new(Interlocked.Increment(ref _nextId), 1, "", name, "NOS", "3917", sale, purchase, 0, 9, 9, 18, true, 5, true, "", "");

    private static ExtractedLine Line(string text) => new(text, text, null, 1, null, null, null, null, null);
    private static readonly Dictionary<string, (long, long)> NoAliases = new();

    private static readonly CatalogueItem UpvcTee = Item("20 MM UPVC TEE (3/4\")"), CpvcTee = Item("20 MM CPVC TEE (3/4\")"), PvcTee = Item("20 MM PVC TEE"),
        UpvcElbow25 = Item("25 MM UPVC ELBOW (1\")"), UpvcTee25 = Item("25 MM UPVC TEE (1\")"),
        Pipe75 = Item("75 MM PVC PIPE (PRINCE)"), Pipe50 = Item("50 MM PVC PIPE (PRINCE)"),
        Primer4 = Item("SH ZINC PRIMER 4 LTR"), Primer1 = Item("SH ZINC PRIMER 1 LTR"), Packing = Item("PACKING CHARGES");
    private static readonly ItemMatcher Matcher = new([UpvcTee, CpvcTee, PvcTee, UpvcElbow25, UpvcTee25, Pipe75, Pipe50, Primer4, Primer1, Packing]);

    private static string Top(LineMatch m) => string.Join(" | ", m.Candidates.Select(c => $"{c.Name} {c.Score:0.00} [{string.Join("; ", c.Notes)}]"));

    [Fact]
    public void Upvc_cpvc_and_pvc_are_different_products()
    {
        var m = Matcher.Match(Line("3/4\" UPVC TEE"), NoAliases);
        Assert.True(m.Candidates[0].ItemId == UpvcTee.ItemId, Top(m));
        var cpvc = m.Candidates.FirstOrDefault(c => c.ItemId == CpvcTee.ItemId);
        Assert.True(cpvc is null || cpvc.Notes.Any(n => n.StartsWith("different material")), Top(m));
        Assert.DoesNotContain("pvc", ItemMatcher.Words("C-PVC tee"));
        Assert.Contains("cpvc", ItemMatcher.Words("C-PVC tee"));
        Assert.Contains("upvc", ItemMatcher.Words("U PVC tee"));
    }

    [Fact]
    public void Counter_inch_notation_2_1_2_is_two_and_a_half_inches()
    {
        var m = Matcher.Match(Line("2\"1/2\" pvc pipe"), NoAliases);
        Assert.True(m.Candidates[0].ItemId == Pipe75.ItemId, Top(m));
        var fifty = m.Candidates.FirstOrDefault(c => c.ItemId == Pipe50.ItemId);
        Assert.True(fifty is null || fifty.Notes.Any(n => n.StartsWith("size differs")), Top(m));
    }

    [Fact]
    public void A_lone_L_is_an_elbow_and_T_a_tee()
    {
        var elbow = Matcher.Match(Line("1' upvc L"), NoAliases);
        Assert.True(elbow.Candidates[0].ItemId == UpvcElbow25.ItemId, Top(elbow));
        var tee = Matcher.Match(Line("1' upvc T"), NoAliases);
        Assert.True(tee.Candidates[0].ItemId == UpvcTee25.ItemId, Top(tee));
        Assert.Equal(["4", "l"], ItemMatcher.Words("4 L"));
        Assert.Equal(["4", "l"], ItemMatcher.Words("4L"));
    }

    [Fact]
    public void Pack_sizes_are_facts_and_star_sizes_split()
    {
        var m = Matcher.Match(Line("SH zinc primer 4 ltrs"), NoAliases);
        Assert.True(m.Candidates[0].ItemId == Primer4.ItemId, Top(m));
        var one = m.Candidates.FirstOrDefault(c => c.ItemId == Primer1.ItemId);
        Assert.True(one is null || one.Notes.Any(n => n.StartsWith("size differs")), Top(m));
        Assert.NotEqual(Primer1.ItemId, m.Chosen?.ItemId);
        Assert.Equal(["20", "25", "mm", "upvc", "bush"], ItemMatcher.Words("20*25 MM U PVC BUSH"));
    }

    [Fact]
    public void Charge_rows_only_match_charge_items_and_learned_wording_wins()
    {
        var pf = Matcher.Match(new ExtractedLine("P & F", "Packing & forwarding", null, 1, null, 220, null, 18, 259.60m), NoAliases);
        Assert.All(pf.Candidates, c => Assert.Equal(Packing.ItemId, c.ItemId));
        var learned = new Dictionary<string, (long, long)> { [ItemMatcher.AliasKey("blue thing")] = (PvcTee.ItemId, PvcTee.UnitId) };
        var m = Matcher.Match(Line("Blue  THING."), learned);
        Assert.Equal((PvcTee.ItemId, "learned"), (m.Chosen?.ItemId, m.Reason));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- // ..")]
    [InlineData("x")]
    public void Unreadable_wording_matches_nothing(string text)
    {
        var m = Matcher.Match(Line(text), NoAliases);
        Assert.Null(m.Chosen);
        Assert.Empty(new ItemMatcher([]).Match(Line("20 mm upvc tee"), NoAliases).Candidates);
    }

    // ------------------------------------------------------------------ reply parsing
    [Theory]
    [InlineData("not json at all", "AI_SERVER")]
    [InlineData("<html>502 Bad Gateway</html>", "AI_SERVER")]
    [InlineData("[]", "AI_REPLY_BLOCKED")]
    [InlineData("{}", "AI_REPLY_BLOCKED")]
    [InlineData("""{"candidates":{}}""", "AI_REPLY_BLOCKED")]
    [InlineData("""{"candidates":[],"promptFeedback":{"blockReason":"SAFETY"}}""", "AI_REPLY_BLOCKED")]
    [InlineData("""{"candidates":[{"finishReason":"SAFETY"}]}""", "AI_REPLY_BLOCKED")]       // no content at all
    [InlineData("""{"candidates":[{"content":{"role":"model"},"finishReason":"RECITATION"}]}""", "AI_REPLY_BLOCKED")]
    [InlineData("""{"candidates":[{"content":{"parts":"x"}}]}""", "AI_REPLY_BLOCKED")]
    [InlineData("""{"candidates":[{"content":{"parts":[{"text":"{"}]},"finishReason":"MAX_TOKENS"}]}""", "AI_REPLY_TOO_LONG")]
    public void Odd_Google_replies_become_known_errors(string json, string code) =>
        Assert.Equal(code, Assert.Throws<HqException>(() => GemmaExtractor.ReadReply(json)).Code);

    [Fact]
    public void Reply_text_skips_thoughts_and_odd_parts()
    {
        var (text, prompt, output) = GemmaExtractor.ReadReply("""
            {"candidates":[{"content":{"parts":[{"text":"thinking...","thought":true},{"inlineData":{}},{"text":42},{"text":"{\"a\":"},{"text":"1}"}]}}],
             "usageMetadata":{"promptTokenCount":1200,"candidatesTokenCount":"oops"}}
            """);
        Assert.Equal("{\"a\":1}", text);
        Assert.Equal(1200, prompt);
        Assert.Null(output);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Sorry, I cannot read this bill.")]
    [InlineData("{\"lines\":[{\"name\":\"25 mm elbow\",\"quantity\":\"2\"")]  // cut off mid-reply
    [InlineData("{'lines':[]}")]
    public void Unreadable_model_output_asks_for_a_retry(string text) =>
        Assert.Equal("AI_REPLY_INVALID", Assert.Throws<HqException>(() => GemmaExtractor.Parse(text)).Code);

    [Theory]
    [InlineData("{\"lines\":[]}")]
    [InlineData("{\"lines\":\"none\"}")]
    [InlineData("{\"lines\":[1,null,\"x\",{\"quantity\":2}]}")]
    [InlineData("{\"lines\":[{\"name\":\"CGST 9%\",\"amount\":9}]}")]
    public void No_item_lines_is_its_own_error(string text) =>
        Assert.Equal("AI_NO_LINES", Assert.Throws<HqException>(() => GemmaExtractor.Parse(text)).Code);

    [Fact]
    public void Partial_and_mistyped_fields_become_null_with_warnings()
    {
        var x = GemmaExtractor.Parse("""
            {"documentType":7,"seller":{"name":"  ","gstin":"37 abdcs 0279 c1z h"},"buyer":"ours",
             "invoiceNumber":12,"invoiceDate":"06/11/2026","priceBasis":"GST extra","discountPercent":"150",
             "lines":[{"name":"25 mm elbow","quantity":true,"rate":"1e3","amount":"₹1,250/-","discountPercent":-5},
                      {"sourceText":"20 mm tee","quantity":"1 1/2","rate":{"v":1}}],
             "invoiceTotal":"30,410","warnings":"not a list"}
            """);
        Assert.Equal("other", x.DocumentType);
        Assert.Equal("37ABDCS0279C1ZH", x.Seller?.Gstin);
        Assert.Null(x.Buyer);
        Assert.Null(x.InvoiceNumber);
        Assert.Null(x.InvoiceDate);
        Assert.Null(x.PriceBasis);
        Assert.Null(x.DiscountPercent);
        Assert.Equal(2, x.Lines.Count);
        Assert.Null(x.Lines[0].Quantity);
        Assert.Null(x.Lines[0].Rate);
        Assert.Null(x.Lines[0].DiscountPercent);
        Assert.Equal("20 mm tee", x.Lines[1].Name);
        Assert.Equal(1.5m, x.Lines[1].Quantity);
        Assert.Null(x.Lines[1].Rate);
        Assert.Equal(30410m, x.InvoiceTotal);
        Assert.Contains(x.Warnings, w => w.Contains("Bill date"));
        Assert.Contains(x.Warnings, w => w.Contains("discount"));
    }

    [Fact]
    public void A_GSTIN_written_with_many_gaps_is_not_cut_short()
    {
        // 15 characters spread over 29: clipping to 20 before removing the spaces lost the last five.
        var x = GemmaExtractor.Parse("""{"seller":{"gstin":"3 7 A B D C S 0 2 7 9 C 1 Z H"},"lines":[{"name":"elbow"}]}""");
        Assert.Equal("37ABDCS0279C1ZH", x.Seller?.Gstin);
    }

    [Theory]
    [InlineData(" 37abdcs0279c1zh ", "37ABDCS0279C1ZH")]
    [InlineData("37 ABDCS 0279\tC1ZH", "37ABDCS0279C1ZH")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void GSTINs_are_compared_without_spaces_in_upper_case(string? raw, string? expected) =>
        Assert.Equal(expected, CaptureService.NormalGstin(raw));

    // ------------------------------------------------------------------ rates
    [Fact]
    public void The_GST_basis_preview_prices_lines_like_the_draft()
    {
        var unread = new ExtractedLine("20 mm tee", "20 mm tee", null, 3, null, null, null, 18, null);
        Assert.Null(CaptureService.PrintedRate(unread));
        // The draft falls back to the catalogue purchase rate; the preview used 0 here, so the inclusive/exclusive
        // comparison against the printed total was made on a different bill from the one the user sees.
        Assert.Equal(41.5m, CaptureService.RateFor(unread, 41.5m));
        var amountOnly = unread with { Amount = 100m };
        Assert.Equal(33.3333m, CaptureService.RateFor(amountOnly, 41.5m));
        var zeroQty = new ExtractedLine("x", "x", null, 0, null, 5m, null, null, 7m);
        Assert.Equal(7m, CaptureService.RateFor(zeroQty)); // quantity 0 is read as 1, as in the draft
        Assert.Equal(27.9m, CaptureService.RateFor(new ExtractedLine("e", "e", null, 150, null, 27.90m, null, 18, 0m)));
    }

    // ------------------------------------------------------------------ images
    private static byte[] Jpeg(int w = 120, int h = 80)
    {
        using var bmp = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.FillRectangle(Brushes.Black, 10, 10, 50, 5); }
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Jpeg);
        return ms.ToArray();
    }

    [Fact]
    public void Normalised_pages_are_jpeg_and_turn()
    {
        var page = ImageNormalizer.Normalize(Jpeg(), 90);
        Assert.True(page is [0xFF, 0xD8, ..]);
        using var img = Image.FromStream(new MemoryStream(page));
        Assert.Equal((80, 120), (img.Width, img.Height));
    }

    [Fact]
    public void Damaged_pictures_are_refused_cleanly()
    {
        var jpeg = Jpeg();
        foreach (var bad in new[] { "not a picture"u8.ToArray(), jpeg[..40], jpeg[..(jpeg.Length / 2)], new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0 }, Png(1, 1, truncate: true) })
        {
            // Either a clean refusal or (GDI+ fills a cut-off JPEG with grey) a picture; never a crash type.
            try { Assert.True(ImageNormalizer.Normalize(bad) is [0xFF, 0xD8, ..]); }
            catch (HqException e) { Assert.Equal(FailureKind.Invalid, e.Kind); }
        }
        Assert.Throws<HqException>(() => ImageNormalizer.Normalize([]));
    }

    [Fact]
    public void A_small_file_claiming_huge_dimensions_is_refused_before_decoding()
    {
        var e = Assert.Throws<HqException>(() => ImageNormalizer.Normalize(Png(60000, 60000, truncate: false)));
        Assert.Equal(FailureKind.Invalid, e.Kind);
    }

    /// <summary>A PNG with the given header dimensions and a tiny (wrong-sized) image body.</summary>
    private static byte[] Png(int width, int height, bool truncate)
    {
        using var ms = new MemoryStream();
        ms.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        void Chunk(string type, byte[] data)
        {
            var len = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
            ms.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            ms.Write(body);
            var crc = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(body));
            ms.Write(crc);
        }
        var ihdr = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk("IHDR", ihdr);
        Chunk("IDAT", [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01]); // a few bytes of zlib: far too little
        if (!truncate) Chunk("IEND", []);
        var bytes = ms.ToArray();
        return truncate ? bytes[..^6] : bytes;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return ~crc;
    }

    // ------------------------------------------------------------------ store state machine
    private static CaptureStore NewStore() => new(Path.Combine(Path.GetTempPath(), "mkhq-api-capture", Guid.NewGuid().ToString("N")));

    [Fact]
    public void A_capture_is_claimed_once_and_a_discard_during_the_read_sticks()
    {
        var store = NewStore();
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        Assert.True(store.Update(c.Id, CaptureStore.States.Extracting, countAttempt: true, onlyFrom: CaptureStore.States.Received));
        Assert.False(store.Update(c.Id, CaptureStore.States.Extracting, countAttempt: true, onlyFrom: CaptureStore.States.Received)); // second worker
        Assert.Equal(1, store.Get(c.Id)!.Attempts);

        store.Update(c.Id, CaptureStore.States.Discarded); // the user throws it away mid-read
        Assert.False(store.Update(c.Id, CaptureStore.States.Ready, draftId: "d1", onlyFrom: CaptureStore.States.Extracting));
        Assert.False(store.Update(c.Id, CaptureStore.States.Failed, errorCode: "X", onlyFrom: CaptureStore.States.Extracting));
        var after = store.Get(c.Id)!;
        Assert.Equal((CaptureStore.States.Discarded, null, null), (after.Status, after.DraftId, after.ErrorCode));
        Assert.Empty(store.List("mbm"));
        Assert.False(store.Update("no-such-id", CaptureStore.States.Ready));
    }

    [Fact]
    public void A_read_cut_off_by_a_restart_is_handed_back()
    {
        var store = NewStore();
        var page = Jpeg();
        var c = store.Add("mbm", "sale", "phone", page, page);
        store.Update(c.Id, CaptureStore.States.Extracting, onlyFrom: CaptureStore.States.Received);
        Assert.Equal([c.Id], store.Pending());
        Assert.Equal(CaptureStore.States.Received, store.Get(c.Id)!.Status);
    }

    [Fact]
    public void Pages_stay_numbered_and_the_last_one_cannot_go()
    {
        var store = NewStore();
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        Assert.Equal(2, store.AddPage(c.Id, page, page));
        Assert.Equal(3, store.AddPage(c.Id, page, page));
        store.RemovePage(c.Id, 1);
        Assert.Equal(2, store.Images(c.Id).Count);
        Assert.Throws<HqException>(() => store.Image(c.Id, 3));
        store.RemovePage(c.Id, 2);
        Assert.Equal("CAPTURE_PAGES", Assert.Throws<HqException>(() => store.RemovePage(c.Id, 1)).Code);
        for (var i = 0; i < 9; i++) store.AddPage(c.Id, page, page);
        Assert.Equal("CAPTURE_PAGES", Assert.Throws<HqException>(() => store.AddPage(c.Id, page, page)).Code);
    }

    // ------------------------------------------------------------------ service: claim, retry, failure
    private sealed class FakeReader(Func<CancellationToken, Task<ExtractionResult>> extract) : IBillReader
    {
        public int Reads;
        public string Name => "fake";
        public Task<ExtractionResult> ExtractAsync(IReadOnlyList<byte[]> pages, string kind, string? model = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Reads);
            return extract(ct);
        }
        public Task<int> OrientationAsync(byte[] page, CancellationToken ct = default) => Task.FromResult(0);
    }

    // Only the store and the reader are used on these paths (a draft build would need HQ and fails, as a crash would).
    private static CaptureService Service(CaptureStore store, IBillReader reader) =>
        new(store, reader, null!, null!, null!, null!, null!, NullLogger<CaptureService>.Instance);

    [Fact]
    public async Task A_reader_timeout_fails_the_photo_instead_of_leaving_it_being_read()
    {
        var store = NewStore();
        var reader = new FakeReader(_ => throw new TaskCanceledException("HttpClient.Timeout"));
        var service = Service(store, reader);
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        await service.ProcessAsync(c.Id, null, CancellationToken.None);
        var after = store.Get(c.Id)!;
        Assert.Equal((CaptureStore.States.Failed, "CAPTURE_FAILED"), (after.Status, after.ErrorCode));
        service.Retry(c.Id, null); // and it can be read again
        Assert.Equal(CaptureStore.States.Received, store.Get(c.Id)!.Status);
    }

    [Fact]
    public async Task Shutdown_leaves_the_read_for_the_next_start()
    {
        var store = NewStore();
        using var stop = new CancellationTokenSource();
        var service = Service(store, new FakeReader(ct => { stop.Cancel(); ct.ThrowIfCancellationRequested(); return null!; }));
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ProcessAsync(c.Id, null, stop.Token));
        Assert.Equal(CaptureStore.States.Extracting, store.Get(c.Id)!.Status);
        Assert.Equal([c.Id], store.Pending());
    }

    [Fact]
    public async Task The_same_photo_queued_twice_is_read_once()
    {
        var store = NewStore();
        var gate = new TaskCompletionSource<ExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new FakeReader(_ => gate.Task);
        var service = Service(store, reader);
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        var first = service.ProcessAsync(c.Id, null, CancellationToken.None);
        await service.ProcessAsync(c.Id, null, CancellationToken.None); // the second worker gives up at once
        Assert.Equal(1, reader.Reads);
        Assert.Equal("CAPTURE_BUSY", Assert.Throws<HqException>(() => service.Retry(c.Id, null)).Code);

        service.Discard("mbm", c.Id);                                    // thrown away while being read
        gate.SetException(new InvalidOperationException("model output"));
        await first;
        Assert.Equal(CaptureStore.States.Discarded, store.Get(c.Id)!.Status);
    }

    [Fact]
    public void A_saved_bill_is_never_read_again()
    {
        var store = NewStore();
        var service = Service(store, new FakeReader(_ => throw new InvalidOperationException()));
        var page = Jpeg();
        var c = store.Add("mbm", "purchase", "desktop", page, page);
        store.Update(c.Id, CaptureStore.States.Saved, documentId: 42);
        Assert.Equal("CAPTURE_BUSY", Assert.Throws<HqException>(() => service.Retry(c.Id, null)).Code);
        Assert.Equal(CaptureStore.States.Saved, store.Get(c.Id)!.Status);
        Assert.Equal("CAPTURE_NOT_FOUND", Assert.Throws<HqException>(() => service.Retry("missing", null)).Code);
        Assert.Equal("CAPTURE_NOT_FOUND", Assert.Throws<HqException>(() => service.Discard("mp", c.Id)).Code); // other firm's photo
    }
}
