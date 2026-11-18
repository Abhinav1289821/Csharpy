using System.Globalization;
using MK.Hq.Domain;

namespace MK.Hq.Tests.Domain;

/// <summary>Money helpers: HQ rounds every stored value half-away-from-zero, never banker's.</summary>
public sealed class DomainMoneyTests
{
    private static decimal D(string s) => decimal.Parse(s, NumberStyles.Number, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("-0.005", "-0.01")]
    [InlineData("0.015", "0.02")]
    [InlineData("0.025", "0.03")]   // banker's would give 0.02
    [InlineData("2.675", "2.68")]
    [InlineData("-2.675", "-2.68")]
    [InlineData("1.004999", "1.00")]
    [InlineData("999999999999.995", "1000000000000.00")]
    public void Round2_is_half_away_from_zero(string value, string expected) =>
        Assert.Equal(D(expected), Money.Round2(D(value)));

    [Theory]
    [InlineData("99.50", "1", RoundingDirection.Nearest, "100")]
    [InlineData("98.50", "1", RoundingDirection.Nearest, "99")]     // banker's would give 98
    [InlineData("98.49", "1", RoundingDirection.Nearest, "98")]
    [InlineData("-0.50", "1", RoundingDirection.Nearest, "-1")]
    [InlineData("99.01", "1", RoundingDirection.Up, "100")]
    [InlineData("100.00", "1", RoundingDirection.Up, "100")]
    [InlineData("99.99", "1", RoundingDirection.Down, "99")]
    [InlineData("-99.01", "1", RoundingDirection.Up, "-99")]
    [InlineData("-99.01", "1", RoundingDirection.Down, "-100")]
    [InlineData("99.25", "0.5", RoundingDirection.Nearest, "99.5")]
    [InlineData("99.24", "0.5", RoundingDirection.Nearest, "99.0")]
    [InlineData("125", "10", RoundingDirection.Nearest, "130")]
    [InlineData("124.99", "10", RoundingDirection.Nearest, "120")]
    [InlineData("99.37", "0", RoundingDirection.Nearest, "99.37")]  // no rounding configured
    [InlineData("99.37", "-1", RoundingDirection.Up, "99.37")]
    public void RoundToStep_follows_TTYPES_rounding(string value, string step, RoundingDirection direction, string expected) =>
        Assert.Equal(D(expected), Money.RoundToStep(D(value), D(step), direction));

    [Theory]
    [InlineData("0", "0.00")]
    [InlineData("1.005", "1.01")]
    [InlineData("-1.005", "-1.01")]
    [InlineData("1234567.8", "1234567.80")]
    [InlineData("-0.004", "0.00")]   // rounds to zero: never print "-0.00" on a bill or warning
    [InlineData("-0.00", "0.00")]
    public void Text_prints_two_places(string value, string expected) =>
        Assert.Equal(expected, Money.Text(D(value)));

    [Fact]
    public void Text_of_a_negative_zero_from_rounding_has_no_minus_sign() =>
        Assert.Equal("0.00", Money.Text(Math.Round(-0.001m, 2)));

    [Theory]
    [InlineData("1.2500", 2)]
    [InlineData("0", 0)]
    [InlineData("0.0000000", 0)]
    [InlineData("100", 0)]
    [InlineData("100.000", 0)]
    [InlineData("1.23456789", 8)]
    [InlineData("-0.10", 1)]
    [InlineData("999999999999.99", 2)]
    public void Scale_counts_significant_places(string value, int expected) =>
        Assert.Equal(expected, Money.Scale(D(value)));

    [Theory]
    [InlineData("12.50", "12.50")]
    [InlineData("-3", "-3")]
    [InlineData("+3", "3")]
    [InlineData("0.123456", "0.123456")]
    [InlineData("0.1234560", "0.123456")] // trailing zeros are not precision
    [InlineData(".5", "0.5")]
    public void ParseExact_accepts_plain_decimals(string text, string expected) =>
        Assert.Equal(D(expected), Money.ParseExact(text, "Rate"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1e5")]
    [InlineData("1,000")]
    [InlineData("abc")]
    [InlineData("0.1234567")]
    [InlineData("99999999999999999999999999999999")]
    public void ParseExact_rejects_anything_else(string? text)
    {
        var e = Assert.Throws<HqException>(() => Money.ParseExact(text, "Rate"));
        Assert.Equal("INVALID_DECIMAL", e.Code);
        Assert.Equal(FailureKind.Invalid, e.Kind);
    }
}
