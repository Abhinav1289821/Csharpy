using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Concurrency;

/// <summary>Async mutual exclusion per key inside this process (reference-counted, no leaks).</summary>
public sealed class KeyedAsyncLock
{
    // Users is only read or changed under _sync. A lock-free version (AddOrUpdate + Interlocked) let a releaser drop the
    // entry to 0 and remove it just after a new caller had re-incremented it, so a third caller created a second
    // semaphore for the same key and two callers ran "exclusively" at once.
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public int Users; }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public async Task<IAsyncDisposable> AcquireAsync(string key, TimeSpan timeout, CancellationToken ct = default)
    {
        Entry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out entry!)) _entries[key] = entry = new Entry();
            entry.Users++;
        }
        bool entered;
        try { entered = await entry.Gate.WaitAsync(timeout, ct); }
        catch { Release(key, entry, false); throw; }
        if (!entered)
        {
            Release(key, entry, false);
            throw HqException.Conflict("OPERATION_BUSY", "The same document is being saved right now; try again in a moment");
        }
        return new Releaser(() => Release(key, entry, true));
    }

    private void Release(string key, Entry entry, bool held)
    {
        if (held) entry.Gate.Release();
        lock (_sync)
        {
            if (--entry.Users == 0) _entries.Remove(key);
        }
    }

    private sealed class Releaser(Action release) : IAsyncDisposable
    {
        private int _done;
        public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref _done, 1) == 0) release(); return ValueTask.CompletedTask; }
    }
}

/// <summary>
/// Cross-process lease on this PC: an exclusively opened, delete-on-close file. Windows releases it the moment
/// the owning process exits, so a crashed terminal never leaves a stale lock behind.
/// </summary>
public sealed class FileLease : IAsyncDisposable
{
    private readonly FileStream _stream;
    private FileLease(FileStream stream, string key) { _stream = stream; Key = key; }
    public string Key { get; }

    public static async Task<FileLease> AcquireAsync(string directory, string key, TimeSpan timeout, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32] + ".lease";
        var path = Path.Combine(directory, name);
        var clock = Stopwatch.StartNew();
        var delay = 15;
        while (true)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 256, FileOptions.DeleteOnClose);
                try
                {
                    var owner = Encoding.UTF8.GetBytes($"{Environment.MachineName} pid={Environment.ProcessId} key={key} at={DateTime.UtcNow:O}");
                    stream.SetLength(0);
                    await stream.WriteAsync(owner, ct);
                    await stream.FlushAsync(ct);
                }
                catch
                {
                    // Opened but not handed out (cancelled, disk full): close it now. Left to the finalizer, the exclusive
                    // handle kept this operation "busy" for every later attempt, including this loop's own retries.
                    await stream.DisposeAsync();
                    throw;
                }
                return new FileLease(stream, key);
            }
            // UnauthorizedAccessException: the previous holder just closed its delete-on-close handle and Windows reports
            // the file as "delete pending" (ERROR_ACCESS_DENIED) for a moment. It is the same "busy" state as a sharing violation.
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && clock.Elapsed < timeout)
            {
                await Task.Delay(delay, ct);
                delay = Math.Min(delay * 2, 250);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw HqException.Conflict("OPERATION_BUSY", "Another MK window on this PC is saving the same thing; try again in a moment");
            }
        }
    }

    public ValueTask DisposeAsync() => _stream.DisposeAsync();
}

/// <summary>
/// HQ's own document edit lock (mdlGeneral.LockRecord): {ServerPath}\UserLocks\{Database}{DocId}.HQS opened for
/// write with read sharing. While we hold it, an HQ user opening the same bill sees "modifying by another user",
/// and while an HQ user has it open we cannot take it.
/// </summary>
public sealed class HqDocumentLock : IAsyncDisposable
{
    private readonly List<(FileStream Stream, string Path)> _held;
    private HqDocumentLock(List<(FileStream, string)> held) => _held = held;

    public static HqDocumentLock Acquire(string? serverPath, bool required, string database, IEnumerable<long> documentIds)
    {
        var ids = documentIds.Where(id => id > 0).Distinct().Order().ToList();
        var held = new List<(FileStream, string)>();
        if (ids.Count == 0) return new HqDocumentLock(held);
        var directory = serverPath is null ? null : Path.Combine(serverPath, "UserLocks");
        if (directory is null || !Directory.Exists(directory))
        {
            Guard.Supported(!required, "HQ_LOCK_FOLDER_MISSING",
                "HQ's UserLocks folder is not reachable, so an edit could collide with HQ. Check HqServerPath in settings.");
            return new HqDocumentLock(held);
        }
        try
        {
            foreach (var id in ids)
            {
                var path = Path.Combine(directory, database + id + ".HQS");
                try
                {
                    held.Add((new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read), path));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    throw HqException.Conflict("HQ_DOCUMENT_LOCKED",
                        "This document is open for editing in HQ (or another MK window). Close it there and try again.", new { documentId = id });
                }
            }
            return new HqDocumentLock(held);
        }
        catch
        {
            foreach (var (s, _) in held) s.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        foreach (var (stream, path) in _held)
        {
            stream.Dispose();
            // Never throw from here: this runs after COMMIT, and an exception would report a saved bill as failed.
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* HQ will reuse or replace it */ }
        }
        _held.Clear();
        return ValueTask.CompletedTask;
    }
}
