namespace MK.Hq.Domain.Text;

/// <summary>Indian-system amount in words for printed invoices ("Rupees One Lakh Twenty Thousand Only").</summary>
public static class AmountInWords
{
    private static readonly string[] Ones =
    [
        "", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen",
        "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen",
    ];
    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string Rupees(decimal amount)
    {
        // A negative amount must not print as if it were owed the other way; "Minus" keeps the printed words honest.
        var sign = Money.Round2(amount) < 0 ? "Minus " : "";
        amount = Money.Round2(Math.Abs(amount));
        var rupees = (long)decimal.Truncate(amount);
        var paise = (int)((amount - rupees) * 100);
        var words = rupees == 0 ? "Zero" : Words(rupees);
        return paise > 0 ? $"{sign}Rupees {words} and {Words(paise)} Paise Only" : $"{sign}Rupees {words} Only";
    }

    private static string Words(long n)
    {
        var parts = new List<string>();
        void Take(long divisor, string name)
        {
            if (n < divisor) return;
            parts.Add(Below100((int)(n / divisor)) + " " + name);
            n %= divisor;
        }
        if (n >= 10_000_000) { parts.Add(Words(n / 10_000_000) + " Crore"); n %= 10_000_000; }
        Take(100_000, "Lakh");
        Take(1_000, "Thousand");
        Take(100, "Hundred");
        if (n > 0) parts.Add(Below100((int)n));
        return string.Join(" ", parts);
    }

    private static string Below100(int n) => n < 20 ? Ones[n] : (Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : ""));
}
