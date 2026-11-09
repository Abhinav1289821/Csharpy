using Dapper;
using MK.Hq.Domain.Ledger;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Domain;

/// <summary>
/// Rebuild each HQ-saved sale/purchase's posting inputs from its own stored lines and charges, run the
/// planner, and require HQ's exact IQDayBook rows: account, type, contra, amount and row order.
/// </summary>
public sealed class LedgerParityTests(ITestOutputHelper output)
{
    private sealed record Header(long Doc_Id, int Tt_Id, int Mode, long Act_ID, decimal Bill_Amt);
    private sealed record Line(long Doc_Id, long Act_Id, decimal Taxable, long Tax_Id, decimal Cgst, long Sgst_Id, decimal Sgst, decimal TaxValue, int Gst_Type, int Ln_No);
    private sealed record Charge(long Doc_Id, long Act_ID, decimal Amount, int Op, int Ln_No);
    private sealed record Book(long Doc_ID, long Act_ID, string TYPE, long Contra_ID, decimal AMOUNT, long SR_NO);

    [Theory]
    [InlineData("ISTMBM26")]
    [InlineData("ISTMP26")]
    public async Task Planner_reproduces_HQ_invoice_ledgers(string database)
    {
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(database));
        var headers = (await cn.QueryAsync<Header>("SELECT CAST(Doc_Id AS bigint) Doc_Id, CAST(Tt_Id AS int) Tt_Id, CAST(Mode AS int) Mode, CAST(Act_ID AS bigint) Act_ID, CAST(Bill_Amt AS decimal(19,2)) Bill_Amt FROM IQDoc_Header WHERE Tt_Id IN (500,501,506,507)")).ToList();
        var lines = (await cn.QueryAsync<Line>("""
            SELECT CAST(Doc_ID AS bigint) Doc_Id, CAST(ISNULL(Act_Id,0) AS bigint) Act_Id, CAST(GrossValue AS decimal(19,2)) Taxable, CAST(ISNULL(Tax_Id,0) AS bigint) Tax_Id,
              CAST(ISNULL(CGST_TaxAmt,0) AS decimal(19,2)) Cgst, CAST(ISNULL(SGST_TaxID,0) AS bigint) Sgst_Id, CAST(ISNULL(SGST_TaxAmt,0) AS decimal(19,2)) Sgst,
              CAST(ISNULL(TaxValue,0) AS decimal(19,2)) TaxValue, CAST(ISNULL(GST_Type,1) AS int) Gst_Type, CAST(Ln_No AS int) Ln_No FROM IQDoc_Delv
            UNION ALL
            SELECT Doc_ID, ISNULL(Act_Id,0), GrossValue, ISNULL(Tax_Id,0), ISNULL(CGST_TaxAmt,0), ISNULL(SGST_TaxID,0), ISNULL(SGST_TaxAmt,0), ISNULL(TaxValue,0), ISNULL(GST_Type,1), Ln_No FROM IQDoc_Recd
            """)).ToLookup(l => l.Doc_Id);
        var charges = (await cn.QueryAsync<Charge>("""
            SELECT CAST(c.Doc_id AS bigint) Doc_Id, CAST(c.Act_ID AS bigint) Act_ID, CAST(c.Amount AS decimal(19,2)) Amount, CAST(ISNULL(t.ChTp_Op,0) AS int) Op, CAST(c.Ln_No AS int) Ln_No
            FROM IQDoc_Chrgs c JOIN IQCHARGES ch ON ch.Chrg_Id=c.Chrg_id JOIN IQCHRG_TYPES t ON t.ChTp_ID=ch.Chrg_Type
            """)).ToLookup(c => c.Doc_Id);
        var books = (await cn.QueryAsync<Book>("SELECT CAST(Doc_ID AS bigint) Doc_ID, CAST(Act_ID AS bigint) Act_ID, TYPE, CAST(ISNULL(Contra_ID,0) AS bigint) Contra_ID, CAST(AMOUNT AS decimal(19,2)) AMOUNT, CAST(SR_NO AS bigint) SR_NO FROM IQDayBook")).ToLookup(b => b.Doc_ID);

        var failures = new List<string>();
        var knownDefects = 0;
        foreach (var h in headers)
        {
            var inward = h.Tt_Id is 500 or 501;
            var settlement = h.Mode switch { 0 => Settlement.Cash, 1 => Settlement.Credit, _ => Settlement.Bank };
            var amounts = new List<(long, decimal)>();
            foreach (var l in lines[h.Doc_Id].OrderBy(l => l.Ln_No))
            {
                amounts.Add((l.Act_Id, l.Taxable));
                if (l.Gst_Type == 0) amounts.Add((l.Tax_Id, l.TaxValue));
                else { amounts.Add((l.Tax_Id, l.Cgst)); amounts.Add((l.Sgst_Id, l.Sgst)); }
            }
            var planned = InvoiceLedger.Plan(inward, settlement, h.Act_ID, amounts,
                charges[h.Doc_Id].OrderBy(c => c.Ln_No).Select(c => new ChargePosting(c.Act_ID, c.Amount, c.Op == 1)), h.Bill_Amt);
            var stored = books[h.Doc_Id].OrderBy(b => b.SR_NO).Select(b => new LedgerRow(b.Act_ID, b.TYPE.Trim()[0], b.Contra_ID, b.AMOUNT)).ToArray();
            if (!planned.SequenceEqual(stored) && KnownHqDefects.Contains((database, h.Doc_Id))) { knownDefects++; continue; }
            if (!planned.SequenceEqual(stored))
                failures.Add($"doc {h.Doc_Id} tt {h.Tt_Id} mode {h.Mode}: HQ [{string.Join("; ", stored.Select(Show))}] engine [{string.Join("; ", planned.Select(Show))}]");
        }
        output.WriteLine($"{database}: {headers.Count} bills, {failures.Count} differ, {knownDefects} known HQ-side defects");
        foreach (var f in failures.Take(25)) output.WriteLine(f);
        Assert.True(failures.Count == 0, $"{failures.Count} of {headers.Count} bills differ; first: {failures.FirstOrDefault()}");
    }

    /// <summary>
    /// Bills whose stored ledger HQ itself left inconsistent with their lines: two cash sales with no
    /// ledger (HQ's non-transactional save failed half-way), one with CGST posted twice, and bills edited
    /// in HQ whose ledger was not rewritten to match. Any new mismatch fails the test.
    /// </summary>
    private static readonly HashSet<(string, long)> KnownHqDefects =
        new long[] { 8404, 8516, 8626, 9200, 9229, 9270, 9322, 9324, 9327, 9335, 9983, 10143 }.Select(id => ("ISTMBM26", id)).ToHashSet();

    private static string Show(LedgerRow r) => $"{r.AccountId} {r.Type} {r.ContraId} {r.Amount}";
}

