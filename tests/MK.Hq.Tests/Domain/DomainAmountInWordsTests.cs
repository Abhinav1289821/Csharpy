using System.Globalization;
using MK.Hq.Domain.Text;

namespace MK.Hq.Tests.Domain;

/// <summary>Indian-system amount in words printed on bills.</summary>
public sealed class DomainAmountInWordsTests
{
    [Theory]
    [InlineData("0", "Rupees Zero Only")]
    [InlineData("0.004", "Rupees Zero Only")]
    [InlineData("0.01", "Rupees Zero and One Paise Only")]
    [InlineData("0.5", "Rupees Zero and Fifty Paise Only")]
    [InlineData("0.995", "Rupees One Only")]
    [InlineData("1", "Rupees One Only")]
    [InlineData("12.345", "Rupees Twelve and Thirty Five Paise Only")]
    [InlineData("19", "Rupees Nineteen Only")]
    [InlineData("20", "Rupees Twenty Only")]
    [InlineData("101", "Rupees One Hundred One Only")]
    [InlineData("110", "Rupees One Hundred Ten Only")]
    [InlineData("1000", "Rupees One Thousand Only")]
    [InlineData("99999", "Rupees Ninety Nine Thousand Nine Hundred Ninety Nine Only")]
    [InlineData("100000", "Rupees One Lakh Only")]
    [InlineData("1000001", "Rupees Ten Lakh One Only")]
    [InlineData("9999999", "Rupees Ninety Nine Lakh Ninety Nine Thousand Nine Hundred Ninety Nine Only")]
    [InlineData("10000000", "Rupees One Crore Only")]
    [InlineData("10000100", "Rupees One Crore One Hundred Only")]
    [InlineData("12345678.90", "Rupees One Crore Twenty Three Lakh Forty Five Thousand Six Hundred Seventy Eight and Ninety Paise Only")]
    [InlineData("999999999.99", "Rupees Ninety Nine Crore Ninety Nine Lakh Ninety Nine Thousand Nine Hundred Ninety Nine and Ninety Nine Paise Only")]
    [InlineData("1000000000", "Rupees One Hundred Crore Only")]
    [InlineData("999999999999.99", "Rupees Ninety Nine Thousand Nine Hundred Ninety Nine Crore Ninety Nine Lakh Ninety Nine Thousand Nine Hundred Ninety Nine and Ninety Nine Paise Only")]
    public void Amounts(string amount, string words) =>
        Assert.Equal(words, AmountInWords.Rupees(decimal.Parse(amount, CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData("-150", "Minus Rupees One Hundred Fifty Only")]
    [InlineData("-0.50", "Minus Rupees Zero and Fifty Paise Only")]
    [InlineData("-0.004", "Rupees Zero Only")] // rounds to nothing: no sign
    public void Negative_amounts_keep_their_sign(string amount, string words) =>
        Assert.Equal(words, AmountInWords.Rupees(decimal.Parse(amount, CultureInfo.InvariantCulture)));
}
