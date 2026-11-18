using System.Globalization;
using System.Reflection;
using MK.Hq.Domain;
using MK.Hq.Engine.Plans;

namespace MK.Hq.Tests.WritePath;

/// <summary>PlanExecutor's value resolution (variables, narration templates, India clock) and its verify comparison,
/// checked without a database through the private helpers the executor runs for every step.</summary>
public sealed class WritePathExecutorTests
{
    private static readonly MethodInfo ResolveValue = typeof(PlanExecutor).GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static,
        [typeof(object), typeof(Dictionary<string, object?>), typeof(DateTime)])!;
    private static readonly MethodInfo SameMethod = typeof(PlanExecutor).GetMethod("Same", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static object? Resolve(object? value, Dictionary<string, object?>? vars = null, DateTime? now = null)
    {
        try { return ResolveValue.Invoke(null, [value, vars ?? new Dictionary<string, object?>(), now ?? new DateTime(2026, 11, 18, 10, 0, 0)]); }
        catch (TargetInvocationException e) { throw e.InnerException!; }
    }

    private static bool Same(object? actual, object expected) => (bool)SameMethod.Invoke(null, [actual, expected])!;

    [Fact]
    public void Interp_fills_variables_once_and_never_re_reads_their_values_as_templates()
    {
        var vars = new Dictionary<string, object?> { ["no"] = 42, ["supp"] = "INV{no}" };
        Assert.Equal("Bill No GST/42 Supp INV{no}", Resolve(new Interp("Bill No GST/{no} Supp {supp}"), vars));
    }

    [Fact]
    public void Interp_leaves_braces_that_are_not_placeholders()
    {
        var vars = new Dictionary<string, object?> { ["no"] = 7 };
        Assert.Equal("{} {a b} { 7", Resolve(new Interp("{} {a b} { {no}"), vars));
    }

    [Fact]
    public void Interp_with_an_unset_variable_is_an_engine_error_not_a_blank()
    {
        var e = Assert.Throws<HqException>(() => Resolve(new Interp("Bill {missing}")));
        Assert.Equal(FailureKind.Internal, e.Kind);
        Assert.Throws<HqException>(() => Resolve(new Ref("missing")));
    }

    [Fact]
    public void Interp_formats_numbers_the_same_on_any_machine_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Amt 1234.50", Resolve(new Interp("Amt {a}"), new Dictionary<string, object?> { ["a"] = 1234.50m }));
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void Clock_fields_come_from_one_instant_even_a_tick_before_midnight()
    {
        var now = new DateTime(2027, 3, 31, 23, 59, 59).AddTicks(9_999_999); // last tick of the financial year
        Assert.Equal("23:59:59", Resolve(new Clock(ClockField.Time), now: now));
        Assert.Equal(new DateTime(2027, 3, 31), Resolve(new Clock(ClockField.Day), now: now));
        Assert.Equal(20270331, Resolve(new Clock(ClockField.CompactDay), now: now));
        var stamp = (DateTime)Resolve(new Clock(ClockField.Stamp), now: now)!;
        Assert.Equal(new DateTime(2027, 3, 31, 23, 59, 59, 999), stamp); // milliseconds kept, sub-millisecond ticks dropped
    }

    [Fact]
    public void Clock_time_is_24_hour_text()
    {
        Assert.Equal("00:00:00", Resolve(new Clock(ClockField.Time), now: new DateTime(2026, 4, 1)));
        Assert.Equal("13:05:09", Resolve(new Clock(ClockField.Time), now: new DateTime(2026, 4, 1, 13, 5, 9)));
    }

    [Fact]
    public void India_clock_is_utc_plus_five_thirty()
    {
        var offset = new IndiaClock().Now - DateTime.UtcNow;
        Assert.InRange(offset.TotalMinutes, 329, 331);
    }

    [Fact]
    public void Verify_comparison_trims_text_and_compares_numbers_by_value()
    {
        Assert.True(Same(" RAMESH ", "RAMESH"));
        Assert.False(Same("Ramesh", "RAMESH"));
        Assert.True(Same(1.00m, 1m));
        Assert.True(Same(3, 3m));
        Assert.True(Same(3L, 3));
        Assert.False(Same(1499.99m, 1500m));
        Assert.False(Same(null, 0));
        Assert.False(Same(null, ""));
        Assert.True(Same(12, "12"));
    }

    [Fact]
    public void Plain_values_pass_through_resolution_unchanged()
    {
        Assert.Equal(5m, Resolve(5m));
        Assert.Null(Resolve(null));
        Assert.Equal("x", Resolve("x"));
        Assert.Equal(9L, Resolve(new Ref("v"), new Dictionary<string, object?> { ["v"] = 9L }));
    }
}
