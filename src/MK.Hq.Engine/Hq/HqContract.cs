using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Hq;

public sealed record ContractStatus(string FirmId, bool WritesAllowed, string Summary, IReadOnlyList<string> ChangedProcedures, IReadOnlyList<string> MissingProcedures,
    bool ReadCommittedSnapshot, DateTime CheckedAt);

/// <summary>
/// The pinned shape of HQ this engine was verified against: a fingerprint of every allow-listed procedure's definition
/// and parameters, plus database settings the numbering protocol depends on (READ_COMMITTED_SNAPSHOT must be OFF, or
/// HQ PCs would read stale MAX(TNo) values past our uncommitted inserts). A vendor update that changes any of it stops
/// writes — never reads — until the parity suite has been re-run and the new contract accepted.
/// </summary>
public sealed class HqContract(HqOptions options, ProcedureCatalog catalog, FirmRegistry firms)
{
    /// <summary>A vendor update can land while MK keeps running for days; writes re-check the contract this often.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, ContractStatus> _status = new();
    private readonly SemaphoreSlim _gate = new(1, 1); // start-up, HQ health, Accept and writes may all check at once
    private string FilePath(HqFirm firm) => Path.Combine(options.DataDirectory, $"hq-contract.{firm.Database}.json");

    public ContractStatus? Status(string firmId) => _status.GetValueOrDefault(firmId);

    public async Task<ContractStatus> CheckAsync(HqFirm firm, bool acceptCurrent = false, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _status.TryRemove(firm.Id, out _);
            return await CheckCoreAsync(firm, acceptCurrent, ct);
        }
        finally { _gate.Release(); }
    }

    private async Task<ContractStatus> CheckCoreAsync(HqFirm firm, bool acceptCurrent, CancellationToken ct)
    {
        await using var s = await firm.OpenAsync(ct);
        catalog.Invalidate(firm.Database);
        var current = (await catalog.AllAsync(s.Connection)).ToDictionary(k => k.Key, v => v.Value.DefinitionHash, StringComparer.OrdinalIgnoreCase);
        var rcsi = await s.Connection.ExecuteScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=DB_NAME()");
        var missing = ProcedureCatalog.Allowed.Where(p => !current.ContainsKey(p)).Order().ToList();

        Directory.CreateDirectory(options.DataDirectory);
        var path = FilePath(firm);
        Dictionary<string, string>? stored = null;
        if (File.Exists(path) && !acceptCurrent)
        {
            try { stored = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct)); }
            catch (JsonException)
            {
                // Never re-pin silently from a damaged file: that would accept whatever HQ is now. Block writes until Accept.
                var damaged = new ContractStatus(firm.Id, false, $"The saved HQ contract ({Path.GetFileName(path)}) is damaged. Re-run the parity tests, then accept the current HQ.",
                    [], [], rcsi, DateTime.UtcNow);
                _status[firm.Id] = damaged;
                return damaged;
            }
        }
        Dictionary<string, string> pinned;
        if (stored is null)
            pinned = current;
        else
        {
            // A procedure newly added to the allow-list is pinned as first seen; otherwise it would never be fingerprinted.
            pinned = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, hash) in current.Where(c => !pinned.ContainsKey(c.Key)).ToList()) pinned[name] = hash;
        }
        if (stored is null || pinned.Count != stored.Count)
        {
            // Write-then-rename: a power cut mid-write must leave the old contract or the new one, never half a file.
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(pinned.OrderBy(k => k.Key).ToDictionary(), new JsonSerializerOptions { WriteIndented = true }), ct);
            File.Move(temp, path, overwrite: true);
        }
        var changed = current.Where(c => pinned.TryGetValue(c.Key, out var h) && h != c.Value).Select(c => c.Key)
            .Concat(pinned.Keys.Where(k => !current.ContainsKey(k))).Distinct().Order().ToList();
        var ok = changed.Count == 0 && missing.Count == 0 && !rcsi;
        var summary = ok ? "HQ matches the verified contract"
            : rcsi ? "READ_COMMITTED_SNAPSHOT is ON in this HQ database; MK cannot number bills safely alongside HQ"
            : missing.Count > 0 ? $"HQ procedures missing: {string.Join(", ", missing)}"
            : $"HQ was updated: {string.Join(", ", changed)} changed. Re-run the parity tests, then accept the new contract.";
        var status = new ContractStatus(firm.Id, ok, summary, changed, missing, rcsi, DateTime.UtcNow);
        _status[firm.Id] = status;
        return status;
    }

    /// <summary>Writes need a verified, recent contract. If the start-up check could not run (SQL Server was still starting)
    /// or the last check is old, check now; never treat "not checked" as "allowed".</summary>
    public async Task RequireWritableAsync(HqFirm firm, CancellationToken ct = default)
    {
        var status = _status.GetValueOrDefault(firm.Id);
        if (status is null || DateTime.UtcNow - status.CheckedAt > MaxAge)
            status = await CheckAsync(firm, ct: ct);
        Guard.Require(status.WritesAllowed, "HQ_CONTRACT_CHANGED", status.Summary, FailureKind.Unsupported);
    }

    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        foreach (var firm in firms.All) await CheckAsync(firm, ct: ct);
    }
}
