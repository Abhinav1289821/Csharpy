using System.Globalization;
using MK.Hq.Engine.Plans;

namespace MK.Hq.Tests.WritePath;

/// <summary>
/// The plan hash is the review fingerprint: posting refuses unless the re-planned hash is identical. So semantically equal
/// plans must hash equal (or every post fails REVIEW_CHANGED), and any difference that reaches HQ must change it.
/// </summary>
public sealed class WritePathPlanHashTests
{
    private static WritePlan Plan(IReadOnlyDictionary<string, object?> args, IReadOnlyList<string>? locks = null, IReadOnlyList<long>? docLocks = null, bool readCommitted = false) =>
        new("Test", [new CallStep("call", "IQDoc_Header_Insert", args) { ReadCommitted = readCommitted }])
        {
            Locks = locks ?? [],
            HqDocumentLocks = docLocks ?? [],
        };

    private static string Hash(params (string Key, object? Value)[] args) =>
        Plan(args.ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal)).Hash;

    [Fact]
    public void Equal_numbers_of_any_type_or_scale_hash_equal()
    {
        var expected = Hash(("x", 1m));
        Assert.Equal(expected, Hash(("x", 1.0m)));
        Assert.Equal(expected, Hash(("x", 1.00m)));
        Assert.Equal(expected, Hash(("x", 1)));
        Assert.Equal(expected, Hash(("x", 1L)));
        Assert.Equal(expected, Hash(("x", (short)1)));
        Assert.Equal(expected, Hash(("x", true)));
        Assert.Equal(Hash(("x", 0m)), Hash(("x", -0.00m)));
        Assert.Equal(Hash(("x", 12.5m)), Hash(("x", 12.500m)));
    }

    [Fact]
    public void Amounts_differing_beyond_twelve_places_hash_differently()
    {
        // The old "0.############" format rounded both to 0.333333333333.
        Assert.NotEqual(Hash(("rate", 0.3333333333333m)), Hash(("rate", 0.3333333333334m)));
        Assert.NotEqual(Hash(("rate", 1m / 3m)), Hash(("rate", 0.333333333333m)));
    }

    [Fact]
    public void Argument_order_does_not_matter_even_for_keys_differing_only_in_case()
    {
        var a = new Dictionary<string, object?>(StringComparer.Ordinal) { ["mB"] = 2, ["mA"] = 1, ["MA"] = "x" };
        var b = new Dictionary<string, object?>(StringComparer.Ordinal) { ["MA"] = "x", ["mA"] = 1, ["mB"] = 2 };
        Assert.Equal(Plan(a).Hash, Plan(b).Hash);
    }

    [Fact]
    public void Locks_hash_as_they_are_taken_distinct_and_sorted()
    {
        var args = new Dictionary<string, object?>();
        Assert.Equal(Plan(args, ["MK:b", "MK:a"], [7, 3]).Hash, Plan(args, ["MK:a", "MK:b", "MK:a"], [3, 7, 7]).Hash);
        Assert.NotEqual(Plan(args, ["MK:a"]).Hash, Plan(args, ["MK:a", "MK:c"]).Hash);
    }

    [Fact]
    public void Read_committed_flag_is_part_of_the_plan()
    {
        var args = new Dictionary<string, object?> { ["x"] = 1 };
        Assert.NotEqual(Plan(args).Hash, Plan(args, readCommitted: true).Hash);
    }

    [Fact]
    public void Hash_does_not_depend_on_the_machine_culture()
    {
        (string, object?)[] args = [("f", 1.5f), ("d", 2.25d), ("n", -3), ("u", 4u), ("m", -1234.5m), ("t", new DateTime(2027, 3, 31, 23, 59, 59))];
        var invariant = WithCulture(CultureInfo.InvariantCulture, () => Hash(args));
        foreach (var name in new[] { "de-DE", "fr-FR", "sv-SE", "hi-IN", "ar-SA" })
            Assert.Equal(invariant, WithCulture(CultureInfo.GetCultureInfo(name), () => Hash(args)));
    }

    [Fact]
    public void Dates_hash_by_value_not_kind_or_type()
    {
        var day = new DateOnly(2026, 4, 1);
        Assert.Equal(Hash(("d", day)), Hash(("d", new DateTime(2026, 4, 1))));
        Assert.Equal(Hash(("d", new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc))), Hash(("d", new DateTime(2026, 4, 1, 10, 0, 0, DateTimeKind.Unspecified))));
        Assert.NotEqual(Hash(("d", new DateTime(2026, 4, 1).AddTicks(1))), Hash(("d", new DateTime(2026, 4, 1))));
        Assert.NotEqual(Hash(("d", new DateOnly(2026, 3, 31))), Hash(("d", day)));
    }

    [Fact]
    public void Doubles_hash_by_their_exact_value_and_never_throw()
    {
        // HQ sends some rates as doubles; SQL Server rounds the binary value, so 0.1+0.2 and 0.3 can store differently.
        Assert.NotEqual(Hash(("r", 0.1 + 0.2)), Hash(("r", 0.3)));
        Assert.Equal(Hash(("r", 5d)), Hash(("r", 5m)));
        Assert.Equal(Hash(("r", 0d)), Hash(("r", -0d)));
        _ = Hash(("r", double.NaN));
        _ = Hash(("r", double.PositiveInfinity));
        _ = Hash(("r", 1e300));
    }

    [Fact]
    public void Null_forms_and_one_character_codes_hash_like_what_binds()
    {
        Assert.Equal(Hash(("x", null)), Hash(("x", DBNull.Value)));
        Assert.Equal(Hash(("x", 'Y')), Hash(("x", "Y")));
        Assert.NotEqual(Hash(("x", "")), Hash(("x", " ")));
        Assert.NotEqual(Hash(("x", "1")), Hash(("x", 1)));
        Assert.NotEqual(Hash(("x", null)), Hash(("x", "null")));
    }

    [Fact]
    public void Plan_tokens_are_distinct_from_look_alike_text()
    {
        Assert.NotEqual(Hash(("x", new Ref("n"))), Hash(("x", "$n")));
        Assert.NotEqual(Hash(("x", new Interp("{n}"))), Hash(("x", "~{n}")));
        Assert.NotEqual(Hash(("x", new Clock(ClockField.Day))), Hash(("x", new Clock(ClockField.Time))));
        Assert.NotEqual(Hash(("x", new Interp("Bill {n}"))), Hash(("x", new Interp("Bill {m}"))));
    }

    [Fact]
    public void Byte_arrays_hash_by_content()
    {
        Assert.NotEqual(Hash(("img", new byte[] { 1, 2 })), Hash(("img", new byte[] { 1, 3 })));
        Assert.Equal(Hash(("img", new byte[] { 1, 2 })), Hash(("img", new byte[] { 1, 2 })));
    }

    [Fact]
    public void Every_step_kind_contributes_its_content()
    {
        static string H(PlanStep s) => new WritePlan("p", [s]).Hash;
        var args = new Dictionary<string, object?> { ["id"] = 1 };
        Assert.NotEqual(H(new SqlStep("s", "SELECT 1", args)), H(new SqlStep("s", "SELECT 2", args)));
        Assert.NotEqual(H(new SqlStep("s", "SELECT 1", args, "a")), H(new SqlStep("s", "SELECT 1", args, "b")));
        Assert.NotEqual(H(new AllocateNumberStep("n", 1, "A", "", "no")), H(new AllocateNumberStep("n", 1, "B", "", "no")));
        Assert.NotEqual(H(new AssertNumberFreeStep("a", 1, "A", 5, "", 9L, true)), H(new AssertNumberFreeStep("a", 1, "A", 5, "", 9L, false)));
        Assert.NotEqual(H(new VerifyStep("v", "SELECT 1", args, 1m, "C", "m")), H(new VerifyStep("v", "SELECT 1", args, 2m, "C", "m")));
        Assert.Equal(H(new VerifyStep("v", "SELECT 1", args, 1m, "C", "m")), H(new VerifyStep("v", "SELECT 1", args, 1.00m, "C", "m")));
        Assert.NotEqual(
            H(new CopyRowsStep("c", "User_IQdaybook_insert", "SELECT 1", args, ["A", "B"], args)),
            H(new CopyRowsStep("c", "User_IQdaybook_insert", "SELECT 1", args, ["A"], args)));
    }

    private static T WithCulture<T>(CultureInfo culture, Func<T> body)
    {
        var (c, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = c;
            CultureInfo.CurrentUICulture = ui;
        }
    }
}
