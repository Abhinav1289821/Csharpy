using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Journal;

namespace MK.Hq.Engine.Capture;

public sealed record LearnRequest(long DocumentId, string Reference, IReadOnlyList<LearnedLine> Lines);
public sealed record LearnedLine(string SourceText, string? Name, long ItemId, long UnitId);

/// <summary>
/// Photo → AI reading (<see cref="IBillReader"/>) → catalogue/party matching → a bill DRAFT for the existing bill editor. A draft is never posted
/// by this service: the shop owner reviews it beside the photo and saves it through the normal reviewed path.
/// Extraction runs in a background queue so phone uploads are ready by the time anyone looks at the screen.
/// </summary>
public sealed class CaptureService(
    CaptureStore store,
    IBillReader extractor,
    Catalogue catalogue,
    FirmRegistry firms,
    FirmProfiles profiles,
    DocumentService documents,
    EngineStore drafts,
    ILogger<CaptureService> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Channel<(string Id, string? Model)> _queue = Channel.CreateUnbounded<(string, string?)>();

    public static readonly IReadOnlySet<string> Kinds = new HashSet<string> { "purchase", "sale", "salesOrder" };

    public ChannelReader<(string Id, string? Model)> Queue => _queue.Reader;

    public CaptureRecord Add(string firmId, string kind, string source, byte[] image)
    {
        firms.Get(firmId);
        Guard.Require(Kinds.Contains(kind), "CAPTURE_KIND", "Choose purchase, sale or estimate");
        var record = store.Add(firmId, kind, source, image, ImageNormalizer.Normalize(image));
        _queue.Writer.TryWrite((record.Id, null));
        return record;
    }

    /// <summary>Adds another page (or the other half) of the same bill and reads the whole bill again.</summary>
    public CaptureRecord AddPage(string firmId, string id, byte[] image)
    {
        var c = Owned(firmId, id);
        Guard.Require(c.Status is not (CaptureStore.States.Saved or CaptureStore.States.Extracting), "CAPTURE_BUSY", "Wait until the photo has been read", FailureKind.Conflict);
        store.AddPage(id, image, ImageNormalizer.Normalize(image));
        return Retry(id, null);
    }

    /// <summary>Turns a page by 90° steps (from the original, so quality never degrades) and reads the bill again.</summary>
    public CaptureRecord Rotate(string firmId, string id, int page, int degrees, bool reread = true)
    {
        var c = Owned(firmId, id);
        Guard.Require(degrees % 90 == 0, "INVALID_ROTATION", "Rotate in steps of 90°");
        Guard.Require(c.Status is not (CaptureStore.States.Saved or CaptureStore.States.Extracting), "CAPTURE_BUSY", "Wait until the photo has been read", FailureKind.Conflict);
        var (original, rotation) = store.Original(id, page);
        var next = ((rotation + degrees) % 360 + 360) % 360;
        store.ReplacePage(id, page, ImageNormalizer.Normalize(original, next), next);
        return reread ? Retry(id, null) : store.Get(id)!;
    }

    public CaptureRecord RemovePage(string firmId, string id, int page)
    {
        var c = Owned(firmId, id);
        // Checked before deleting: a saved bill keeps its evidence, and a page must not vanish under a read in progress.
        Guard.Require(c.Status is not (CaptureStore.States.Saved or CaptureStore.States.Extracting), "CAPTURE_BUSY", "Wait until the photo has been read", FailureKind.Conflict);
        store.RemovePage(id, page);
        return Retry(id, null);
    }

    private CaptureRecord Owned(string firmId, string id)
    {
        var c = store.Get(id);
        Guard.Require(c is not null && c.Firm == firmId, "CAPTURE_NOT_FOUND", "That photo is no longer here", FailureKind.NotFound);
        return c!;
    }

    public CaptureRecord Retry(string id, string? model)
    {
        // Compare-and-set on the status we checked: a check followed by a blind update let a bill saved (Learn) or claimed
        // by a worker in between be put back to "received" and read again, giving a second draft of an already saved bill.
        while (true)
        {
            var c = store.Get(id) ?? throw HqException.NotFound("CAPTURE_NOT_FOUND", "That photo is no longer here");
            Guard.Require(c.Status is not (CaptureStore.States.Saved or CaptureStore.States.Extracting), "CAPTURE_BUSY", "This photo is being read or is already saved as a bill", FailureKind.Conflict);
            if (store.Update(id, CaptureStore.States.Received, onlyFrom: c.Status)) break;
        }
        _queue.Writer.TryWrite((id, model));
        return store.Get(id)!;
    }

    public void RequeuePending()
    {
        foreach (var id in store.Pending()) _queue.Writer.TryWrite((id, null));
    }

    public async Task ProcessAsync(string id, string? model, CancellationToken ct)
    {
        // Claim atomically: the same id can be queued twice (a rotate or retry while it waits), and two workers must not
        // both read it (double AI cost, and two drafts of one bill, either of which could be saved).
        if (!store.Update(id, CaptureStore.States.Extracting, countAttempt: true, onlyFrom: CaptureStore.States.Received)) return;
        var c = store.Get(id);
        if (c is null) return;
        try
        {
            await UprightAsync(id, ct);
            var result = await extractor.ExtractAsync(store.Images(id), c.Kind, model, ct);
            var (draftId, summary) = await BuildDraftAsync(c, result, ct);
            // Only finish a read that is still ours: a Discard (or a save) meanwhile must not be brought back to life.
            if (!store.Update(id, CaptureStore.States.Ready, result.Model, JsonSerializer.Serialize(result, Json), draftId, summary, elapsedMs: result.ElapsedMs,
                    onlyFrom: CaptureStore.States.Extracting))
            {
                drafts.DeleteDraft(draftId, c.Firm);
                log.LogInformation("Capture {Id} changed while it was read; its new draft was dropped", id);
                return;
            }
            log.LogInformation("Capture {Id} read by {Model} in {Ms} ms: {Summary}", id, result.Model, result.ElapsedMs, summary);
        }
        catch (HqException e)
        {
            store.Update(id, CaptureStore.States.Failed, errorCode: e.Code, errorMessage: e.Message, onlyFrom: CaptureStore.States.Extracting);
            log.LogWarning("Capture {Id} failed: {Code} {Message}", id, e.Code, e.Message);
        }
        // Only a shutdown leaves the read "extracting" (Pending() hands it back at the next start). Any other cancellation
        // (an HTTP or agent timeout inside a reader) must fail it, or the photo stays "being read" and refuses every
        // retry, rotate and new page until the service restarts.
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            store.Update(id, CaptureStore.States.Failed, errorCode: "CAPTURE_FAILED", errorMessage: "Reading the photo failed unexpectedly; retry", onlyFrom: CaptureStore.States.Extracting);
            log.LogError(e, "Capture {Id} crashed", id);
        }
    }

    /// <summary>Turns sideways or upside-down pages upright once (only pages never rotated by hand or by an earlier check).</summary>
    private async Task UprightAsync(string id, CancellationToken ct)
    {
        var count = store.Images(id).Count;
        for (var page = 1; page <= count; page++)
        {
            var (original, rotation) = store.Original(id, page);
            if (rotation != 0) continue;
            var turn = await extractor.OrientationAsync(store.Image(id, page), ct);
            if (turn != 0) store.ReplacePage(id, page, ImageNormalizer.Normalize(original, turn), turn);
        }
    }

    /// <summary>After the bill is saved in HQ: remember the wording → item choices, and close the capture.</summary>
    public void Learn(string firmId, string id, LearnRequest request)
    {
        var c = Owned(firmId, id);
        store.Learn(firmId, request.Lines.SelectMany(l => new[] { (ItemMatcher.AliasKey(l.SourceText), l.ItemId, l.UnitId) }
            .Concat(l.Name is null ? [] : [(ItemMatcher.AliasKey(l.Name), l.ItemId, l.UnitId)])));
        store.Update(id, CaptureStore.States.Saved, documentId: request.DocumentId, reference: request.Reference);
        if (c.DraftId is not null) drafts.DeleteDraft(c.DraftId, firmId);
    }

    public void Discard(string firmId, string id)
    {
        var c = Owned(firmId, id);
        store.Update(id, CaptureStore.States.Discarded);
        if (c.DraftId is not null) drafts.DeleteDraft(c.DraftId, firmId);
    }

    // ------------------------------------------------------------------ draft building

    private async Task<(string DraftId, string Summary)> BuildDraftAsync(CaptureRecord c, ExtractionResult result, CancellationToken ct)
    {
        var firm = firms.Get(c.Firm);
        var profile = await profiles.GetAsync(firm, ct);
        var snapshot = await catalogue.GetAsync(firm, ct);
        var x = result.Data;
        var warnings = new List<string>(x.Warnings);
        var purchase = c.Kind == "purchase";
        var today = DateOnly.FromDateTime(DateTime.Now);

        // ---- party ----
        var written = purchase ? x.Seller : x.Buyer;
        long? accountId = null;
        var paymentMode = 0;
        var interstate = false;
        if (purchase)
        {
            // Normalised once for both uses: a lowercase or spaced reading must neither fail the check digit nor miss the
            // supplier (whose HQ GSTIN is compared in the same form).
            var gstin = NormalGstin(written?.Gstin);
            if (gstin is not null && !Gstin.IsValid(gstin)) warnings.Add($"Supplier GSTIN '{gstin}' fails the GST check digit; check the photo");
            var byGstin = gstin is null ? [] : snapshot.Parties.Where(p => p.Active && NormalGstin(p.Gstin) == gstin && p.Type is "Supplier" or "Customer-Supplier").ToList();
            var byName = written?.Name is { } n ? snapshot.Parties.Where(p => p.Active && p.IsSupplier && Same(p.Name, n)).ToList() : [];
            if (byGstin.Count == 1) { accountId = byGstin[0].AccountId; paymentMode = 1; }
            else if (byGstin.Count > 1) warnings.Add($"{byGstin.Count} HQ suppliers share GSTIN {gstin}; choose the account");
            else if (byName.Count == 1) { accountId = byName[0].AccountId; paymentMode = 1; warnings.Add($"Supplier matched by name only ({byName[0].Name}); confirm it"); }
            else warnings.Add($"Supplier '{written?.Name ?? "?"}'{(gstin is null ? "" : $" ({gstin})")} is not in HQ. Choose the account, or create it in HQ first.");
            var stateCode = Gstin.StateCode(gstin);
            interstate = stateCode is not null && stateCode != "37";
            if (interstate) warnings.Add($"Supplier is in another state ({Gstin.HqStateCaption(stateCode!) ?? stateCode}): saved as an IGST purchase");
            if (NormalGstin(x.Buyer?.Gstin) is { } buyerGstin && NormalGstin(firm.Options.Gstin) is { } own && buyerGstin != own)
                warnings.Add($"This bill is addressed to GSTIN {buyerGstin}, not {firm.Name} ({own}). Check the firm.");
            else if (x.Buyer?.Name is { } buyer && firms.All.FirstOrDefault(f => f.Id != firm.Id && Same(f.Name, buyer)) is { } other)
                warnings.Add($"The bill is addressed to {buyer}; it may belong to {other.Name}");
        }
        else if (written is not null)
        {
            var mobile = Digits(written.Mobile);
            var match = snapshot.Parties.Where(p => p.Active && p.IsCustomer &&
                ((mobile.Length >= 10 && (Digits(p.Mobile).EndsWith(mobile[^10..]) || Digits(p.Phone).EndsWith(mobile[^10..]))) || (written.Name is { } wn && Same(p.Name, wn)))).ToList();
            if (match.Count == 1) accountId = match[0].AccountId;
        }

        // ---- dates and supplier reference ----
        var date = today;
        if (purchase && x.InvoiceDate is { } billDate)
        {
            if (billDate >= firm.PeriodStart && billDate <= today) date = billDate;
            else warnings.Add($"Printed bill date {billDate:dd-MM-yyyy} is outside this year or in the future; using today");
        }
        if (purchase && x.InvoiceNumber is null) warnings.Add("Supplier bill number not read; enter it");

        // ---- lines ----
        var matcher = new ItemMatcher(snapshot.Items);
        var aliases = store.Aliases(c.Firm);
        IReadOnlyDictionary<long, int> history;
        await using (var s = await firm.OpenAsync(ct))
            history = await HistoryAsync(s, purchase, accountId, purchase ? NormalGstin(written?.Gstin) : written?.Gstin, before: null);
        var lines = new List<object>();
        var matched = 0;
        foreach (var (l, i) in x.Lines.Select((l, i) => (l, i)))
        {
            var m = matcher.Match(l, aliases, history: history);
            var chosen = m.Chosen;
            if (chosen is not null) matched++;
            var quantity = l.Quantity;
            if (quantity is null or <= 0)
            {
                warnings.Add($"Line {i + 1} ({l.Name}): quantity not read; set to 1");
                quantity = 1;
            }
            // The same rule as the GST-basis preview below (RateFor), so the basis is chosen on the rates the draft shows.
            var rate = PrintedRate(l);
            if (l.Rate is { } printedRate && rate != Money.Round(printedRate, 4) && l.Amount is { } printedAmount)
                // e.g. P&F printed as 220 + 18% = 259.60: the amount is what the bill charges for this row.
                warnings.Add($"Line {i + 1} ({l.Name}): {Money.Plain(quantity.Value)} × {Money.Text(printedRate)} ≠ printed amount {Money.Text(printedAmount)}; using {Money.Text(printedAmount)} ÷ {Money.Plain(quantity.Value)}");
            else if (l.Rate is null && l.Amount is { } amt && purchase)
                warnings.Add($"Line {i + 1}: rate worked out from the amount ({Money.Text(amt)} ÷ {Money.Plain(quantity.Value)})");
            rate ??= chosen is null ? null : purchase ? chosen.PurchaseRate : chosen.SaleRate;
            lines.Add(new
            {
                itemId = chosen?.ItemId,
                unitId = chosen?.UnitId,
                name = chosen?.Name ?? l.Name,
                quantity = Money.Plain(quantity.Value),
                rate = rate is null ? "" : Money.Plain(rate.Value),
                discountPercent = (l.DiscountPercent ?? x.DiscountPercent) is { } d && d > 0 ? Money.Plain(d) : "",
                description = (string?)null,
                lineId = (long?)null,
                orderLineId = (long?)null,
                sourceText = l.SourceText,
                written = new { l.Name, l.Unit, l.Hsn, l.GstRate, l.Amount, l.Rate },
                match = new { reason = m.Reason, candidates = m.Candidates },
            });
        }
        if (matched < x.Lines.Count) warnings.Add($"{x.Lines.Count - matched} of {x.Lines.Count} lines need an item chosen");

        // ---- price basis: the one that reproduces the printed total ----
        string? basis = x.PriceBasis;
        decimal? computed = null;
        if (purchase && x.InvoiceTotal is { } printed && matched == x.Lines.Count && accountId is not null)
        {
            var results = new List<(string Basis, decimal Payable)>();
            foreach (var b in new[] { "exclusive", "inclusive" })
            {
                try
                {
                    var p = await documents.PreviewAsync(c.Firm, new DocumentRequest
                    {
                        Kind = DocumentKind.Purchase, Date = date, PaymentMode = paymentMode, AccountId = accountId, Interstate = interstate,
                        Basis = b == "inclusive" ? TaxBasis.Inclusive : TaxBasis.Exclusive,
                        Supplier = new SupplierBill((x.InvoiceNumber ?? "PREVIEW") + "~", date),
                        // Exactly the draft's lines: its rate (catalogue purchase rate when none was read, not 0) and discount.
                        Lines = x.Lines.Select(l => (Line: l, Item: matcher.Match(l, aliases, history: history).Chosen!))
                            .Select(p => new LineRequest(p.Item.ItemId, p.Item.UnitId, p.Line.Quantity is > 0 ? p.Line.Quantity.Value : 1,
                                RateFor(p.Line, p.Item.PurchaseRate), Math.Max(0, p.Line.DiscountPercent ?? x.DiscountPercent ?? 0))).ToList(),
                    }, ct);
                    results.Add((b, p.Quote.Payable));
                }
                catch (HqException) { /* leave basis for the user */ }
            }
            var exact = results.Where(r => r.Payable == printed).ToList();
            if (exact.Count >= 1) { basis = exact[0].Basis; computed = exact[0].Payable; }
            else if (results.Count > 0)
            {
                var close = results.MinBy(r => Math.Abs(r.Payable - printed));
                basis ??= close.Basis;
                computed = close.Payable;
                warnings.Add($"Printed total ₹{Money.Text(printed)} but the lines give ₹{Money.Text(close.Payable)} ({close.Basis} of GST). Check quantities, rates, discounts and any freight/packing.");
            }
        }

        var payload = new
        {
            kind = c.Kind,
            date = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            paymentMode,
            accountId,
            party = new { name = written?.Name ?? "", town = written?.Town ?? "", phone = written?.Mobile ?? "", mobile = "", gstin = written?.Gstin ?? "", address = written?.Address ?? "" },
            interstate,
            basis,
            dueDate = (string?)null,
            notes = "",
            message = (string?)null,
            salespersonId = (int?)null,
            numberSuffix = "",
            supplier = new { number = x.InvoiceNumber ?? "", date = (x.InvoiceDate ?? date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
            sourceOrderId = (long?)null,
            lines,
            capture = new
            {
                id = c.Id, pages = store.Images(c.Id).Count, model = result.Model, printedTotal = x.InvoiceTotal, computedTotal = computed, warnings, documentType = x.DocumentType,
                seller = x.Seller, buyer = x.Buyer, notes = x.Notes, taxes = new { x.Cgst, x.Sgst, x.Igst, x.RoundOff, x.Subtotal },
            },
        };
        var partyName = accountId is { } aid ? snapshot.Parties.FirstOrDefault(p => p.AccountId == aid)?.Name : written?.Name;
        var title = $"Photo · {partyName ?? "unknown party"} · {x.Lines.Count} lines{(x.InvoiceTotal is { } t ? " · ₹" + Money.Text(t) : "")}";
        // A re-read replaces the earlier draft of this photo instead of piling up copies.
        if (c.DraftId is not null) drafts.DeleteDraft(c.DraftId, c.Firm);
        var draftId = Guid.NewGuid().ToString();
        drafts.SaveDraft(draftId, c.Firm, c.Kind, title, JsonSerializer.Serialize(payload, Json), 0);
        var summary = $"{partyName ?? "unknown party"} · {matched}/{x.Lines.Count} items matched{(x.InvoiceTotal is { } tt ? " · printed ₹" + Money.Text(tt) : "")}";
        return (draftId, summary);
    }

    /// <summary>
    /// How often each item appeared on this supplier's earlier HQ purchase bills (by account, or by GSTIN on cash bills).
    /// For sales and estimates: the shop's regularly sold items (3+ times in the last year). Only bills before
    /// <paramref name="before"/> count, when given (used by the evaluation so a bill never sees itself).
    /// </summary>
    public static async Task<IReadOnlyDictionary<long, int>> HistoryAsync(Sql.HqSession s, bool purchase, long? accountId, string? gstin, DateOnly? before)
    {
        var cutoff = before?.ToDateTime(TimeOnly.MinValue) ?? new DateTime(2100, 1, 1);
        var rows = purchase
            ? await s.QueryAsync<(long Item, int N)>("""
                SELECT CAST(r.Item_ID AS bigint), COUNT(*) FROM dbo.IQDoc_Recd r JOIN dbo.IQDoc_Header h ON h.Doc_Id=r.Doc_ID
                LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id
                WHERE h.Tt_Id IN (500,501) AND h.Doc_Date < @cutoff
                  AND ((@account IS NOT NULL AND h.HeadAct_ID=@account) OR (@gstin <> '' AND UPPER(LTRIM(RTRIM(ISNULL(c.GSTIN,''))))=@gstin))
                GROUP BY r.Item_ID
                """, new { cutoff, account = accountId, gstin = (gstin ?? "").Trim().ToUpperInvariant() })
            : await s.QueryAsync<(long Item, int N)>("""
                SELECT Item, COUNT(*) FROM (
                  SELECT CAST(d.Item_ID AS bigint) Item FROM dbo.IQDoc_Delv d JOIN dbo.IQDoc_Header h ON h.Doc_Id=d.Doc_ID WHERE h.Doc_Date < @cutoff AND h.Doc_Date >= DATEADD(year,-1,@cutoff)
                  UNION ALL
                  SELECT CAST(o.Item_ID AS bigint) FROM dbo.IQOrd_Detail o JOIN dbo.IQDoc_Header h ON h.Doc_Id=o.Doc_ID WHERE h.Tt_Id=249 AND h.Doc_Date < @cutoff AND h.Doc_Date >= DATEADD(year,-1,@cutoff)
                ) x GROUP BY Item HAVING COUNT(*) >= 3
                """, new { cutoff = before is null ? DateTime.Today.AddDays(1) : cutoff });
        return rows.ToDictionary(r => r.Item, r => purchase ? r.N : Math.Max(1, r.N / 10));
    }

    /// <summary>The unit rate the bill itself gives: the printed rate, unless the printed amount says otherwise (e.g. P&amp;F
    /// incl. GST); the amount ÷ quantity when only the amount was read; null when neither was.</summary>
    public static decimal? PrintedRate(ExtractedLine l)
    {
        var q = l.Quantity is > 0 ? l.Quantity.Value : 1;
        if (l.Rate is { } r && (l.Amount is not { } a || a <= 0 || Math.Abs(r * q - a) <= Math.Max(1m, a * 0.01m))) return Money.Round(r, 4);
        return l.Amount is { } amount ? Money.Round(amount / q, 4) : null;
    }

    /// <summary>The unit rate the draft uses: <see cref="PrintedRate"/>, else the matched item's own rate
    /// (<paramref name="fallback"/>) — the draft never prices an unread line at 0.</summary>
    public static decimal RateFor(ExtractedLine l, decimal fallback = 0) => PrintedRate(l) ?? fallback;

    /// <summary>A GSTIN as compared and checked: every space removed, upper case; null when nothing is left.
    /// <see cref="Gstin.IsValid"/> itself stays strict.</summary>
    public static string? NormalGstin(string? gstin)
    {
        if (gstin is null) return null;
        var s = new string(gstin.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();
        return s.Length == 0 ? null : s;
    }

    private static bool Same(string a, string b) => ItemMatcher.AliasKey(a) == ItemMatcher.AliasKey(b);
    private static string Digits(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());
}
