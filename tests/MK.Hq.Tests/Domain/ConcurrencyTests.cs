using MK.Hq.Domain;
using MK.Hq.Engine.Concurrency;

namespace MK.Hq.Tests.Domain;

/// <summary>Database-free checks of the in-process and cross-process write guards.</summary>
public sealed class ConcurrencyTests
{
    [Fact]
    public async Task Keyed_lock_never_admits_two_holders_of_one_key()
    {
        // Many short holders on one key: the old lock-free version could drop an entry that a new caller had just
        // re-acquired, so a later caller got a second semaphore and ran alongside it.
        var locks = new KeyedAsyncLock();
        var inside = 0;
        var maxInside = 0;
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 100_000; i++)
            {
                await using var held = await locks.AcquireAsync("op:same", TimeSpan.FromSeconds(30));
                var now = Interlocked.Increment(ref inside);
                InterlockedMax(ref maxInside, now);
                Interlocked.Decrement(ref inside);
            }
        })));
        Assert.Equal(1, maxInside);
    }

    [Fact]
    public async Task Keyed_lock_times_out_as_busy_and_recovers()
    {
        var locks = new KeyedAsyncLock();
        var first = await locks.AcquireAsync("k", TimeSpan.FromSeconds(1));
        var e = await Assert.ThrowsAsync<HqException>(() => locks.AcquireAsync("k", TimeSpan.FromMilliseconds(30)));
        Assert.Equal("OPERATION_BUSY", e.Code);
        await first.DisposeAsync();
        await using var again = await locks.AcquireAsync("k", TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task File_lease_handover_between_holders_never_fails()
    {
        // Back-to-back holders: the next open can land while Windows still has the old file "delete pending",
        // which surfaces as UnauthorizedAccessException rather than a sharing violation.
        var dir = Path.Combine(Path.GetTempPath(), "mkhq-lease", Guid.NewGuid().ToString("N"));
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
            {
                await using var lease = await FileLease.AcquireAsync(dir, "op:same", TimeSpan.FromSeconds(30));
            }
        })));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}
