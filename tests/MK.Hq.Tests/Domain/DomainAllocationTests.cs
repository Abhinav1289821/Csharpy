using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;

namespace MK.Hq.Tests.Domain;

/// <summary>Bill-wise settlement when a bill's ledger is replaced (amend) or removed (delete).</summary>
public sealed class DomainAllocationTests
{
    // Bill party row 1 (customer 500, debit 1000) settled by receipts 9 (250) and 10 (150, of a 600 receipt used elsewhere too).
    private static readonly SettlementEntry Bill = new(1, 500, 'D', 1000m, 400m);
    private static readonly SettlementEntry Receipt9 = new(9, 500, 'C', 250m, 250m);
    private static readonly SettlementEntry Receipt10 = new(10, 500, 'C', 600m, 450m);
    private static readonly AllocationLink Link9 = new(9, 1, 250m);
    private static readonly AllocationLink Link10 = new(10, 1, 150m);

    [Fact]
    public void Preserve_moves_every_link_to_the_replacement_party_row()
    {
        var change = AllocationPlanner.Plan([Bill], [Receipt9, Receipt10], [new SettlementEntry(-1, 500, 'D', 1200m, 0), new SettlementEntry(-2, 401, 'C', 1200m, 0)],
            [Link9, Link10], AllocationDisposition.Preserve);
        Assert.Equal([new AllocationLink(9, -1, 250m), new AllocationLink(10, -1, 150m)], change.Links);
        Assert.Equal([(-2L, 0m), (-1L, 400m)], change.Counters);
        Assert.Equal(0m, change.Released);
    }

    [Fact]
    public void Preserve_allows_shrinking_exactly_to_the_settled_amount()
    {
        var change = AllocationPlanner.Plan([Bill], [Receipt9, Receipt10], [new SettlementEntry(-1, 500, 'D', 400m, 0)], [Link9, Link10], AllocationDisposition.Preserve);
        Assert.Equal((-1L, 400m), Assert.Single(change.Counters));
    }

    [Fact]
    public void Preserve_maps_links_where_the_bill_is_the_source()
    {
        var bill = new SettlementEntry(1, 500, 'C', 300m, 300m); // a purchase bill settled by a payment
        var payment = new SettlementEntry(9, 500, 'D', 300m, 300m);
        var change = AllocationPlanner.Plan([bill], [payment], [new SettlementEntry(-1, 500, 'C', 300m, 0)], [new AllocationLink(1, 9, 300m)], AllocationDisposition.Preserve);
        Assert.Equal(new AllocationLink(-1, 9, 300m), Assert.Single(change.Links));
    }

    [Fact]
    public void Unsettled_bill_needs_no_replacement_party_row()
    {
        var open = new SettlementEntry(1, 500, 'D', 1000m, 0m);
        var change = AllocationPlanner.Plan([open], [], [], [], AllocationDisposition.Preserve);
        Assert.Empty(change.Links);
        Assert.Empty(change.Counters);
    }

    [Fact]
    public void Release_detaches_links_and_lowers_each_payment_counter()
    {
        var change = AllocationPlanner.Plan([Bill], [Receipt9, Receipt10], [new SettlementEntry(-1, 500, 'D', 900m, 0)], [Link9, Link10], AllocationDisposition.Release);
        Assert.Empty(change.Links);
        Assert.Equal([(-1L, 0m), (9L, 0m), (10L, 300m)], change.Counters);
        Assert.Equal(400m, change.Released);
    }

    [Fact]
    public void Release_on_delete_leaves_untouched_related_rows_alone()
    {
        var other = new SettlementEntry(11, 500, 'C', 50m, 50m); // settled against some other bill
        var change = AllocationPlanner.Plan([Bill], [Receipt9, Receipt10, other], [], [Link9, Link10], AllocationDisposition.Release);
        Assert.DoesNotContain(change.Counters, c => c.EntryId == 11);
    }

    public static TheoryData<string, SettlementEntry[], SettlementEntry[], SettlementEntry[], AllocationLink[], AllocationDisposition> Conflicts => new()
    {
        { "related row is the bill's own", [Bill], [Bill with { }, Receipt9], [], [Link9], AllocationDisposition.Release },
        { "replacement pre-allocated", [Bill], [Receipt9, Receipt10], [new(-1, 500, 'D', 1000m, 1m)], [Link9, Link10], AllocationDisposition.Preserve },
        { "counter above amount", [Bill with { Allocated = 1001m }], [Receipt9], [], [Link9], AllocationDisposition.Release },
        { "negative counter", [Bill], [Receipt9 with { Allocated = -1m }], [], [Link9], AllocationDisposition.Release },
        { "zero link", [Bill with { Allocated = 0m }], [Receipt9], [], [Link9 with { Amount = 0m }], AllocationDisposition.Release },
        { "missing endpoint", [Bill with { Allocated = 250m }], [], [], [Link9], AllocationDisposition.Release },
        { "link inside the bill", [Bill with { Allocated = 0m }, new(2, 500, 'C', 10m, 0m)], [], [], [new(2, 1, 5m)], AllocationDisposition.Release },
        { "self link", [Bill with { Allocated = 0m }], [], [], [new(1, 1, 5m)], AllocationDisposition.Release },
        { "different party", [Bill with { Allocated = 250m }], [Receipt9 with { AccountId = 501 }], [], [Link9], AllocationDisposition.Release },
        { "same direction", [Bill with { Allocated = 250m }], [Receipt9 with { Type = 'D' }], [], [Link9], AllocationDisposition.Release },
        { "cash-type direction is still debit", [Bill with { Allocated = 250m }], [Receipt9 with { Type = 'P' }], [], [Link9], AllocationDisposition.Release },
        { "bill counter disagrees with links", [Bill with { Allocated = 300m }], [Receipt9, Receipt10], [], [Link9, Link10], AllocationDisposition.Release },
        { "payment counter below its links", [Bill], [Receipt9 with { Allocated = 200m, Amount = 250m }, Receipt10], [], [Link9, Link10], AllocationDisposition.Release },
        { "settled bill deleted under preserve", [Bill], [Receipt9, Receipt10], [], [Link9, Link10], AllocationDisposition.Preserve },
        { "party side flipped", [Bill], [Receipt9, Receipt10], [new(-1, 500, 'C', 1000m, 0)], [Link9, Link10], AllocationDisposition.Preserve },
        { "two candidate rows", [Bill], [Receipt9, Receipt10], [new(-1, 500, 'D', 600m, 0), new(-2, 500, 'D', 600m, 0)], [Link9, Link10], AllocationDisposition.Preserve },
        { "replacement smaller than settled", [Bill], [Receipt9, Receipt10], [new(-1, 500, 'D', 399.99m, 0)], [Link9, Link10], AllocationDisposition.Preserve },
    };

    [Theory]
    [MemberData(nameof(Conflicts))]
    public void Inconsistent_or_unsafe_settlement_is_refused(string why, SettlementEntry[] existing, SettlementEntry[] related, SettlementEntry[] replacement,
        AllocationLink[] links, AllocationDisposition disposition)
    {
        var e = Assert.Throws<HqException>(() => AllocationPlanner.Plan(existing, related, replacement, links, disposition));
        Assert.True(e.Code == "HQ_ALLOCATION_CONFLICT" && e.Kind == FailureKind.Conflict, why);
    }
}
