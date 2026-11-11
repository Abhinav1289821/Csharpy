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
    public const string Mbm = "MKHQ_HEALTH_MBM", Mp = "MKHQ_HEALTH_MP";
    public static readonly string Backups = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\Databases"));
    public ServiceProvider Engine { get; private set; } = null!;

    public static string Latest(string prefix) =>
        Directory.GetFiles(Directory.Exists(Backups) ? Backups : @"C:\MK_WORKSPACE\mk-hq\Databases", prefix + "_*.bak").OrderDescending().First();

    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Mbm, Latest("ISTMBM26"));
        await ScratchDatabase.RestoreAsync(Mp, Latest("ISTMP26"));
        Engine = ScratchDatabase.Engine("mbm", Mbm, configure: o => o.Firms =
        [
            o.Firms[0],
            new FirmOptions { Id = "mp", Name = "MP", Database = Mp, PeriodStart = new DateOnly(2026, 4, 1), PeriodEnd = new DateOnly(2027, 3, 31) },
        ]);
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
        foreach (var firm in new[] { "mbm", "mp" })
        {
            var before = (await Health.RunAsync(firm)).Findings.ToDictionary(f => f.Check.Id, f => f.Count);
            // Automatic repairs, and decision repairs as if the owner said yes: each must clear its check and worsen nothing.
            foreach (var c in Health.Checks.Where(c => (c.Fix == "auto" || (c.Fix == "owner" && c.FixSql is not null)) && before.GetValueOrDefault(c.Id) > 0))
            {
                var preview = await Health.PreviewRepairAsync(firm, c.Id);
                var result = await Health.RepairAsync(firm, c.Id, Guid.NewGuid().ToString(), preview.Hash, decided: c.Fix == "owner");
                output.WriteLine($"{firm} repaired {c.Id}: {result.Before} → {result.After}");
                var now = (await Health.RunAsync(firm)).Findings.ToDictionary(f => f.Check.Id, f => f.Count);
                Assert.Equal(0, now[c.Id]);
                foreach (var (id, count) in now)
                    Assert.True(count <= before.GetValueOrDefault(id), $"{firm}: repairing {c.Id} made {id} worse ({before.GetValueOrDefault(id)} → {count})");
                before = now;
            }
        }
    }
}
