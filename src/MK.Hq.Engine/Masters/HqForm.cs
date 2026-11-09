using System.Globalization;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Masters;

/// <summary>
/// How HQ's WinForms master screens turn text boxes and numbers into procedure arguments. Reproducing these exactly is
/// what makes a master row written by MK indistinguishable from one HQ wrote.
/// </summary>
public static class HqForm
{
    /// <summary>HQ's CStr1 on an untrimmed box: blank becomes a single space (" "), anything else is kept as typed.</summary>
    public static string Spaced(string? value) => string.IsNullOrEmpty(value) || value.Trim().Length == 0 ? " " : value;

    /// <summary>HQ's CStr1(x).Trim(): blank becomes "", anything else is trimmed.</summary>
    public static string Trimmed(string? value) => (value ?? "").Trim();

    /// <summary>A loaded value for a box HQ fills with .Trim() and saves with CStr1 (edit path).</summary>
    public static string Reloaded(object? value) => Spaced(Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim());

    public static string ReloadedTrimmed(object? value) => Trimmed(Convert.ToString(value, CultureInfo.InvariantCulture));

    public static decimal Number(object? value) => value is null or DBNull ? 0 : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    public static long Id(object? value) => value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    /// <summary>
    /// HQ sends some rates as a .NET double into a numeric(18,2) parameter; SQL Server then rounds the binary value, so
    /// 349.575 (binary 349.57499999…) is stored as 349.57, not 349.58. This reproduces that conversion exactly.
    /// </summary>
    public static decimal FloatToNumeric(decimal value, int scale)
    {
        var binary = (double)value;
        var exact = decimal.Parse(binary.ToString("G17", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return Math.Round(exact, scale, MidpointRounding.AwayFromZero);
    }

    /// <summary>HQ binds text as varchar (code page 1252): anything outside it would silently become '?', and an apostrophe
    /// is stored doubled by some screens and stripped by their duplicate checks. Refuse both up front.</summary>
    /// <param name="apostrophe">The item screen passes text as a parameter (the shop types '' as an inch mark); the account
    /// screen doubles apostrophes into the stored name, so there they are refused.</param>
    public static string Text(string? value, string field, int max, bool required = false, bool apostrophe = false)
    {
        var v = value ?? "";
        Guard.Require(!required || v.Trim().Length > 0, "MASTER_FIELD_REQUIRED", $"Enter the {field}");
        Guard.Require(v.Length <= max, "MASTER_FIELD_TOO_LONG", $"The {field} can be at most {max} characters");
        Guard.Require(v.All(c => (apostrophe || c != '\'') && ((c >= ' ' && c <= '~') || (c >= '\u00A0' && c <= '\u00FF'))), "MASTER_FIELD_CHARACTERS",
            $"The {field} can only use English letters, digits and common punctuation{(apostrophe ? "" : " (no apostrophe)")} - HQ would store anything else as '?'");
        Guard.Require(!v.StartsWith(' '), "MASTER_FIELD_CHARACTERS", $"The {field} cannot start with a space");
        return v;
    }
}
