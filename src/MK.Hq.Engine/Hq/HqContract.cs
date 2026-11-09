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
    private readonly ConcurrentDictionary<string, ContractStatus> _status = new();
    private string FilePath(HqFirm firm) => Path.Combine(options.DataDirectory, $"hq-contract.{firm.Database}.json");

    public ContractStatus? Status(string firmId) => _status.GetValueOrDefault(firmId);

    public async Task<ContractStatus> CheckAsync(HqFirm firm, bool acceptCurrent = false, CancellationToken ct = default)
    {
        await using var s = await firm.OpenAsync(ct);
        catalog.Invalidate(firm.Database);
        var current = (await catalog.AllAsync(s.Connection)).ToDictionary(k => k.Key, v => v.Value.DefinitionHash, StringComparer.OrdinalIgnoreCase);
        var rcsi = await s.Connection.ExecuteScalarAsync<bool>("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name=DB_NAME()");
        var missing = ProcedureCatalog.Allowed.Where(p => !current.ContainsKey(p)).Order().ToList();

        Directory.CreateDirectory(options.DataDirectory);
        var path = FilePath(firm);
        Dictionary<string, string>? pinned = null;
        if (File.Exists(path) && !acceptCurrent)
            pinned = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct));
        if (pinned is null)
            pinned = current;
        else
        {
            // A procedure newly added to the allow-list is pinned as first seen; otherwise it would never be fingerprinted.
            pinned = new Dictionary<string, string>(pinned, StringComparer.OrdinalIgnoreCase);
            foreach (var (name, hash) in current.Where(c => !pinned.ContainsKey(c.Key)).ToList()) pinned[name] = hash;
        }
        if (!File.Exists(path) || acceptCurrent || pinned.Count != (JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(path, ct))?.Count ?? -1))
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(pinned.OrderBy(k => k.Key).ToDictionary(), new JsonSerializerOptions { WriteIndented = true }), ct);
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

    public void RequireWritable(HqFirm firm)
    {
        var status = _status.GetValueOrDefault(firm.Id);
        Guard.Require(status is null || status.WritesAllowed, "HQ_CONTRACT_CHANGED", status?.Summary ?? "", FailureKind.Unsupported);
    }

    public async Task CheckAllAsync(CancellationToken ct = default)
    {
        foreach (var firm in firms.All) await CheckAsync(firm, ct: ct);
    }
}
