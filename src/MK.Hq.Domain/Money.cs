using System.Globalization;

namespace MK.Hq.Domain;

/// <summary>
/// Exact decimal money helpers. Every rounding in this engine is half-away-from-zero, which is what
/// HQ's stored values show (and what decimal.js ROUND_HALF_UP did in the prototype).
/// </summary>
public static class Money
{
    public const decimal MaxAmount = 999_999_999_999.99m;

    public static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal Round(decimal value, int places) => Math.Round(value, places, MidpointRounding.AwayFromZero);

    public static string Text(decimal value) => Round2(value).ToString("0.00", CultureInfo.InvariantCulture);

    public static string Plain(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>Number of significant fractional digits (1.2500 → 2).</summary>
    public static int Scale(decimal value)
    {
        value = value / 1.0000000000000000000000000000m; // normalise trailing zeros
        return (decimal.GetBits(value)[3] >> 16) & 0xFF;
    }

    /// <summary>Rounds to a multiple of <paramref name="step"/> using HQ's TTYPES.RoundedOffType semantics.</summary>
    public static decimal RoundToStep(decimal value, decimal step, RoundingDirection direction)
    {
        if (step <= 0) return value;
        var units = value / step;
        var whole = direction switch
        {
            RoundingDirection.Up => Math.Ceiling(units),
            RoundingDirection.Down => Math.Floor(units),
            _ => Math.Round(units, 0, MidpointRounding.AwayFromZero),
        };
        return whole * step;
    }

    public static decimal ParseExact(string? text, string field, int maxScale = 6)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(text), "INVALID_DECIMAL", $"{field} is required");
        Guard.Require(decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value),
            "INVALID_DECIMAL", $"{field} must be a plain decimal number");
        Guard.Require(Scale(value) <= maxScale, "INVALID_DECIMAL", $"{field} has too many decimal places");
        return value;
    }
}

public enum RoundingDirection { Nearest = 0, Up = 1, Down = 2 }

