using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Tests.Capture;

public sealed class HistoryMatchingTests
{
    [Theory]
    [InlineData("1 inch UPVC PIPE", "25 MM UPVC PIPE", "32 MM UPVC PIPE")]
    [InlineData("1 inch PVC PIPE", "32 MM PVC PIPE", "25 MM PVC PIPE")]
    [InlineData("3 inch PVC PIPE", "90 MM PVC PIPE", "80 MM PVC PIPE")]
    [InlineData("2.5 inch PVC PIPE", "75 MM PVC PIPE", "65 MM PVC PIPE")]
    [InlineData("25 MM UPVC PIPE", "1 inch UPVC PIPE", "0.75 inch UPVC PIPE")]
    public void Material_specific_trade_sizes_reject_neighbouring_sizes(string query, string correct, string wrong)
    {
        var matcher = new ItemMatcher([Item(1, correct), Item(2, wrong)]);
        var match = matcher.Match(new ExtractedLine(query, query, null, null, null, null, null, null, null), new Dictionary<string, (long, long)>());
        Assert.Equal(1, match.Candidates[0].ItemId);
        Assert.DoesNotContain(match.Candidates[0].Notes, n => n.StartsWith("size differs"));
        Assert.Contains(match.Candidates.Single(c => c.ItemId == 2).Notes, n => n.StartsWith("size differs"));
    }

    [Fact]
    public void Shared_numbers_and_material_do_not_make_a_soap_box_a_pipe()
    {
        var query = "3 inch PVC PIPE";
        var matcher = new ItemMatcher([Item(1, "90 MM PVC PIPE 6 Kgf"), Item(2, "PVC SOAP BOX 3 IN 1")]);
        var match = matcher.Match(new ExtractedLine(query, query, null, null, null, null, null, null, null), new Dictionary<string, (long, long)>());
        Assert.Equal(1, match.Candidates[0].ItemId);
        Assert.Contains(match.Candidates.Single(c => c.ItemId == 2).Notes, n => n.StartsWith("different product"));

        var missing = new ItemMatcher([Item(2, "PVC SOAP BOX 3 IN 1")]).Match(
            new ExtractedLine(query, query, null, null, null, null, null, null, null), new Dictionary<string, (long, long)>());
        Assert.Null(missing.Chosen);
    }

    [Fact]
    public void A_single_conflicting_candidate_is_never_chosen()
    {
        var query = "25 MM UPVC TEE";
        var matcher = new ItemMatcher([Item(1, "25 MM CPVC TEE")]);
        var match = matcher.Match(new ExtractedLine(query, query, null, null, null, null, null, null, null),
            new Dictionary<string, (long, long)>(), history: new Dictionary<long, int> { [1] = 100 });
        Assert.Null(match.Chosen);
        Assert.Contains(match.Candidates[0].Notes, n => n.StartsWith("different material"));
    }

    private static CatalogueItem Item(long id, string name) =>
        new(id, 3, "", name, "PCS", "3917", 118, 100, 130, 9, 9, 18, true, 10, true, "Pipes", "");
}
