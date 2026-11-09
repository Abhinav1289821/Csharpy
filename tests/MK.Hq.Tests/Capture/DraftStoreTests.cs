using MK.Hq.Domain;
using MK.Hq.Engine.Journal;

namespace MK.Hq.Tests.Capture;

public sealed class DraftStoreTests
{
    [Fact]
    public void Drafts_save_reload_update_with_optimistic_versions()
    {
        var store = new EngineStore(Path.Combine(Path.GetTempPath(), "mkhq-drafts", Guid.NewGuid().ToString("N")));
        var id = Guid.NewGuid().ToString();
        var first = store.SaveDraft(id, "mbm", "purchase", "t1", "{\"a\":1}", 0);
        Assert.Equal(1, first.Version);
        var second = store.SaveDraft(id, "mbm", "purchase", "t2", "{\"a\":2}", 1);
        Assert.Equal((2, "t2"), (second.Version, second.Title));
        Assert.Equal(HqException.Conflict("x", "y").Kind, Assert.Throws<HqException>(() => store.SaveDraft(id, "mbm", "purchase", "t3", "{}", 1)).Kind);
        Assert.Single(store.Drafts("mbm"));
        Assert.True(store.DeleteDraft(id, "mbm"));
    }
}
