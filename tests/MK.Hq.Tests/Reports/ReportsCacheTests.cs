using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.Reports;

/// <summary>Catalogue and firm-profile caches (database-free, with a fake reader and clock).</summary>
public sealed class ReportsCacheTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static HqFirm Firm() =>
        new(new FirmOptions { Id = "mbm", Name = "MK Building Materials", Database = "ISTMBM26" }, new HqOptions(), new ProcedureCatalog());

    private static CatalogueItem Item(string name) =>
        new(1, 1, "", name, "NOS", "", 10, 8, 12, 9, 9, 18, true, 5, true, "", "");

    /// <summary>A reader whose HQ the test controls: stamp, item name, and an optional gate that holds a read in flight.</summary>
    private sealed class FakeHq
    {
        public string Stamp = "s1";
        public string Name = "PIPE";
        public int Reads, FullReads;
        public readonly List<string?> KnownStamps = [];
        public TaskCompletionSource? Hold;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CatalogueRead> ReadAsync(HqFirm firm, string? known, CancellationToken ct)
        {
            Reads++;
            KnownStamps.Add(known);
            // What HQ held when the read began (a save may land while the read is still going).
            var stamp = Stamp;
            var name = Name;
            Entered.TrySetResult();
            if (Hold is { } h) await h.Task;
            if (known is not null && known == stamp) return new CatalogueRead(stamp, null, null);
            FullReads++;
            return new CatalogueRead(stamp, [Item(name)], []);
        }
    }

    [Fact]
    public async Task Invalidate_during_a_read_is_not_undone_by_that_read()
    {
        // A bill saved while the catalogue was being read: the read saw the old masters. Before, it stored them with a fresh
        // timestamp after Invalidate had run, so the browser was served the pre-save list as current.
        var hq = new FakeHq { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var catalogue = new Catalogue(new Clock(), hq.ReadAsync);
        var firm = Firm();
        var inFlight = catalogue.GetAsync(firm);
        await hq.Entered.Task;
        hq.Name = "PIPE 2\""; hq.Stamp = "s2"; // the save commits ...
        catalogue.Invalidate(firm.Id);        // ... and invalidates
        hq.Hold.SetResult();
        Assert.Equal("PIPE", (await inFlight).Items[0].Name); // that caller gets what it read

        hq.Hold = null;
        var next = await catalogue.GetAsync(firm);
        Assert.Equal("PIPE 2\"", next.Items[0].Name);
        Assert.Equal(2, hq.Reads);
    }

    [Fact]
    public async Task Unchanged_stamp_after_an_invalidate_in_flight_is_not_cached_either()
    {
        var hq = new FakeHq();
        var clock = new Clock();
        var catalogue = new Catalogue(clock, hq.ReadAsync);
        var firm = Firm();
        await catalogue.GetAsync(firm);
        clock.Now += TimeSpan.FromSeconds(4); // past the stamp interval: the next call checks the stamp
        hq.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hq.Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var check = catalogue.GetAsync(firm);
        await hq.Entered.Task;
        catalogue.Invalidate(firm.Id);
        hq.Hold.SetResult();
        await check;
        hq.Hold = null;
        await catalogue.GetAsync(firm);
        Assert.Equal(3, hq.Reads);
        Assert.Null(hq.KnownStamps[2]); // invalidated: the masters are read again, not just the stamp
    }

    [Fact]
    public async Task Same_stamp_reuses_the_snapshot_and_reads_only_the_stamp()
    {
        var hq = new FakeHq();
        var clock = new Clock();
        var catalogue = new Catalogue(clock, hq.ReadAsync);
        var firm = Firm();
        var first = await catalogue.GetAsync(firm);
        Assert.Same(first, await catalogue.GetAsync(firm)); // within 3 s: no look at HQ at all
        Assert.Equal(1, hq.Reads);
        clock.Now += TimeSpan.FromSeconds(4);
        Assert.Same(first, await catalogue.GetAsync(firm));
        Assert.Equal(2, hq.Reads);
        Assert.Equal(1, hq.FullReads);
        Assert.Equal("s1", hq.KnownStamps[1]);
    }

    [Fact]
    public async Task A_change_the_stamp_misses_still_shows_within_the_maximum_age()
    {
        var hq = new FakeHq();
        var clock = new Clock();
        var catalogue = new Catalogue(clock, hq.ReadAsync);
        var firm = Firm();
        var first = await catalogue.GetAsync(firm);
        hq.Name = "PIPE RENAMED"; // the stamp stays "s1"
        clock.Now += TimeSpan.FromSeconds(30);
        Assert.Equal("PIPE", (await catalogue.GetAsync(firm)).Items[0].Name);
        clock.Now += Catalogue.MaxAge;
        var later = await catalogue.GetAsync(firm);
        Assert.Equal("PIPE RENAMED", later.Items[0].Name);
        Assert.NotEqual(first.Stamp, later.Stamp); // so the browser's ETag changes and it does not get a 304
    }

    [Fact]
    public async Task Forced_reread_with_the_same_content_keeps_the_etag()
    {
        var hq = new FakeHq();
        var clock = new Clock();
        var catalogue = new Catalogue(clock, hq.ReadAsync);
        var firm = Firm();
        var first = await catalogue.GetAsync(firm);
        clock.Now += Catalogue.MaxAge + TimeSpan.FromSeconds(1);
        var again = await catalogue.GetAsync(firm);
        Assert.Equal(2, hq.FullReads);
        Assert.Same(first, again);
    }

    [Fact]
    public void Content_stamp_follows_content_only()
    {
        var a = Catalogue.ContentStamp([Item("PIPE")], []);
        Assert.Equal(a, Catalogue.ContentStamp([Item("PIPE")], []));
        Assert.NotEqual(a, Catalogue.ContentStamp([Item("PIPE ")], []));
        Assert.NotEqual(a, Catalogue.ContentStamp([Item("PIPE") with { Stock = 4 }], []));
    }

    [Fact]
    public void Stamp_watches_every_shown_column_and_the_ledger_sides()
    {
        // The browser keeps its list until the ETag changes; a column left out of the stamp is an edit nobody sees.
        foreach (var column in new[] { "Act_OpSign", "Act_Code", "Act_Cat", "Item_Pack", "Grp_ID", "Cat_ID", "Add_Line1", "Add_Line2", "Grp_Name", "Item_CatName" })
            Assert.Contains(column, Catalogue.StampSql);
        Assert.Contains("BINARY_CHECKSUM(SR_NO,Act_ID,Contra_ID,TYPE,AMOUNT", Catalogue.StampSql);
        // The live change feed appends to it, so it must stay one SELECT expression.
        Assert.StartsWith("SELECT", Catalogue.StampSql.Trim());
        Assert.DoesNotContain("CONCAT(", Catalogue.StampSql, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(")", Catalogue.StampSql.Trim());
    }

    // ------------------------------------------------------------------ firm profiles

    private static FirmProfile Profile(HqFirm firm, int skipAudit) => new()
    {
        Firm = firm,
        Config = new HqConfig(skipAudit, 0, 0, 0, 0, 0, 0, 0, 0),
        Types = new Dictionary<int, TransactionType>(),
        ChargeSets = new Dictionary<int, IReadOnlyList<ChargeRow>>(),
        PaymentModes = [],
        Salespeople = [],
        StateCaption = "37-Andhra Pradesh",
        LoadedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Profile_invalidated_during_a_load_is_loaded_again()
    {
        // A data-health repair changed HQ's settings while a screen was loading the profile: the old settings must not be
        // cached for the next 30 seconds of saves.
        var firm = Firm();
        var setting = 0;
        var loads = 0;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profiles = new FirmProfiles(new Clock(), async (f, ct) =>
        {
            loads++;
            var seen = setting;
            entered.TrySetResult();
            await hold.Task;
            return Profile(f, seen);
        });
        var inFlight = profiles.GetAsync(firm);
        await entered.Task;
        setting = 1;
        profiles.Invalidate(firm.Id);
        hold.SetResult();
        Assert.Equal(0, (await inFlight).Config.SkipAudit);
        Assert.Equal(1, (await profiles.GetAsync(firm)).Config.SkipAudit);
        Assert.Equal(2, loads);
        Assert.Equal(1, (await profiles.GetAsync(firm)).Config.SkipAudit);
        Assert.Equal(2, loads); // and the fresh one is cached
    }

    [Fact]
    public async Task Profile_is_cached_for_thirty_seconds()
    {
        var firm = Firm();
        var clock = new Clock();
        var loads = 0;
        var profiles = new FirmProfiles(clock, (f, ct) => { loads++; return Task.FromResult(Profile(f, 0)); });
        await profiles.GetAsync(firm);
        clock.Now += TimeSpan.FromSeconds(29);
        await profiles.GetAsync(firm);
        Assert.Equal(1, loads);
        clock.Now += TimeSpan.FromSeconds(2);
        await profiles.GetAsync(firm);
        Assert.Equal(2, loads);
    }

    [Fact]
    public void Dependency_injection_still_builds_both_caches_from_the_clock_alone()
    {
        // The test constructors take a reader no container registers; the container must pick the production ones.
        var services = new ServiceCollection().AddSingleton(TimeProvider.System).AddSingleton<Catalogue>().AddSingleton<FirmProfiles>();
        using var sp = services.BuildServiceProvider(validateScopes: true);
        Assert.NotNull(sp.GetRequiredService<Catalogue>());
        Assert.NotNull(sp.GetRequiredService<FirmProfiles>());
    }
}
