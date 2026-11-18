using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Reports;

public sealed record LedgerLine
{
    public DateOnly Date { get; init; }
    public long DocumentId { get; init; }
    public string Reference { get; init; } = "";
    public string TypeName { get; init; } = "";
    public string Narration { get; init; } = "";
    public string Contra { get; init; } = "";
    public decimal Debit { get; init; }
    public decimal Credit { get; init; }
    public decimal Balance { get; set; }
}

public sealed record LedgerReport(long AccountId, string AccountName, string AccountType, DateOnly From, DateOnly To, decimal Opening, decimal Debit, decimal Credit, decimal Closing, IReadOnlyList<LedgerLine> Lines);

public sealed record OutstandingParty
{
    public long AccountId { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public string Town { get; init; } = "";
    public string Phone { get; init; } = "";
    public bool BillWise { get; init; }
    public decimal Balance { get; init; }
    public decimal Current { get; init; }
    public decimal Days30 { get; init; }
    public decimal Days60 { get; init; }
    public decimal Days90 { get; init; }
    public decimal Older { get; init; }
    public DateOnly? OldestBill { get; init; }
}

public sealed record DayBookRow
{
    public long DocumentId { get; init; }
    public DateOnly Date { get; init; }
    public string TypeName { get; init; } = "";
    public string Reference { get; init; } = "";
    public string Party { get; init; } = "";
    public int Mode { get; init; }
    public decimal Amount { get; init; }
    public string Narration { get; init; } = "";
}

public sealed record RegisterRow
{
    public long DocumentId { get; init; }
    public DateOnly Date { get; init; }
    public string Reference { get; init; } = "";
    public string SupplierBill { get; init; } = "";
    public string Party { get; init; } = "";
    public string Gstin { get; init; } = "";
    public int Mode { get; init; }
    public decimal Taxable { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }
    public decimal Igst { get; init; }
    public decimal RoundOff { get; init; }
    public decimal Total { get; init; }
}

public sealed record GstSlabRow
{
    public string Section { get; init; } = "";
    public decimal Rate { get; init; }
    public int Bills { get; init; }
    public decimal Taxable { get; init; }
    public decimal Cgst { get; init; }
    public decimal Sgst { get; init; }
    public decimal Igst { get; init; }
}

public sealed record HsnRow
{
    public string Hsn { get; init; } = "";
    public decimal Rate { get; init; }
    public decimal Quantity { get; init; }
    public decimal Taxable { get; init; }
    public decimal Tax { get; init; }
}

public sealed record RegisterReport(string Kind, DateOnly From, DateOnly To, IReadOnlyList<RegisterRow> Rows, IReadOnlyList<GstSlabRow> Slabs, IReadOnlyList<HsnRow> Hsn,
    decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst, decimal Total);

public sealed record StockRow
{
    public long ItemId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public string Unit { get; init; } = "";
    public decimal Stock { get; init; }
    public decimal PurchaseRate { get; init; }
    public decimal SaleRate { get; init; }
    public decimal Value { get; init; }
    public DateOnly? LastSale { get; init; }
}

public sealed record DashboardSummary(DateOnly Day, decimal SalesToday, int BillsToday, decimal CashSalesToday, decimal ReceiptsToday, decimal PaymentsToday,
    decimal PurchasesToday, decimal CashInHand, decimal Receivable, decimal Payable, decimal SalesMonth, decimal SalesYear, IReadOnlyList<DayBookRow> Recent);

/// <summary>
/// Read-only reports straight from HQ's tables. Cash is HQ's implicit contra: a P/R row against Cash moves Cash
/// the other way, so the cash book adds those rows to Cash's explicit ones.
/// </summary>
public sealed class ReportService(FirmRegistry firms, IHqClock clock)
{
    /// <summary>The accounts the outstanding and dashboard figures cover.</summary>
    public static string PartyAccounts(string alias) =>
        $"{alias}.Act_Cat='A' AND {alias}.Act_Type IN ('Customer','Supplier','Customer-Supplier')";

    /// <summary>An IQDayBook row's amount on its own account, debit positive: D and P debit it, C and R credit it.</summary>
    public static string Signed(string row) =>
        $"CASE WHEN {row}.TYPE IN ('D','P') THEN {row}.AMOUNT WHEN {row}.TYPE IN ('C','R') THEN -{row}.AMOUNT ELSE 0 END";

    /// <summary>
    /// An account's balance, debit positive, exactly as its ledger closes: <see cref="Opening"/>, its own rows, and for Cash
    /// the P/R rows that move it implicitly. The one definition behind the ledger, the outstanding list, the dashboard and the
    /// party balances in the catalogue (before, the catalogue left Cash's implicit rows out and the dashboard counted entries
    /// dated after its day). <paramref name="before"/>: a SQL expression; only rows dated before it count.
    /// </summary>
    public static string Balance(string alias, string? before = null)
    {
        string Cut(string row) => before is null ? "" : $" AND {row}.TRDATE < {before}";
        const long cash = InvoiceLedger.HqCashAccountId;
        return $"({Opening(alias)}"
            + $" + ISNULL((SELECT SUM({Signed("bd")}) FROM dbo.IQDayBook bd WHERE bd.Act_ID={alias}.Act_Id{Cut("bd")}),0)"
            + $" + CASE WHEN {alias}.Act_Id={cash} THEN ISNULL((SELECT SUM(CASE WHEN bc.TYPE='R' THEN bc.AMOUNT ELSE -bc.AMOUNT END) FROM dbo.IQDayBook bc"
            + $" WHERE bc.Contra_ID={cash} AND bc.Act_ID<>{cash} AND bc.TYPE IN ('P','R'){Cut("bc")}),0) ELSE 0 END)";
    }

    /// <summary>A bill line's GST as the register's columns count it: CGST + SGST, or the whole TaxValue for IGST lines
    /// (GST_Type 0). HQ rounds CGST and SGST separately, so TaxValue can differ from their sum by a paisa (23 MBM lines);
    /// the HSN summary used TaxValue and so disagreed with the register and slab totals on the same screen.</summary>
    public static string LineTax(string line) =>
        $"CASE WHEN ISNULL({line}.GST_Type,1)=0 THEN {line}.TaxValue ELSE {line}.CGST_TaxAmt+{line}.SGST_TaxAmt END";

    /// <summary>A report's days as a half-open range of datetimes, so an HQ row stamped later on the last day still counts
    /// (comparing <c>&lt;= @to</c> at midnight drops it).</summary>
    public static (DateTime From, DateTime Until) Range(DateOnly from, DateOnly to)
    {
        Guard.Require(from <= to, "INVALID_DATE_RANGE", $"The start {from:dd-MM-yyyy} is after the end {to:dd-MM-yyyy}");
        return (from.ToDateTime(TimeOnly.MinValue), to.AddDays(1).ToDateTime(TimeOnly.MinValue));
    }

    private const string Movement = """
        SELECT d.trdate, d.Doc_ID, d.SR_NO, d.Act_ID, d.Contra_ID, d.TYPE, d.AMOUNT, d.Narration, d.Tseries, d.Tno,
          CASE WHEN d.Act_ID=@account THEN (CASE WHEN d.TYPE IN ('D','P') THEN d.AMOUNT ELSE 0 END)
               ELSE (CASE WHEN d.TYPE='R' THEN d.AMOUNT ELSE 0 END) END Debit,
          CASE WHEN d.Act_ID=@account THEN (CASE WHEN d.TYPE IN ('C','R') THEN d.AMOUNT ELSE 0 END)
               ELSE (CASE WHEN d.TYPE='P' THEN d.AMOUNT ELSE 0 END) END Credit
        FROM dbo.IQDayBook d
        WHERE d.Act_ID=@account OR (@account=6 AND d.Contra_ID=6 AND d.Act_ID<>6 AND d.TYPE IN ('P','R'))
        """;

    /// <summary>An account's opening balance as HQ's ledger has it: HQ posts the master's opening as an IQDayBook row
    /// (Doc_ID -1, VTYPE opbal, dated the day before the year); the master figure counts only where that row is missing.</summary>
    public static string Opening(string alias) =>
        $"CASE WHEN EXISTS (SELECT 1 FROM dbo.IQDayBook o WHERE o.Act_ID={alias}.Act_Id AND o.Doc_ID=-1) THEN 0 ELSE CASE WHEN {alias}.Act_OpSign='C' THEN -1 ELSE 1 END*ISNULL({alias}.Act_OPbal,0) END";

    public async Task<LedgerReport> LedgerAsync(string firmId, long accountId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var f = from ?? firm.PeriodStart;
        var t = to ?? firm.PeriodEnd;
        var (fromAt, until) = Range(f, t);
        await using var s = await firm.OpenAsync(ct);
        var account = await s.QueryFirstOrDefaultAsync<(string Name, string Type, decimal Opening)?>(
            $"SELECT LTRIM(RTRIM(a.Act_Name)), LTRIM(RTRIM(ISNULL(a.Act_Type,''))), CAST({Opening("a")} AS decimal(19,2)) FROM dbo.IQAccmst a WHERE a.Act_Id=@accountId",
            new { accountId }) ?? throw HqException.NotFound("HQ_ACCOUNT_UNAVAILABLE", "Account not found");
        var before = await s.ScalarAsync<decimal>($"SELECT CAST(ISNULL(SUM(Debit-Credit),0) AS decimal(19,2)) FROM ({Movement}) m WHERE m.trdate < @from",
            new { account = accountId, from = fromAt });
        var lines = await s.QueryAsync<LedgerLine>($"""
            SELECT CAST(m.trdate AS date) Date, CAST(m.Doc_ID AS bigint) DocumentId, RTRIM(ISNULL(m.Tseries,''))+'/'+CAST(ISNULL(m.Tno,0) AS varchar(12)) Reference,
              RTRIM(ISNULL(t.Ttype,'')) TypeName, LTRIM(RTRIM(ISNULL(m.Narration,''))) Narration,
              LTRIM(RTRIM(ISNULL(c.Act_Name,''))) Contra, CAST(m.Debit AS decimal(19,2)) Debit, CAST(m.Credit AS decimal(19,2)) Credit
            FROM ({Movement}) m LEFT JOIN dbo.IQDoc_Header h ON h.Doc_Id=m.Doc_ID LEFT JOIN dbo.TTypes t ON t.TT_ID=h.Tt_Id
            LEFT JOIN dbo.IQAccmst c ON c.Act_Id = CASE WHEN m.Act_ID=@account THEN NULLIF(m.Contra_ID,0) ELSE m.Act_ID END
            WHERE m.trdate >= @from AND m.trdate < @until ORDER BY m.trdate, m.Doc_ID, m.SR_NO
            """, new { account = accountId, from = fromAt, until });
        var opening = account.Opening + before;
        var running = opening;
        foreach (var l in lines) l.Balance = running += l.Debit - l.Credit;
        return new LedgerReport(accountId, account.Name, account.Type, f, t, opening, lines.Sum(l => l.Debit), lines.Sum(l => l.Credit), running, lines);
    }

    public async Task<IReadOnlyList<OutstandingParty>> OutstandingAsync(string firmId, bool receivables, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var side = receivables ? 1 : -1;
        // What each party owes on this side, from the same balance as its ledger; the opening HQ never posted as a row is dated
        // the day before the year, where HQ dates the rows it does post.
        var parties = await s.QueryAsync<DueRow>($"""
            SELECT p.* FROM (
              SELECT CAST(a.Act_Id AS bigint) AccountId, LTRIM(RTRIM(a.Act_Name)) Name, LTRIM(RTRIM(a.Act_Type)) Type, LTRIM(RTRIM(ISNULL(ad.Act_Town,''))) Town,
                LTRIM(RTRIM(COALESCE(NULLIF(ad.Act_Cell,''), ad.Act_Ph1, ''))) Phone, CAST(ISNULL(a.Bill2Bill,0) AS bit) BillWise,
                CAST(@side * {Balance("a")} AS decimal(19,2)) Balance, CAST(@side * ({Opening("a")}) AS decimal(19,2)) MasterOpening
              FROM dbo.IQAccmst a LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id=a.Act_Id
              WHERE {PartyAccounts("a")}) p
            WHERE p.Balance > 0.004
            """, new { side });
        // The entries that add to it: debits (D, and P paid out to the party) for customers; credits (C, R) for suppliers.
        var entries = (await s.QueryAsync<DueEntry>($"""
            SELECT CAST(d.Act_ID AS bigint) AccountId, CAST(d.TRDATE AS date) Date, CAST(d.SR_NO AS bigint) [Order], CAST(d.AMOUNT AS decimal(19,2)) Amount,
              CAST(CASE WHEN ISNULL(a.Bill2Bill,0)=1 THEN d.AMOUNT-ISNULL(d.BL_adjusted,0) ELSE d.AMOUNT END AS decimal(19,2)) [Open]
            FROM dbo.IQDayBook d JOIN dbo.IQAccmst a ON a.Act_Id=d.Act_ID
            WHERE {PartyAccounts("a")} AND d.TYPE IN (@t1, @t2)
            """, new { t1 = receivables ? "D" : "C", t2 = receivables ? "P" : "R" })).ToLookup(e => e.AccountId);
        var today = DateOnly.FromDateTime(clock.Now);
        var openingDay = firm.PeriodStart.AddDays(-1);
        return parties.Select(p =>
            {
                var own = entries[p.AccountId].Select(e => new AgeingEntry(e.Date, e.Order, e.Amount, e.Open));
                if (p.MasterOpening > 0) own = own.Append(new AgeingEntry(openingDay, -1, p.MasterOpening, p.MasterOpening));
                var age = Ageing.Age(p.Balance, own, today);
                return new OutstandingParty
                {
                    AccountId = p.AccountId, Name = p.Name, Type = p.Type, Town = p.Town, Phone = p.Phone, BillWise = p.BillWise, Balance = p.Balance,
                    Current = age.Current, Days30 = age.Days30, Days60 = age.Days60, Days90 = age.Days90, Older = age.Older, OldestBill = age.OldestBill,
                };
            })
            .OrderByDescending(p => p.Balance).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private sealed record DueRow
    {
        public long AccountId { get; init; }
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string Town { get; init; } = "";
        public string Phone { get; init; } = "";
        public bool BillWise { get; init; }
        public decimal Balance { get; init; }
        public decimal MasterOpening { get; init; }
    }

    private sealed record DueEntry
    {
        public long AccountId { get; init; }
        public DateOnly Date { get; init; }
        public long Order { get; init; }
        public decimal Amount { get; init; }
        public decimal Open { get; init; }
    }

    public async Task<IReadOnlyList<DayBookRow>> DayBookAsync(string firmId, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        return await DayBookAsync(s, from, to, 2000);
    }

    private static async Task<IReadOnlyList<DayBookRow>> DayBookAsync(HqSession s, DateOnly from, DateOnly to, int take)
    {
        var (fromAt, until) = Range(from, to);
        return await s.QueryAsync<DayBookRow>("""
            SELECT TOP (@take) CAST(h.Doc_Id AS bigint) DocumentId, CAST(h.Doc_Date AS date) Date, RTRIM(ISNULL(t.Ttype,'')) TypeName,
              RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12))+RTRIM(ISNULL(h.TExtn,'')) Reference,
              LTRIM(RTRIM(CASE WHEN h.Mode=1 OR ISNULL(c.Act_Name,'')='' THEN ISNULL(a.Act_Name,'') ELSE c.Act_Name END)) Party,
              CAST(ISNULL(h.Mode,0) AS int) Mode, CAST(ISNULL(h.Bill_Amt,0) AS decimal(19,2)) Amount, LTRIM(RTRIM(ISNULL(h.Narration,''))) Narration
            FROM dbo.IQDoc_Header h LEFT JOIN dbo.TTypes t ON t.TT_ID=h.Tt_Id LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id
            LEFT JOIN dbo.IQAccmst a ON a.Act_Id = CASE WHEN h.Tt_Id BETWEEN 204 AND 209 THEN (SELECT TOP 1 x.Act_ID FROM dbo.IQDayBook x WHERE x.Doc_ID=h.Doc_Id AND x.lnno=0 ORDER BY x.SR_NO) ELSE h.HeadAct_ID END
            WHERE h.Doc_Date >= @from AND h.Doc_Date < @until AND h.Tt_Id <> 222
            ORDER BY h.Doc_Date DESC, h.Doc_Id DESC
            """, new { take, from = fromAt, until });
    }

    public async Task<RegisterReport> RegisterAsync(string firmId, bool sales, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var tts = sales ? "506,507" : "500,501";
        var table = sales ? "IQDoc_Delv" : "IQDoc_Recd";
        var (fromAt, until) = Range(from, to);
        var args = new { from = fromAt, until };
        var rows = await s.QueryAsync<RegisterRow>($"""
            SELECT CAST(h.Doc_Id AS bigint) DocumentId, CAST(h.Doc_Date AS date) Date, RTRIM(h.TSeries)+'/'+CAST(h.TNo AS varchar(12))+RTRIM(ISNULL(h.TExtn,'')) Reference,
              LTRIM(RTRIM(ISNULL(h.Supp_Billno,''))) SupplierBill,
              LTRIM(RTRIM(CASE WHEN h.Mode=1 OR ISNULL(c.Act_Name,'')='' THEN ISNULL(a.Act_Name,'') ELSE c.Act_Name END)) Party,
              UPPER(LTRIM(RTRIM(CASE WHEN h.Mode=1 THEN ISNULL(ad.GSTIN,'') ELSE ISNULL(c.GSTIN,'') END))) Gstin, CAST(ISNULL(h.Mode,0) AS int) Mode,
              CAST(ISNULL(l.Taxable,0) AS decimal(19,2)) Taxable, CAST(ISNULL(l.Cgst,0) AS decimal(19,2)) Cgst, CAST(ISNULL(l.Sgst,0) AS decimal(19,2)) Sgst,
              CAST(ISNULL(l.Igst,0) AS decimal(19,2)) Igst, CAST(ISNULL(h.Bill_Amt,0)-ISNULL(h.Product_Value,0) AS decimal(19,2)) RoundOff, CAST(ISNULL(h.Bill_Amt,0) AS decimal(19,2)) Total
            FROM dbo.IQDoc_Header h
            LEFT JOIN (SELECT x.Doc_ID, SUM(x.GrossValue) Taxable, SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN 0 ELSE x.CGST_TaxAmt END) Cgst,
                SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN 0 ELSE x.SGST_TaxAmt END) Sgst, SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN x.TaxValue ELSE 0 END) Igst
                FROM dbo.{table} x GROUP BY x.Doc_ID) l ON l.Doc_ID=h.Doc_Id
            LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id LEFT JOIN dbo.IQAccmst a ON a.Act_Id=h.HeadAct_ID LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id=h.HeadAct_ID
            WHERE h.Tt_Id IN ({tts}) AND h.Doc_Date >= @from AND h.Doc_Date < @until
            ORDER BY h.Doc_Date, h.TNo
            """, args);
        var slabs = await s.QueryAsync<GstSlabRow>($"""
            SELECT CASE WHEN LEN(LTRIM(RTRIM(CASE WHEN h.Mode=1 THEN ISNULL(ad.GSTIN,'') ELSE ISNULL(c.GSTIN,'') END)))=15 THEN 'B2B' ELSE 'B2C' END Section,
              CAST(x.Tax AS decimal(9,2)) Rate, COUNT(DISTINCT h.Doc_Id) Bills, CAST(SUM(x.GrossValue) AS decimal(19,2)) Taxable,
              CAST(SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN 0 ELSE x.CGST_TaxAmt END) AS decimal(19,2)) Cgst,
              CAST(SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN 0 ELSE x.SGST_TaxAmt END) AS decimal(19,2)) Sgst,
              CAST(SUM(CASE WHEN ISNULL(x.GST_Type,1)=0 THEN x.TaxValue ELSE 0 END) AS decimal(19,2)) Igst
            FROM dbo.IQDoc_Header h JOIN dbo.{table} x ON x.Doc_ID=h.Doc_Id
            LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id=h.HeadAct_ID
            WHERE h.Tt_Id IN ({tts}) AND h.Doc_Date >= @from AND h.Doc_Date < @until
            GROUP BY CASE WHEN LEN(LTRIM(RTRIM(CASE WHEN h.Mode=1 THEN ISNULL(ad.GSTIN,'') ELSE ISNULL(c.GSTIN,'') END)))=15 THEN 'B2B' ELSE 'B2C' END, x.Tax
            ORDER BY 1, 2
            """, args);
        var hsn = await s.QueryAsync<HsnRow>($"""
            SELECT LTRIM(RTRIM(ISNULL(x.HSNCODE,''))) Hsn, CAST(x.Tax AS decimal(9,2)) Rate, CAST(SUM(x.Qty) AS decimal(19,2)) Quantity,
              CAST(SUM(x.GrossValue) AS decimal(19,2)) Taxable, CAST(SUM({LineTax("x")}) AS decimal(19,2)) Tax
            FROM dbo.IQDoc_Header h JOIN dbo.{table} x ON x.Doc_ID=h.Doc_Id
            WHERE h.Tt_Id IN ({tts}) AND h.Doc_Date >= @from AND h.Doc_Date < @until
            GROUP BY LTRIM(RTRIM(ISNULL(x.HSNCODE,''))), x.Tax ORDER BY 1, 2
            """, args);
        return new RegisterReport(sales ? "sales" : "purchases", from, to, rows, slabs, hsn,
            rows.Sum(r => r.Taxable), rows.Sum(r => r.Cgst), rows.Sum(r => r.Sgst), rows.Sum(r => r.Igst), rows.Sum(r => r.Total));
    }

    public async Task<IReadOnlyList<StockRow>> StockAsync(string firmId, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        return await s.QueryAsync<StockRow>("""
            SELECT CAST(im.Item_ID AS bigint) ItemId, LTRIM(RTRIM(ISNULL(im.Item_Code,''))) Code, LTRIM(RTRIM(im.Item_Name)) Name, LTRIM(RTRIM(ISNULL(g.Grp_Name,''))) [Group],
              LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(iu.ReportPack)),''), im.Item_Pack, ''))) Unit, CAST(ISNULL(im.Stk,0) AS decimal(19,3)) Stock,
              CAST(ISNULL(iu.Unit_Prate,0) AS decimal(19,2)) PurchaseRate, CAST(ISNULL(COALESCE(iu.PP_SaleRate, iu.Unit_Srate),0) AS decimal(19,2)) SaleRate,
              CAST(ISNULL(im.Stk,0)*ISNULL(iu.Unit_Prate,0) AS decimal(19,2)) Value,
              (SELECT CAST(MAX(h.Doc_Date) AS date) FROM dbo.IQDoc_Delv d JOIN dbo.IQDoc_Header h ON h.Doc_Id=d.Doc_ID WHERE d.Item_ID=im.Item_ID) LastSale
            FROM dbo.IQItems_Mast im JOIN dbo.IQItems_Units iu ON iu.Item_ID=im.Item_ID LEFT JOIN dbo.IQGroups g ON g.Grp_ID=im.Grp_ID
            WHERE ISNULL(im.Item_Stat,0)=0 ORDER BY im.Item_Name
            """);
    }

    public async Task<DashboardSummary> DashboardAsync(string firmId, DateOnly day, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        // Half-open days: everything dated on or before the dashboard's day, whatever time HQ stamped it with.
        var (d, next) = Range(day, day);
        var monthStart = new DateTime(day.Year, day.Month, 1);
        var k = await s.QueryFirstOrDefaultAsync<(decimal Sales, int Bills, decimal Cash, decimal Receipts, decimal Payments, decimal Purchases, decimal Month, decimal Year)>("""
            SELECT
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (506,507) AND Doc_Date>=@d THEN Bill_Amt END),0) AS decimal(19,2)),
              ISNULL(SUM(CASE WHEN Tt_Id IN (506,507) AND Doc_Date>=@d THEN 1 ELSE 0 END),0),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (506,507) AND Doc_Date>=@d AND Mode=0 THEN Bill_Amt END),0) AS decimal(19,2)),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (204,205) AND Doc_Date>=@d THEN Bill_Amt END),0) AS decimal(19,2)),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (206,207) AND Doc_Date>=@d THEN Bill_Amt END),0) AS decimal(19,2)),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (500,501) AND Doc_Date>=@d THEN Bill_Amt END),0) AS decimal(19,2)),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (506,507) AND Doc_Date>=@m THEN Bill_Amt END),0) AS decimal(19,2)),
              CAST(ISNULL(SUM(CASE WHEN Tt_Id IN (506,507) THEN Bill_Amt END),0) AS decimal(19,2))
            FROM dbo.IQDoc_Header WHERE Doc_Date < @next
            """, new { d, next, m = monthStart });
        var cash = await s.ScalarAsync<decimal>($"SELECT CAST({Balance("a", "@next")} AS decimal(19,2)) FROM dbo.IQAccmst a WHERE a.Act_Id={InvoiceLedger.HqCashAccountId}",
            new { next });
        // Same parties and balances as the outstanding list (each rounded as it shows them), as of the dashboard's day.
        var (recv, pay) = await s.QueryFirstOrDefaultAsync<(decimal, decimal)>($"""
            SELECT ISNULL(SUM(CASE WHEN x.b > 0.004 THEN x.b END),0), ISNULL(-SUM(CASE WHEN x.b < -0.004 THEN x.b END),0) FROM (
              SELECT CAST({Balance("a", "@next")} AS decimal(19,2)) b FROM dbo.IQAccmst a WHERE {PartyAccounts("a")}) x
            """, new { next });
        var recent = await DayBookAsync(s, day.AddDays(-30), day, 12);
        return new DashboardSummary(day, k.Sales, k.Bills, k.Cash, k.Receipts, k.Payments, k.Purchases, cash, recv, pay, k.Month, k.Year, recent);
    }
}
