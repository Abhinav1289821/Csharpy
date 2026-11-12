using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Integrity;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Workflows;

/// <summary>Scratch copies of the latest live backups (mk-hq\Databases), both firms in one engine (cross-firm checks).</summary>
public sealed class ScratchHealth : IAsyncLifetime
{
    public const string Mbm = "MKHQ_HEALTH_MBM", Mp = "MKHQ_HEALTH_MP", Main = "MKHQ_HEALTH_MAIN";
    public static readonly string Backups = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Databases"));
    public ServiceProvider Engine { get; private set; } = null!;

    public static string Latest(string prefix) =>
        Directory.GetFiles(Directory.Exists(Backups) ? Backups : @"C:\MK_WORKSPACE\mk-hq\Databases", prefix + "_*.bak").OrderDescending().First();

    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Mbm, Latest("ISTMBM26"));
        await ScratchDatabase.RestoreAsync(Mp, Latest("ISTMP26"));
        await ScratchDatabase.RestoreAsync(Main, Latest("ISTMAIN"));
        Engine = ScratchDatabase.Engine("mbm", Mbm, configure: o =>
        {
            o.Firms =
            [
                o.Firms[0],
                new FirmOptions { Id = "mp", Name = "MP", Database = Mp, PeriodStart = new DateOnly(2026, 4, 1), PeriodEnd = new DateOnly(2027, 3, 31) },
            ];
            o.CompanyDatabase = Main;
        });
    }

    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

[Trait("Category", "Workflow")]
public sealed class IntegrityTests(ScratchHealth fx, ITestOutputHelper output) : IClassFixture<ScratchHealth>
{
    private IntegrityService Health => fx.Engine.GetRequiredService<IntegrityService>();

    [Fact]
    public async Task Every_check_runs_on_the_live_data_and_its_count_matches_its_listing()
    {
        Assert.NotEmpty(Health.Checks);
        foreach (var (firm, db) in new[] { ("mbm", ScratchHealth.Mbm), ("mp", ScratchHealth.Mp) })
        {
            var report = await Health.RunAsync(firm);
            await using var cn = new SqlConnection(TestDatabase.ConnectionString(db));
            foreach (var f in report.Findings)
            {
                output.WriteLine($"{firm} {f.Check.Id,-28} {f.Count,6}  {f.Check.Fix,-6} {f.Check.Title}{(f.Error is null ? "" : "  ERROR " + f.Error)}");
                Assert.True(f.Error is null, $"{firm} {f.Check.Id}: {f.Error}");
                if (f.Check.Scope == "cross") continue;
                // The verification inside every repair counts the check this way; it must agree with the listing.
                Assert.Equal(f.Count, await cn.ExecuteScalarAsync<int>(IntegrityService.Count(f.Check.DetectSql), commandTimeout: 300));
            }
        }
    }

    [Fact]
    public async Task Every_repair_clears_its_check_and_makes_no_other_check_worse()
    {
        // Cross-firm checks compare the two firms, so a repair in one firm can widen a difference until the same repair runs in
        // the other: they are compared once both firms are done.
        var cross = Health.Checks.Where(c => c.Scope == "cross").Select(c => c.Id).ToHashSet();
        var crossBefore = (await Health.RunAsync("mbm")).Findings.Where(f => cross.Contains(f.Check.Id)).ToDictionary(f => f.Check.Id, f => f.Count);
        foreach (var firm in new[] { "mbm", "mp" })
        {
            var before = (await Health.RunAsync(firm)).Findings.ToDictionary(f => f.Check.Id, f => f.Count);
            // Automatic repairs, and decision repairs as if the owner said yes: each must clear its check and worsen nothing.
            foreach (var c in Health.Checks.Where(c => (c.Fix == "auto" || (c.Fix == "owner" && c.FixSql is not null)) && before.GetValueOrDefault(c.Id) > 0))
            {
                var preview = await Health.PreviewRepairAsync(firm, c.Id);
                // One-at-a-time decisions: each finding in turn, taking the first (likeliest) choice offered.
                var picks = c.Key is null or { Each: true } ? [null] : preview.Items.ToList();
                var refused = 0;
                var leftByHq = 0;
                foreach (var item in picks)
                {
                    var options = item?.Options ?? preview.Options;
                    if (c.Choice is { Free: true } && options.Count == 0)
                    {
                        refused++; // no suggestion: waits for the owner to type the value
                        output.WriteLine($"{firm} {c.Id} {item?.Key}: no suggestion, waits for the owner's value");
                        continue;
                    }
                    Assert.True(c.Choice is null || options.Count > 0, $"{firm} {c.Id}: no choices offered for {item?.Text}");
                    if (item is not null) preview = await Health.PreviewRepairAsync(firm, c.Id);
                    try
                    {
                        var result = await Health.RepairAsync(firm, c.Id, Guid.NewGuid().ToString(), preview.Hash, decided: c.Fix == "owner",
                            key: item?.Key, choice: c.Choice is null ? null : options[0].Value);
                        leftByHq += result.After;
                        output.WriteLine($"{firm} repaired {c.Id} {item?.Key}{(c.Choice is null ? "" : " → " + options[0].Label)}: {result.Before} → {result.After} ({result.Message})");
                    }
                    // A decision may wait for something only the owner can supply (a GSTIN with no unique correction, an item
                    // HSN): such a repair refuses and changes nothing, which is the promise. Automatic repairs never may.
                    catch (MK.Hq.Domain.HqException e) when (c.Fix == "owner" && e.Code is "INTEGRITY_REPAIR_INCOMPLETE" or "INTEGRITY_NOTHING_TO_REPAIR" or "HQ_REFUSED")
                    {
                        refused++;
                        output.WriteLine($"{firm} {c.Id} {item?.Key}: refused, nothing changed ({e.Message}) {item?.Text}");
                    }
                }
                var now = (await Health.RunAsync(firm)).Findings.ToDictionary(f => f.Check.Id, f => f.Count);
                if (refused == picks.Count) Assert.Equal(before[c.Id], now[c.Id]);
                else if (c.Key is { Each: false } || c.RepairsWhere is not null) Assert.True(now[c.Id] < before[c.Id], $"{firm} {c.Id}: no finding was repaired");
                // Accounts HQ's own rules refuse stay listed (left by HQ); everything else must be clear.
                else if (c.Key is { Each: true }) Assert.True(now[c.Id] <= leftByHq, $"{firm} {c.Id}: {now[c.Id]} left, HQ refused {leftByHq}");
                else Assert.Equal(0, now[c.Id]);
                foreach (var (id, count) in now.Where(x => !cross.Contains(x.Key) && !c.Reveals.Contains(x.Key)))
                    Assert.True(count <= before.GetValueOrDefault(id), $"{firm}: repairing {c.Id} made {id} worse ({before.GetValueOrDefault(id)} → {count})");
                before = now;
            }
        }
        var crossAfter = (await Health.RunAsync("mbm")).Findings.Where(f => cross.Contains(f.Check.Id)).ToDictionary(f => f.Check.Id, f => f.Count);
        var revealed = Health.Checks.SelectMany(c => c.Reveals).ToHashSet();
        foreach (var (id, count) in crossAfter.Where(x => !revealed.Contains(x.Key)))
            Assert.True(count <= crossBefore.GetValueOrDefault(id), $"the repairs made {id} worse ({crossBefore.GetValueOrDefault(id)} → {count})");
    }
}
