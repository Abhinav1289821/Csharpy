using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Domain.Text;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Documents;

using Row = IReadOnlyDictionary<string, object?>;

/// <summary>Every HQ row of one document plus the bill-wise links that touch it, with a content hash.</summary>
public sealed record DocumentState(
    long DocumentId, int TtId, DocumentKind? Kind, Row Header, IReadOnlyList<Row> Lines, Row? CashTran, IReadOnlyList<Row> Charges,
    IReadOnlyList<SettlementEntry> Ledger, IReadOnlyList<AllocationLink> Links, IReadOnlyList<SettlementEntry> Related,
    int OtherFeatureRows, string ContentHash)
{
    public int ModifyCnt => Convert.ToInt32(Header["ModifyCnt"] ?? 0, CultureInfo.InvariantCulture);
    public string Series => DocumentResolver.Text(Header, "TSeries");
    public int Number => Convert.ToInt32(Header["TNo"], CultureInfo.InvariantCulture);
    public string Reference => Series + "/" + Number + DocumentResolver.Text(Header, "TExtn");
    public string LineKey => Kind switch { DocumentKind.Sale => "Delv_Id", DocumentKind.Purchase => "Recd_Id", _ => "OrdDltdID" };
    public IEnumerable<long> LineIds => Lines.Select(l => Convert.ToInt64(l[LineKey], CultureInfo.InvariantCulture));
    public IEnumerable<long> ItemIds => Lines.Select(l => Convert.ToInt64(l["Item_ID"], CultureInfo.InvariantCulture));
}

public sealed record DocumentSummary
{
    public long DocumentId { get; init; }
    public int TtId { get; init; }
    public string Kind { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Series { get; init; } = "";
    public int Number { get; init; }
    public string Reference { get; init; } = "";
    public DateOnly Date { get; init; }
    public int Mode { get; init; }
    public long AccountId { get; init; }
    public string Party { get; init; } = "";
    public string Town { get; init; } = "";
    public decimal Amount { get; init; }
    public decimal Open { get; init; }
    public int Lines { get; init; }
    public int ModifyCnt { get; init; }
    public string EnteredOn { get; init; } = "";
    public bool Cancelled { get; init; }
}

public sealed record DocumentLineView(long LineId, int Number, long ItemId, long UnitId, string Code, string Name, string Unit, string Hsn, string Description,
    decimal Quantity, decimal Rate, decimal DiscountPercent, decimal Discount, decimal Gross, decimal Taxable, decimal TaxRate, decimal Cgst, decimal Sgst, decimal Igst,
    decimal Net, long? OrderLineId, decimal? Supplied);

public sealed record DocumentView(
    DocumentSummary Summary, string FirmName, string[] Letterhead, string? FirmGstin, DateOnly DueDate, string PaymentCaption, TaxBasis Basis, bool Interstate,
    PreviewParty Party, string? SupplierBillNumber, DateOnly? SupplierBillDate, string Notes, string Message, string Salesperson,
    IReadOnlyList<DocumentLineView> Lines, IReadOnlyList<TaxSlab> Slabs, decimal Quantity, decimal Gross, decimal Discount, decimal Taxable,
    decimal Tax, decimal BeforeRounding, decimal RoundOff, decimal Total, string TotalInWords,
    IReadOnlyList<PreviewLedgerRow> Ledger, IReadOnlyList<SettlementView> Settlements, string ContentHash);

public sealed record SettlementView
{
    public long EntryId { get; init; }
    public long DocumentId { get; init; }
    public string Reference { get; init; } = "";
    public DateOnly Date { get; init; }
    public decimal Amount { get; init; }
}

public sealed class DocumentReader
{
    private static readonly string[] FeatureTables =
        ["IQCreditCard", "IQbnktran", "IQDoc_Params", "IQDoc_SrCrAdj", "IQDoc_Delv_D", "IQTCbook", "TimberDetails_Entry", "Vehicle_SerivceDetails"];

    /// <summary>Reads (and, inside a serializable transaction, locks) every row of a document.</summary>
    public static async Task<DocumentState?> StateAsync(HqSession s, long documentId)
    {
        var header = (await s.RowsAsync("SELECT * FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id = documentId })).FirstOrDefault();
        if (header is null) return null;
        var tt = Convert.ToInt32(header["Tt_Id"], CultureInfo.InvariantCulture);
        var kind = DocumentKinds.FromTt(tt);
        var (table, key) = kind switch
        {
            DocumentKind.Sale => ("IQDoc_Delv", "Delv_Id"),
            DocumentKind.Purchase => ("IQDoc_Recd", "Recd_Id"),
            DocumentKind.SalesOrder or DocumentKind.PurchaseOrder => ("IQOrd_Detail", "OrdDltdID"),
            _ => ((string?)null, (string?)null),
        };
        var lines = table is null ? [] : await s.RowsAsync($"SELECT * FROM dbo.{table} WHERE Doc_ID=@id ORDER BY Ln_No, {key}", new { id = documentId });
        var cash = (await s.RowsAsync("SELECT * FROM dbo.IQCash_TRAN WHERE Doc_Id=@id", new { id = documentId })).FirstOrDefault();
        var charges = await s.RowsAsync("SELECT * FROM dbo.IQDoc_Chrgs WHERE Doc_id=@id ORDER BY Ln_No", new { id = documentId });
        var ledger = await s.QueryAsync<SettlementEntry>("""
            SELECT CAST(SR_NO AS bigint) Id, CAST(Act_ID AS bigint) AccountId, LEFT(LTRIM(TYPE),1) Type, CAST(AMOUNT AS decimal(19,2)) Amount, CAST(ISNULL(BL_adjusted,0) AS decimal(19,2)) Allocated
            FROM dbo.IQDayBook WHERE Doc_ID=@id ORDER BY SR_NO
            """, new { id = documentId });
        var links = await s.QueryAsync<AllocationLink>("""
            SELECT CAST(a.Sr_No AS bigint) Source, CAST(a.Rel_Sr_No AS bigint) Target, CAST(a.Amount AS decimal(19,2)) Amount FROM dbo.ADJUST a
            WHERE EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=@id AND d.SR_NO IN (a.Sr_No, a.Rel_Sr_No)) ORDER BY a.Sr_No, a.Rel_Sr_No, a.Amount
            """, new { id = documentId });
        var related = await s.QueryAsync<SettlementEntry>("""
            SELECT CAST(e.SR_NO AS bigint) Id, CAST(e.Act_ID AS bigint) AccountId, LEFT(LTRIM(e.TYPE),1) Type, CAST(e.AMOUNT AS decimal(19,2)) Amount, CAST(ISNULL(e.BL_adjusted,0) AS decimal(19,2)) Allocated
            FROM dbo.IQDayBook e WHERE e.Doc_ID<>@id AND EXISTS (SELECT 1 FROM dbo.ADJUST a JOIN dbo.IQDayBook d ON d.SR_NO IN (a.Sr_No, a.Rel_Sr_No)
              WHERE d.Doc_ID=@id AND e.SR_NO IN (a.Sr_No, a.Rel_Sr_No)) ORDER BY e.SR_NO
            """, new { id = documentId });
        var other = 0;
        foreach (var t in FeatureTables)
            other += await s.ScalarAsync<int>($"SELECT COUNT(*) FROM dbo.{t} WHERE Doc_ID=@id", new { id = documentId });
        if (kind is DocumentKind.Sale or DocumentKind.Purchase)
            other += await s.ScalarAsync<int>($"""
                SELECT (SELECT COUNT(*) FROM dbo.IQDoc_Header h WITH (READCOMMITTEDLOCK) WHERE h.Doc_Id<>@id AND (h.ProcRefID=@id OR h.ZDoc_ID=@id))
                     + (SELECT COUNT(*) FROM dbo.IQDoc_Recd_SRNOS WHERE Doc_ID=@id OR Delv_DocID=@id)
                     + (SELECT COUNT(*) FROM dbo.{(kind == DocumentKind.Sale ? "IQDoc_Recd" : "IQDoc_Delv")} WHERE Doc_ID=@id)
                """, new { id = documentId });

        var hash = Hash(header, lines, cash, charges, ledger, links, related, other);
        return new DocumentState(documentId, tt, kind, header, lines, cash, charges, ledger, links, related, other, hash);
    }

    private static string Hash(Row header, IReadOnlyList<Row> lines, Row? cash, IReadOnlyList<Row> charges, IReadOnlyList<SettlementEntry> ledger,
        IReadOnlyList<AllocationLink> links, IReadOnlyList<SettlementEntry> related, int other)
    {
        var sb = new StringBuilder();
        void RowText(Row? r)
        {
            if (r is null) { sb.Append("null;"); return; }
            foreach (var (k, v) in r.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                sb.Append(k).Append('=').Append(v switch
                {
                    null => "~",
                    DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
                    decimal m => m.ToString("0.############", CultureInfo.InvariantCulture),
                    double f => f.ToString("R", CultureInfo.InvariantCulture),
                    byte[] b => Convert.ToBase64String(b),
                    _ => Convert.ToString(v, CultureInfo.InvariantCulture),
                }).Append('|');
            sb.Append(';');
        }
        RowText(header);
        foreach (var l in lines) RowText(l);
        RowText(cash);
        foreach (var c in charges) RowText(c);
        foreach (var e in ledger) sb.Append(e).Append(';');
        foreach (var l in links) sb.Append(l).Append(';');
        foreach (var r in related) sb.Append(r).Append(';');
        sb.Append(other);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    public static async Task<IReadOnlyList<DocumentSummary>> ListAsync(HqSession s, DocumentFilter f) =>
        await s.QueryAsync<DocumentSummary>($"""
            SELECT TOP (@take) CAST(h.Doc_Id AS bigint) DocumentId, CAST(h.Tt_Id AS int) TtId, RTRIM(ISNULL(t.Vouchtype,'')) Kind, RTRIM(ISNULL(t.Ttype,'')) TypeName,
              RTRIM(h.TSeries) Series, h.TNo Number, RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12))+RTRIM(ISNULL(h.TExtn,'')) Reference,
              CAST(h.Doc_Date AS date) Date, CAST(ISNULL(h.Mode,0) AS int) Mode, CAST(ISNULL(h.HeadAct_ID,h.Act_ID) AS bigint) AccountId,
              LTRIM(RTRIM(CASE WHEN h.Mode=1 OR c.Act_Name IS NULL OR LTRIM(c.Act_Name)='' THEN ISNULL(a.Act_Name,'') ELSE c.Act_Name END)) Party,
              LTRIM(RTRIM(ISNULL(c.Act_Town,''))) Town, CAST(ISNULL(h.Bill_Amt,0) AS decimal(19,2)) Amount,
              CAST(ISNULL((SELECT SUM(d.AMOUNT-ISNULL(d.BL_adjusted,0)) FROM dbo.IQDayBook d WHERE d.Doc_ID=h.Doc_Id AND d.Act_ID=h.HeadAct_ID AND d.TYPE IN ('D','C') AND h.Mode=1),0) AS decimal(19,2)) [Open],
              (SELECT COUNT(*) FROM dbo.IQDoc_Delv x WHERE x.Doc_ID=h.Doc_Id) + (SELECT COUNT(*) FROM dbo.IQDoc_Recd x WHERE x.Doc_ID=h.Doc_Id) + (SELECT COUNT(*) FROM dbo.IQOrd_Detail x WHERE x.Doc_ID=h.Doc_Id) Lines,
              CAST(ISNULL(h.ModifyCnt,0) AS int) ModifyCnt, ISNULL(h.BillEnteredSystem,'') EnteredOn, CAST(ISNULL(h.Doc_Cancel,0) AS bit) Cancelled
            FROM dbo.IQDoc_Header h
            LEFT JOIN dbo.TTypes t ON t.TT_ID=h.Tt_Id
            LEFT JOIN dbo.IQAccmst a ON a.Act_Id=h.HeadAct_ID
            LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id
            WHERE (@tts IS NULL OR h.Tt_Id IN (SELECT CAST(value AS int) FROM (SELECT LTRIM(RTRIM(x.i.value('.','varchar(10)'))) value FROM (SELECT CAST('<i>'+REPLACE(@tts,',','</i><i>')+'</i>' AS xml) xx) z CROSS APPLY z.xx.nodes('/i') x(i)) q))
              AND (@from IS NULL OR h.Doc_Date >= @from) AND (@to IS NULL OR h.Doc_Date <= @to)
              AND (@account IS NULL OR h.HeadAct_ID=@account OR h.Act_ID=@account)
              AND (@search IS NULL OR c.Act_Name LIKE @search OR a.Act_Name LIKE @search OR RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12)) LIKE @search OR h.Supp_Billno LIKE @search)
              AND (@before IS NULL OR h.Doc_Id < @before)
              AND (@only IS NULL OR h.Doc_Id = @only)
            ORDER BY h.Doc_Date DESC, h.Doc_Id DESC
            """, new
        {
            take = Math.Clamp(f.Take, 1, 500),
            tts = f.TtIds is { Count: > 0 } ? string.Join(',', f.TtIds) : null,
            from = f.From?.ToDateTime(TimeOnly.MinValue),
            to = f.To?.ToDateTime(TimeOnly.MinValue),
            account = f.AccountId,
            search = string.IsNullOrWhiteSpace(f.Search) ? null : "%" + f.Search.Trim().Replace("[", "[[]").Replace("%", "[%]") + "%",
            before = f.BeforeId,
            only = f.DocumentId,
        });

    public static async Task<DocumentView?> ViewAsync(HqSession s, FirmProfile profile, long documentId)
    {
        var state = await StateAsync(s, documentId);
        if (state is null) return null;
        // By id, not by scanning the latest 500 bills by date: a back-dated bill fell outside that window and failed to open.
        var summary = (await ListAsync(s, new DocumentFilter { Take = 1, DocumentId = documentId })).Single();
        var h = state.Header;
        var basis = Convert.ToInt32(h["Exclusive"] ?? 0, CultureInfo.InvariantCulture) == 1 ? TaxBasis.Inclusive : TaxBasis.Exclusive;
        var interstate = Convert.ToInt32(h["GST_Type"] ?? 1, CultureInfo.InvariantCulture) == 0;
        // Looked up by item and unit: an item with two unit rows (HQ allows it) has two rows here, and opening any bill with
        // it must not fail. IQItems_Units has no unique key, so a repeated row just shows the first (the item's own unit row
        // before any batch row); a line whose unit row has gone still shows the item's name.
        var unitRows = await s.QueryAsync<(long Id, long UnitId, string Code, string Name, string Unit)>("""
            SELECT CAST(im.Item_ID AS bigint), CAST(iu.UNit_Id AS bigint), LTRIM(RTRIM(ISNULL(im.Item_Code,''))), LTRIM(RTRIM(im.Item_Name)),
              LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(iu.ReportPack)),''), im.Item_Pack, '')))
            FROM dbo.IQItems_Mast im JOIN dbo.IQItems_Units iu ON iu.Item_ID=im.Item_ID WHERE im.Item_ID IN @ids
            ORDER BY CASE WHEN ISNULL(iu.Recd_ID,0)=0 THEN 0 ELSE 1 END, iu.ItemUnitsDtldID
            """, new { ids = state.ItemIds.DefaultIfEmpty(0).ToArray() });
        var byUnit = unitRows.GroupBy(x => (x.Id, x.UnitId)).ToDictionary(g => g.Key, g => g.First());
        var byItem = unitRows.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var supplied = new Dictionary<long, decimal>();
        if (state.Kind == DocumentKind.SalesOrder && state.Lines.Count > 0)
            foreach (var (id, qty) in await s.QueryAsync<(long, decimal)>(
                         "SELECT CAST(OrdDelv_Id AS bigint), CAST(SUM(Qty) AS decimal(19,4)) FROM dbo.IQDoc_Delv WHERE OrdDelv_Id IN @ids GROUP BY OrdDelv_Id",
                         new { ids = state.LineIds.ToArray() }))
                supplied[id] = qty;

        decimal D(Row r, string k) => r.TryGetValue(k, out var v) && v is not null ? Convert.ToDecimal(v, CultureInfo.InvariantCulture) : 0m;
        var lines = state.Lines.Select((l, i) =>
        {
            var itemId = Convert.ToInt64(l["Item_ID"], CultureInfo.InvariantCulture);
            var unitId = Convert.ToInt64(l["UnitID"], CultureInfo.InvariantCulture);
            var it = byUnit.TryGetValue((itemId, unitId), out var exact) ? exact : byItem.GetValueOrDefault(itemId);
            var taxRate = D(l, "Tax");
            var cgst = interstate ? 0 : D(l, "CGST_TaxAmt");
            var sgst = interstate ? 0 : D(l, "SGST_TaxAmt");
            var igst = interstate ? D(l, "TaxValue") : 0;
            var lineId = Convert.ToInt64(l[state.LineKey], CultureInfo.InvariantCulture);
            var orderLink = l.TryGetValue("OrdDelv_Id", out var o) && o is not null && Convert.ToInt64(o, CultureInfo.InvariantCulture) > 0 ? Convert.ToInt64(o, CultureInfo.InvariantCulture) : (long?)null;
            return new DocumentLineView(lineId, i + 1, itemId, unitId, it.Code ?? "", it.Name ?? "#" + itemId, it.Unit ?? "",
                DocumentResolver.Text(l, "HSNCODE"), DocumentResolver.Text(l, "ItemDesc"), D(l, "Qty"), D(l, "Rate"), D(l, "Disc"), D(l, "DiscValue"), D(l, "Value"),
                D(l, "GrossValue"), taxRate, cgst, sgst, igst, D(l, "NetValue"), orderLink, supplied.TryGetValue(lineId, out var sq) ? sq : null);
        }).ToList();
        var slabs = lines.GroupBy(l => l.TaxRate).OrderBy(g => g.Key)
            .Select(g => new TaxSlab(g.Key, g.Sum(x => x.Taxable), g.Sum(x => x.Cgst), g.Sum(x => x.Sgst), g.Sum(x => x.Igst))).ToList();
        var cash = state.CashTran ?? new Dictionary<string, object?>();
        var party = new PreviewParty(Convert.ToInt64(h["Act_ID"] ?? 0, CultureInfo.InvariantCulture), Convert.ToInt64(h["HeadAct_ID"] ?? 0, CultureInfo.InvariantCulture),
            summary.Party, DocumentResolver.Text(cash, "Act_Town"), DocumentResolver.Text(cash, "Act_PH1"), DocumentResolver.Text(cash, "GSTIN"),
            (int)(DocumentResolver.Number(cash, "GSTTaxType") ?? 2), DocumentResolver.Text(cash, "Actstate"),
            (DocumentResolver.Text(cash, "Address") + " " + DocumentResolver.Text(cash, "Address2")).Trim());
        var accountNames = state.Ledger.Count == 0 ? new Dictionary<long, string>() :
            (await s.QueryAsync<(long, string)>("SELECT CAST(Act_Id AS bigint), LTRIM(RTRIM(Act_Name)) FROM dbo.IQAccmst WHERE Act_Id IN @ids",
                new { ids = state.Ledger.Select(e => e.AccountId).Distinct().ToArray() })).ToDictionary(x => x.Item1, x => x.Item2);
        var contra = await s.QueryAsync<(long Sr, long Contra)>("SELECT CAST(SR_NO AS bigint), CAST(ISNULL(Contra_ID,0) AS bigint) FROM dbo.IQDayBook WHERE Doc_ID=@id", new { id = documentId });
        var contraOf = contra.ToDictionary(x => x.Sr, x => x.Contra);
        var settlements = await s.QueryAsync<SettlementView>("""
            SELECT CAST(o.SR_NO AS bigint) EntryId, CAST(o.Doc_ID AS bigint) DocumentId, RTRIM(ISNULL(o.Tseries,''))+'/'+CAST(ISNULL(o.Tno,0) AS varchar(12)) Reference,
              CAST(o.trdate AS date) Date, CAST(a.Amount AS decimal(19,2)) Amount
            FROM dbo.ADJUST a JOIN dbo.IQDayBook me ON me.SR_NO IN (a.Sr_No, a.Rel_Sr_No) AND me.Doc_ID=@id
            JOIN dbo.IQDayBook o ON o.SR_NO = CASE WHEN a.Sr_No=me.SR_NO THEN a.Rel_Sr_No ELSE a.Sr_No END
            ORDER BY o.trdate, o.SR_NO
            """, new { id = documentId });
        var product = D(h, "Product_Value");
        var total = D(h, "Bill_Amt");
        var mode = Convert.ToInt32(h["Mode"] ?? 0, CultureInfo.InvariantCulture);
        var supplierNo = DocumentResolver.Text(h, "Supp_Billno");
        return new DocumentView(summary, profile.Firm.Name, profile.Firm.Options.Letterhead, profile.Firm.Options.Gstin,
            DateOnly.FromDateTime(h["Doc_Duedate"] as DateTime? ?? (DateTime)h["Doc_Date"]!),
            state.Kind is DocumentKind.SalesOrder or DocumentKind.PurchaseOrder ? "Order" : profile.PaymentModes.ElementAtOrDefault(mode)?.Caption ?? mode.ToString(CultureInfo.InvariantCulture),
            basis, interstate, party,
            state.Kind == DocumentKind.Purchase ? supplierNo : null,
            state.Kind == DocumentKind.Purchase && h["Supp_Billdate"] is DateTime sd ? DateOnly.FromDateTime(sd) : null,
            DocumentResolver.Text(h, "Narration"), DocumentResolver.Text(h, "Message"), DocumentResolver.Text(h, "Doctorname"),
            lines, slabs, lines.Sum(l => l.Quantity), lines.Sum(l => l.Gross), lines.Sum(l => l.Discount), lines.Sum(l => l.Taxable),
            lines.Sum(l => l.Cgst + l.Sgst + l.Igst), product, total - product, total, AmountInWords.Rupees(total),
            state.Ledger.Select(e => new PreviewLedgerRow(e.AccountId, accountNames.GetValueOrDefault(e.AccountId, "#" + e.AccountId), e.Type, contraOf.GetValueOrDefault(e.Id), e.Amount)).ToList(),
            settlements, state.ContentHash);
    }
}

public sealed record DocumentFilter
{
    public IReadOnlyList<int>? TtIds { get; init; }
    public DateOnly? From { get; init; }
    public DateOnly? To { get; init; }
    public long? AccountId { get; init; }
    public string? Search { get; init; }
    public long? BeforeId { get; init; }
    /// <summary>Exactly this document (used when opening one bill).</summary>
    public long? DocumentId { get; init; }
    public int Take { get; init; } = 100;
}
