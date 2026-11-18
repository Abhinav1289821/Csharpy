using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;

namespace MK.Hq.Tests.Domain;

/// <summary>
/// Table-driven checks of HQ's GST line and bill arithmetic (database-free). The expected values are worked by hand
/// from FrmInwards.Calc_GridTotals_EachRow's rules; GstParityTests is the oracle that those rules match real HQ lines.
/// </summary>
public sealed class DomainGstTests
{
    private static decimal D(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static LineQuote Quote(string qty, string rate, string disc, string slab, TaxBasis basis, TaxMode mode) =>
        GstCalculator.PriceLine(new LineInput(D(qty), D(rate), D(disc), TaxRates.FromSlab(D(slab))), basis, mode);

    /// <param name="expected">gross|discount|taxable|cgst|sgst|igst|aggregate tax|net</param>
    [Theory]
    // Exclusive: each half rounded on its own; the combined tax and the net from the unsplit rate.
    [InlineData("1", "100", "0", "18", TaxBasis.Exclusive, TaxMode.Local, "100|0|100|9|9|0|18|118")]
    [InlineData("1", "0.25", "0", "18", TaxBasis.Exclusive, TaxMode.Local, "0.25|0|0.26|0.02|0.02|0|0.05|0.30")]   // halves 0.0225, combined 0.045 → 0.05 (not banker's 0.04)
    [InlineData("1", "0.25", "0", "18", TaxBasis.Exclusive, TaxMode.Interstate, "0.25|0|0.25|0|0|0.05|0.05|0.30")]
    [InlineData("1", "10", "0", "0.1", TaxBasis.Exclusive, TaxMode.Local, "10|0|9.99|0.01|0.01|0|0.01|10.01")]     // 0.005 halves round up, combined does not
    [InlineData("1", "100", "0", "0.25", TaxBasis.Exclusive, TaxMode.Local, "100|0|99.99|0.13|0.13|0|0.25|100.25")]
    [InlineData("1", "33.33", "0", "3", TaxBasis.Exclusive, TaxMode.Local, "33.33|0|33.33|0.50|0.50|0|1.00|34.33")]
    [InlineData("3", "33.33", "12.5", "18", TaxBasis.Exclusive, TaxMode.Local, "99.99|12.50|87.50|7.87|7.87|0|15.75|103.24")]
    [InlineData("1.5", "2.35", "0", "5", TaxBasis.Exclusive, TaxMode.Interstate, "3.53|0|3.53|0|0|0.18|0.18|3.71")]
    [InlineData("2", "50", "100", "18", TaxBasis.Exclusive, TaxMode.Local, "100|100|0|0|0|0|0|0")]
    [InlineData("1", "100", "0", "0", TaxBasis.Exclusive, TaxMode.Local, "100|0|100|0|0|0|0|100")]
    [InlineData("1", "0", "0", "18", TaxBasis.Exclusive, TaxMode.Local, "0|0|0|0|0|0|0|0")]
    // Inclusive: tax extracted from the unrounded qty×rate less the rounded discount; each half rounded on its own.
    [InlineData("1", "118", "0", "18", TaxBasis.Inclusive, TaxMode.Local, "118|0|100|9|9|0|18|118")]
    [InlineData("1", "1.05", "0", "5", TaxBasis.Inclusive, TaxMode.Local, "1.05|0|0.99|0.03|0.03|0|0.06|1.05")]    // odd paise: 0.05 tax → 0.025 halves → 0.03 each
    [InlineData("1", "1.05", "0", "5", TaxBasis.Inclusive, TaxMode.Interstate, "1.05|0|1.00|0|0|0.05|0.05|1.05")]
    [InlineData("10", "491", "45", "18", TaxBasis.Inclusive, TaxMode.Local, "4910|2209.50|2288.56|205.97|205.97|0|411.94|2700.50")]
    [InlineData("3", "0.3333", "0", "18", TaxBasis.Inclusive, TaxMode.Local, "1.00|0|0.84|0.08|0.08|0|0.16|1.00")]
    [InlineData("2", "50", "100", "18", TaxBasis.Inclusive, TaxMode.Local, "100|100|0|0|0|0|0|0")]
    [InlineData("1", "100", "0", "0", TaxBasis.Inclusive, TaxMode.Local, "100|0|100|0|0|0|0|100")]
    [InlineData("1", "100.1", "0", "0.1", TaxBasis.Inclusive, TaxMode.Local, "100.1|0|100.00|0.05|0.05|0|0.10|100.10")]
    [InlineData("1", "100", "0", "0.25", TaxBasis.Inclusive, TaxMode.Interstate, "100|0|99.75|0|0|0.25|0.25|100")]
    [InlineData("1", "103", "0", "3", TaxBasis.Inclusive, TaxMode.Local, "103|0|100|1.50|1.50|0|3|103")]
    public void Line_matches_HQ_rules(string qty, string rate, string disc, string slab, TaxBasis basis, TaxMode mode, string expected)
    {
        var q = Quote(qty, rate, disc, slab, basis, mode);
        var e = expected.Split('|').Select(D).ToArray();
        Assert.Equal((e[0], e[1], e[2], e[3], e[4], e[5], e[6], e[7]),
            (q.Gross, q.Discount, q.Taxable, q.Cgst, q.Sgst, q.Igst, q.AggregateTax, q.Net));
        // HQ derives the taxable value from the net, so the line always foots.
        Assert.Equal(q.Net, q.Taxable + q.ComponentTax);
        if (mode == TaxMode.Interstate) Assert.Equal((0m, 0m), (q.Cgst, q.Sgst));
        else Assert.Equal(0m, q.Igst);
    }

    [Fact]
    public void Slab_rates_split_evenly_for_local_bills()
    {
        Assert.Equal(new TaxRates(0.05m, 0.05m, 0.1m), TaxRates.FromSlab(0.1m));
        Assert.Equal(new TaxRates(0.125m, 0.125m, 0.25m), TaxRates.FromSlab(0.25m));
        Assert.Equal(new TaxRates(1.5m, 1.5m, 3m), TaxRates.FromSlab(3m));
        Assert.Equal(3m, TaxRates.FromSlab(3m).Total(TaxMode.Local));
        Assert.Equal(3m, TaxRates.FromSlab(3m).Total(TaxMode.Interstate));
    }

    [Fact]
    public void Interstate_ignores_unequal_local_halves()
    {
        var q = GstCalculator.PriceLine(new LineInput(1, 100, 0, new TaxRates(9, 6, 18)), TaxBasis.Exclusive, TaxMode.Interstate);
        Assert.Equal((18m, 18m, 118m), (q.TaxRate, q.Igst, q.Net));
    }

    [Theory]
    [InlineData("0", "10", "0", "9", "9", "18", TaxMode.Local, "INVALID_QUANTITY", FailureKind.Invalid)]
    [InlineData("-1", "10", "0", "9", "9", "18", TaxMode.Local, "INVALID_QUANTITY", FailureKind.Invalid)]
    [InlineData("1.00001", "10", "0", "9", "9", "18", TaxMode.Local, "HQ_PRECISION_UNSUPPORTED", FailureKind.Invalid)]
    [InlineData("1", "-0.01", "0", "9", "9", "18", TaxMode.Local, "INVALID_RATE", FailureKind.Invalid)]
    [InlineData("1", "1.00001", "0", "9", "9", "18", TaxMode.Local, "HQ_PRECISION_UNSUPPORTED", FailureKind.Invalid)]
    [InlineData("1", "10", "-1", "9", "9", "18", TaxMode.Local, "INVALID_DISCOUNT", FailureKind.Invalid)]
    [InlineData("1", "10", "100.01", "9", "9", "18", TaxMode.Local, "INVALID_DISCOUNT", FailureKind.Invalid)]
    [InlineData("1", "10", "1.00001", "9", "9", "18", TaxMode.Local, "INVALID_DISCOUNT", FailureKind.Invalid)]
    [InlineData("1", "10", "0", "9", "6", "15", TaxMode.Local, "HQ_TAX_PROFILE_UNSUPPORTED", FailureKind.Unsupported)]
    [InlineData("1", "10", "0", "-9", "-9", "18", TaxMode.Local, "INVALID_TAX", FailureKind.Invalid)]
    [InlineData("1", "10", "0", "0", "0", "101", TaxMode.Interstate, "INVALID_TAX", FailureKind.Invalid)]
    [InlineData("1000000", "9999999", "0", "0", "0", "0", TaxMode.Local, "AMOUNT_TOO_LARGE", FailureKind.Invalid)]
    public void Bad_lines_are_refused(string qty, string rate, string disc, string cgst, string sgst, string igst, TaxMode mode, string code, FailureKind kind)
    {
        var e = Assert.Throws<HqException>(() => GstCalculator.PriceLine(
            new LineInput(D(qty), D(rate), D(disc), new TaxRates(D(cgst), D(sgst), D(igst))), TaxBasis.Exclusive, mode));
        Assert.Equal((code, kind), (e.Code, e.Kind));
    }

    [Fact]
    public void Exactly_100_percent_discount_and_4_place_precision_are_accepted()
    {
        var q = GstCalculator.PriceLine(new LineInput(1.2345m, 9.8765m, 100m, TaxRates.FromSlab(18)), TaxBasis.Exclusive, TaxMode.Local);
        Assert.Equal((12.19m, 12.19m, 0m), (q.Gross, q.Discount, q.Net));
    }

    [Theory]
    [InlineData("99.50", RoundingDirection.Nearest, "1", "100", "0.50")]
    [InlineData("98.50", RoundingDirection.Nearest, "1", "99", "0.50")]
    [InlineData("99.49", RoundingDirection.Nearest, "1", "99", "-0.49")]
    [InlineData("99.01", RoundingDirection.Up, "1", "100", "0.99")]
    [InlineData("99.99", RoundingDirection.Down, "1", "99", "-0.99")]
    [InlineData("99.37", RoundingDirection.Nearest, "0", "99.37", "0")]
    [InlineData("99.25", RoundingDirection.Nearest, "0.5", "99.5", "0.25")]
    [InlineData("100.00", RoundingDirection.Up, "1", "100", "0")]
    public void Bill_round_off_is_payable_minus_line_nets(string rate, RoundingDirection direction, string step, string payable, string roundOff)
    {
        var quote = GstCalculator.Price([new LineInput(1, D(rate), 0, TaxRates.FromSlab(0))],
            new PricingPolicy(TaxBasis.Exclusive, direction, D(step)), TaxMode.Local);
        Assert.Equal((D(rate), D(payable), D(roundOff)), (quote.BeforeRounding, quote.Payable, quote.RoundOff));
        Assert.Equal(quote.Payable, quote.BeforeRounding + quote.RoundOff);
    }

    [Fact]
    public void Bill_totals_and_slabs_are_sums_of_lines()
    {
        var quote = GstCalculator.Price(
            [new LineInput(1, 100, 0, TaxRates.FromSlab(18)), new LineInput(1, 100, 0, TaxRates.FromSlab(5)), new LineInput(2, 25, 0, TaxRates.FromSlab(18.00m))],
            new PricingPolicy(TaxBasis.Exclusive, RoundingDirection.Nearest, 1), TaxMode.Local);
        Assert.Equal((4m, 250m, 16m, 16m, 0m, 282m, 282m, 0m),
            (quote.Quantity, quote.Taxable, quote.Cgst, quote.Sgst, quote.Igst, quote.BeforeRounding, quote.Payable, quote.RoundOff));
        Assert.Equal(quote.BeforeRounding, quote.Taxable + quote.TotalTax);
        // 18 and 18.00 are one slab; slabs are ordered by rate.
        Assert.Equal([new TaxSlab(5, 100, 2.5m, 2.5m, 0), new TaxSlab(18, 150, 13.5m, 13.5m, 0)], quote.Slabs);
    }

    [Fact]
    public void Line_errors_name_the_line()
    {
        var e = Assert.Throws<HqException>(() => GstCalculator.Price(
            [new LineInput(1, 10, 0, TaxRates.FromSlab(18)), new LineInput(0, 10, 0, TaxRates.FromSlab(18))],
            PricingPolicy.ExclusiveUnrounded, TaxMode.Local));
        Assert.Equal("INVALID_QUANTITY", e.Code);
        Assert.StartsWith("Line 2: ", e.Message);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(501, "0")]
    [InlineData(1, "-1")]
    [InlineData(1, "100.01")]
    public void Bill_shape_and_policy_are_checked(int lines, string step)
    {
        var e = Assert.Throws<HqException>(() => GstCalculator.Price(
            Enumerable.Repeat(new LineInput(1, 10, 0, TaxRates.FromSlab(18)), lines).ToArray(),
            new PricingPolicy(TaxBasis.Exclusive, RoundingDirection.Nearest, D(step)), TaxMode.Local));
        Assert.Contains(e.Code, new[] { "INVALID_LINES", "INVALID_POLICY" });
    }

    [Fact]
    public void Bill_total_above_HQs_limit_is_refused()
    {
        var big = new LineInput(1, 999_999_999_999.99m, 0, TaxRates.FromSlab(0));
        var e = Assert.Throws<HqException>(() => GstCalculator.Price([big, big], PricingPolicy.ExclusiveUnrounded, TaxMode.Local));
        Assert.Equal("AMOUNT_TOO_LARGE", e.Code);
    }

    [Theory]
    [InlineData("3", "33.33", "12.5", "18", TaxBasis.Exclusive, TaxMode.Local)]
    [InlineData("10", "491", "45", "18", TaxBasis.Inclusive, TaxMode.Local)]
    [InlineData("7", "12.3456", "0", "5", TaxBasis.Inclusive, TaxMode.Interstate)]
    [InlineData("1", "250", "10", "0.25", TaxBasis.Exclusive, TaxMode.Local)]
    public void RateForNet_finds_a_rate_that_reproduces_the_printed_amount(string qty, string rate, string disc, string slab, TaxBasis basis, TaxMode mode)
    {
        var net = Quote(qty, rate, disc, slab, basis, mode).Net;
        var found = GstCalculator.RateForNet(D(qty), net, D(disc), TaxRates.FromSlab(D(slab)), basis, mode);
        Assert.NotNull(found);
        Assert.True(Money.Scale(found!.Value) <= 4);
        Assert.Equal(net, Quote(qty, Money.Plain(found.Value), disc, slab, basis, mode).Net);
    }

    [Theory]
    [InlineData("0", "10", "0")]
    [InlineData("1", "0", "0")]
    [InlineData("1", "-5", "0")]
    [InlineData("1", "10", "100")]
    [InlineData("1000", "1.01", "0")]   // 0.0010 gives 1.00 and 0.0011 gives 1.10: no 4-place rate prints 1.01
    public void RateForNet_returns_null_when_no_rate_fits(string qty, string net, string disc) =>
        Assert.Null(GstCalculator.RateForNet(D(qty), D(net), D(disc), TaxRates.FromSlab(0), TaxBasis.Exclusive, TaxMode.Local));
}
