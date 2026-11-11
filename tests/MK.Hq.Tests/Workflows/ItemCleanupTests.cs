using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Engine.Masters;
using MK.Hq.Tests.Support;

namespace MK.Hq.Tests.Workflows;

public sealed class ScratchCleanupMbm : IAsyncLifetime
{
    public const string Database = "MKHQ_CLEANUP_MBM";
    public ServiceProvider Engine { get; private set; } = null!;
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "mkhq-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Database, "ISTMBM26.bak");
        Engine = ScratchDatabase.Engine("mbm", Database, DataDirectory);
    }
    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

/// <summary>The owner's guided clean-up end to end on a scratch restore: answers recorded, applied through the HQ-identical
/// item save, items changed in HQ meanwhile left alone, and re-running repeats nothing.</summary>
[Trait("Category", "Workflow")]
public sealed class ItemCleanupTests(ScratchCleanupMbm fx) : IClassFixture<ScratchCleanupMbm>
{
    private ItemCleanupService Cleanup => fx.Engine.GetRequiredService<ItemCleanupService>();
    private static SqlConnection Sql() => new(TestDatabase.ConnectionString(ScratchCleanupMbm.Database));

    private sealed record Row(long Id, string Name, string Code, long Grp, decimal Vat, string Hsn, int Stat, decimal Prate, decimal Srate);

    private static Task<Row> ItemAsync(SqlConnection cn, long id) => cn.QueryFirstAsync<Row>("""
        SELECT CAST(m.Item_ID AS bigint) Id, LTRIM(RTRIM(m.Item_Name)) Name, LTRIM(RTRIM(m.Item_Code)) Code, CAST(m.Grp_ID AS bigint) Grp, CAST(m.VAT AS decimal(9,2)) Vat,
          LTRIM(RTRIM(ISNULL(m.HSNCODE,''))) Hsn, CAST(ISNULL(m.Item_Stat,0) AS int) Stat, CAST(u.Unit_Prate AS decimal(19,2)) Prate, CAST(u.Unit_WSrate AS decimal(19,2)) Srate
        FROM dbo.IQItems_Mast m JOIN dbo.IQItems_Units u ON u.Item_ID=m.Item_ID AND u.Recd_ID=0 WHERE m.Item_ID=@id
        """, new { id });

    private static CleanupProposal From(Row r, string? proposed, string? check, string[] changes, string? autoName = null) => new()
    {
        Id = r.Id, Code = r.Code, GroupId = r.Grp, Current = r.Name, Proposed = proposed, AutoName = autoName, Check = check, Changes = changes,
        Hsn = r.Hsn, Gst = r.Vat, Active = r.Stat == 0,
    };

    [Fact]
    public async Task Answers_are_applied_through_HQs_item_save_exactly_once()
    {
        await using var cn = Sql();
        var ids = (await cn.QueryAsync<long>("SELECT TOP 8 CAST(Item_ID AS bigint) FROM dbo.IQItems_Mast WHERE ISNULL(Item_Stat,0)=0 AND VAT=18 ORDER BY Item_ID DESC")).ToList();
        var rows = new List<Row>();
        foreach (var id in ids) rows.Add(await ItemAsync(cn, id));
        var (auto, ruleYes, ruleNo, own, retire, hsn, stale, excel) = (rows[0], rows[1], rows[2], rows[3], rows[4], rows[5], rows[6], rows[7]);

        var file = new CleanupFile("mbm", "ISTMBM26", "2026-11-10T10:00:00", new Dictionary<string, string> { ["units"] = "Units", ["singular"] = "Singular" },
        [
            From(auto, auto.Name + " MK1", "Automatic", ["spelling"]),
            From(ruleYes, ruleYes.Name + " MK2", "Tidy-up", ["units"]),
            From(ruleNo, ruleNo.Name + " MK3", "Tidy-up", ["singular", "capitals"], autoName: ruleNo.Name + " MK3A"),
            From(own, own.Name + " MK4", "Review", ["order"]),
            From(retire, null, null, []) with { Retire = true },
            From(hsn, null, null, []) with { DataCheck = "HSN missing", HsnSuggest = "39172390" },
            From(stale, stale.Name + " MK7", "Automatic", ["spelling"]) with { Current = stale.Name + " (OLD)" },
            From(excel, excel.Name + " MK8", "Automatic", ["spelling"]) with { Answers = new() { ["approve"] = "N" } },
        ]);
        var summary = await Cleanup.ImportAsync("mbm", file);
        Assert.Equal(2, summary.Stages.Single(s => s.Key == "rules").Open); // two rules, none answered yet

        Cleanup.DecideRule("mbm", "units", "yes");
        Cleanup.DecideRule("mbm", "singular", "no");
        Cleanup.Decide("mbm", [(own.Id, new CleanupDecision { Name = "MK OWN WORDING" }), (retire.Id, new CleanupDecision { Retire = true }),
            (hsn.Id, new CleanupDecision { Hsn = "39172390" })]);
        Assert.Equal(7, Cleanup.Summary("mbm").ReadyToApply); // everything except the Excel "N"

        CleanupProgress p;
        string? batch = null;
        do { p = await Cleanup.ApplyAsync("mbm", batch, take: 3); batch = p.BatchId; } while (p.Remaining > 0);
        Assert.Equal((6, 1, 0), (p.Applied, p.Skipped, p.Failed));

        Assert.Equal(auto.Name + " MK1", (await ItemAsync(cn, auto.Id)).Name);
        Assert.Equal(ruleYes.Name + " MK2", (await ItemAsync(cn, ruleYes.Id)).Name);
        Assert.Equal(ruleNo.Name + " MK3A", (await ItemAsync(cn, ruleNo.Id)).Name); // refused rule: only the automatic part
        Assert.Equal("MK OWN WORDING", (await ItemAsync(cn, own.Id)).Name);
        Assert.Equal(1, (await ItemAsync(cn, retire.Id)).Stat);
        Assert.Equal("39172390", (await ItemAsync(cn, hsn.Id)).Hsn);
        Assert.Equal(stale.Name, (await ItemAsync(cn, stale.Id)).Name);   // changed in HQ meanwhile: left alone
        Assert.Equal(excel.Name, (await ItemAsync(cn, excel.Id)).Name);   // his Excel "N"
        var changed = await ItemAsync(cn, auto.Id);
        Assert.Equal((auto.Code, auto.Grp, auto.Vat, auto.Prate, auto.Srate), (changed.Code, changed.Grp, changed.Vat, changed.Prate, changed.Srate));

        // Running it again writes nothing, and nothing is left waiting except the item changed in HQ meanwhile (needs a fresh list).
        var again = await Cleanup.ApplyAsync("mbm", null);
        Assert.Equal((0, 0), (again.Applied, again.Remaining));
        Assert.Equal(0, Cleanup.Summary("mbm").ReadyToApply);
        // An applied item cannot be re-answered by mistake.
        Assert.Equal("CLEANUP_APPLIED", Assert.Throws<HqException>(() => Cleanup.Decide("mbm", [(auto.Id, new CleanupDecision { KeepName = true })])).Code);
    }
}
