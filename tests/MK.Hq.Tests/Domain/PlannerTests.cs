using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Domain.Text;

namespace MK.Hq.Tests.Domain;

/// <summary>Fast, database-free checks of the pure domain rules.</summary>
public sealed class PlannerTests
{
    [Fact]
    public void Sale_ledger_is_netted_per_account_and_sorted_like_HQ()
    {
        // The real HQ bill CS/879 (doc 10327): 6 lines at 18%, round-off -0.48 on a 'Less' charge.
        var rows = InvoiceLedger.Plan(inward: false, Settlement.Credit, 11155,
            [(10364, 3245.32m), (10341, 292.08m), (10343, 292.08m)], [new ChargePosting(11134, -0.48m, IsAdd: false)], 3829.00m);
        Assert.Equal(
            [new LedgerRow(10341, 'C', 0, 292.08m), new LedgerRow(10343, 'C', 0, 292.08m), new LedgerRow(10364, 'C', 0, 3245.32m),
             new LedgerRow(11134, 'C', 0, 0.48m), new LedgerRow(11155, 'D', 0, 3829.00m)],
            rows);
    }

    [Fact]
    public void Add_type_round_off_balances_the_books()
    {
        var rows = InvoiceLedger.Plan(false, Settlement.Credit, 900, [(100, 99.52m)], [new ChargePosting(424, -0.52m, IsAdd: true)], 99m);
        Assert.Equal(0m, rows.Sum(r => r.Signed));
    }

    [Fact]
    public void Cash_sale_uses_cash_as_implicit_contra()
    {
        var rows = InvoiceLedger.Plan(false, Settlement.Cash, 6, [(100, 100m), (101, 9m), (102, 9m)], [], 118m);
        Assert.All(rows, r => Assert.Equal((r.Type, r.ContraId), ('R', 6L)));
        Assert.DoesNotContain(rows, r => r.AccountId == 6);
    }

    [Fact]
    public void Bank_receipt_books_line_credit_and_bank_debit()
    {
        var spec = VoucherSpec.For(VoucherKind.BankReceipt);
        var bank = new VoucherAccount(10701, "KVB", "Bank", false);
        var customer = new VoucherAccount(11178, "Upright", "Customer", true);
        var rows = VoucherLedger.Plan(spec, bank, [customer], [new VoucherLine(11178, 40000m, "neft")]);
        Assert.Equal([new VoucherLedgerRow(11178, 'C', 10701, 40000m, "neft", 0), new VoucherLedgerRow(10701, 'D', 11178, 40000m, "neft", -1)], rows);
    }

    [Fact]
    public void Preserved_settlement_moves_to_the_new_party_row_and_refuses_shrinking_below_it()
    {
        var bill = new SettlementEntry(1, 500, 'D', 1000m, 400m);
        var receipt = new SettlementEntry(9, 500, 'C', 400m, 400m);
        var link = new AllocationLink(9, 1, 400m);
        var ok = AllocationPlanner.Plan([bill], [receipt], [new SettlementEntry(-1, 500, 'D', 1200m, 0)], [link], AllocationDisposition.Preserve);
        Assert.Equal(new AllocationLink(9, -1, 400m), ok.Links.Single());
        Assert.Contains((-1L, 400m), ok.Counters);
        var tooSmall = Assert.Throws<HqException>(() =>
            AllocationPlanner.Plan([bill], [receipt], [new SettlementEntry(-1, 500, 'D', 300m, 0)], [link], AllocationDisposition.Preserve));
        Assert.Equal("HQ_ALLOCATION_CONFLICT", tooSmall.Code);
        var released = AllocationPlanner.Plan([bill], [receipt], [], [link], AllocationDisposition.Release);
        Assert.Equal((9L, 0m), released.Counters.Single());
        Assert.Equal(400m, released.Released);
    }

    [Fact]
    public void Inconsistent_legacy_counters_are_refused_not_guessed()
    {
        var bill = new SettlementEntry(1, 500, 'D', 1000m, 300m); // counter says 300, links say 400
        var receipt = new SettlementEntry(9, 500, 'C', 400m, 400m);
        Assert.Throws<HqException>(() => AllocationPlanner.Plan([bill], [receipt], [], [new AllocationLink(9, 1, 400m)], AllocationDisposition.Release));
    }

    [Theory]
    [InlineData("37AABCU9603R1ZM", true)]
    [InlineData("37AABCU9603R1ZN", false)]
    [InlineData("37AAB HP9506P1ZJ", false)]
    public void Gstin_check_digit(string gstin, bool valid) => Assert.Equal(valid, Gstin.IsValid(gstin));

    [Fact]
    public void Amount_in_words_uses_lakhs_and_crores() =>
        Assert.Equal("Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Ninety Paise Only", AmountInWords.Rupees(12345678.90m));

    [Fact]
    public void Inclusive_line_matches_HQ_bill_CS_879_line_1()
    {
        var q = GstCalculator.PriceLine(new LineInput(10, 491, 45, TaxRates.FromSlab(18)), TaxBasis.Inclusive, TaxMode.Local);
        Assert.Equal((2209.50m, 2288.56m, 205.97m, 205.97m, 411.94m, 2700.50m), (q.Discount, q.Taxable, q.Cgst, q.Sgst, q.AggregateTax, q.Net));
    }
}
