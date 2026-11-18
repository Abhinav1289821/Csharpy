using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MK.Hq.Engine.Concurrency;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Journal;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.WritePath;

/// <summary>
/// WriteCoordinator's settle and recovery paths that need no HQ answer. The firm points at a closed local port, so any
/// probe that does reach for SQL Server fails fast as "unavailable" (no database is touched).
/// </summary>
public sealed class WritePathCoordinatorTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Rig(WriteCoordinator Coordinator, EngineStore Store, FirmRegistry Firms, HqFirm Firm, HqOptions Options);

    private static Rig NewRig()
    {
        var options = new HqOptions
        {
            Server = "tcp:127.0.0.1,1",
            DataDirectory = Path.Combine(Path.GetTempPath(), "mkhq-writepath-coord", Guid.NewGuid().ToString("N")),
            Firms = [new FirmOptions { Id = "mbm", Name = "Test", Database = "MKHQ_NOWHERE_MBM26" }],
        };
        var catalog = new ProcedureCatalog();
        var firms = new FirmRegistry(options, catalog);
        var store = new EngineStore(options.DataDirectory);
        var coordinator = new WriteCoordinator(store, new PlanExecutor(new IndiaClock()), options, new HqContract(options, catalog, firms),
            NullLogger<WriteCoordinator>.Instance);
        return new Rig(coordinator, store, firms, firms.All.Single(), options);
    }

    private static string Begin(EngineStore store, OperationStatus status, string? evidence = null)
    {
        var id = Guid.NewGuid().ToString();
        store.BeginOperation(id, "mbm", "sale", "h");
        store.Update(id, status, 10, evidence, evidence is null ? null : "{\"documentId\":10}");
        return id;
    }

    [Fact]
    public async Task Settle_without_evidence_is_not_committed_without_asking_HQ()
    {
        var rig = NewRig();
        var id = Begin(rig.Store, OperationStatus.Pending);
        Assert.False(await rig.Coordinator.SettleAsync(rig.Firm, rig.Store.GetOperation(id)!));
    }

    [Fact]
    public async Task Settle_with_unreadable_evidence_is_unknown_not_an_exception()
    {
        // Used to throw JsonException out of SettleAsync (deserialised outside the try): in the commit path that escaped
        // RunAsync raw, and in recovery it stopped the whole pass.
        var rig = NewRig();
        var id = Begin(rig.Store, OperationStatus.Committing, "{not json");
        Assert.Null(await rig.Coordinator.SettleAsync(rig.Firm, rig.Store.GetOperation(id)!));
        Assert.Equal(OperationStatus.Committing, rig.Store.GetOperation(id)!.Status);
    }

    [Fact]
    public async Task Settle_when_HQ_cannot_be_reached_is_unknown_and_leaves_the_record_open()
    {
        var rig = NewRig();
        var evidence = JsonSerializer.Serialize(new WriteEvidence(WriteEvidence.Create, 10), Json);
        var id = Begin(rig.Store, OperationStatus.Uncertain, evidence);
        Assert.Null(await rig.Coordinator.SettleAsync(rig.Firm, rig.Store.GetOperation(id)!));
        Assert.Equal(OperationStatus.Uncertain, rig.Store.GetOperation(id)!.Status);
    }

    [Fact]
    public async Task Recovery_keeps_going_past_a_record_it_cannot_settle()
    {
        var rig = NewRig();
        var open = Begin(rig.Store, OperationStatus.Pending);                    // older: no evidence, provably not committed
        await Task.Delay(5);
        var bad = Begin(rig.Store, OperationStatus.Committing, "{not json");     // newest: listed (and settled) first
        Assert.Equal(1, await rig.Coordinator.RecoverAsync(rig.Firms));
        Assert.Equal(OperationStatus.Rejected, rig.Store.GetOperation(open)!.Status);
        Assert.Equal("HQ_NOT_COMMITTED", rig.Store.GetOperation(open)!.ErrorCode);
        Assert.Equal(OperationStatus.Committing, rig.Store.GetOperation(bad)!.Status);
    }

    [Fact]
    public async Task Recovery_leaves_an_operation_another_MK_is_saving_to_its_owner()
    {
        // A second MK started while the first is mid-save holds no lease on that operation: it must not settle it from a
        // stale listing (no evidence yet reads as "not committed").
        var rig = NewRig();
        var busy = Begin(rig.Store, OperationStatus.Pending);
        await using (await FileLease.AcquireAsync(Path.Combine(rig.Options.DataDirectory, "leases"), $"op:mbm:{busy}", TimeSpan.Zero))
        {
            Assert.Equal(0, await rig.Coordinator.RecoverAsync(rig.Firms));
            Assert.Equal(OperationStatus.Pending, rig.Store.GetOperation(busy)!.Status);
        }
        Assert.Equal(1, await rig.Coordinator.RecoverAsync(rig.Firms));
        Assert.Equal(OperationStatus.Rejected, rig.Store.GetOperation(busy)!.Status);
    }

    [Fact]
    public async Task Recovery_skips_firms_that_are_no_longer_configured()
    {
        var rig = NewRig();
        var id = Guid.NewGuid().ToString();
        rig.Store.BeginOperation(id, "gone", "sale", "h");
        Assert.Equal(0, await rig.Coordinator.RecoverAsync(rig.Firms));
        Assert.Equal(OperationStatus.Pending, rig.Store.GetOperation(id)!.Status);
    }

    [Fact]
    public async Task Run_refuses_an_operation_id_that_is_not_a_guid_before_anything_else()
    {
        var rig = NewRig();
        var e = await Assert.ThrowsAsync<MK.Hq.Domain.HqException>(() => rig.Coordinator.RunAsync(
            new WriteRequest<int>(rig.Firm, "not-a-guid", "sale", "h", [], _ => throw new InvalidOperationException())));
        Assert.Equal("INVALID_OPERATION_ID", e.Code);
    }

    [Fact]
    public void Evidence_round_trips_through_the_journal_json()
    {
        var evidence = new WriteEvidence(WriteEvidence.VoucherAmend, 42, Reference: "RCT/7") { AfterEntry = 900, ProbeSql = "SELECT 0" };
        var back = JsonSerializer.Deserialize<WriteEvidence>(JsonSerializer.Serialize(evidence, Json), Json)!;
        Assert.Equal(evidence, back);
        Assert.Equal(900, back.AfterEntry);
    }
}
