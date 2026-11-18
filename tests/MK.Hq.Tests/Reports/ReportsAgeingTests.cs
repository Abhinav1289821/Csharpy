using MK.Hq.Engine.Reports;

namespace MK.Hq.Tests.Reports;

/// <summary>Outstanding ageing (database-free): the age columns are a breakdown of the balance beside them.</summary>
public sealed class ReportsAgeingTests
{
    private static readonly DateOnly Today = new(2026, 11, 18);

    private static AgeingEntry Bill(int daysAgo, decimal amount, decimal? open = null, long order = 0) =>
        new(Today.AddDays(-daysAgo), order, amount, open ?? amount);

    [Fact]
    public void Non_bill_wise_party_ages_only_what_it_owes_newest_first()
    {
        // Three bills of 100, 150 owed: the payments settled the oldest. The old query aged every unallocated debit (300).
        var age = Ageing.Age(150m, [Bill(200, 100), Bill(100, 100), Bill(10, 100)], Today);
        Assert.Equal(100m, age.Current);
        Assert.Equal(50m, age.Days90);
        Assert.Equal(0m, age.Older);
        Assert.Equal(150m, age.Total);
        Assert.Equal(Today.AddDays(-100), age.OldestBill);
    }

    [Fact]
    public void Bill_wise_allocation_keeps_an_unpaid_old_bill_old()
    {
        // HQ's allocation says the new bill is paid and the old one is not: the old one is what is due.
        var age = Ageing.Age(100m, [Bill(200, 100, open: 100), Bill(10, 100, open: 0)], Today);
        Assert.Equal(100m, age.Older);
        Assert.Equal(0m, age.Current);
        Assert.Equal(Today.AddDays(-200), age.OldestBill);
    }

    [Fact]
    public void On_account_receipt_reduces_the_oldest_open_bills()
    {
        // Two open bills (200) but a 50 receipt not allocated to either: 150 owed, the newest bill whole.
        var age = Ageing.Age(150m, [Bill(200, 100), Bill(10, 100)], Today);
        Assert.Equal(100m, age.Current);
        Assert.Equal(50m, age.Older);
        Assert.Equal(150m, age.Total);
    }

    [Fact]
    public void Allocation_that_does_not_explain_the_balance_falls_back_to_newest_first()
    {
        // Every bill marked settled, yet 80 is owed (allocations that do not tally): the newest allocated amounts carry it.
        var age = Ageing.Age(80m, [Bill(100, 100, open: 0), Bill(40, 50, open: 0)], Today);
        Assert.Equal(50m, age.Days30);
        Assert.Equal(30m, age.Days90);
        Assert.Equal(80m, age.Total);
    }

    [Fact]
    public void Owed_with_nothing_to_date_it_goes_to_the_oldest_column()
    {
        var age = Ageing.Age(70m, [], Today);
        Assert.Equal(70m, age.Older);
        Assert.Null(age.OldestBill);
        Assert.Equal(70m, age.Total);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-25)]
    [InlineData(0.004)]
    public void Nothing_owed_nothing_aged(decimal due)
    {
        var age = Ageing.Age(due, [Bill(10, 100)], Today);
        Assert.Equal(0m, age.Total);
        Assert.Null(age.OldestBill);
    }

    [Theory]
    [InlineData(0, "current")]
    [InlineData(29, "current")]
    [InlineData(30, "30")]
    [InlineData(59, "30")]
    [InlineData(60, "60")]
    [InlineData(89, "60")]
    [InlineData(90, "90")]
    [InlineData(179, "90")]
    [InlineData(180, "older")]
    [InlineData(-3, "current")] // post-dated
    public void Column_boundaries_match_the_screen_headings(int daysAgo, string column)
    {
        var age = Ageing.Age(10m, [Bill(daysAgo, 10)], Today);
        var got = new Dictionary<string, decimal> { ["current"] = age.Current, ["30"] = age.Days30, ["60"] = age.Days60, ["90"] = age.Days90, ["older"] = age.Older };
        Assert.Equal(10m, got[column]);
        Assert.Equal(10m, got.Values.Sum());
    }

    [Fact]
    public void Same_day_entries_are_taken_in_posting_order_newest_first()
    {
        var day = Today.AddDays(-45);
        var age = Ageing.Age(30m, [new AgeingEntry(day, 1, 100, 100), new AgeingEntry(day, 2, 20, 20), Bill(10, 10, order: 3)], Today);
        Assert.Equal(10m, age.Current);
        Assert.Equal(20m, age.Days30);
    }

    [Fact]
    public void Overallocated_entries_never_count_negative()
    {
        // BL_adjusted above the amount (an allocation left behind by an edit): open is negative, which must not offset other bills.
        var age = Ageing.Age(100m, [Bill(10, 100, open: -40), Bill(100, 100, open: 100)], Today);
        Assert.Equal(100m, age.Days90);
        Assert.Equal(0m, age.Current);
    }
}
