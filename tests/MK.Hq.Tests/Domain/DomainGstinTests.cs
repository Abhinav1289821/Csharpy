using MK.Hq.Domain.Gst;

namespace MK.Hq.Tests.Domain;

/// <summary>GSTIN check digit, state code and HQ's state captions.</summary>
public sealed class DomainGstinTests
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>GSTN's published algorithm written left to right (factor 1 on odd positions, 2 on even), independent of the engine's loop.</summary>
    private static string WithCheckDigit(string first14)
    {
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var p = Alphabet.IndexOf(first14[i]) * (i % 2 == 0 ? 1 : 2);
            sum += p / 36 + p % 36;
        }
        return first14 + Alphabet[(36 - sum % 36) % 36];
    }

    [Theory]
    [InlineData("37AABCU9603R1Z")]
    [InlineData("36AAACH7409R1Z")]
    [InlineData("27AAPFU0939F1Z")]
    [InlineData("97AABCU9603R1Z")] // Other Territory
    [InlineData("99AABCU9603R1Z")] // Centre Jurisdiction: HQ's pattern accepts any two digits
    [InlineData("01ABCDE1234FZZ")]
    public void Exactly_one_check_character_is_valid(string first14)
    {
        var good = WithCheckDigit(first14);
        Assert.True(Gstin.IsValid(good), good);
        foreach (var c in Alphabet.Where(c => c != good[14]))
            Assert.False(Gstin.IsValid(first14 + c), first14 + c);
    }

    [Theory]
    [InlineData("37AABCU9603R1ZM", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("37AABCU9603R1Z", false)]        // 14 characters
    [InlineData("37AABCU9603R1ZMX", false)]      // 16 characters
    [InlineData(" 37AABCU9603R1ZM", false)]      // callers trim
    [InlineData("37aabcu9603r1zm", false)]       // callers upper-case (DocumentResolver, AccountMaster, health check)
    [InlineData("37AABCU9603R1YM", false)]       // 14th character must be Z
    [InlineData("37AABCU9603R0ZM", false)]       // entity number 0 does not exist
    [InlineData("3XAABCU9603R1ZM", false)]
    [InlineData("37AABC19603R1ZM", false)]       // PAN shape: five letters
    public void Shape_and_check_digit(string? gstin, bool valid) => Assert.Equal(valid, Gstin.IsValid(gstin));

    [Theory]
    [InlineData("37AABCU9603R1ZM", "37")]
    [InlineData("97", "97")]
    [InlineData("3", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("A7AABCU9603R1ZM", null)]
    [InlineData("３７AABCU9603R1ZM", null)] // full-width digits are not a state code (would read as "another state")
    [InlineData("٣٧AABCU9603R1ZM", null)] // Arabic-Indic digits likewise
    public void State_code_is_two_ascii_digits(string? gstin, string? expected) => Assert.Equal(expected, Gstin.StateCode(gstin));

    /// <summary>FrmInwards.cmbstate (a drop-down list): the only captions HQ's bill form can show for the party's state.</summary>
    private static readonly string[] HqBillStates =
    [
        "01-Jammu & Kashmir", "02-Himachal Pradesh", "03-Punjab", "04-Chandigarh", "05-Uttarakhand", "06-Haryana", "07-Delhi", "08-Rajasthan", "09-Uttar Pradesh", "10-Bihar",
        "11-Sikkim", "12-Arunachal Pradesh", "13-Nagaland", "14-Manipur", "15-Mizoram", "16-Tripura", "17-Meghalaya", "18-Assam", "19-West Bengal", "20-Jharkhand",
        "21-Odisha", "22-Chhattisgarh", "23-Madhya Pradesh", "24-Gujarat", "25-Daman & Diu", "26-Dadra & Nagar Haveli", "27-Maharashtra", "29-Karnataka", "30-Goa", "31-Lakshdweep",
        "32-Kerala", "33-Tamil Nadu", "34-Pondicherry", "35-Andaman & Nicobar Islands", "36-Telangana", "37-Andhra Pradesh", "97-Other Territory",
    ];

    [Fact]
    public void State_captions_are_exactly_HQs()
    {
        Assert.Equal(HqBillStates, Gstin.States.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}-{kv.Value}"));
        foreach (var caption in HqBillStates) Assert.Equal(caption, Gstin.HqStateCaption(caption[..2]));
    }

    [Theory]
    [InlineData("38")] // Ladakh: HQ has no caption, so the account's own state is kept
    [InlineData("28")]
    [InlineData("99")]
    [InlineData("00")]
    public void Codes_HQ_cannot_show_have_no_caption(string code) => Assert.Null(Gstin.HqStateCaption(code));
}
