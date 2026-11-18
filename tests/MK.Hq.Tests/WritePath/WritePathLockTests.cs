using System.Reflection;
using MK.Hq.Domain;
using MK.Hq.Engine.Concurrency;

namespace MK.Hq.Tests.WritePath;

/// <summary>Edge cases of the in-process lock, the cross-process lease and HQ's .HQS document lock.</summary>
public sealed class WritePathLockTests
{
    private static string TempDir() => Path.Combine(Path.GetTempPath(), "mkhq-writepath-lock", Guid.NewGuid().ToString("N"));

    private static int Entries(KeyedAsyncLock locks) =>
        ((System.Collections.ICollection)typeof(KeyedAsyncLock).GetField("_entries", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(locks)!).Count;

    [Fact]
    public async Task Keyed_lock_cancelled_while_waiting_gives_back_its_place()
    {
        var locks = new KeyedAsyncLock();
        var held = await locks.AcquireAsync("k", TimeSpan.FromSeconds(1));
        using var cts = new CancellationTokenSource();
        var waiting = locks.AcquireAsync("k", TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await held.DisposeAsync();
        Assert.Equal(0, Entries(locks));
        await using var again = await locks.AcquireAsync("k", TimeSpan.Zero);
    }

    [Fact]
    public async Task Keyed_lock_zero_timeout_takes_a_free_key_and_refuses_a_held_one()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("k", TimeSpan.Zero);
        Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<HqException>(() => locks.AcquireAsync("k", TimeSpan.Zero))).Code);
        await using (await locks.AcquireAsync("other", TimeSpan.Zero)) { }
        await first.DisposeAsync();
        await first.DisposeAsync(); // a second dispose must not release someone else's hold
        var second = await locks.AcquireAsync("k", TimeSpan.Zero);
        await first.DisposeAsync();
        Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<HqException>(() => locks.AcquireAsync("k", TimeSpan.Zero))).Code);
        await second.DisposeAsync();
        Assert.Equal(0, Entries(locks));
    }

    [Fact]
    public async Task File_lease_cancelled_after_opening_does_not_keep_the_file()
    {
        // The handle used to leak when the owner-line write was cancelled; the key then stayed busy until a GC finalizer ran.
        var dir = TempDir();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FileLease.AcquireAsync(dir, "op:x", TimeSpan.FromSeconds(5), cts.Token));
        await using var lease = await FileLease.AcquireAsync(dir, "op:x", TimeSpan.Zero);
    }

    [Fact]
    public async Task File_lease_zero_timeout_refuses_a_held_key_and_frees_on_dispose()
    {
        var dir = TempDir();
        var held = await FileLease.AcquireAsync(dir, "op:y", TimeSpan.Zero);
        Assert.Equal("OPERATION_BUSY", (await Assert.ThrowsAsync<HqException>(() => FileLease.AcquireAsync(dir, "op:y", TimeSpan.Zero))).Code);
        await using (await FileLease.AcquireAsync(dir, "op:z", TimeSpan.Zero)) { }
        await held.DisposeAsync();
        Assert.Empty(Directory.GetFiles(dir)); // delete-on-close: nothing left behind
        await using var again = await FileLease.AcquireAsync(dir, "op:y", TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Hq_document_lock_blocks_a_second_holder_and_cleans_up()
    {
        var server = TempDir();
        Directory.CreateDirectory(Path.Combine(server, "UserLocks"));
        var first = HqDocumentLock.Acquire(server, true, "ISTMBM26", [12, 12, 0, -1]);
        Assert.True(File.Exists(Path.Combine(server, "UserLocks", "ISTMBM2612.HQS")));
        Assert.Single(Directory.GetFiles(Path.Combine(server, "UserLocks"))); // ids <= 0 (new documents) take no lock
        var e = Assert.Throws<HqException>(() => HqDocumentLock.Acquire(server, true, "ISTMBM26", [5, 12]));
        Assert.Equal("HQ_DOCUMENT_LOCKED", e.Code);
        Assert.False(File.Exists(Path.Combine(server, "UserLocks", "ISTMBM265.HQS")) && IsHeld(Path.Combine(server, "UserLocks", "ISTMBM265.HQS")));
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.False(File.Exists(Path.Combine(server, "UserLocks", "ISTMBM2612.HQS")));
        await using var again = HqDocumentLock.Acquire(server, true, "ISTMBM26", [5, 12]);
    }

    [Fact]
    public async Task Hq_document_lock_needs_the_folder_only_when_required()
    {
        var missing = TempDir();
        Assert.Equal("HQ_LOCK_FOLDER_MISSING", Assert.Throws<HqException>(() => HqDocumentLock.Acquire(missing, true, "DB", [1])).Code);
        Assert.Equal("HQ_LOCK_FOLDER_MISSING", Assert.Throws<HqException>(() => HqDocumentLock.Acquire(null, true, "DB", [1])).Code);
        await using (HqDocumentLock.Acquire(missing, false, "DB", [1])) { }
        await using (HqDocumentLock.Acquire(null, true, "DB", [])) { } // creating a document locks nothing
    }

    private static bool IsHeld(string path)
    {
        try { using var s = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read); return false; }
        catch (IOException) { return true; }
    }
}
