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
    /// <summary>Plain words for the shop: what is wrong.</summary>
    public string Say { get; init; } = "";
    /// <summary>Plain words: the owner's question (decisions).</summary>
    public string Ask { get; init; } = "";
    /// <summary>Plain words: what the repair changes.</summary>
    public string Will { get; init; } = "";
    /// <summary>Technical: why it matters (audit notes, docs/audit).</summary>
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
    /// <summary>The repair is made one finding at a time: this column of the finding is passed to the repair as @Param.</summary>
    public IntegrityKey? Key { get; init; }
    /// <summary>The owner picks a value for the repair (e.g. which account a charge should post to) from OptionsSql.</summary>
    public IntegrityChoice? Choice { get; init; }
    /// <summary>Checks that must be clear in this firm before this repair may run (e.g. the setting before the bills).</summary>
    public IReadOnlyList<string> After { get; init; } = [];
    /// <summary>For a repair that settles only part of the findings (e.g. lines whose item now has an HSN): the condition, over
    /// the finding's columns, that picks that part. The repair must clear that part; the rest stays for the owner.</summary>
    public string? RepairsWhere { get; init; }
    /// <summary>Checks this repair can bring to light (a corrected GSTIN can show that two accounts are the same party).</summary>
    public IReadOnlyList<string> Reveals { get; init; } = [];
}

/// <param name="Each">The repair runs once per finding (all in one transaction) instead of for one chosen finding.</param>
public sealed record IntegrityKey(string Param, string Column, bool Each = false);

/// <param name="OptionsSql">Returns columns Value and Label, best candidates first.</param>
/// <param name="Free">The owner may also type a value of his own (e.g. the GSTIN from the party's bill); the options are suggestions.</param>
public sealed record IntegrityChoice(string Param, string Label, string OptionsSql, bool Free = false);

/// <param name="Options">Choices for this finding, when the choice depends on it (its OptionsSql uses the key's @Param).</param>
public sealed record RepairItem(string Key, string Text, IReadOnlyList<RepairOption>? Options = null);

public sealed record RepairOption(string Value, string Label);

public sealed record IntegrityFinding(IntegrityCheck Check, int Count, IReadOnlyList<string> Examples, string? Error);

public sealed record IntegrityReport(string FirmId, DateTime CheckedAt, IReadOnlyList<IntegrityFinding> Findings);

public sealed record RepairPreview(string CheckId, string Title, int Findings, string What, string Hash, IReadOnlyList<string> Examples)
{
    /// <summary>For one-at-a-time repairs: the findings that can be chosen.</summary>
    public IReadOnlyList<RepairItem> Items { get; init; } = [];
    public string? ChoiceLabel { get; init; }
    public IReadOnlyList<RepairOption> Options { get; init; } = [];
}

public sealed record RepairResult(string CheckId, int Before, int After, string Message);

public sealed record RepairLogEntry(string Id, string Firm, string CheckId, string Title, long Before, long After, string At, string Details);

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
    private readonly FirmProfiles _profiles;
    private readonly string _log;
    private readonly string? _companyDatabase;
    private readonly IReadOnlyList<IntegrityCheck> _checks;

    public IntegrityService(FirmRegistry firms, WriteCoordinator coordinator, Catalogue catalogue, FirmProfiles profiles, HqOptions options)
    {
        _profiles = profiles;
        _firms = firms;
        _coordinator = coordinator;
        _catalogue = catalogue;
        _companyDatabase = string.IsNullOrWhiteSpace(options.CompanyDatabase) ? null : options.CompanyDatabase.Trim();
        _checks = LoadCatalogue();
        // A cross-firm repair would write another firm's database outside that firm's write lock; they are decisions in HQ.
        Guard.Invariant(_checks.All(c => c.Scope != "cross" || c.FixSql is null), "Cross-firm checks cannot carry a repair");
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
        {
            foreach (var f in _firms.All) sql = sql.Replace("{" + f.Id.ToUpperInvariant() + "}", f.Database, StringComparison.Ordinal);
            if (_companyDatabase is not null) sql = sql.Replace("{MAIN}", _companyDatabase, StringComparison.Ordinal);
        }
        return sql;
    }

    /// <summary>Cross checks need every firm (and HQ's company database when they read it).</summary>
    private bool Runnable(IntegrityCheck c) =>
        c.Scope != "cross" || (_firms.All.Count >= 2 && (_companyDatabase is not null || !c.DetectSql.Contains("{MAIN}", StringComparison.Ordinal)));

    // ------------------------------------------------------------------ detection

    public async Task<IntegrityReport> RunAsync(string firmId, string? area = null, CancellationToken ct = default)
    {
        var firm = _firms.Get(firmId);
        var findings = new List<IntegrityFinding>();
        await using var s = await firm.OpenAsync(ct);
        foreach (var c in _checks.Where(c => area is null || c.Area == area))
        {
            if (!Runnable(c)) continue;
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
    public static string Count(string detect) => $"SELECT COUNT(*) FROM (\n{Body(detect)}\n) findings";

    private static string Body(string detect) =>
        System.Text.RegularExpressions.Regex.Replace(detect.Trim().TrimEnd(';'), @"\s+ORDER\s+BY\s+[^()]*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

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
        var items = new List<RepairItem>();
        if (c.Key is { Each: false } k)
            foreach (var r in rows.Take(100))
            {
                var key = Str(r[k.Column]);
                items.Add(new RepairItem(key, Text(r), c.Choice is { } ci && PerItem(c) ? await OptionsAsync(s, ci, c, key) : null));
            }
        var what = c.Fix == "owner" && c.Ask.Length > 0 ? c.Ask : c.Will.Length > 0 ? c.Will : c.Advice;
        // Findings: how many the repair will settle (a partial repair: only its part). The hash covers the whole check.
        var repairable = c.RepairsWhere is null ? rows.Count : await s.ScalarAsync<int>($"{Count(Sql(c))} WHERE ({c.RepairsWhere})");
        return new RepairPreview(c.Id, c.Title, repairable, what, Hash(c, rows), rows.Take(20).Select(Text).ToList())
        {
            Items = items,
            ChoiceLabel = c.Choice?.Label,
            Options = c.Choice is { } ch && !PerItem(c) ? await OptionsAsync(s, ch, c, null) : [],
        };
    }

    private static bool PerItem(IntegrityCheck c) =>
        c.Key is { } k && c.Choice is { } ch && ch.OptionsSql.Contains("@" + k.Param, StringComparison.OrdinalIgnoreCase);

    private static async Task<IReadOnlyList<RepairOption>> OptionsAsync(HqSession s, IntegrityChoice choice, IntegrityCheck c, string? key)
    {
        var args = new DynamicParameters();
        if (PerItem(c)) args.Add(c.Key!.Param, Arg(key!));
        return (await s.RowsAsync(choice.OptionsSql, args)).Select(r => new RepairOption(Str(r["Value"]), Str(r["Label"]))).ToList();
    }

    private static string Str(object? v) => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)?.Trim() ?? "";

    /// <summary>A chosen value as a query argument (numbers as numbers, so they compare with HQ's int columns).</summary>
    private static object Arg(string v) => long.TryParse(v, out var n) ? n : v;

    private static string Literal(string v) => long.TryParse(v, out var n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : "N'" + v.Replace("'", "''") + "'";

    /// <summary>What the owner reviewed: the check and every finding it listed. Hashing only the count let a repair rewrite a
    /// bill that entered the list after the review while another left it.</summary>
    private static string Hash(IntegrityCheck c, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var findings = rows.Select(r => string.Join("\u001f", r.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key + "=" + Convert.ToString(kv.Value, System.Globalization.CultureInfo.InvariantCulture))))
            .Order(StringComparer.Ordinal);
        var text = $"{c.Id}|{c.FixSql}|{c.EngineRepair}|{rows.Count}\n" + string.Join("\n", findings);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <param name="decided">For a finding that needs the owner's decision: he has read the question and chosen the repair.</param>
    /// <param name="key">For one-at-a-time repairs: which finding (its key column, as listed by the preview).</param>
    /// <param name="choice">For repairs with a choice: the owner's pick, one of the preview's options.</param>
    public async Task<RepairResult> RepairAsync(string firmId, string checkId, string operationId, string expectedHash, bool decided = false,
        string? key = null, string? choice = null, CancellationToken ct = default)
    {
        var c = Get(checkId);
        Guard.Supported(c.Fix == "auto" || (c.Fix == "owner" && decided), "INTEGRITY_NEEDS_DECISION", "This repair needs your decision first");
        Guard.Supported(c.FixSql is not null || c.EngineRepair is not null, "INTEGRITY_NOT_REPAIRABLE", "This finding has no prepared repair; it is handled in HQ");
        var single = c.Key is { Each: false };
        Guard.Require(!single || !string.IsNullOrWhiteSpace(key), "INTEGRITY_PICK_ONE", "Choose which one to repair");
        if (!single) key = null;
        Guard.Require(c.Choice is null || !string.IsNullOrWhiteSpace(choice), "INTEGRITY_CHOOSE", $"Choose: {c.Choice?.Label}");
        var firm = _firms.Get(firmId);
        var detect = Sql(c);
        // Every repair may use the firm's financial-year start (scripts that re-save an account rewrite its opening row).
        var args = new Dictionary<string, object?> { ["periodStart"] = firm.PeriodStart.ToDateTime(TimeOnly.MinValue) };
        if (single) args[c.Key!.Param] = Arg(key!.Trim());
        if (c.Choice is { } ch) args[ch.Param] = Arg(choice!.Trim());
        // One-at-a-time repairs verify that finding only; the others stay for their own decision.
        var part = c.RepairsWhere is { } w ? $" WHERE ({w})" : "";
        var verify = single ? $"{Count(detect)} WHERE findings.[{c.Key!.Column}]=@{c.Key.Param}" : Count(detect) + part;
        var probe = single ? $"{Count(detect)} WHERE findings.[{c.Key!.Column}]={Literal(key!.Trim())}" : Count(detect) + part;
        int before = 0;
        var examples = new List<string>();
        RepairResult result;
        if (c.FixSql is not null)
        {
            result = await _coordinator.RunAsync(new WriteRequest<RepairResult>(firm, operationId, "repair." + c.Id, expectedHash, [], async session =>
            {
                foreach (var first in c.After.Select(Get))
                    Guard.Require(await session.ScalarAsync<int>(Count(Sql(first))) == 0, "INTEGRITY_ORDER",
                        $"Do this first: {first.Title}", FailureKind.Conflict);
                var rows = await session.RowsAsync(detect);
                Guard.Require(Hash(c, rows) == expectedHash, "REVIEW_CHANGED", "The data changed since you looked; check again", FailureKind.Conflict);
                if (single && c.Key is { } k1)
                {
                    rows = rows.Where(r => Str(r[k1.Column]) == key!.Trim()).ToList();
                    Guard.Require(rows.Count > 0, "REVIEW_CHANGED", "That finding is no longer there; check again", FailureKind.Conflict);
                }
                if (c.Choice is { Free: true })
                    Guard.Require(choice!.Trim().Length <= 100, "INTEGRITY_CHOICE_INVALID", "That value is too long");
                else if (c.Choice is { } c1)
                    Guard.Require((await OptionsAsync(session, c1, c, key?.Trim())).Any(o => o.Value == choice!.Trim()), "INTEGRITY_CHOICE_INVALID",
                        $"“{choice}” is not one of the choices for {c1.Label.ToLowerInvariant()}");
                before = c.RepairsWhere is null ? rows.Count : await session.ScalarAsync<int>(Count(detect) + part);
                Guard.Require(before > 0, "INTEGRITY_NOTHING_TO_REPAIR", "Nothing here can be repaired yet; see the question above");
                examples = rows.Take(50).Select(Text).ToList();
                List<PlanStep> steps;
                if (c.Key is { Each: true } ke)
                {
                    // "Each" repairs: the statement once per finding (e.g. HQ's account re-save for every account listed), each
                    // behind its own savepoint. One HQ refuses (its own form rule, e.g. a GSTIN of the wrong length) is undone
                    // and stays listed for the owner; every other one must come out clear.
                    var keyRows = part.Length == 0 ? rows : await session.RowsAsync($"SELECT * FROM (\n{Body(detect)}\n) findings{part}");
                    var keys = keyRows.Select(r => Str(r[ke.Column])).Distinct().ToList();
                    var each = System.Text.RegularExpressions.Regex.Replace(c.FixSql, @"(?i)SET\s+XACT_ABORT\s+ON\s*;?", "");
                    // XACT_ABORT OFF for this step only (a SET inside sp_executesql ends with it): with it on, the first refusal
                    // would doom the whole transaction instead of returning to the savepoint.
                    var wrapped = $"""
                        SET XACT_ABORT OFF;
                        SAVE TRANSACTION mk_each;
                        BEGIN TRY
                        {each}
                        END TRY
                        BEGIN CATCH
                          IF XACT_STATE() = -1 THROW;
                          ROLLBACK TRANSACTION mk_each;
                          INSERT INTO #mk_refused(k, msg) VALUES (CAST(@{ke.Param} AS nvarchar(100)), ERROR_MESSAGE());
                        END CATCH
                        """;
                    steps =
                    [
                        new SqlStep("Refusals list", "IF OBJECT_ID('tempdb..#mk_refused') IS NULL CREATE TABLE #mk_refused(k nvarchar(100) NOT NULL, msg nvarchar(2000) NULL); ELSE TRUNCATE TABLE #mk_refused;",
                            new Dictionary<string, object?>()), // no parameters: a plain batch, so the table outlives this step
                        .. keys.Select(v => (PlanStep)new SqlStep($"{c.Title}: {v}", wrapped, new Dictionary<string, object?>(args) { [ke.Param] = Arg(v) })),
                        new SqlStep("Refused", "SELECT COUNT(*) FROM #mk_refused", args, CaptureScalar: "refused"),
                        new SqlStep("Why", "SELECT TOP 1 msg FROM #mk_refused ORDER BY msg", args, CaptureScalar: "refusedWhy"),
                        new VerifyStep("HQ accepts at least one", $"SELECT CASE WHEN (SELECT COUNT(*) FROM #mk_refused) < {keys.Count} THEN 0 ELSE 1 END", args, 0,
                            "HQ_REFUSED", "HQ's own rules refuse every one of these; nothing was changed. Open one in HQ to see why"),
                        new VerifyStep("Every accepted one is clear", $"{Count(detect)}{(part.Length > 0 ? part + " AND" : " WHERE")} CAST(findings.[{ke.Column}] AS nvarchar(100)) NOT IN (SELECT k FROM #mk_refused)",
                            args, 0, "INTEGRITY_REPAIR_INCOMPLETE", "The repair did not clear every finding; nothing was changed"),
                    ];
                }
                else steps =
                [
                    new SqlStep(c.Title, c.FixSql, args),
                    new VerifyStep("The check finds nothing any more", verify, args, 0,
                        "INTEGRITY_REPAIR_INCOMPLETE", "The repair did not clear every finding; nothing was changed"),
                ];
                var plan = new WritePlan($"Repair: {c.Title}", steps);
                return new PreparedWrite<RepairResult>(plan, o =>
                {
                    var refused = o.Variables.TryGetValue("refused", out var r) ? Convert.ToInt32(r, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    var why = refused > 0 && o.Variables.TryGetValue("refusedWhy", out var w) ? $"; {refused} left as they are (HQ: {w})" : "";
                    return (new RepairResult(c.Id, before, refused, $"{before - refused} repaired{why}"),
                        new WriteEvidence(WriteEvidence.Repair, 0, Reference: c.Id) { ProbeSql = refused > 0 ? null : probe });
                });
            }), ct);
        }
        else throw HqException.Unsupported("INTEGRITY_ENGINE_REPAIR", $"Engine repair '{c.EngineRepair}' is not available");
        _catalogue.Invalidate(firm.Id);
        _profiles.Invalidate(firm.Id); // config repairs (round-off type, charge accounts, bill types) change the firm profile
        Log(firmId, c, result.Before, result.After, JsonSerializer.Serialize(new { key, choice, examples }, Json));
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
