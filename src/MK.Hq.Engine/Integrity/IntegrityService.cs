using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Integrity;

/// <summary>
/// One data-health check, as audited against the shop's live data (docs/audit/*.md). Detection is a read-only query that
/// returns one row per finding (a text column <c>Finding</c>). A repair is either a set-based statement tested to clear the
/// check, or one of the engine's HQ-identical write paths (see <see cref="IntegrityService.EngineRepairs"/>).
/// </summary>
public sealed record IntegrityCheck
{
    public string Id { get; init; } = "";
    public string Area { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary>error, warning, info</summary>
    public string Severity { get; init; } = "warning";
    public string Why { get; init; } = "";
    public string DetectSql { get; init; } = "";
    /// <summary>firm (runs in each firm's database) or cross (compares the two; {MBM} and {MP} are the database names).</summary>
    public string Scope { get; init; } = "firm";
    /// <summary>auto, owner, report</summary>
    public string Fix { get; init; } = "report";
    public string? FixSql { get; init; }
    /// <summary>For auto repairs done through the engine's HQ-identical services instead of SQL (e.g. "stock").</summary>
    public string? EngineRepair { get; init; }
    /// <summary>What to do (auto), the question to ask (owner) or what to know (report), in plain words.</summary>
    public string Advice { get; init; } = "";
    /// <summary>Restrict a repair to findings dated on or after this day (earlier months' GST returns are likely filed).</summary>
    public bool SinceGstLock { get; init; }
}

public sealed record IntegrityFinding(IntegrityCheck Check, int Count, IReadOnlyList<string> Examples, string? Error);

public sealed record IntegrityReport(string FirmId, DateTime CheckedAt, IReadOnlyList<IntegrityFinding> Findings);

public sealed record RepairPreview(string CheckId, string Title, int Findings, string What, string Hash, IReadOnlyList<string> Examples);

public sealed record RepairResult(string CheckId, int Before, int After, string Message);

public sealed record RepairLogEntry(string Id, string Firm, string CheckId, string Title, int Before, int After, string At, string Details);

/// <summary>
/// The data-health programme: runs every check in the catalogue (Integrity/checks.json) and performs the automatic
/// repairs through the same write path as bills — one SERIALIZABLE transaction under MK's per-firm write lock, the repair,
/// then the check re-run inside the transaction and required to find nothing, else everything rolls back. Every repair is
/// logged locally (integrity.db), never in HQ.
/// </summary>
public sealed class IntegrityService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly FirmRegistry _firms;
    private readonly WriteCoordinator _coordinator;
    private readonly Catalogue _catalogue;
    private readonly string _log;
    private readonly IReadOnlyList<IntegrityCheck> _checks;

    public IntegrityService(FirmRegistry firms, WriteCoordinator coordinator, Catalogue catalogue, HqOptions options)
    {
        _firms = firms;
        _coordinator = coordinator;
        _catalogue = catalogue;
        _checks = LoadCatalogue();
        Directory.CreateDirectory(options.DataDirectory);
        _log = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDirectory, "integrity.db") }.ToString();
        using var cn = new SqliteConnection(_log);
        cn.Open();
        cn.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS repairs(id TEXT PRIMARY KEY, firm TEXT NOT NULL, check_id TEXT NOT NULL, title TEXT NOT NULL,
              before_count INTEGER NOT NULL, after_count INTEGER NOT NULL, at TEXT NOT NULL, details TEXT NOT NULL);
            """);
    }

    public IReadOnlyList<IntegrityCheck> Checks => _checks;

    private static IReadOnlyList<IntegrityCheck> LoadCatalogue()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MK.Hq.Engine.Integrity.checks.json")
            ?? throw HqException.Internal("The data-health catalogue is missing from the engine");
        var checks = JsonSerializer.Deserialize<List<IntegrityCheck>>(stream, Json)!;
        Guard.Invariant(checks.Select(c => c.Id).Distinct().Count() == checks.Count, "Duplicate data-health check ids");
        return checks;
    }

    private string Sql(IntegrityCheck c)
    {
        var sql = c.DetectSql;
        if (c.Scope == "cross")
            foreach (var f in _firms.All) sql = sql.Replace("{" + f.Id.ToUpperInvariant() + "}", f.Database, StringComparison.Ordinal);
        return sql;
    }

    // ------------------------------------------------------------------ detection

    public async Task<IntegrityReport> RunAsync(string firmId, string? area = null, CancellationToken ct = default)
    {
        var firm = _firms.Get(firmId);
        var findings = new List<IntegrityFinding>();
        await using var s = await firm.OpenAsync(ct);
        foreach (var c in _checks.Where(c => area is null || c.Area == area))
        {
            if (c.Scope == "cross" && _firms.All.Count < 2) continue;
            try
            {
                var rows = await s.RowsAsync(Sql(c));
                findings.Add(new IntegrityFinding(c, rows.Count, rows.Take(20).Select(Text).ToList(), null));
            }
            catch (Exception e) when (e is HqException or Microsoft.Data.SqlClient.SqlException)
            {
                findings.Add(new IntegrityFinding(c, 0, [], e.Message));
            }
        }
        return new IntegrityReport(firmId, DateTime.UtcNow, findings);
    }

    private static string Text(IReadOnlyDictionary<string, object?> row) =>
        row.TryGetValue("Finding", out var f) && f is not null ? Convert.ToString(f, System.Globalization.CultureInfo.InvariantCulture)! : string.Join(" · ", row.Values);

    private IntegrityCheck Get(string id) => _checks.FirstOrDefault(c => c.Id == id) ?? throw HqException.NotFound("INTEGRITY_CHECK", $"No check {id}");

    /// <summary>The check as a count (catalogue queries never start with WITH; a final ORDER BY is dropped).</summary>
    public static string Count(string detect) =>
        $"SELECT COUNT(*) FROM (\n{System.Text.RegularExpressions.Regex.Replace(detect.Trim().TrimEnd(';'), @"\s+ORDER\s+BY\s+[^()]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase)}\n) findings";

    // ------------------------------------------------------------------ repairs

    /// <summary>Repairs done through the engine's HQ-identical services rather than SQL, by name.</summary>
    public static readonly IReadOnlySet<string> EngineRepairs = new HashSet<string> { "stock" };

    public async Task<RepairPreview> PreviewRepairAsync(string firmId, string checkId, CancellationToken ct = default)
    {
        var c = Get(checkId);
        Guard.Supported(c.Fix != "report" && (c.FixSql is not null || c.EngineRepair is not null), "INTEGRITY_NOT_REPAIRABLE", "This finding has no prepared repair; it is handled in HQ");
        var firm = _firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var rows = await s.RowsAsync(Sql(c));
        return new RepairPreview(c.Id, c.Title, rows.Count, c.Advice, Hash(c, rows.Count), rows.Take(20).Select(Text).ToList());
    }

    private static string Hash(IntegrityCheck c, int count) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{c.Id}|{c.FixSql}|{c.EngineRepair}|{count}"))).ToLowerInvariant();

    /// <param name="decided">For a finding that needs the owner's decision: he has read the question and chosen the repair.</param>
    public async Task<RepairResult> RepairAsync(string firmId, string checkId, string operationId, string expectedHash, bool decided = false, CancellationToken ct = default)
    {
        var c = Get(checkId);
        Guard.Supported(c.Fix == "auto" || (c.Fix == "owner" && decided), "INTEGRITY_NEEDS_DECISION", "This repair needs your decision first");
        Guard.Supported(c.FixSql is not null || c.EngineRepair is not null, "INTEGRITY_NOT_REPAIRABLE", "This finding has no prepared repair; it is handled in HQ");
        var firm = _firms.Get(firmId);
        var detect = Sql(c);
        int before = 0;
        var examples = new List<string>();
        RepairResult result;
        if (c.FixSql is not null)
        {
            result = await _coordinator.RunAsync(new WriteRequest<RepairResult>(firm, operationId, "repair." + c.Id, expectedHash, [], async session =>
            {
                var rows = await session.RowsAsync(detect);
                before = rows.Count;
                examples = rows.Take(50).Select(Text).ToList();
                Guard.Require(Hash(c, before) == expectedHash, "REVIEW_CHANGED", "The data changed since you looked; check again", FailureKind.Conflict);
                var plan = new WritePlan($"Repair: {c.Title}", [
                    new SqlStep(c.Title, c.FixSql, new Dictionary<string, object?>()),
                    new VerifyStep("The check finds nothing any more", Count(detect), new Dictionary<string, object?>(), 0,
                        "INTEGRITY_REPAIR_INCOMPLETE", "The repair did not clear every finding; nothing was changed"),
                ]);
                return new PreparedWrite<RepairResult>(plan, _ => (new RepairResult(c.Id, before, 0, $"{before} repaired"),
                    new WriteEvidence(WriteEvidence.Repair, 0, Reference: c.Id) { ProbeSql = Count(detect) }));
            }), ct);
        }
        else throw HqException.Unsupported("INTEGRITY_ENGINE_REPAIR", $"Engine repair '{c.EngineRepair}' is not available");
        _catalogue.Invalidate(firm.Id);
        Log(firmId, c, result.Before, result.After, JsonSerializer.Serialize(new { examples }, Json));
        return result;
    }

    private void Log(string firmId, IntegrityCheck c, int before, int after, string details)
    {
        using var cn = new SqliteConnection(_log);
        cn.Open();
        cn.Execute("INSERT INTO repairs(id, firm, check_id, title, before_count, after_count, at, details) VALUES (@id, @firmId, @check, @title, @before, @after, @at, @details)",
            new { id = Guid.NewGuid().ToString(), firmId, check = c.Id, title = c.Title, before, after, at = DateTime.UtcNow.ToString("O"), details });
    }

    public IReadOnlyList<RepairLogEntry> Log(string firmId)
    {
        using var cn = new SqliteConnection(_log);
        cn.Open();
        return cn.Query<RepairLogEntry>("SELECT id Id, firm Firm, check_id CheckId, title Title, before_count Before, after_count After, at At, details Details FROM repairs WHERE firm=@firmId ORDER BY at DESC LIMIT 200",
            new { firmId }).ToList();
    }
}
