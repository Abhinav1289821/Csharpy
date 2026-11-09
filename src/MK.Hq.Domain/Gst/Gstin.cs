using System.Text.RegularExpressions;

namespace MK.Hq.Domain.Gst;

public static partial class Gstin
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex Shape();

    /// <summary>Same base-36 check digit HQ's GSTINValidator uses.</summary>
    public static bool IsValid(string? value)
    {
        if (value is null || !Shape().IsMatch(value)) return false;
        int factor = 2, sum = 0;
        for (var i = 13; i >= 0; i--)
        {
            var product = Alphabet.IndexOf(value[i]) * factor;
            sum += product / 36 + product % 36;
            factor = factor == 2 ? 1 : 2;
        }
        return value[14] == Alphabet[(36 - sum % 36) % 36];
    }

    public static string? StateCode(string? gstin) =>
        gstin is { Length: >= 2 } && char.IsDigit(gstin[0]) && char.IsDigit(gstin[1]) ? gstin[..2] : null;

    /// <summary>HQ's state captions ("37-Andhra Pradesh").</summary>
    public static readonly IReadOnlyDictionary<string, string> States = new Dictionary<string, string>
    {
        ["01"] = "Jammu and Kashmir", ["02"] = "Himachal Pradesh", ["03"] = "Punjab", ["04"] = "Chandigarh", ["05"] = "Uttarakhand",
        ["06"] = "Haryana", ["07"] = "Delhi", ["08"] = "Rajasthan", ["09"] = "Uttar Pradesh", ["10"] = "Bihar", ["11"] = "Sikkim",
        ["12"] = "Arunachal Pradesh", ["13"] = "Nagaland", ["14"] = "Manipur", ["15"] = "Mizoram", ["16"] = "Tripura", ["17"] = "Meghalaya",
        ["18"] = "Assam", ["19"] = "West Bengal", ["20"] = "Jharkhand", ["21"] = "Odisha", ["22"] = "Chhattisgarh", ["23"] = "Madhya Pradesh",
        ["24"] = "Gujarat", ["26"] = "Dadra and Nagar Haveli and Daman and Diu", ["27"] = "Maharashtra", ["29"] = "Karnataka", ["30"] = "Goa",
        ["31"] = "Lakshadweep", ["32"] = "Kerala", ["33"] = "Tamil Nadu", ["34"] = "Puducherry", ["35"] = "Andaman and Nicobar Islands",
        ["36"] = "Telangana", ["37"] = "Andhra Pradesh", ["38"] = "Ladakh", ["97"] = "Other Territory",
    };

    public static string? HqStateCaption(string code) => States.TryGetValue(code, out var name) ? $"{code}-{name}" : null;
}
