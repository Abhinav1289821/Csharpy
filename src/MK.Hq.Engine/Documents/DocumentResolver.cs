using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Documents;

/// <summary>Server-resolved HQ identities for one bill line. None of these come from the browser.</summary>
public sealed record ItemMapping(
    CatalogueItem Item, long PrimaryUnitId, int UnitEquivalence, decimal IssueConversion, int StockConversion, decimal Mrp,
    long BaseAccountId, long TaxAccountId, long SgstAccountId, int ItemInclusive, long CessAccountId, long AdditionalCessAccountId,
    decimal PurchaseRate, decimal DealerRate, decimal WholesaleRate, decimal SgRate, decimal OldTax, string ItemDescription, int ThirdConversion,
    decimal TextileRate, decimal TextileThreshold);

public sealed record PartyAccount(long AccountId, string Name, string Type, string Code, bool BillWise, int Usage, long ControlAccountId, int Tcs);

/// <summary>Everything a document write needs, read from HQ and validated. Writes nothing.</summary>
public sealed record ResolvedDocument(
    FirmProfile Profile,
    DocumentRequest Request,
    TransactionType Type,
    Settlement Settlement,
    int Mode,
    PartyAccount Head,
    long ActId,
    IReadOnlyDictionary<string, object?> Address,
    PreviewParty Snapshot,
    string AccountTown,
    IReadOnlyList<ItemMapping> Items,
    DocumentQuote Quote,
    IReadOnlyList<LedgerRow> Ledger,
    IReadOnlyList<ChargeRow> Charges,
    ChargeRow? RoundingCharge,
    Salesperson? Salesperson,
    DateOnly DueDate,
    string Message,
    IReadOnlyList<long> OrderLineIds,
    IReadOnlyList<string> Warnings)
{
    public DocumentKind Kind => Request.Kind;
    public string Notes => (Request.Notes ?? "").Trim();
    public string Suffix => (Request.NumberSuffix ?? "").Trim();
}

public sealed class DocumentResolver(HqOptions options)
{
    private const string ItemSql = """
        SELECT CAST(iu.Unit_PrimaryID AS bigint) PrimaryUnitId, CAST(ISNULL(iu.Unit_Equ,0) AS int) UnitEquivalence,
          CAST(ISNULL(iu.IssueConv,0) AS decimal(18,4)) IssueConversion, CAST(ISNULL(iu.StockConv,0) AS int) StockConversion,
          CAST(ISNULL(iu.Unit_MRP,0) AS decimal(19,4)) Mrp, CAST(ISNULL(ta.Act_ID,0) AS bigint) BaseAccountId,
          CAST(ISNULL(CASE WHEN @inward=1 THEN im.CGSTVIP_ID ELSE im.CGSTVOP_ID END,0) AS bigint) CgstAccountId,
          CAST(ISNULL(CASE WHEN @inward=1 THEN im.SGSTVIP_ID ELSE im.SGSTVOP_ID END,0) AS bigint) SgstAccountId,
          CAST(ISNULL(im.Inc_Tax,0) AS int) ItemInclusive, CAST(ISNULL(im.GSTCess_ActID,0) AS bigint) CessAccountId,
          CAST(ISNULL(im.GSTAddlCess_ActID,0) AS bigint) AdditionalCessAccountId,
          CAST(ISNULL(im.Allow_Serialno,0) AS int) Serial, CAST(ISNULL(im.GSTCess,0) AS decimal(9,4)) Cess, CAST(ISNULL(im.GSTAddLCessRate,0) AS decimal(9,4)) AddlCess,
          CAST(ISNULL(im.EnableSetNewGSTRate_On_ItemPriceAbove_TextileItems,0) AS int) Textile, CAST(ISNULL(iu.ThirdConv,0) AS int) ThirdConversion,
          CAST(ISNULL(iu.Unit_Prate,0) AS decimal(19,6)) PurchaseRate, CAST(ISNULL(iu.Unit_Srate,0) AS decimal(19,6)) DealerRate,
          CAST(ISNULL(iu.Unit_WSrate,0) AS decimal(19,6)) WholesaleRate, CAST(ISNULL(iu.SGRATE,0) AS decimal(19,6)) SgRate,
          CAST(ISNULL(im.VAT,0) AS decimal(19,6)) OldTax, ISNULL(im.Item_Desc,'') ItemDescription,
          CAST(ISNULL(im.NewGSTRate_TextileItems,0) AS decimal(19,4)) TextileRate, CAST(ISNULL(im.ItemPriceAbove_TextitleItems,0) AS decimal(19,4)) TextileThreshold
        FROM dbo.IQItems_Mast im JOIN dbo.IQItems_Units iu ON im.Item_ID=iu.Item_ID
        LEFT JOIN dbo.Ttype_Acnt ta ON ta.Ic_Id=im.Item_ID AND ta.TT_ID=@tt
        WHERE im.Item_ID=@item AND iu.UNit_Id=@unit
        """;

    private sealed record ItemRow(long PrimaryUnitId, int UnitEquivalence, decimal IssueConversion, int StockConversion, decimal Mrp, long BaseAccountId,
        long CgstAccountId, long SgstAccountId, int ItemInclusive, long CessAccountId, long AdditionalCessAccountId, int Serial, decimal Cess, decimal AddlCess,
        int Textile, int ThirdConversion, decimal PurchaseRate, decimal DealerRate, decimal WholesaleRate, decimal SgRate, decimal OldTax, string ItemDescription,
        decimal TextileRate, decimal TextileThreshold);

    /// <summary>Set by an amendment so the bill being corrected does not count as its own duplicate.</summary>
    private long ExcludeDocumentId { get; init; }

    public DocumentResolver Excluding(long documentId) => new(options) { ExcludeDocumentId = documentId };

    public async Task<ResolvedDocument> ResolveAsync(HqSession s, FirmProfile profile, DocumentRequest r)
    {
        var firm = profile.Firm;
        var warnings = new List<string>();
        profile.RequireWritableProfile();
        firm.RequireInPeriod(r.Date);
        Guard.Require(r.Lines.Count is > 0 and <= 300, "INVALID_LINES", "A bill needs between 1 and 300 lines");
        Guard.Require((r.Notes ?? "").Length <= 1500, "NARRATION_TOO_LONG", "Narration can be at most 1500 characters");
        Guard.Require((r.NumberSuffix ?? "").Trim().Length <= 3, "INVALID_SUFFIX", "Number suffix can be at most 3 characters");
        Guard.Supported(!r.Interstate || options.AllowInterstate, "HQ_INVOICE_PROFILE_UNSUPPORTED", "Interstate bills are switched off in settings");
        Guard.Supported(!(r.Interstate && r.Kind.IsOrder()), "HQ_INVOICE_PROFILE_UNSUPPORTED", "HQ has no interstate order type configured");

        // ---- transaction type ----
        var type = profile.Type(r.Kind.TtId(r.Interstate));
        Guard.Supported(type.VouchType == r.Kind.VouchType(), "HQ_CONFIGURATION_MISMATCH", $"HQ type {type.TtId} is '{type.VouchType}', not '{r.Kind.VouchType()}'");
        Guard.Supported(type.TaxMode == (r.Interstate ? TaxMode.Interstate : TaxMode.Local), "HQ_CONFIGURATION_MISMATCH", "HQ type's GST mode differs from the bill");
        Guard.Supported(type.BranchDefault == 0, "HQ_CONFIGURATION_UNSUPPORTED", "Branch-wise posting is not supported");
        Guard.Supported(type.RestartDaily == 0, "HQ_CONFIGURATION_UNSUPPORTED", "Daily-restarting bill numbers are not supported");
        Guard.Supported(type.Series.Length is > 0 and <= 8, "HQ_CONFIGURATION_UNSUPPORTED", "HQ series must be 1 to 8 characters");
        Guard.Supported(type.SeriesType == 3, "HQ_CONFIGURATION_UNSUPPORTED", "Only series-wide numbering (SeriesType 3) is verified");

        // ---- settlement ----
        var mode = r.Kind.IsOrder() ? 0 : r.PaymentMode;
        var allowed = r.Kind.IsInward() ? options.PurchasePaymentModes : options.SalePaymentModes;
        Guard.Supported(r.Kind.IsOrder() || allowed.Contains(mode), "HQ_INVOICE_PROFILE_UNSUPPORTED", "This payment mode is not enabled for this bill type");
        var settlement = mode switch { 0 => Settlement.Cash, 1 => Settlement.Credit, _ => Settlement.Bank };
        if (settlement == Settlement.Bank)
        {
            Guard.Supported(mode is 2 or 4 or 5 or 6 or 7, "HQ_INVOICE_PROFILE_UNSUPPORTED", "HQ's 'Others' split payment needs HQ's own dialog");
            Guard.Supported(profile.Config.DefaultUpiBank == 0, "HQ_CONFIGURATION_UNSUPPORTED", "A default UPI bank is configured in HQ; that profile is not verified");
            var position = profile.PaymentModes.ElementAtOrDefault(mode);
            Guard.Supported(position is not null && position.Id == mode && position.Status == 0, "HQ_CONFIGURATION_CHANGED", "HQ payment modes changed or this mode is inactive");
        }

        // ---- party ----
        long headId = settlement == Settlement.Cash ? r.AccountId ?? InvoiceLedger.HqCashAccountId : r.AccountId ?? 0;
        Guard.Require(headId > 0, "PARTY_REQUIRED", settlement == Settlement.Bank ? "Choose the bank account the money went to" : "Choose the customer/supplier account");
        var head = await s.QueryFirstOrDefaultAsync<PartyAccount>("""
            SELECT CAST(Act_Id AS bigint) AccountId, LTRIM(RTRIM(Act_Name)) Name, LTRIM(RTRIM(ISNULL(Act_Type,''))) Type, LTRIM(RTRIM(ISNULL(Act_Code,''))) Code,
              CAST(ISNULL(Bill2Bill,0) AS bit) BillWise, CAST(ISNULL(Act_Usage,0) AS int) Usage, CAST(ISNULL(Act_ControlID,0) AS bigint) ControlAccountId,
              CAST(ISNULL(IsTCSAPPLY,0) AS int) Tcs
            FROM dbo.IQAccmst WHERE Act_Id=@id AND Act_Cat='A'
            """, new { id = headId }) ?? throw HqException.NotFound("HQ_INVALID_PARTY", "That account does not exist in HQ");
        Guard.Require(head.Usage == 0, "HQ_INVALID_PARTY", $"{head.Name} is marked inactive in HQ");
        Guard.Supported(head.ControlAccountId == 0, "HQ_CONFIGURATION_UNSUPPORTED", "Control-account parties are not supported");
        Guard.Supported(head.Tcs == 0, "HQ_CONFIGURATION_UNSUPPORTED", "TCS parties are not supported");
        if (settlement == Settlement.Credit)
        {
            Guard.Require(head.AccountId != InvoiceLedger.HqCashAccountId, "HQ_INVALID_PARTY", "A credit bill needs a customer or supplier account, not Cash");
            var okTypes = r.Kind.IsInward() ? new[] { "Supplier", "Customer-Supplier" } : new[] { "Customer", "Customer-Supplier" };
            Guard.Require(okTypes.Contains(head.Type), "HQ_INVALID_PARTY", $"{head.Name} is a {head.Type} account; credit {(r.Kind.IsInward() ? "purchases need a supplier" : "sales need a customer")}");
            Guard.Supported(profile.Config.CreditLimitCheck == 0, "HQ_CONFIGURATION_UNSUPPORTED", "HQ credit-limit checking is switched on");
            Guard.Supported(!(r.Kind == DocumentKind.Purchase && head.Type == "Customer-Supplier" && head.BillWise && profile.Config.BillWise == 1),
                "HQ_CONFIGURATION_UNSUPPORTED", "Bill-wise customer-supplier purchases need HQ's allocation review");
        }
        if (settlement == Settlement.Bank)
            Guard.Require(head.Type == "Bank", "HQ_BANK_ACCOUNT_REQUIRED", "Card/UPI bills need a bank ledger");

        var addresses = await s.RowsAsync("SELECT * FROM dbo.IQAccAdr WHERE Act_Id=@id", new { id = headId });
        Guard.Supported(addresses.Count <= 1, "HQ_INVALID_PARTY", "The account has more than one address record");
        Guard.Supported(settlement != Settlement.Bank || addresses.Count == 1, "HQ_BANK_ACCOUNT_REQUIRED", "The bank ledger has no address record in HQ");
        var address = Normalise(addresses.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(Text(address, "Act_state"))) address["Act_state"] = profile.StateCaption;

        var snapshot = Snapshot(profile, r, settlement, head, address, warnings);

        // ---- items ----
        var items = new List<ItemMapping>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var line = r.Lines[i];
            var at = $"Line {i + 1}: ";
            var item = (await Catalogue.ItemsAsync(s, line.ItemId)).FirstOrDefault(x => x.UnitId == line.UnitId)
                ?? throw HqException.NotFound("HQ_ITEM_NOT_FOUND", at + "item/unit not found in HQ");
            Guard.Require(item.Active, "HQ_ITEM_INACTIVE", at + $"{item.Name} is inactive in HQ");
            Guard.Require(item.TaxConfigured, "HQ_ITEM_TAX_MISSING", at + $"{item.Name} has no GST rates in HQ");
            var row = (await s.QueryAsync<ItemRow>(ItemSql, new { inward = r.Kind.IsInward() ? 1 : 0, tt = type.TtId, item = line.ItemId, unit = line.UnitId })).Single();
            Guard.Supported(row.Serial == 0 && row.Cess == 0 && row.AddlCess == 0 && row.Textile == 0, "HQ_ITEM_PROFILE_UNSUPPORTED", at + "serial-number, cess or textile-rate items are not supported");
            Guard.Supported(row.ThirdConversion is 0 or 1, "HQ_ITEM_PROFILE_UNSUPPORTED", at + "third-pack conversions are not supported");
            Guard.Supported(row.UnitEquivalence > 0 && row.IssueConversion > 0, "HQ_UNIT_UNSUPPORTED", at + "the unit conversion in HQ is not set");
            long taxAccount = row.CgstAccountId, sgstAccount = row.SgstAccountId, baseAccount = row.BaseAccountId;
            if (r.Interstate)
                (baseAccount, taxAccount, sgstAccount) = await InterstateLedgersAsync(s, r.Kind, item.Igst, at);
            if (!r.Kind.IsOrder())
            {
                Guard.Supported(baseAccount > 0, "HQ_MAPPING_MISSING", at + $"{item.Name} has no {type.Name} ledger in HQ (Ttype_Acnt)");
                var taxes = r.Interstate ? item.Igst : item.Cgst;
                Guard.Supported(taxes == 0 || taxAccount > 0, "HQ_MAPPING_MISSING", at + $"{item.Name} has no GST ledger in HQ");
                Guard.Supported(r.Interstate || item.Sgst == 0 || sgstAccount > 0, "HQ_MAPPING_MISSING", at + $"{item.Name} has no SGST ledger in HQ");
            }
            items.Add(new ItemMapping(item, row.PrimaryUnitId, row.UnitEquivalence, row.IssueConversion, row.StockConversion, row.Mrp, baseAccount, taxAccount, sgstAccount,
                row.ItemInclusive, row.CessAccountId, row.AdditionalCessAccountId, row.PurchaseRate, row.DealerRate, row.WholesaleRate, row.SgRate, row.OldTax,
                row.ItemDescription, row.ThirdConversion, row.TextileRate, row.TextileThreshold));
            if (!r.Kind.IsInward() && r.Kind == DocumentKind.Sale && item.Stock - line.Quantity < 0)
                warnings.Add($"Line {i + 1}: {item.Name} stock will go below zero ({Money.Plain(item.Stock)} in hand)");
        }

        // ---- pricing ----
        var policy = type.Policy(r.Basis);
        var orderScale = r.Kind.IsOrder() ? 2 : 4;
        var quote = GstCalculator.Price(
            r.Lines.Select((l, i) => new LineInput(l.Quantity, l.Rate, l.DiscountPercent, items[i].Item.Taxes)).ToList(),
            policy, type.TaxMode, orderScale, orderScale);
        Guard.Require(Money.Scale(quote.Quantity) <= 2, "HQ_PRECISION_UNSUPPORTED", "HQ stores the bill's total quantity with 2 decimal places");
        Guard.Require(quote.Payable > 0 || r.Kind.IsOrder(), "INVALID_TOTAL", "Bill total must be more than zero");

        // ---- charges ----
        var charges = profile.Charges(type.ChargeSet);
        Guard.Supported(charges.All(c => c.Rate == 0), "HQ_CONFIGURATION_UNSUPPORTED", "Automatic percentage charges are configured on this bill type");
        ChargeRow? rounding = type.RoundedOffTo > 0 ? charges.LastOrDefault() : null;
        Guard.Supported(quote.RoundOff == 0 || rounding is { AccountId: > 0 }, "HQ_CONFIGURATION_UNSUPPORTED", "HQ cannot round this bill without its round-off charge row");

        // ---- ledger (invoices only) ----
        IReadOnlyList<LedgerRow> ledger = [];
        if (!r.Kind.IsOrder())
        {
            var amounts = new List<(long, decimal)>();
            for (var i = 0; i < items.Count; i++)
            {
                var q = quote.Lines[i];
                amounts.Add((items[i].BaseAccountId, q.Taxable));
                if (r.Interstate) amounts.Add((items[i].TaxAccountId, q.Igst));
                else { amounts.Add((items[i].TaxAccountId, q.Cgst)); amounts.Add((items[i].SgstAccountId, q.Sgst)); }
            }
            var chargePostings = rounding is null || quote.RoundOff == 0 ? [] : new[] { new ChargePosting(rounding.AccountId, quote.RoundOff, rounding.IsAdd) };
            ledger = InvoiceLedger.Plan(r.Kind.IsInward(), settlement, headId, amounts, chargePostings, quote.Payable);
            Guard.Require(ledger.All(l => l.AccountId != InvoiceLedger.HqCashAccountId) || settlement != Settlement.Cash,
                "HQ_ACCOUNT_ROLE_CONFLICT", "Cash cannot also be an item or tax ledger");
            if (rounding is not null && quote.RoundOff != 0 && !rounding.IsAdd)
                warnings.Add($"Round-off {Money.Text(quote.RoundOff)} is posted the way HQ posts it ('{rounding.Name}' is set to 'Less' in HQ; see HQ health).");
        }

        // ---- salesperson, due date, message ----
        Salesperson? salesperson = null;
        if (r.SalespersonId is { } smId)
            salesperson = profile.Salespeople.FirstOrDefault(p => p.Id == smId) ?? throw HqException.NotFound("HQ_SALESPERSON_UNAVAILABLE", "That salesperson is not active in HQ");
        var due = r.DueDate ?? r.Date.AddDays(type.DefaultDays);
        Guard.Require(due >= r.Date, "INVALID_DUE_DATE", "Due date cannot be before the bill date");
        var message = r.Message ?? type.Message;

        // ---- purchase specifics ----
        if (r.Kind == DocumentKind.Purchase)
        {
            Guard.Require(r.Supplier is not null && !string.IsNullOrWhiteSpace(r.Supplier.Number), "HQ_SUPPLIER_REFERENCE_INVALID", "Enter the supplier's bill number");
            Guard.Require(r.Supplier!.Number.Trim().Length <= 20, "HQ_SUPPLIER_REFERENCE_INVALID", "HQ stores supplier bill numbers up to 20 characters");
            // A supplier reference belongs to the supplier regardless of cash/credit. READCOMMITTEDLOCK: never hold a
            // range lock on IQDoc_Header inside our serializable transaction (it would delay an HQ PC's insert).
            var duplicate = await s.QueryFirstOrDefaultAsync<(long Doc, string Series, int No)?>("""
                SELECT TOP 1 CAST(h.Doc_Id AS bigint), RTRIM(h.TSeries), h.TNo
                FROM dbo.IQDoc_Header h WITH (READCOMMITTEDLOCK)
                LEFT JOIN dbo.IQAccmst a ON a.Act_Id=h.HeadAct_ID
                LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id=h.HeadAct_ID
                LEFT JOIN dbo.IQCash_TRAN c WITH (READCOMMITTEDLOCK) ON c.Doc_Id=h.Doc_Id
                WHERE h.Tt_Id IN (500,501) AND LTRIM(RTRIM(h.Supp_Billno))=@invoice AND ISNULL(h.Doc_Cancel,0)=0 AND h.Doc_Id<>@self
                  AND ((@party<>6 AND h.Mode<>0 AND h.HeadAct_ID=@party)
                    OR (@gstin<>'' AND UPPER(LTRIM(RTRIM(CASE WHEN h.Mode=0 THEN ISNULL(c.GSTIN,'') ELSE ISNULL(ad.GSTIN,'') END)))=@gstin)
                    OR (@name<>'' AND (@gstin='' OR LTRIM(RTRIM(CASE WHEN h.Mode=0 THEN ISNULL(c.GSTIN,'') ELSE ISNULL(ad.GSTIN,'') END))='')
                      AND LTRIM(RTRIM(CASE WHEN h.Mode=0 THEN ISNULL(c.Act_Name,'') ELSE ISNULL(a.Act_Name,'') END))=@name))
                ORDER BY h.Doc_Id DESC
                """, new { invoice = r.Supplier.Number.Trim(), party = headId, gstin = snapshot.Gstin, name = snapshot.Name, self = ExcludeDocumentId });
            Guard.Require(duplicate is null, "SUPPLIER_INVOICE_ALREADY_EXISTS",
                $"This supplier bill is already in HQ as {duplicate?.Series}/{duplicate?.No}. Open that bill instead of saving a copy.", FailureKind.Conflict,
                new { documentId = duplicate?.Doc });
        }

        var orderLines = await ResolveOrderLinksAsync(s, r, items, quote);

        var accountTown = Text(address, "Act_Town");
        return new ResolvedDocument(profile, r, type, settlement, mode, head,
            ActId: settlement == Settlement.Cash ? InvoiceLedger.HqCashAccountId : headId,
            address, snapshot, accountTown, items, quote, ledger, charges, rounding, salesperson, due, message, orderLines, warnings);
    }

    private PreviewParty Snapshot(FirmProfile profile, DocumentRequest r, Settlement settlement, PartyAccount head, Dictionary<string, object?> address, List<string> warnings)
    {
        var p = r.Party ?? new PartySnapshot();
        string Master(string key) => Text(address, key);
        if (settlement == Settlement.Credit)
        {
            // HQ re-opens credit bills from the account master; a different snapshot would silently vanish in HQ.
            void Same(string? supplied, string master, string what) =>
                Guard.Require(supplied is null || string.Equals(supplied.Trim(), master.Trim(), StringComparison.OrdinalIgnoreCase),
                    "HQ_CREDIT_PARTY_CONFLICT", $"For credit bills HQ uses the account's {what}; change it in the account instead");
            Same(p.Name, head.Name, "name");
            Same(p.Gstin, Master("GSTIN"), "GSTIN");
        }
        var gstType = p.GstType ?? (int)(Number(address, "GSTTaxType") ?? 2);
        var gstin = (p.Gstin ?? Master("GSTIN")).Trim().ToUpperInvariant();
        var state = p.State is { Length: > 0 } st ? NormaliseState(st) : Master("Act_state");
        if (r.Interstate && gstType <= 1)
        {
            var code = Gstin.StateCode(gstin);
            Guard.Require(code is not null && code != "37", "HQ_TAX_PROFILE_UNSUPPORTED", "An interstate bill needs the party's other-state GSTIN");
            state = Gstin.HqStateCaption(code!) ?? state;
            Guard.Supported(settlement == Settlement.Credit, "HQ_INVOICE_PROFILE_UNSUPPORTED", "Interstate bills are verified for credit parties only");
        }
        else if (!r.Interstate)
        {
            Guard.Require(string.IsNullOrEmpty(state) || state.StartsWith("37", StringComparison.Ordinal), "HQ_TAX_PROFILE_UNSUPPORTED",
                "This party is in another state; make it an interstate (IGST) bill");
            Guard.Require(Gstin.StateCode(gstin) is null or "37", "HQ_TAX_PROFILE_UNSUPPORTED", "This GSTIN belongs to another state; make it an interstate (IGST) bill");
        }
        if (gstType <= 1 && !Gstin.IsValid(gstin))
        {
            // A typed GSTIN must be right. A wrong GSTIN already saved on an HQ account is reported, not blocked:
            // HQ accepts it, and the fix belongs in the account master.
            Guard.Require(p.Gstin is null, "HQ_PARTY_GST_INVALID", "This GSTIN is not valid (check digit failed)");
            warnings.Add($"{head.Name}'s GSTIN in HQ ({gstin}) fails the GST check digit. Correct the account so GST returns match.");
        }

        var name = settlement == Settlement.Bank
            ? (p.Name ?? head.Name)
            : settlement == Settlement.Credit ? head.Name : (p.Name ?? (head.AccountId == InvoiceLedger.HqCashAccountId ? "" : head.Name));
        if (settlement == Settlement.Cash) Guard.Require(!string.IsNullOrWhiteSpace(name) || r.Kind.IsOrder(), "PARTY_NAME_REQUIRED", "Enter the customer's name");
        return new PreviewParty(
            AccountId: settlement == Settlement.Cash ? InvoiceLedger.HqCashAccountId : head.AccountId,
            HeadAccountId: head.AccountId,
            // Text is stored exactly as typed, as HQ does.
            Name: name ?? "",
            Town: p.Town ?? Master("Act_Town"),
            Phone: p.Phone ?? Master("Act_Ph1"),
            Gstin: gstin,
            GstType: gstType,
            State: state,
            Address: p.Address ?? Master("Add_Line1"));
    }

    private static string NormaliseState(string state)
    {
        state = state.Trim();
        if (state.Length >= 2 && char.IsDigit(state[0]) && char.IsDigit(state[1])) return Gstin.HqStateCaption(state[..2]) ?? state;
        var match = Gstin.States.FirstOrDefault(kv => string.Equals(kv.Value, state, StringComparison.OrdinalIgnoreCase));
        return match.Key is null ? state : $"{match.Key}-{match.Value}";
    }

    private static async Task<(long Base, long Tax, long Sgst)> InterstateLedgersAsync(HqSession s, DocumentKind kind, decimal rate, string at)
    {
        Guard.Supported(GstCalculator.Slabs.Contains(rate), "HQ_CONFIGURATION_UNSUPPORTED", at + "unsupported IGST slab");
        var inward = kind.IsInward();
        var suffix = rate == 0.1m ? "0.10" : Money.Plain(rate);
        var baseName = rate == 0 ? (inward ? "Purchase – Exempted" : "Sales – Exempt") : $"IGST {(inward ? "Purchase" : "Sales")} {suffix}%";
        var taxName = $"IGST {(inward ? "Input" : "Output")} {suffix}%";
        var bases = await s.QueryAsync<long>("SELECT CAST(Act_ID AS bigint) FROM dbo.IQAccmst WHERE Act_Cat='A' AND Act_Name=@n", new { n = baseName });
        var taxes = rate == 0 ? [0L] : await s.QueryAsync<long>("SELECT CAST(Act_ID AS bigint) FROM dbo.IQAccmst WHERE Act_Cat='A' AND Act_Name=@n", new { n = taxName });
        Guard.Supported(bases.Count == 1 && taxes.Count == 1, "HQ_MAPPING_MISSING", at + $"HQ has no single '{baseName}' / '{taxName}' ledger");
        return (bases[0], taxes[0], 0);
    }

    private static async Task<IReadOnlyList<long>> ResolveOrderLinksAsync(HqSession s, DocumentRequest r, IReadOnlyList<ItemMapping> items, DocumentQuote quote)
    {
        if (r.SourceOrderId is null)
        {
            Guard.Require(r.Lines.All(l => l.OrderLineId is null), "INVALID_ORDER_LINK", "Order lines need the source order");
            return [];
        }
        Guard.Require(r.Kind == DocumentKind.Sale, "INVALID_ORDER_LINK", "Only sales can be made from a sales order");
        var order = await s.QueryFirstOrDefaultAsync<(int Tt, int Cancel, DateTime Date)?>(
            "SELECT CAST(Tt_Id AS int), CAST(ISNULL(Doc_Cancel,0) AS int), Doc_Date FROM dbo.IQDoc_Header WITH (UPDLOCK, HOLDLOCK) WHERE Doc_Id=@id", new { id = r.SourceOrderId });
        Guard.Require(order is { Tt: 249, Cancel: 0 }, "HQ_ORDER_UNAVAILABLE", "The sales order is no longer available in HQ", FailureKind.Conflict);
        Guard.Require(DateOnly.FromDateTime(order!.Value.Date) <= r.Date, "HQ_ORDER_UNAVAILABLE", "The sale is dated before its order");
        var lines = await s.QueryAsync<(long Id, long Item, long Unit, decimal Qty, decimal Supplied)>("""
            SELECT CAST(o.OrdDltdID AS bigint), CAST(o.Item_ID AS bigint), CAST(o.UnitID AS bigint), CAST(o.Qty AS decimal(19,4)),
              CAST(ISNULL((SELECT SUM(d.Qty) FROM dbo.IQDoc_Delv d WITH (UPDLOCK, HOLDLOCK) WHERE d.OrdDelv_Id=o.OrdDltdID),0) AS decimal(19,4))
            FROM dbo.IQOrd_Detail o WITH (UPDLOCK, HOLDLOCK) WHERE o.Doc_ID=@id
            """, new { id = r.SourceOrderId });
        var result = new List<long>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var link = r.Lines[i].OrderLineId;
            if (link is null) { result.Add(0); continue; }
            var o = lines.FirstOrDefault(l => l.Id == link);
            Guard.Require(o.Id != 0 && o.Item == items[i].Item.ItemId && o.Unit == items[i].Item.UnitId, "INVALID_ORDER_LINK", $"Line {i + 1} does not match its order line");
            Guard.Require(quote.Lines[i].Quantity <= o.Qty - o.Supplied, "HQ_ORDER_QUANTITY_EXCEEDED", $"Line {i + 1} is more than the order's remaining {Money.Plain(o.Qty - o.Supplied)}", FailureKind.Conflict);
            result.Add(o.Id);
        }
        return result;
    }

    private static Dictionary<string, object?> Normalise(IReadOnlyDictionary<string, object?>? row) =>
        row is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : row.ToDictionary(kv => kv.Key, kv => kv.Value is string sv ? sv.Trim() : kv.Value, StringComparer.OrdinalIgnoreCase);

    public static string Text(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)!.Trim() : "";

    public static decimal? Number(IReadOnlyDictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var v) && v is not null ? Convert.ToDecimal(v, System.Globalization.CultureInfo.InvariantCulture) : null;
}
