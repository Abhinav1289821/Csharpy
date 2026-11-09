namespace MK.Hq.Domain.Gst;

/// <summary>HQ stores "Exclusive=1" on a header when prices INCLUDE tax. We name it for what it means.</summary>
public enum TaxBasis { Exclusive = 0, Inclusive = 1 }

/// <summary>Local = CGST+SGST (TTYPES.Firstor=1); Interstate = IGST (Firstor=0).</summary>
public enum TaxMode { Local = 1, Interstate = 0 }

public sealed record PricingPolicy(TaxBasis Basis, RoundingDirection Rounding, decimal RoundingStep)
{
    public static readonly PricingPolicy ExclusiveUnrounded = new(TaxBasis.Exclusive, RoundingDirection.Nearest, 0m);
}

/// <summary>GST rates as configured on an HQ item (IQItems_Mast.CGST/SGST/IGST).</summary>
public sealed record TaxRates(decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Total(TaxMode mode) => mode == TaxMode.Interstate ? Igst : Cgst + Sgst;
    public static TaxRates FromSlab(decimal slab) => new(slab / 2, slab / 2, slab);
}

public sealed record LineInput(decimal Quantity, decimal Rate, decimal DiscountPercent, TaxRates Taxes);

public sealed record LineQuote(
    decimal Quantity,
    decimal Rate,
    decimal DiscountPercent,
    /// <summary>R2(qty × rate) — HQ "Value".</summary>
    decimal Gross,
    /// <summary>HQ "DiscValue".</summary>
    decimal Discount,
    /// <summary>HQ "GrossValue": the taxable value.</summary>
    decimal Taxable,
    decimal TaxRate,
    decimal CgstRate,
    decimal Cgst,
    decimal SgstRate,
    decimal Sgst,
    decimal IgstRate,
    decimal Igst,
    /// <summary>HQ "TaxValue": combined tax, rounded independently of its components in exclusive mode.</summary>
    decimal AggregateTax,
    /// <summary>HQ "NetValue".</summary>
    decimal Net)
{
    public decimal ComponentTax => Cgst + Sgst + Igst;
}

public sealed record TaxSlab(decimal Rate, decimal Taxable, decimal Cgst, decimal Sgst, decimal Igst)
{
    public decimal Tax => Cgst + Sgst + Igst;
}

public sealed record DocumentQuote(
    PricingPolicy Policy,
    TaxMode Mode,
    IReadOnlyList<LineQuote> Lines,
    decimal Quantity,
    decimal Gross,
    decimal Discount,
    decimal Taxable,
    decimal Cgst,
    decimal Sgst,
    decimal Igst,
    decimal BeforeRounding,
    decimal RoundOff,
    decimal Payable,
    IReadOnlyList<TaxSlab> Slabs)
{
    public decimal TotalTax => Cgst + Sgst + Igst;
}

/// <summary>
/// HQ's GST arithmetic, reproduced exactly from FrmInwards.Calc_GridTotals_EachRow and verified
/// against every stored sale, purchase and order line in both firms (see GstParityTests).
/// <list type="bullet">
/// <item>Inclusive: tax is extracted from the unrounded qty×rate less the rounded discount; each GST
/// half is rounded on its own and the taxable value is derived from the net.</item>
/// <item>Exclusive: each component is rounded on the discounted value, while the combined
/// "TaxValue" and the net are rounded from the unsplit rate.</item>
/// </list>
/// </summary>
public static class GstCalculator
{
    public static readonly IReadOnlySet<decimal> Slabs = new HashSet<decimal> { 0m, 0.1m, 0.25m, 1m, 1.5m, 3m, 5m, 6m, 7.5m, 12m, 18m, 28m, 40m };

    public static LineQuote PriceLine(LineInput line, TaxBasis basis, TaxMode mode, int maxQuantityScale = 4, int maxRateScale = 4)
    {
        Guard.Require(line.Quantity > 0, "INVALID_QUANTITY", "Quantity must be greater than zero");
        Guard.Require(Money.Scale(line.Quantity) <= maxQuantityScale, "HQ_PRECISION_UNSUPPORTED", $"HQ stores quantity with {maxQuantityScale} decimal places");
        Guard.Require(line.Rate >= 0, "INVALID_RATE", "Rate cannot be negative");
        Guard.Require(Money.Scale(line.Rate) <= maxRateScale, "HQ_PRECISION_UNSUPPORTED", $"HQ stores rate with {maxRateScale} decimal places");
        Guard.Require(line.DiscountPercent is >= 0 and <= 100, "INVALID_DISCOUNT", "Discount must be between 0% and 100%");
        Guard.Require(Money.Scale(line.DiscountPercent) <= 4, "INVALID_DISCOUNT", "Discount has too many decimal places");

        var t = line.Taxes;
        decimal cgstRate = 0, sgstRate = 0, igstRate = 0;
        if (mode == TaxMode.Interstate)
        {
            igstRate = t.Igst;
        }
        else
        {
            Guard.Supported(t.Cgst == t.Sgst, "HQ_TAX_PROFILE_UNSUPPORTED", "HQ GST pricing requires equal CGST and SGST rates");
            cgstRate = t.Cgst;
            sgstRate = t.Sgst;
        }
        var totalRate = cgstRate + sgstRate + igstRate;
        Guard.Require(totalRate is >= 0 and <= 100, "INVALID_TAX", "Tax rate must be between 0% and 100%");

        var product = line.Quantity * line.Rate;
        var gross = Money.Round2(product);
        Guard.Require(gross <= Money.MaxAmount, "AMOUNT_TOO_LARGE", "Line value is too large");
        var discount = Money.Round2(gross * line.DiscountPercent / 100m);

        decimal cgst, sgst, igst, aggregate, net, taxable;
        if (basis == TaxBasis.Inclusive)
        {
            var source = product - discount;
            var raw = totalRate == 0 ? 0 : source * totalRate / (100m + totalRate);
            if (mode == TaxMode.Interstate)
            {
                igst = Money.Round2(raw);
                cgst = sgst = 0;
            }
            else
            {
                cgst = sgst = Money.Round2(raw / 2m);
                igst = 0;
            }
            aggregate = cgst + sgst + igst;
            net = Money.Round2(source);
            taxable = net - aggregate;
        }
        else
        {
            var source = gross - discount;
            cgst = Money.Round2(source * cgstRate / 100m);
            sgst = Money.Round2(source * sgstRate / 100m);
            igst = Money.Round2(source * igstRate / 100m);
            aggregate = Money.Round2(source * totalRate / 100m);
            net = Money.Round2(source + aggregate);
            taxable = net - (cgst + sgst + igst);
        }

        return new LineQuote(line.Quantity, line.Rate, line.DiscountPercent, gross, discount, taxable,
            totalRate, cgstRate, cgst, sgstRate, sgst, igstRate, igst, aggregate, net);
    }

    public static DocumentQuote Price(IReadOnlyList<LineInput> lines, PricingPolicy policy, TaxMode mode, int maxQuantityScale = 4, int maxRateScale = 4)
    {
        Guard.Require(lines.Count is > 0 and <= 500, "INVALID_LINES", "A document needs between 1 and 500 lines");
        Guard.Require(policy.RoundingStep is >= 0 and <= 100, "INVALID_POLICY", "Rounding step must be between 0 and 100 rupees");

        var quoted = new LineQuote[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            try { quoted[i] = PriceLine(lines[i], policy.Basis, mode, maxQuantityScale, maxRateScale); }
            catch (HqException e) { throw new HqException(e.Code, $"Line {i + 1}: {e.Message}", e.Kind, new { line = i + 1 }); }
        }

        var before = quoted.Sum(l => l.Net);
        var payable = Money.RoundToStep(before, policy.RoundingStep, policy.Rounding);
        Guard.Require(payable <= Money.MaxAmount, "AMOUNT_TOO_LARGE", "Document total is too large");

        var slabs = quoted
            .GroupBy(l => l.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g => new TaxSlab(g.Key, g.Sum(l => l.Taxable), g.Sum(l => l.Cgst), g.Sum(l => l.Sgst), g.Sum(l => l.Igst)))
            .ToArray();

        return new DocumentQuote(policy, mode, quoted,
            Quantity: quoted.Sum(l => l.Quantity),
            Gross: quoted.Sum(l => l.Gross),
            Discount: quoted.Sum(l => l.Discount),
            Taxable: quoted.Sum(l => l.Taxable),
            Cgst: quoted.Sum(l => l.Cgst),
            Sgst: quoted.Sum(l => l.Sgst),
            Igst: quoted.Sum(l => l.Igst),
            BeforeRounding: before,
            RoundOff: payable - before,
            Payable: payable,
            Slabs: slabs);
    }

    /// <summary>
    /// Solves for the unit rate that produces a printed line net (used when a supplier bill prints only
    /// the line amount). Returns null when no 4-place rate reproduces it exactly.
    /// </summary>
    public static decimal? RateForNet(decimal quantity, decimal net, decimal discountPercent, TaxRates taxes, TaxBasis basis, TaxMode mode)
    {
        if (quantity <= 0 || net <= 0 || discountPercent >= 100) return null;
        var rate = taxes.Total(mode);
        var guess = basis == TaxBasis.Inclusive
            ? net / quantity / (1 - discountPercent / 100m)
            : net / (1 + rate / 100m) / quantity / (1 - discountPercent / 100m);
        var start = Money.Round(guess, 4);
        for (var delta = 0m; delta <= 0.0050m; delta += 0.0001m)
        {
            foreach (var candidate in new[] { start + delta, start - delta })
            {
                if (candidate < 0) continue;
                var q = PriceLine(new LineInput(quantity, candidate, discountPercent, taxes), basis, mode);
                if (q.Net == net) return candidate;
            }
        }
        return null;
    }
}
