namespace MK.Hq.Engine.Reports;

/// <summary>One ledger entry that adds to what a party owes (a sale or payment to a customer; a purchase or receipt from a
/// supplier). <paramref name="Open"/> is the part HQ's bill-wise allocation still shows unpaid; for a party HQ does not keep
/// bill by bill it is the whole amount.</summary>
public sealed record AgeingEntry(DateOnly Date, long Order, decimal Amount, decimal Open);

public sealed record AgeingResult(decimal Current, decimal Days30, decimal Days60, decimal Days90, decimal Older, DateOnly? OldestBill)
{
    public decimal Total => Current + Days30 + Days60 + Days90 + Older;
}

/// <summary>
/// Splits what a party owes by age. The columns are read as a breakdown of the balance beside them, so they must add up
/// to it: the balance is the ledger's (opening plus every entry), and it is laid on the newest entries first, because a
/// payment settles the oldest bills first. Bill-wise parties use HQ's allocation first (an older bill left unpaid stays
/// old); whatever the allocation cannot explain (on-account receipts, allocations that do not tally) falls back to the
/// same newest-first rule over the already-allocated parts. Before this, every unallocated debit was aged, so a
/// non-bill-wise customer showed every bill of the year as due (MBM demo copy: 35.5 lakh aged against 23.9 lakh owed).
/// </summary>
public static class Ageing
{
    public static AgeingResult Age(decimal due, IEnumerable<AgeingEntry> entries, DateOnly today)
    {
        if (due <= 0.004m) return new AgeingResult(0, 0, 0, 0, 0, null);
        var newestFirst = entries.OrderByDescending(e => e.Date).ThenByDescending(e => e.Order).ToList();
        var left = due;
        var parts = new List<(DateOnly Date, decimal Amount)>();
        // Pass 1: what HQ's allocation shows open. Pass 2: the allocated remainder, only if the balance needs it.
        foreach (var pass in new Func<AgeingEntry, decimal>[] { e => e.Open, e => e.Amount - Math.Max(e.Open, 0) })
            foreach (var e in newestFirst)
            {
                if (left <= 0) break;
                var take = Math.Min(Math.Max(pass(e), 0), left);
                if (take <= 0) continue;
                parts.Add((e.Date, take));
                left -= take;
            }
        decimal current = 0, d30 = 0, d60 = 0, d90 = 0, older = 0;
        foreach (var (date, amount) in parts)
        {
            var age = today.DayNumber - date.DayNumber;
            if (age < 30) current += amount;
            else if (age < 60) d30 += amount;
            else if (age < 90) d60 += amount;
            else if (age < 180) d90 += amount;
            else older += amount;
        }
        // Owed with no entry to date it (cannot happen while HQ's opening rows exist): oldest column, so the total still agrees.
        older += Math.Max(left, 0);
        return new AgeingResult(current, d30, d60, d90, older, parts.Count == 0 ? null : parts.Min(p => p.Date));
    }
}
