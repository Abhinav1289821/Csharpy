using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Reports;

namespace MK.Hq.Tests.Reports;

/// <summary>The shared report SQL fragments (database-free). Their results were checked against the MBM and MP demo copies
/// when written: every account's balance equals the old per-screen sums except Cash in the catalogue, which now equals the
/// dashboard's cash in hand.</summary>
public sealed class ReportsSqlTests
{
    [Fact]
    public void Balance_counts_cash_implicit_rows_for_cash_only()
    {
        var sql = ReportService.Balance("a");
        Assert.Contains(ReportService.Opening("a"), sql);
        Assert.Contains($"a.Act_Id={InvoiceLedger.HqCashAccountId} THEN", sql);
        Assert.Contains($"bc.Contra_ID={InvoiceLedger.HqCashAccountId}", sql);
        Assert.DoesNotContain("TRDATE <", sql);
    }

    [Fact]
    public void Balance_as_of_a_day_cuts_both_the_account_rows_and_the_cash_rows()
    {
        // The dashboard's receivable and payable ignored its day (post-dated bills counted on today's screen).
        var sql = ReportService.Balance("a", "@next");
        Assert.Contains("bd.TRDATE < @next", sql);
        Assert.Contains("bc.TRDATE < @next", sql);
    }

    [Fact]
    public void Every_ledger_side_is_signed_and_nothing_else()
    {
        var sql = ReportService.Signed("d");
        Assert.Contains("d.TYPE IN ('D','P') THEN d.AMOUNT", sql);
        Assert.Contains("d.TYPE IN ('C','R') THEN -d.AMOUNT", sql);
        Assert.EndsWith("ELSE 0 END", sql); // an unknown side is not quietly a credit (the catalogue's old rule)
    }

    [Fact]
    public void Party_accounts_are_ledger_accounts_only()
    {
        // The dashboard left out Act_Cat='A' (a group named like a party type would have been counted).
        Assert.Contains("p.Act_Cat='A'", ReportService.PartyAccounts("p"));
    }

    [Fact]
    public void Hsn_tax_is_the_register_columns_not_tax_value()
    {
        var sql = ReportService.LineTax("x");
        Assert.Contains("ISNULL(x.GST_Type,1)=0 THEN x.TaxValue", sql);
        Assert.Contains("x.CGST_TaxAmt+x.SGST_TaxAmt", sql);
    }
}
