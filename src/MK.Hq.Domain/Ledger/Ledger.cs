namespace MK.Hq.Domain.Ledger;

/// <summary>
/// One IQDayBook row as HQ stores it. HQ's TYPE column encodes both side and settlement:
/// D/C for credit or bank documents (debit/credit), P/R for cash documents (cash paid out / received,
/// i.e. the counter account is debited / credited and Cash is the implicit contra).
/// </summary>
public sealed record LedgerRow(long AccountId, char Type, long ContraId, decimal Amount)
{
    public bool IsDebit => Type is 'D' or 'P';
    public decimal Signed => IsDebit ? Amount : -Amount;
}

public enum Settlement { Cash, Credit, Bank }

/// <summary>A document charge row (IQDoc_Chrgs) that HQ posts to the ledger.</summary>
/// <param name="IsAdd">IQCHRG_TYPES.ChTp_Op = 1 ("Add"); otherwise HQ treats it as "Less".</param>
public sealed record ChargePosting(long AccountId, decimal SignedAmount, bool IsAdd);

/// <summary>
/// HQ's invoice posting (FrmInwards.AccountPosting_GST + SaveDaybookActwise_Summary), reproduced as a
/// pure function:
/// <list type="number">
/// <item>Item base and tax accounts are accumulated per account and posted on the document's side
/// (debit for purchases, credit for sales).</item>
/// <item>Each non-zero charge is posted by HQ's Add/Less rule. For the round-off charge, which both
/// firms configure as "Less", this puts a negative sale round-off on the credit side; HQ's own
/// bills show that, so the engine matches it rather than "correcting" HQ's books.</item>
/// <item>The party row (credit and bank documents) carries the bill amount.</item>
/// <item>HQ then re-reads its rows grouped by (account, contra), nets them and rewrites them in that
/// order — which is why HQ ledgers are always sorted by account.</item>
/// </list>
/// </summary>
public static class InvoiceLedger
{
    public const long HqCashAccountId = 6;

    public static IReadOnlyList<LedgerRow> Plan(
        bool inward,
        Settlement settlement,
        long partyAccountId,
        IEnumerable<(long AccountId, decimal Amount)> lineAmounts,
        IEnumerable<ChargePosting> charges,
        decimal payable)
    {
        var cash = settlement == Settlement.Cash;
        var contra = settlement switch
        {
            Settlement.Cash => HqCashAccountId,
            Settlement.Bank => partyAccountId,
            _ => 0L,
        };

        var posted = new List<(long Account, decimal Signed)>();

        // 1. Items and taxes, aggregated per account in first-seen order, positive amounts only (HQ skips <= 0).
        var perAccount = new Dictionary<long, decimal>();
        var order = new List<long>();
        foreach (var (account, amount) in lineAmounts)
        {
            if (account <= 0 || amount == 0) continue;
            if (!perAccount.ContainsKey(account)) { perAccount[account] = 0; order.Add(account); }
            perAccount[account] += amount;
        }
        foreach (var account in order)
        {
            var amount = perAccount[account];
            if (amount <= 0) continue;
            posted.Add((account, inward ? amount : -amount));
        }

        // 2. Charges by HQ's Add/Less rule.
        foreach (var charge in charges)
        {
            if (charge.SignedAmount == 0 || charge.AccountId <= 0) continue;
            var abs = Math.Abs(charge.SignedAmount);
            bool debit = inward
                ? (charge.IsAdd ? charge.SignedAmount > 0 : charge.SignedAmount < 0)
                : (charge.IsAdd ? charge.SignedAmount < 0 : charge.SignedAmount > 0);
            posted.Add((charge.AccountId, debit ? abs : -abs));
        }

        // 3. Party row for credit and bank settlement; cash documents use Cash as the implicit contra.
        if (!cash && payable > 0)
            posted.Add((partyAccountId, inward ? -payable : payable));

        // 4. SaveDaybookActwise_Summary: SUM(signed) GROUP BY Act_Id, Contra_Id, rewritten in that order.
        return posted
            .GroupBy(p => p.Account)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var net = g.Sum(p => p.Signed);
                var type = net > 0 ? (cash ? 'P' : 'D') : (cash ? 'R' : 'C');
                return new LedgerRow(g.Key, type, contra, Math.Abs(net));
            })
            .ToArray();
    }
}
