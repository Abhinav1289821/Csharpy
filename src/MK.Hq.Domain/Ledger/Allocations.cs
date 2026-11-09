namespace MK.Hq.Domain.Ledger;

/// <summary>An IQDayBook row as seen by bill-wise settlement (BL_adjusted is the allocated amount).</summary>
public sealed record SettlementEntry(long Id, long AccountId, char Type, decimal Amount, decimal Allocated)
{
    public bool IsDebit => Type is 'D' or 'P';
    public decimal Open => Amount - Allocated;
}

/// <summary>An ADJUST row: <see cref="Source"/> settles <see cref="Target"/> for <see cref="Amount"/>.</summary>
public sealed record AllocationLink(long Source, long Target, decimal Amount);

public enum AllocationDisposition
{
    /// <summary>Keep payments attached: move each settled amount onto the replacement party row.</summary>
    Preserve,
    /// <summary>Detach payments; they become unallocated (on-account) again.</summary>
    Release,
}

public sealed record AllocationChange(IReadOnlyList<AllocationLink> Links, IReadOnlyList<(long EntryId, decimal Allocated)> Counters, decimal Released);

/// <summary>
/// Reconciles bill-wise settlement when a document's ledger is replaced (amend) or removed (delete).
/// It refuses to guess through inconsistent legacy data: every counter must equal its actual links.
/// </summary>
public static class AllocationPlanner
{
    public static AllocationChange Plan(
        IReadOnlyList<SettlementEntry> existing,
        IReadOnlyList<SettlementEntry> related,
        IReadOnlyList<SettlementEntry> replacement,
        IReadOnlyList<AllocationLink> links,
        AllocationDisposition disposition)
    {
        static void Fail(bool ok, string message)
        {
            if (!ok) throw HqException.Conflict("HQ_ALLOCATION_CONFLICT", message);
        }

        var old = existing.ToDictionary(e => e.Id);
        var rel = related.ToDictionary(e => e.Id);
        Fail(old.Keys.All(k => !rel.ContainsKey(k)), "A related ledger row belongs to the edited bill");
        Fail(replacement.All(r => r.Allocated == 0), "Replacement ledger must start without allocations");
        foreach (var e in existing.Concat(related)) Fail(e.Allocated >= 0 && e.Allocated <= e.Amount, "Allocation counter exceeds its ledger amount");

        var touched = new Dictionary<long, decimal>();
        foreach (var link in links)
        {
            Fail(link.Amount > 0, "Allocation amount must be positive");
            var a = old.GetValueOrDefault(link.Source) ?? rel.GetValueOrDefault(link.Source);
            var b = old.GetValueOrDefault(link.Target) ?? rel.GetValueOrDefault(link.Target);
            Fail(a is not null && b is not null && a.Id != b.Id, "An allocation has a missing endpoint");
            Fail(old.ContainsKey(a!.Id) != old.ContainsKey(b!.Id), "Each allocation must connect this document to another one");
            Fail(a.AccountId == b.AccountId && a.IsDebit != b.IsDebit, "Allocation accounts or directions disagree");
            touched[a.Id] = touched.GetValueOrDefault(a.Id) + link.Amount;
            touched[b.Id] = touched.GetValueOrDefault(b.Id) + link.Amount;
        }
        foreach (var e in existing) Fail(touched.GetValueOrDefault(e.Id) == e.Allocated, "Bill allocation counter does not match its actual links");
        foreach (var e in related) Fail(touched.GetValueOrDefault(e.Id) <= e.Allocated, "A payment's allocation counter is smaller than its links to this bill");

        var counters = replacement.ToDictionary(r => r.Id, _ => 0m);
        var mapped = new Dictionary<long, long>();
        if (disposition == AllocationDisposition.Preserve)
        {
            foreach (var e in existing)
            {
                var allocated = touched.GetValueOrDefault(e.Id);
                if (allocated == 0) continue;
                var targets = replacement.Where(r => r.AccountId == e.AccountId && r.Type == e.Type).ToList();
                Fail(targets.Count == 1, "A settled bill needs exactly one replacement row for the same party and side");
                mapped[e.Id] = targets[0].Id;
                counters[targets[0].Id] += allocated;
            }
            foreach (var r in replacement)
                Fail(counters[r.Id] <= r.Amount, "The corrected bill is smaller than the payments already settled against it");
            return new AllocationChange(
                links.Select(l => new AllocationLink(mapped.GetValueOrDefault(l.Source, l.Source), mapped.GetValueOrDefault(l.Target, l.Target), l.Amount)).ToArray(),
                counters.OrderBy(c => c.Key).Select(c => (c.Key, c.Value)).ToArray(),
                0m);
        }

        foreach (var e in related)
            if (touched.TryGetValue(e.Id, out var removed)) counters[e.Id] = e.Allocated - removed;
        return new AllocationChange([], counters.OrderBy(c => c.Key).Select(c => (c.Key, c.Value)).ToArray(), links.Sum(l => l.Amount));
    }
}
