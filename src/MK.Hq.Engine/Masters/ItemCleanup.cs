using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Engine.Masters;

/// <summary>One item's proposal, as tools/item-cleanup/propose.py exported it (proposals-&lt;firm&gt;.json).</summary>
public sealed record CleanupProposal
{
    public long Id { get; init; }
    public string Code { get; init; } = "";
    public string Group { get; init; } = "";
    public long GroupId { get; init; }
    public string Current { get; init; } = "";
    public string? Proposed { get; init; }
    /// <summary>The name with only the automatic fixes (used when the owner refuses one of the item's formatting rules).</summary>
    public string? AutoName { get; init; }
    /// <summary>Automatic, Tidy-up or Review.</summary>
    public string? Check { get; init; }
    public IReadOnlyList<string> Changes { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];
    public IReadOnlyList<long> DuplicateOf { get; init; } = [];
    public int Sold12 { get; init; }
    public string? LastSale { get; init; }
    public string? LastPurchase { get; init; }
    public decimal Stock { get; init; }
    public decimal SaleRate { get; init; }
    public bool Retire { get; init; }
    public bool Active { get; init; } = true;
    public string? Family { get; init; }
    public string? SuggestGroup { get; init; }
    public string? DataCheck { get; init; }
    public string Hsn { get; init; } = "";
    public decimal Gst { get; init; }
    public string? HsnSuggest { get; init; }
    public Dictionary<string, string>? Answers { get; init; }
}

public sealed record CleanupFile(string Firm, string Database, string Generated, Dictionary<string, string> Rules, IReadOnlyList<CleanupProposal> Items);

/// <summary>The owner's answer for one item. Null fields are "not decided".</summary>
public sealed record CleanupDecision
{
    /// <summary>The name to use (the proposal, or his own wording).</summary>
    public string? Name { get; init; }
    /// <summary>Keep the current name.</summary>
    public bool KeepName { get; init; }
    /// <summary>true = make inactive, false = keep active.</summary>
    public bool? Retire { get; init; }
    public long? GroupId { get; init; }
    public bool GroupLeave { get; init; }
    public string? Hsn { get; init; }
    public decimal? Gst { get; init; }
    public bool DataLeave { get; init; }
    /// <summary>excel, screen</summary>
    public string Source { get; init; } = "screen";
}

public sealed record CleanupItem(CleanupProposal Proposal, CleanupDecision? Decision, string Status, string? Message, CleanupChange? Pending);

/// <summary>What would be written to HQ for an item: only the fields that change.</summary>
public sealed record CleanupChange(string? Name, bool? Active, long? GroupId, string? Hsn, decimal? Gst)
{
    public bool IsEmpty => Name is null && Active is null && GroupId is null && Hsn is null && Gst is null;
}

public sealed record CleanupExample(string Before, string After);

public sealed record CleanupRule(string Rule, string Title, int Items, string? Decision, IReadOnlyList<CleanupExample> Examples);

public sealed record CleanupStage(string Key, string Title, int Total, int Open);

public sealed record CleanupSummary(string Firm, string? Generated, int Items, IReadOnlyList<CleanupStage> Stages, IReadOnlyList<CleanupRule> Rules,
    int ReadyToApply, int Applied, int Skipped, int Failed, IReadOnlyList<CleanupBatch> Batches);

public sealed record CleanupBatch(string Id, string Kind, string CreatedAt, int Applied, int Skipped, int Failed);

public sealed record CleanupProgress(string BatchId, int Applied, int Skipped, int Failed, int Remaining, IReadOnlyList<string> Messages);

/// <summary>
/// The owner's guided item clean-up. Proposals come from tools/item-cleanup; his answers are kept here (cleanup.db, never in
/// HQ) as he gives them, so he can stop and resume. Applying writes each item through <see cref="ItemMasterService"/> — the
/// HQ-identical Items-screen save — one item per transaction, skipping any item that changed in HQ since the list was made.
/// </summary>
public sealed class ItemCleanupService
{
    /// <summary>Changes the owner said need no approval; and formatting he approves rule by rule.</summary>
    public static readonly IReadOnlySet<string> Automatic = new HashSet<string> { "spaces", "capitals", "spelling", "split words", "ISI to HEAVY" };
    public static readonly IReadOnlySet<string> Tidy = new HashSet<string> { "units", "brackets", "inch mark", "inch size", "sizes", "singular", "brand", "paint order", "watt last" };

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _connectionString;
    private readonly FirmRegistry _firms;
    private readonly ItemMasterService _items;

    public ItemCleanupService(HqOptions options, FirmRegistry firms, ItemMasterService items)
    {
        _firms = firms;
        _items = items;
        Directory.CreateDirectory(options.DataDirectory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(options.DataDirectory, "cleanup.db"), Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        using var cn = Open();
        cn.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS cleanup_files(firm TEXT PRIMARY KEY, generated TEXT, rules TEXT NOT NULL, imported_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS cleanup_items(firm TEXT NOT NULL, item_id INTEGER NOT NULL, proposal TEXT NOT NULL, decision TEXT,
              status TEXT NOT NULL, message TEXT, batch TEXT, before TEXT, after TEXT, updated_at TEXT NOT NULL, PRIMARY KEY(firm, item_id));
            CREATE TABLE IF NOT EXISTS cleanup_rules(firm TEXT NOT NULL, rule TEXT NOT NULL, decision TEXT NOT NULL, PRIMARY KEY(firm, rule));
            CREATE TABLE IF NOT EXISTS cleanup_batches(id TEXT PRIMARY KEY, firm TEXT NOT NULL, kind TEXT NOT NULL, created_at TEXT NOT NULL);
            """);
    }

    private SqliteConnection Open()
    {
        var cn = new SqliteConnection(_connectionString);
        cn.Open();
        cn.Execute("PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        return cn;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    // ------------------------------------------------------------------ import

    /// <summary>Loads (or reloads) the proposal list. Answers already given on screen are kept when the proposal for that item
    /// is unchanged; answers written in the Excel sheet are taken over; applied items stay applied.</summary>
    public async Task<CleanupSummary> ImportAsync(string firmId, CleanupFile file, CancellationToken ct = default)
    {
        var firm = _firms.Get(firmId);
        Guard.Require(string.Equals(file.Firm, firmId, StringComparison.OrdinalIgnoreCase), "CLEANUP_WRONG_FIRM",
            $"This list is for {file.Firm}; choose that firm first");
        Guard.Require(file.Items.Count > 0, "CLEANUP_EMPTY", "The list has no items");
        await using var s = await firm.OpenAsync(ct);
        var groups = (await s.QueryAsync<(long Id, string Name)>("SELECT CAST(Grp_ID AS bigint), LTRIM(RTRIM(Grp_Name)) FROM dbo.IQGroups"))
            .GroupBy(g => g.Name.ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First().Id);

        using var cn = Open();
        using var tx = cn.BeginTransaction();
        var existing = cn.Query<(long Id, string Proposal, string? Decision, string Status)>(
            "SELECT item_id, proposal, decision, status FROM cleanup_items WHERE firm=@firmId", new { firmId }, tx).ToDictionary(x => x.Id);
        foreach (var p in file.Items)
        {
            var json = JsonSerializer.Serialize(p, Json);
            existing.TryGetValue(p.Id, out var old);
            if (old.Status is "applied" && old.Proposal is not null) continue; // done; a fresh list made after applying will show its new name
            var decision = FromExcel(p, groups);
            if (decision is null && old.Decision is not null && old.Proposal is not null && Same(old.Proposal, p)) decision = JsonSerializer.Deserialize<CleanupDecision>(old.Decision, Json);
            cn.Execute("""
                INSERT INTO cleanup_items(firm, item_id, proposal, decision, status, message, updated_at) VALUES (@firmId, @id, @json, @decision, 'open', NULL, @now)
                ON CONFLICT(firm, item_id) DO UPDATE SET proposal=excluded.proposal, decision=excluded.decision, status='open', message=NULL, updated_at=excluded.updated_at
                """, new { firmId, id = p.Id, json, decision = decision is null ? null : JsonSerializer.Serialize(decision, Json), now = Now() }, tx);
        }
        cn.Execute("""
            INSERT INTO cleanup_files(firm, generated, rules, imported_at) VALUES (@firmId, @generated, @rules, @now)
            ON CONFLICT(firm) DO UPDATE SET generated=excluded.generated, rules=excluded.rules, imported_at=excluded.imported_at
            """, new { firmId, generated = file.Generated, rules = JsonSerializer.Serialize(file.Rules, Json), now = Now() }, tx);
        tx.Commit();
        return Summary(firmId);
    }

    private static bool Same(string oldJson, CleanupProposal p)
    {
        var o = JsonSerializer.Deserialize<CleanupProposal>(oldJson, Json)!;
        return o.Current == p.Current && o.Proposed == p.Proposed && o.Hsn == p.Hsn && o.Gst == p.Gst && o.GroupId == p.GroupId;
    }

    /// <summary>Answers the owner wrote in the review sheet's blue columns.</summary>
    private static CleanupDecision? FromExcel(CleanupProposal p, IReadOnlyDictionary<string, long> groups)
    {
        if (p.Answers is not { Count: > 0 } a) return null;
        string? Get(string k) => a.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        bool? YesNo(string k) => Get(k)?.ToUpperInvariant() switch { "Y" or "YES" => true, "N" or "NO" => false, _ => null };
        var approve = YesNo("approve");
        var own = Get("ownName")?.ToUpperInvariant();
        var keep = YesNo("keepActive");
        var move = Get("moveToGroup");
        decimal? gst = decimal.TryParse(Get("setGst")?.TrimEnd('%'), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var g) ? g : null;
        return new CleanupDecision
        {
            Name = own ?? (approve == true ? p.Proposed : null),
            KeepName = own is null && approve == false,
            Retire = keep is null ? null : !keep.Value,
            GroupId = move is not null && groups.TryGetValue(move.ToUpperInvariant(), out var id) ? id : null,
            Hsn = Get("setHsn"), Gst = gst, Source = "excel",
        };
    }

    // ------------------------------------------------------------------ reading

    private List<CleanupItem> Items(string firmId, SqliteConnection cn, IReadOnlyDictionary<string, string> rules)
    {
        return cn.Query<(string Proposal, string? Decision, string Status, string? Message)>(
                "SELECT proposal, decision, status, message FROM cleanup_items WHERE firm=@firmId ORDER BY item_id", new { firmId })
            .Select(x =>
            {
                var p = JsonSerializer.Deserialize<CleanupProposal>(x.Proposal, Json)!;
                var d = x.Decision is null ? null : JsonSerializer.Deserialize<CleanupDecision>(x.Decision, Json);
                // Only open items wait to be written: applied, undone, skipped (changed in HQ) and failed ones need a fresh list or a new answer.
                var change = x.Status == "open" ? ChangeOf(p, d, rules) : null;
                return new CleanupItem(p, d, x.Status, x.Message, change is { IsEmpty: false } ? change : null);
            }).ToList();
    }

    private Dictionary<string, string> RuleDecisions(string firmId, SqliteConnection cn) =>
        cn.Query<(string Rule, string Decision)>("SELECT rule, decision FROM cleanup_rules WHERE firm=@firmId", new { firmId }).ToDictionary(x => x.Rule, x => x.Decision);

    /// <summary>What his answers so far mean for HQ (null fields are left as they are).</summary>
    public static CleanupChange ChangeOf(CleanupProposal p, CleanupDecision? d, IReadOnlyDictionary<string, string> rules)
    {
        string? name = null;
        if (d?.Name is { Length: > 0 } chosen) name = chosen;
        else if (!(d?.KeepName ?? false) && p.Proposed is not null)
        {
            var tidy = p.Changes.Where(Tidy.Contains).ToList();
            if (p.Check == "Automatic") name = p.Proposed;
            else if (p.Check == "Tidy-up")
            {
                if (tidy.All(t => rules.GetValueOrDefault(t) == "yes")) name = p.Proposed;
                else if (tidy.Any(t => rules.GetValueOrDefault(t) == "no")) name = p.AutoName; // only the automatic part
            }
        }
        // A name that differs only by a space before or after it still needs saving: HQ's item save trims it.
        if (name is not null && name == p.Current && !p.Changes.Contains("spaces")) name = null;
        bool? active = d?.Retire switch { true when p.Active => false, _ => null };
        long? group = d?.GroupId is { } gid && gid != p.GroupId ? gid : null;
        var hsn = d?.Hsn is { Length: > 0 } h && h != p.Hsn ? h : null;
        decimal? gst = d?.Gst is { } g && g != p.Gst ? g : null;
        return new CleanupChange(name, active, group, hsn, gst);
    }

    public CleanupSummary Summary(string firmId)
    {
        using var cn = Open();
        var file = cn.QueryFirstOrDefault<(string? Generated, string Rules)?>("SELECT generated, rules FROM cleanup_files WHERE firm=@firmId", new { firmId });
        var titles = file is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(file.Value.Rules, Json)!;
        var rules = RuleDecisions(firmId, cn);
        var items = Items(firmId, cn, rules);
        var live = items.Where(i => i.Status is not "applied").ToList();
        int Unanswered(Func<CleanupItem, bool> belongs, Func<CleanupItem, bool> answered) => live.Count(i => belongs(i) && !answered(i));
        static bool Review(CleanupItem i) => i.Proposal.Check == "Review" && i.Proposal.Proposed is not null;
        static bool Retire(CleanupItem i) => i.Proposal.Retire && i.Proposal.Active;
        static bool Data(CleanupItem i) => i.Proposal.DataCheck is not null && !i.Proposal.Retire;
        static bool Group(CleanupItem i) => i.Proposal.SuggestGroup is not null;
        static bool NameAnswered(CleanupItem i) => i.Decision is { } d && (d.Name is not null || d.KeepName);
        var stages = new List<CleanupStage>
        {
            new("automatic", "Spelling, capitals and spaces", live.Count(i => i.Proposal.Check == "Automatic"), 0),
            new("rules", "Formatting rules", titles.Keys.Count(Tidy.Contains), titles.Keys.Count(r => Tidy.Contains(r) && !rules.ContainsKey(r) && live.Any(i => i.Proposal.Changes.Contains(r)))),
            new("review", "Names to decide", live.Count(Review), Unanswered(Review, NameAnswered)),
            new("retire", "Items not used for a year", live.Count(Retire), Unanswered(Retire, i => i.Decision?.Retire is not null)),
            new("data", "GST and HSN to check", live.Count(Data), Unanswered(Data, i => i.Decision is { } d && (d.Hsn is not null || d.Gst is not null || d.DataLeave))),
            new("groups", "Items in the wrong group", live.Count(Group), Unanswered(Group, i => i.Decision is { } d && (d.GroupId is not null || d.GroupLeave))),
        };
        var ruleRows = titles.Where(t => Tidy.Contains(t.Key)).Select(t =>
        {
            var with = live.Where(i => i.Proposal.Check == "Tidy-up" && i.Proposal.Changes.Contains(t.Key)).ToList();
            var examples = with.OrderByDescending(i => i.Proposal.Sold12).Take(6).Select(i => new CleanupExample(i.Proposal.Current, i.Proposal.Proposed!)).ToList();
            return new CleanupRule(t.Key, t.Value, with.Count, rules.GetValueOrDefault(t.Key), examples);
        }).Where(r => r.Items > 0).OrderByDescending(r => r.Items).ToList();
        var batches = cn.Query<(string Id, string Kind, string CreatedAt)>("SELECT id, kind, created_at FROM cleanup_batches WHERE firm=@firmId ORDER BY created_at DESC LIMIT 10", new { firmId })
            .Select(b => new CleanupBatch(b.Id, b.Kind, b.CreatedAt,
                cn.ExecuteScalar<int>("SELECT COUNT(*) FROM cleanup_items WHERE batch=@id AND status IN ('applied','undone')", new { id = b.Id }),
                cn.ExecuteScalar<int>("SELECT COUNT(*) FROM cleanup_items WHERE batch=@id AND status='skipped'", new { id = b.Id }),
                cn.ExecuteScalar<int>("SELECT COUNT(*) FROM cleanup_items WHERE batch=@id AND status='failed'", new { id = b.Id }))).ToList();
        return new CleanupSummary(firmId, file?.Generated, items.Count, stages, ruleRows, live.Count(i => i.Pending is not null),
            items.Count(i => i.Status == "applied"), items.Count(i => i.Status == "skipped"), items.Count(i => i.Status == "failed"), batches);
    }

    /// <summary>The items of one stage, open ones first; "ids" fetches particular items (the other half of a duplicate).</summary>
    public IReadOnlyList<CleanupItem> List(string firmId, string stage, IReadOnlyCollection<long>? ids = null, int take = 200)
    {
        using var cn = Open();
        var rules = RuleDecisions(firmId, cn);
        var items = Items(firmId, cn, rules);
        if (ids is { Count: > 0 }) return items.Where(i => ids.Contains(i.Proposal.Id)).ToList();
        var live = items.Where(i => i.Status is not ("applied" or "undone"));
        IEnumerable<CleanupItem> pick = stage switch
        {
            "automatic" => live.Where(i => i.Proposal.Check == "Automatic").OrderByDescending(i => i.Proposal.Sold12),
            "review" => live.Where(i => i.Proposal.Check == "Review" && i.Proposal.Proposed is not null)
                .OrderBy(i => i.Decision is { } d && (d.Name is not null || d.KeepName)).ThenByDescending(i => i.Proposal.Sold12),
            "retire" => live.Where(i => i.Proposal.Retire && i.Proposal.Active).OrderBy(i => i.Proposal.Group).ThenBy(i => i.Proposal.Current),
            "data" => live.Where(i => i.Proposal.DataCheck is not null && !i.Proposal.Retire)
                .OrderBy(i => i.Decision is { } d && (d.Hsn is not null || d.Gst is not null || d.DataLeave)).ThenByDescending(i => i.Proposal.Sold12),
            "groups" => live.Where(i => i.Proposal.SuggestGroup is not null),
            "pending" => live.Where(i => i.Pending is not null),
            "done" => items.Where(i => i.Status is "applied" or "skipped" or "failed" or "undone"),
            _ when stage.StartsWith("rule:", StringComparison.Ordinal) => live.Where(i => i.Proposal.Check == "Tidy-up" && i.Proposal.Changes.Contains(stage[5..])),
            _ => throw HqException.Invalid("CLEANUP_STAGE", "Unknown list"),
        };
        return pick.Take(Math.Clamp(take, 1, 3000)).ToList();
    }

    // ------------------------------------------------------------------ answers

    /// <summary>Records answers (merging with earlier ones). Nothing is written to HQ.</summary>
    public CleanupSummary Decide(string firmId, IReadOnlyList<(long ItemId, CleanupDecision Decision)> answers)
    {
        using var cn = Open();
        using var tx = cn.BeginTransaction();
        foreach (var (id, d) in answers)
        {
            var row = cn.QueryFirstOrDefault<(string Proposal, string? Decision, string Status)?>("SELECT proposal, decision, status FROM cleanup_items WHERE firm=@firmId AND item_id=@id", new { firmId, id }, tx)
                ?? throw HqException.NotFound("CLEANUP_ITEM", $"Item {id} is not in the list");
            Guard.Require(row.Status is not "applied", "CLEANUP_APPLIED", "This item is already written to HQ; load a fresh list to change it again", FailureKind.Conflict);
            var old = row.Decision is null ? new CleanupDecision() : JsonSerializer.Deserialize<CleanupDecision>(row.Decision, Json)!;
            var merged = old with
            {
                Name = d.KeepName ? null : d.Name ?? old.Name, KeepName = d.KeepName || (d.Name is null && old.KeepName),
                Retire = d.Retire ?? old.Retire, GroupId = d.GroupLeave ? null : d.GroupId ?? old.GroupId, GroupLeave = d.GroupLeave || (d.GroupId is null && old.GroupLeave),
                Hsn = d.DataLeave ? null : d.Hsn ?? old.Hsn, Gst = d.DataLeave ? null : d.Gst ?? old.Gst, DataLeave = d.DataLeave || (d.Hsn is null && d.Gst is null && old.DataLeave),
                Source = "screen",
            };
            if (merged.Name is { } n)
            {
                Guard.Require(n.Trim().Length is > 0 and <= 70, "MASTER_FIELD_TOO_LONG", "A name has 1 to 70 characters");
                merged = merged with { Name = n.Trim().ToUpperInvariant() };
            }
            if (merged.Hsn is { } h) Guard.Require(System.Text.RegularExpressions.Regex.IsMatch(h, @"^(\d{4}|\d{6}|\d{8})$"), "CLEANUP_HSN", "An HSN code has 4, 6 or 8 digits");
            if (merged.Gst is { } g) Guard.Require(ItemMasterService.Rates.Contains(g), "ITEM_GST_RATE", "Choose a GST rate HQ knows");
            cn.Execute("UPDATE cleanup_items SET decision=@json, status=CASE WHEN status IN ('skipped','failed','undone') THEN 'open' ELSE status END, message=NULL, updated_at=@now WHERE firm=@firmId AND item_id=@id",
                new { firmId, id, json = JsonSerializer.Serialize(merged, Json), now = Now() }, tx);
        }
        tx.Commit();
        return Summary(firmId);
    }

    public CleanupSummary DecideRule(string firmId, string rule, string? decision)
    {
        Guard.Require(Tidy.Contains(rule), "CLEANUP_RULE", "Unknown rule");
        using var cn = Open();
        if (decision is null) cn.Execute("DELETE FROM cleanup_rules WHERE firm=@firmId AND rule=@rule", new { firmId, rule });
        else
        {
            Guard.Require(decision is "yes" or "no", "CLEANUP_RULE", "Answer yes or no");
            cn.Execute("INSERT INTO cleanup_rules(firm, rule, decision) VALUES (@firmId, @rule, @decision) ON CONFLICT(firm, rule) DO UPDATE SET decision=excluded.decision",
                new { firmId, rule, decision });
        }
        return Summary(firmId);
    }

    // ------------------------------------------------------------------ writing to HQ

    /// <summary>Writes up to <paramref name="take"/> decided changes to HQ (call again until Remaining is 0). Each item is one
    /// HQ-identical save with an operation id fixed by batch and item, so an interrupted batch resumes without repeating work.</summary>
    private async Task<CleanupProgress> ApplyCoreAsync(string firmId, string? batchId, int take, CancellationToken ct)
    {
        batchId = Batch(firmId, batchId, "apply");
        var messages = new List<string>();
        List<CleanupItem> todo;
        using (var cn = Open()) todo = Items(firmId, cn, RuleDecisions(firmId, cn)).Where(i => i.Pending is not null && i.Status is "open").Take(Math.Clamp(take, 1, 50)).ToList();
        foreach (var item in todo)
        {
            var p = item.Proposal;
            var c = item.Pending!;
            ItemRequest current;
            try { current = await _items.LoadAsync(firmId, p.Id, ct); }
            catch (HqException e) { Mark(firmId, p.Id, "failed", e.Message, batchId); messages.Add($"{p.Current}: {e.Message}"); continue; }
            // Changed in HQ since the list was made? Leave it alone; a fresh list will show it again.
            if (current.Name.Trim() != p.Current || (current.Hsn ?? "") != p.Hsn || current.GstRate != p.Gst || current.Active != p.Active || current.GroupId != p.GroupId)
            {
                Mark(firmId, p.Id, "skipped", "Changed in HQ after the list was made; make a fresh list to see it again", batchId);
                messages.Add($"{p.Current}: changed in HQ meanwhile, left as it is");
                continue;
            }
            var target = current with
            {
                Name = c.Name ?? current.Name, Active = c.Active ?? current.Active, GroupId = c.GroupId ?? current.GroupId,
                Hsn = c.Hsn ?? current.Hsn, GstRate = c.Gst ?? current.GstRate,
            };
            try
            {
                var preview = await _items.PreviewAsync(firmId, target, ct);
                await _items.SaveAsync(firmId, new ItemPost(OperationId(batchId, p.Id), preview.Hash, target), ct);
                Mark(firmId, p.Id, "applied", null, batchId, current, target);
            }
            catch (HqException e)
            {
                Mark(firmId, p.Id, "failed", e.Message, batchId);
                messages.Add($"{p.Current}: {e.Message}");
            }
        }
        return Progress(firmId, batchId, messages, remaining: () =>
        {
            using var cn = Open();
            return Items(firmId, cn, RuleDecisions(firmId, cn)).Count(i => i.Pending is not null && i.Status is "open");
        });
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _writing = new();

    /// <summary>Writes up to <paramref name="take"/> decided changes to HQ (call again until Remaining is 0). One run per
    /// firm at a time: a second click (or a second window) is refused rather than racing the first over the same items.</summary>
    public Task<CleanupProgress> ApplyAsync(string firmId, string? batchId, int take = 20, CancellationToken ct = default) =>
        OneAtATime(firmId, () => ApplyCoreAsync(firmId, batchId, take, ct));

    private async Task<CleanupProgress> OneAtATime(string firmId, Func<Task<CleanupProgress>> run)
    {
        var gate = _writing.GetOrAdd(firmId, _ => new SemaphoreSlim(1, 1));
        Guard.Require(await gate.WaitAsync(0), "CLEANUP_BUSY", "MK is already writing the clean-up to HQ; wait for it to finish", FailureKind.Conflict);
        try { return await run(); }
        finally { gate.Release(); }
    }

    private string Batch(string firmId, string? batchId, string kind)
    {
        using var cn = Open();
        if (batchId is not null && cn.ExecuteScalar<int>("SELECT COUNT(*) FROM cleanup_batches WHERE id=@batchId AND firm=@firmId", new { batchId, firmId }) == 1) return batchId;
        var id = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
        cn.Execute("INSERT INTO cleanup_batches(id, firm, kind, created_at) VALUES (@id, @firmId, @kind, @now)", new { id, firmId, kind, now = Now() });
        return id;
    }

    private void Mark(string firmId, long id, string status, string? message, string batch, ItemRequest? before = null, ItemRequest? after = null)
    {
        using var cn = Open();
        cn.Execute("UPDATE cleanup_items SET status=@status, message=@message, batch=@batch, before=COALESCE(@before, before), after=COALESCE(@after, after), updated_at=@now WHERE firm=@firmId AND item_id=@id",
            new { firmId, id, status, message, batch, before = before is null ? null : JsonSerializer.Serialize(before, Json), after = after is null ? null : JsonSerializer.Serialize(after, Json), now = Now() });
    }

    private CleanupProgress Progress(string firmId, string batchId, List<string> messages, Func<int> remaining, string done = "applied")
    {
        using var cn = Open();
        int Count(string status) => cn.ExecuteScalar<int>("SELECT COUNT(*) FROM cleanup_items WHERE firm=@firmId AND batch=@batchId AND status=@status", new { firmId, batchId, status });
        return new CleanupProgress(batchId, Count(done), Count("skipped"), Count("failed"), remaining(), messages);
    }

    private static string OperationId(string batch, long item) =>
        new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"cleanup:{batch}:{item}"))[..16]).ToString();
}
