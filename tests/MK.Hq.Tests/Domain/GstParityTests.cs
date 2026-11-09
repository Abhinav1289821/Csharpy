using Dapper;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using Microsoft.Data.SqlClient;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Domain;

/// <summary>
/// The strongest available oracle: recompute every line HQ itself saved this year, in both firms,
/// and require the engine to reproduce HQ's stored discount, taxable value, GST halves, combined tax
/// and net to the paisa.
/// </summary>
public sealed class GstParityTests(ITestOutputHelper output)
{
    private sealed record StoredLine(
        long Doc_Id, string Tbl, int Tt_Id, int Exclusive, int GST_Type,
        decimal Qty, decimal Rate, decimal Disc, decimal DiscValue, decimal Value, decimal GrossValue,
        decimal Tax, decimal TaxValue, decimal CGST_Tax, decimal CGST_TaxAmt, decimal SGST_Tax, decimal SGST_TaxAmt, decimal NetValue);

    private const string Sql = """
        SELECT CAST(h.Doc_Id AS bigint) Doc_Id, x.Tbl, CAST(h.Tt_Id AS int) Tt_Id, CAST(ISNULL(h.Exclusive,0) AS int) Exclusive, CAST(ISNULL(h.GST_Type,1) AS int) GST_Type,
          CAST(x.Qty AS decimal(19,6)) Qty, CAST(x.Rate AS decimal(19,6)) Rate, CAST(ISNULL(x.Disc,0) AS decimal(19,6)) Disc,
          CAST(ISNULL(x.DiscValue,0) AS decimal(19,2)) DiscValue, CAST(x.Value AS decimal(19,2)) Value, CAST(x.GrossValue AS decimal(19,2)) GrossValue,
          CAST(ISNULL(x.Tax,0) AS decimal(9,4)) Tax, CAST(ISNULL(x.TaxValue,0) AS decimal(19,2)) TaxValue,
          CAST(ISNULL(x.CGST_Tax,0) AS decimal(9,4)) CGST_Tax, CAST(ISNULL(x.CGST_TaxAmt,0) AS decimal(19,2)) CGST_TaxAmt,
          CAST(ISNULL(x.SGST_Tax,0) AS decimal(9,4)) SGST_Tax, CAST(ISNULL(x.SGST_TaxAmt,0) AS decimal(19,2)) SGST_TaxAmt,
          CAST(x.NetValue AS decimal(19,2)) NetValue
        FROM IQDoc_Header h CROSS APPLY (
            SELECT 'Delv' Tbl, Qty, Rate, Disc, DiscValue, Value, GrossValue, Tax, TaxValue, CGST_Tax, CGST_TaxAmt, SGST_Tax, SGST_TaxAmt, NetValue FROM IQDoc_Delv d WHERE d.Doc_ID=h.Doc_Id AND ISNULL(d.Free,0)=0
            UNION ALL SELECT 'Recd', Qty, Rate, Disc, DiscValue, Value, GrossValue, Tax, TaxValue, CGST_Tax, CGST_TaxAmt, SGST_Tax, SGST_TaxAmt, NetValue FROM IQDoc_Recd r WHERE r.Doc_ID=h.Doc_Id AND ISNULL(r.Free,0)=0
            UNION ALL SELECT 'Ord', Qty, Rate, Disc, DiscValue, Value, GrossValue, Tax, TaxValue, CGST_Tax, CGST_TaxAmt, SGST_Tax, SGST_TaxAmt, NetValue FROM IQOrd_Detail o WHERE o.Doc_ID=h.Doc_Id
        ) x
        WHERE h.Tt_Id IN (500,501,506,507,248,249) AND x.Qty > 0 AND x.Rate >= 0
        """;

    private const long LastLegacyEditedDocument = 9324;

    [Theory]
    [InlineData("ISTMBM26")]
    [InlineData("ISTMP26")]
    public async Task Engine_reproduces_every_stored_HQ_line(string database)
    {
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(database));
        var rows = (await cn.QueryAsync<StoredLine>(Sql)).ToList();
        Assert.NotEmpty(rows);

        var failures = new List<string>();
        var legacy = 0;
        foreach (var r in rows)
        {
            var mode = r.GST_Type == 0 ? TaxMode.Interstate : TaxMode.Local;
            var basis = r.Exclusive == 1 ? TaxBasis.Inclusive : TaxBasis.Exclusive;
            var taxes = mode == TaxMode.Interstate ? new TaxRates(0, 0, r.Tax) : new TaxRates(r.CGST_Tax, r.SGST_Tax, r.Tax);
            var q = GstCalculator.PriceLine(new LineInput(r.Qty, r.Rate, r.Disc, taxes), basis, mode, maxQuantityScale: 6, maxRateScale: 6);
            var cgst = mode == TaxMode.Interstate ? q.Igst : q.Cgst;
            var ok = q.Discount == r.DiscValue && q.Taxable == r.GrossValue && q.AggregateTax == r.TaxValue && q.Net == r.NetValue && q.Gross == r.Value
                     && (mode == TaxMode.Interstate || (cgst == r.CGST_TaxAmt && q.Sgst == r.SGST_TaxAmt));
            if (!ok && basis == TaxBasis.Inclusive && mode == TaxMode.Local && r.Doc_Id <= LastLegacyEditedDocument)
            {
                // HQ's edit-mode recalculation (seen only on 4 bills edited in June/July 2026) rounds the combined
                // tax first and halves it afterwards. Every newer bill follows the save-path rule the engine uses.
                var raw = (r.Qty * r.Rate - r.DiscValue) * r.Tax / (100 + r.Tax);
                var agg = Money.Round2(raw);
                var half = (decimal)Math.Round((double)agg / 2, 2, MidpointRounding.AwayFromZero); // VB double arithmetic
                var halfOfRaw = Money.Round2(raw / 2);
                var halvesMatch = (half == r.CGST_TaxAmt || halfOfRaw == r.CGST_TaxAmt) && r.CGST_TaxAmt == r.SGST_TaxAmt;
                if (agg == r.TaxValue && halvesMatch && q.Net - agg == r.GrossValue) { legacy++; continue; }
            }
            if (!ok)
                failures.Add($"{r.Tbl} doc {r.Doc_Id} tt {r.Tt_Id} {basis}/{mode} q={r.Qty} rate={r.Rate} disc={r.Disc} tax={r.Tax}: " +
                             $"HQ disc {r.DiscValue} taxable {r.GrossValue} tax {r.TaxValue} c {r.CGST_TaxAmt} s {r.SGST_TaxAmt} net {r.NetValue} | " +
                             $"engine disc {q.Discount} taxable {q.Taxable} tax {q.AggregateTax} c {cgst} s {q.Sgst} net {q.Net}");
        }

        output.WriteLine($"{database}: {rows.Count} lines checked, {failures.Count} differ, {legacy} match HQ's legacy edit-path rounding");
        Assert.True(legacy <= 13, "Legacy rounding must stay confined to the four known bills");
        foreach (var f in failures.Take(40)) output.WriteLine(f);
        Assert.True(failures.Count == 0, $"{failures.Count} of {rows.Count} lines differ; first: {failures.FirstOrDefault()}");
    }

    [Theory]
    [InlineData("ISTMBM26")]
    [InlineData("ISTMP26")]
    public async Task Engine_reproduces_HQ_round_off_and_bill_amount(string database)
    {
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(database));
        var rows = (await cn.QueryAsync<(int Doc_Id, decimal Product, decimal Bill, int Type, decimal Step, decimal Lines)>("""
            SELECT h.Doc_Id, CAST(h.Product_Value AS decimal(19,2)), CAST(h.Bill_Amt AS decimal(19,2)), CAST(ISNULL(t.RoundedOffType,0) AS int), CAST(ISNULL(t.RoundedOffTo,0) AS decimal(9,2)),
              CAST((SELECT ISNULL(SUM(NetValue),0) FROM IQDoc_Delv d WHERE d.Doc_ID=h.Doc_Id) + (SELECT ISNULL(SUM(NetValue),0) FROM IQDoc_Recd r WHERE r.Doc_ID=h.Doc_Id) AS decimal(19,2))
            FROM IQDoc_Header h JOIN TTYPES t ON t.TT_ID=h.Tt_Id
            WHERE h.Tt_Id IN (500,501,506,507)
              AND NOT EXISTS (SELECT 1 FROM IQDoc_Chrgs c JOIN IQCHARGES ch ON ch.Chrg_Id=c.Chrg_id WHERE c.Doc_id=h.Doc_Id AND c.Amount<>0 AND c.Act_ID<>t.Roundoff_ActID AND ch.Chrg_Name NOT LIKE '%Round%')
            """)).ToList();
        var failures = rows.Where(r => r.Lines == r.Product)
            .Where(r => Money.RoundToStep(r.Product, r.Step, (RoundingDirection)r.Type) != r.Bill)
            .Select(r => $"doc {r.Doc_Id}: product {r.Product} step {r.Step} type {r.Type} HQ bill {r.Bill} engine {Money.RoundToStep(r.Product, r.Step, (RoundingDirection)r.Type)}")
            .ToList();
        output.WriteLine($"{database}: {rows.Count} bills, {failures.Count} differ");
        foreach (var f in failures.Take(20)) output.WriteLine(f);
        Assert.Empty(failures);
    }
}


