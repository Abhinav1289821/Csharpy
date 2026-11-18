using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;

namespace MK.Hq.Tests.Domain;

/// <summary>Invoice and voucher ledger planning (database-free); LedgerParityTests is the oracle over real bills.</summary>
public sealed class DomainLedgerTests
{
    [Fact]
    public void Credit_purchase_debits_items_and_taxes_and_credits_the_supplier()
    {
        var rows = InvoiceLedger.Plan(inward: true, Settlement.Credit, 900, [(102, 90m), (100, 1000m), (101, 90m)], [], 1180m);
        Assert.Equal(
            [new LedgerRow(100, 'D', 0, 1000m), new LedgerRow(101, 'D', 0, 90m), new LedgerRow(102, 'D', 0, 90m), new LedgerRow(900, 'C', 0, 1180m)],
            rows);
        Assert.Equal(0m, rows.Sum(r => r.Signed));
    }

    [Fact]
    public void Bank_sale_uses_the_bank_account_as_party_and_contra()
    {
        var rows = InvoiceLedger.Plan(false, Settlement.Bank, 10701, [(100, 100m), (101, 9m), (102, 9m)], [], 118m);
        Assert.Equal(
            [new LedgerRow(100, 'C', 10701, 100m), new LedgerRow(101, 'C', 10701, 9m), new LedgerRow(102, 'C', 10701, 9m), new LedgerRow(10701, 'D', 10701, 118m)],
            rows);
    }

    [Fact]
    public void Cash_purchase_is_paid_out_against_cash_with_no_party_row()
    {
        var rows = InvoiceLedger.Plan(true, Settlement.Cash, InvoiceLedger.HqCashAccountId, [(100, 100m), (101, 2.5m), (102, 2.5m)], [], 105m);
        Assert.Equal([new LedgerRow(100, 'P', 6, 100m), new LedgerRow(101, 'P', 6, 2.5m), new LedgerRow(102, 'P', 6, 2.5m)], rows);
    }

    [Fact]
    public void Line_amounts_are_totalled_per_account_and_non_positive_totals_dropped()
    {
        var rows = InvoiceLedger.Plan(false, Settlement.Credit, 900,
            [(100, 50m), (100, 50.5m), (0, 9m), (-1, 9m), (101, 0m), (102, 10m), (102, -10m), (103, 5m), (103, -6m)], [], 100.5m);
        Assert.Equal([new LedgerRow(100, 'C', 0, 100.5m), new LedgerRow(900, 'D', 0, 100.5m)], rows);
    }

    /// <summary>HQ's Add/Less rule (FrmInwards.AccountPosting_GST): the side follows the charge type and the amount's sign.</summary>
    [Theory]
    [InlineData(true, true, "5", 'D')]
    [InlineData(true, true, "-5", 'C')]
    [InlineData(true, false, "5", 'C')]
    [InlineData(true, false, "-5", 'D')]
    [InlineData(false, true, "5", 'C')]
    [InlineData(false, true, "-5", 'D')]
    [InlineData(false, false, "5", 'D')]
    [InlineData(false, false, "-5", 'C')]
    public void Charge_side_follows_HQs_add_less_rule(bool inward, bool isAdd, string amount, char type)
    {
        var rows = InvoiceLedger.Plan(inward, Settlement.Credit, 900, [], [new ChargePosting(424, decimal.Parse(amount), isAdd)], 0m);
        Assert.Equal(new LedgerRow(424, type, 0, 5m), Assert.Single(rows));
    }

    [Fact]
    public void Zero_and_unmapped_charges_post_nothing()
    {
        var rows = InvoiceLedger.Plan(false, Settlement.Credit, 900, [], [new ChargePosting(424, 0m, true), new ChargePosting(0, 5m, true)], 0m);
        Assert.Empty(rows);
    }

    [Fact]
    public void Charge_on_an_item_account_nets_with_it()
    {
        var add = InvoiceLedger.Plan(false, Settlement.Credit, 900, [(100, 100m)], [new ChargePosting(100, 10m, IsAdd: true)], 110m);
        Assert.Equal(new LedgerRow(100, 'C', 0, 110m), add[0]);
        var less = InvoiceLedger.Plan(false, Settlement.Credit, 900, [(100, 100m)], [new ChargePosting(100, 10m, IsAdd: false)], 90m);
        Assert.Equal(new LedgerRow(100, 'C', 0, 90m), less[0]);
    }

    [Fact]
    public void Zero_value_bill_has_no_party_row()
    {
        Assert.Empty(InvoiceLedger.Plan(false, Settlement.Credit, 900, [(100, 0m)], [], 0m));
    }

    private static readonly VoucherAccount Cash = new(6, "Cash on hand", "Cash", false);
    private static readonly VoucherAccount Bank = new(10701, "KVB", "Bank", false);
    private static readonly VoucherAccount Customer = new(11178, "Upright", "Customer", true);
    private static readonly VoucherAccount Supplier = new(11200, "Pipes Co", "Supplier", true);
    private static readonly VoucherAccount Returns = new(300, "Sales Returns", "Expenses", false);

    [Fact]
    public void Bank_payment_debits_the_supplier_and_credits_the_bank()
    {
        var rows = VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankPayment), Bank, [Supplier, Customer],
            [new VoucherLine(11200, 500m, "chq 1"), new VoucherLine(11178, 250m, "")]);
        Assert.Equal(
            [new VoucherLedgerRow(11200, 'D', 10701, 500m, "chq 1", 0), new VoucherLedgerRow(11178, 'D', 10701, 250m, "", 1),
             new VoucherLedgerRow(10701, 'C', 11200, 750m, "chq 1", -1)],
            rows);
    }

    [Theory]
    [InlineData(VoucherKind.CashReceipt, 'R')]
    [InlineData(VoucherKind.CashPayment, 'P')]
    public void Cash_vouchers_use_cash_as_implicit_contra_with_no_header_row(VoucherKind kind, char type)
    {
        var rows = VoucherLedger.Plan(VoucherSpec.For(kind), Cash, [Customer], [new VoucherLine(11178, 100m, "")]);
        Assert.Equal(new VoucherLedgerRow(11178, type, 6, 100m, "", 0), Assert.Single(rows));
    }

    [Theory]
    [InlineData(VoucherKind.CreditNote, 'D', 'C')]
    [InlineData(VoucherKind.DebitNote, 'C', 'D')]
    public void Notes_post_line_and_header_on_opposite_sides(VoucherKind kind, char lineType, char headerType)
    {
        var rows = VoucherLedger.Plan(VoucherSpec.For(kind), Customer, [Returns], [new VoucherLine(300, 59m, "return")]);
        Assert.Equal([new VoucherLedgerRow(300, lineType, 11178, 59m, "return", 0), new VoucherLedgerRow(11178, headerType, 300, 59m, "return", -1)], rows);
    }

    [Fact]
    public void Cash_deposited_by_bank_receipt_is_a_contra_row_outside_the_header_total()
    {
        // FrmFin_Vouchers.SaveDaybookData: a cash line books bank 'P' against Cash; the header row still takes line 1 as contra.
        var rows = VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Cash, Customer],
            [new VoucherLine(6, 500m, "deposit"), new VoucherLine(11178, 1000m, "neft")]);
        Assert.Equal(
            [new VoucherLedgerRow(10701, 'P', 6, 500m, "deposit", 0), new VoucherLedgerRow(11178, 'C', 10701, 1000m, "neft", 1),
             new VoucherLedgerRow(10701, 'D', 6, 1000m, "deposit", -1)],
            rows);
        var onlyCash = VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Cash], [new VoucherLine(6, 500m, "")]);
        Assert.Equal(new VoucherLedgerRow(10701, 'P', 6, 500m, "", 0), Assert.Single(onlyCash));
    }

    [Theory]
    [InlineData(VoucherKind.CashReceipt, 'D')]
    [InlineData(VoucherKind.BankReceipt, 'D')]
    [InlineData(VoucherKind.CashPayment, 'C')]
    [InlineData(VoucherKind.BankPayment, 'C')]
    public void Header_side_for_a_non_cash_header_follows_receipt_or_payment(VoucherKind kind, char headerType)
    {
        // HQ picks the header side from the voucher type alone (aType2: receipts 'D', payments 'C'), not from the mode.
        var rows = VoucherLedger.Plan(VoucherSpec.For(kind), Bank, [Customer], [new VoucherLine(11178, 10m, "")]);
        Assert.Equal(headerType, rows.Single(r => r.Line == -1).Type);
    }

    [Fact]
    public void A_line_cannot_use_the_header_account()
    {
        var e = Assert.Throws<HqException>(() => VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Bank], [new VoucherLine(10701, 1m, "")]));
        Assert.Equal("HQ_DATA_INVALID", e.Code);
        var mismatch = Assert.Throws<HqException>(() => VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Customer], [new VoucherLine(11200, 1m, "")]));
        Assert.Equal("HQ_DATA_INVALID", mismatch.Code);
        var count = Assert.Throws<HqException>(() => VoucherLedger.Plan(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Customer, Supplier], [new VoucherLine(11178, 1m, "")]));
        Assert.Equal(FailureKind.Internal, count.Kind);
    }

    [Fact]
    public void Voucher_specs_match_HQs_TTYPES()
    {
        Assert.Equal(6, VoucherSpec.All.Count);
        foreach (var spec in VoucherSpec.All.Values)
        {
            Assert.Same(spec, VoucherSpec.ForTt(spec.TtId));
            Assert.Equal(1, new[] { spec.IsReceipt, spec.IsPayment, spec.IsNote }.Count(x => x));
        }
        Assert.Null(VoucherSpec.ForTt(506));
        Assert.True(VoucherSpec.For(VoucherKind.BankPayment).IsBank);
        Assert.False(VoucherSpec.For(VoucherKind.CreditNote).IsBank);
    }

    [Fact]
    public void Audit_rows_one_per_cash_line_two_per_bank_or_note_line()
    {
        var bank = VoucherLedger.AuditRows(VoucherSpec.For(VoucherKind.BankReceipt), Bank, [Customer, Cash],
            [new VoucherLine(11178, 100m, ""), new VoucherLine(6, 50m, "dep")]);
        Assert.Equal(
            [(new VoucherLedgerRow(11178, 'C', 10701, 100m, " ", 0), 0), (new VoucherLedgerRow(10701, 'D', 11178, 100m, " ", 0), 1),
             (new VoucherLedgerRow(10701, 'P', 6, 50m, "dep", 1), 0)],
            bank);

        var cash = VoucherLedger.AuditRows(VoucherSpec.For(VoucherKind.CashPayment), Cash, [Supplier], [new VoucherLine(11200, 70m, "x")]);
        Assert.Equal([(new VoucherLedgerRow(11200, 'P', 6, 70m, "x", 0), 0)], cash);

        var note = VoucherLedger.AuditRows(VoucherSpec.For(VoucherKind.CreditNote), Customer, [Returns], [new VoucherLine(300, 59m, "r")]);
        Assert.Equal(
            [(new VoucherLedgerRow(11178, 'C', 300, 59m, "r", 0), 0), (new VoucherLedgerRow(300, 'D', 11178, 59m, "r", 0), 1)],
            note);
    }
}
