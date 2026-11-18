using MK.Hq.Domain;
using MK.Hq.Engine.Journal;

namespace MK.Hq.Tests.WritePath;

/// <summary>The SQLite operation journal and drafts: status transitions, what Update keeps, Restart, concurrency.</summary>
public sealed class WritePathJournalTests
{
    private static EngineStore NewStore() => new(Path.Combine(Path.GetTempPath(), "mkhq-writepath", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Begin_creates_pending_once_and_returns_the_existing_record_after()
    {
        var store = NewStore();
        var id = Guid.NewGuid().ToString();
        var first = store.BeginOperation(id, "mbm", "sale", "h1");
        Assert.Equal(OperationStatus.Pending, first.Status);
        store.Update(id, OperationStatus.Committed, 5, "{}", "{\"ok\":1}");
        var again = store.BeginOperation(id, "other", "purchase", "h2");
        Assert.Equal(("mbm", "sale", "h1", OperationStatus.Committed), (again.Firm, again.Kind, again.RequestHash, again.Status));
    }

    [Fact]
    public void Update_keeps_evidence_and_receipt_when_not_given_and_replaces_the_error()
    {
        var store = NewStore();
        var id = Guid.NewGuid().ToString();
        store.BeginOperation(id, "mbm", "sale", "h");
        store.Update(id, OperationStatus.Committing, 77, "{\"kind\":\"create\"}", "{\"r\":1}");
        store.Update(id, OperationStatus.Uncertain, errorCode: "HQ_COMMIT_UNCERTAIN", errorMessage: "dropped");
        var r = store.GetOperation(id)!;
        Assert.Equal((OperationStatus.Uncertain, 77L, "{\"kind\":\"create\"}", "{\"r\":1}", "HQ_COMMIT_UNCERTAIN"), (r.Status, r.DocumentId!.Value, r.Evidence, r.Result, r.ErrorCode));
        store.Update(id, OperationStatus.Committed);
        r = store.GetOperation(id)!;
        Assert.Equal((OperationStatus.Committed, "{\"r\":1}", (string?)null, (string?)null), (r.Status, r.Result, r.ErrorCode, r.ErrorMessage));
    }

    [Fact]
    public void Restart_drops_the_earlier_attempts_evidence_receipt_and_error()
    {
        // Update cannot clear these (COALESCE); a retried operation left with the old attempt's evidence could later be
        // "settled" on it, e.g. an amend's ModifyCnt n+1 reached by someone else's save in HQ.
        var store = NewStore();
        var id = Guid.NewGuid().ToString();
        store.BeginOperation(id, "mbm", "sale.amend", "h");
        store.Update(id, OperationStatus.Committing, 77, "{\"kind\":\"amend\"}", "{\"r\":1}");
        store.Update(id, OperationStatus.Rejected, errorCode: "HQ_NOT_COMMITTED", errorMessage: "x");
        store.Restart(id);
        var r = store.GetOperation(id)!;
        Assert.Equal(OperationStatus.Pending, r.Status);
        Assert.Null(r.DocumentId);
        Assert.Null(r.Evidence);
        Assert.Null(r.Result);
        Assert.Null(r.ErrorCode);
        Assert.Null(r.ErrorMessage);
        Assert.Equal(("mbm", "sale.amend", "h"), (r.Firm, r.Kind, r.RequestHash));
    }

    [Fact]
    public void Unsettled_listing_holds_only_pending_committing_and_uncertain()
    {
        var store = NewStore();
        var ids = Enum.GetValues<OperationStatus>().ToDictionary(s => s, _ => Guid.NewGuid().ToString());
        foreach (var (status, id) in ids)
        {
            store.BeginOperation(id, "mbm", "k", "h");
            store.Update(id, status);
        }
        var listed = store.Operations(null, 50, unsettledOnly: true).Select(o => o.Status).Order().ToArray();
        Assert.Equal([OperationStatus.Pending, OperationStatus.Committing, OperationStatus.Uncertain], listed);
        Assert.Empty(store.Operations("other", 50));
        Assert.Equal(5, store.Operations("mbm", 50).Count);
    }

    [Fact]
    public async Task Concurrent_begins_of_one_id_make_one_record_and_of_many_ids_never_fail_busy()
    {
        var store = NewStore();
        var same = Guid.NewGuid().ToString();
        var records = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => store.BeginOperation(same, "mbm", "k", "h"))));
        Assert.All(records, r => Assert.Equal(records[0].CreatedAt, r.CreatedAt));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                var id = Guid.NewGuid().ToString();
                store.BeginOperation(id, "mbm", "k", "h");
                store.Update(id, OperationStatus.Committed, 1, "{}", "{}");
            }
        })));
        Assert.Equal(16 * 20 + 1, store.Operations("mbm", 1000).Count);
    }

    [Fact]
    public void Draft_versions_refuse_stale_writes_wrong_firm_and_double_create()
    {
        var store = NewStore();
        var id = Guid.NewGuid().ToString();
        store.SaveDraft(id, "mbm", "sale", "t", "{}", 0);
        Assert.Equal("DRAFT_CHANGED", Assert.Throws<HqException>(() => store.SaveDraft(id, "mbm", "sale", "t", "{}", 0)).Code);
        Assert.Equal("DRAFT_CHANGED", Assert.Throws<HqException>(() => store.SaveDraft(id, "mp", "sale", "t", "{}", 1)).Code);
        Assert.Equal(2, store.SaveDraft(id, "mbm", "purchase", "t2", "{\"a\":1}", 1).Version);
        Assert.Equal("DRAFT_CHANGED", Assert.Throws<HqException>(() => store.SaveDraft(id, "mbm", "sale", "t", "{}", 1)).Code);
        Assert.False(store.DeleteDraft(id, "mp"));
        Assert.Empty(store.Drafts("mp"));
        Assert.True(store.DeleteDraft(id, "mbm"));
        Assert.Null(store.Draft(id));
    }

    [Fact]
    public void Journal_survives_a_new_store_on_the_same_folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mkhq-writepath", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid().ToString();
        new EngineStore(dir).BeginOperation(id, "mbm", "k", "h");
        Assert.NotNull(new EngineStore(dir).GetOperation(id));
    }
}
