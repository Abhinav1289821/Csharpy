using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Reports;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.Reports;

/// <summary>Financial-year derivation, period boundaries and report date ranges (database-free).</summary>
public sealed class ReportsPeriodTests
{
    private static HqFirm Firm(string database, DateOnly? start = null, DateOnly? end = null) =>
        new(new FirmOptions { Id = "mbm", Name = "MBM", Database = database, PeriodStart = start, PeriodEnd = end }, new HqOptions(), new ProcedureCatalog());

    [Fact]
    public void Year_comes_from_the_database_name()
    {
        var f = Firm("ISTMBM26");
        Assert.Equal(new DateOnly(2026, 4, 1), f.PeriodStart);
        Assert.Equal(new DateOnly(2027, 3, 31), f.PeriodEnd);
    }

    [Fact]
    public void Period_boundaries_are_inclusive_and_nothing_beyond()
    {
        var f = Firm("ISTMBM26");
        f.RequireInPeriod(new DateOnly(2026, 4, 1));
        f.RequireInPeriod(new DateOnly(2027, 3, 31));
        Assert.Equal("DATE_OUTSIDE_PERIOD", Assert.Throws<HqException>(() => f.RequireInPeriod(new DateOnly(2026, 3, 31))).Code);
        Assert.Equal("DATE_OUTSIDE_PERIOD", Assert.Throws<HqException>(() => f.RequireInPeriod(new DateOnly(2027, 4, 1))).Code);
    }

    [Fact]
    public void Configured_year_wins_over_the_name()
    {
        var f = Firm("MKHQ_DEMO_MBM", new DateOnly(2026, 4, 1), new DateOnly(2027, 3, 31));
        Assert.Equal(new DateOnly(2027, 3, 31), f.PeriodEnd);
    }

    [Fact]
    public void Only_a_start_configured_gives_a_year_from_it()
    {
        // Before: a lone PeriodStart was ignored for the name's year (ISTMBM26 → 2026-27 although 2027-04-01 was set), or
        // refused outright for a name without digits.
        var f = Firm("ISTMBM26", start: new DateOnly(2027, 4, 1));
        Assert.Equal(new DateOnly(2027, 4, 1), f.PeriodStart);
        Assert.Equal(new DateOnly(2028, 3, 31), f.PeriodEnd);
        var g = Firm("MKHQ_DEMO_MBM", start: new DateOnly(2026, 4, 1));
        Assert.Equal(new DateOnly(2027, 3, 31), g.PeriodEnd);
    }

    [Fact]
    public void Only_an_end_configured_gives_the_year_before_it()
    {
        var f = Firm("MKHQ_DEMO_MP", end: new DateOnly(2028, 3, 31)); // a leap-year February inside
        Assert.Equal(new DateOnly(2027, 4, 1), f.PeriodStart);
    }

    [Fact]
    public void A_start_after_the_end_is_refused_at_start_up()
    {
        // Otherwise every bill date is "outside the financial year" with no hint why.
        var e = Assert.Throws<HqException>(() => Firm("ISTMBM26", new DateOnly(2027, 4, 1), new DateOnly(2027, 3, 31)));
        Assert.Equal("INVALID_CONFIGURATION", e.Code);
    }

    [Fact]
    public void A_name_without_a_year_needs_the_period_configured()
    {
        Assert.Equal("INVALID_CONFIGURATION", Assert.Throws<HqException>(() => Firm("MKHQ_DEMO_MBM")).Code);
    }

    [Fact]
    public void Report_range_is_half_open_so_the_last_day_counts_whole()
    {
        // "<= @to" at midnight dropped anything HQ stamped with a time on the last day.
        var (from, until) = ReportService.Range(new DateOnly(2027, 3, 31), new DateOnly(2027, 3, 31));
        Assert.Equal(new DateTime(2027, 3, 31), from);
        Assert.Equal(new DateTime(2027, 4, 1), until);
        Assert.True(new DateTime(2027, 3, 31, 18, 30, 0) < until);
    }

    [Fact]
    public void Report_range_backwards_is_refused_not_silently_empty()
    {
        var e = Assert.Throws<HqException>(() => ReportService.Range(new DateOnly(2026, 5, 2), new DateOnly(2026, 5, 1)));
        Assert.Equal("INVALID_DATE_RANGE", e.Code);
        Assert.Equal(FailureKind.Invalid, e.Kind);
    }
}
