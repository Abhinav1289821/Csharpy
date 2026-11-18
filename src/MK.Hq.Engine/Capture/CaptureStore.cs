using Dapper;
using Microsoft.Data.Sqlite;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Capture;

public sealed record CaptureRecord
{
    public string Id { get; init; } = "";
    public string Firm { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "";
    public string Source { get; init; } = "";
    public int Pages { get; init; }
    public string? Model { get; init; }
    public string? DraftId { get; init; }
    public string? Summary { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
    public long? DocumentId { get; init; }
    public string? Reference { get; init; }
    public int Attempts { get; init; }
    public long? ElapsedMs { get; init; }
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

/// <summary>
/// Photos (one capture = one bill, any number of pages) and their extraction state, in a local SQLite file next to the
/// journal. Also the learned "written wording → HQ item" aliases. Never stored in HQ's database.
/// </summary>
public sealed class CaptureStore
{
    public static class States
    {
        public const string Received = "received", Extracting = "extracting", Ready = "ready", Failed = "failed", Saved = "saved", Discarded = "discarded";
    }

    private readonly string _cs;
    private const string Columns = """
        c.id Id, c.firm Firm, c.kind Kind, c.status Status, c.source Source, (SELECT COUNT(*) FROM pages p WHERE p.capture_id=c.id) Pages,
        c.model Model, c.draft_id DraftId, c.summary Summary, c.error_code ErrorCode, c.error_message ErrorMessage, c.document_id DocumentId,
        c.reference Reference, c.attempts Attempts, c.elapsed_ms ElapsedMs, c.created_at CreatedAt, c.updated_at UpdatedAt
        """;

    public CaptureStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _cs = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataDirectory, "captures.db"), Mode = SqliteOpenMode.ReadWriteCreate, ForeignKeys = true }.ToString();
        using var cn = Open();
        cn.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS captures(
              id TEXT PRIMARY KEY, firm TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL, source TEXT NOT NULL,
              model TEXT, extraction TEXT, draft_id TEXT, summary TEXT, error_code TEXT, error_message TEXT, document_id INTEGER, reference TEXT,
              attempts INTEGER NOT NULL DEFAULT 0, elapsed_ms INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_captures_firm ON captures(firm, created_at);
            CREATE TABLE IF NOT EXISTS pages(
              capture_id TEXT NOT NULL REFERENCES captures(id) ON DELETE CASCADE, page INTEGER NOT NULL, image BLOB NOT NULL, original BLOB NOT NULL,
              rotation INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(capture_id, page));
            CREATE TABLE IF NOT EXISTS aliases(
              firm TEXT NOT NULL, key TEXT NOT NULL, item_id INTEGER NOT NULL, unit_id INTEGER NOT NULL, uses INTEGER NOT NULL, updated_at TEXT NOT NULL,
              PRIMARY KEY(firm, key));
            """);
    }

    private SqliteConnection Open()
    {
        var cn = new SqliteConnection(_cs);
        cn.Open();
        cn.Execute("PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;");
        return cn;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    public CaptureRecord Add(string firm, string kind, string source, byte[] original, byte[] normalized)
    {
        var id = Guid.NewGuid().ToString();
        using var cn = Open();
        using var tx = cn.BeginTransaction();
        cn.Execute("INSERT INTO captures(id,firm,kind,status,source,created_at,updated_at) VALUES(@id,@firm,@kind,'received',@source,@now,@now)",
            new { id, firm, kind, source, now = Now() }, tx);
        cn.Execute("INSERT INTO pages(capture_id,page,image,original) VALUES(@id,1,@normalized,@original)", new { id, normalized, original }, tx);
        tx.Commit();
        return Get(id)!;
    }

    public int AddPage(string id, byte[] original, byte[] normalized)
    {
        using var cn = Open();
        // MAX+1 and the insert in one (immediate) transaction: two pages uploaded at once must not pick the same number.
        using var tx = cn.BeginTransaction();
        var page = cn.ExecuteScalar<int>("SELECT COALESCE(MAX(page),0)+1 FROM pages WHERE capture_id=@id", new { id }, tx);
        Guard.Require(page <= 10, "CAPTURE_PAGES", "A bill can have at most 10 pages");
        cn.Execute("INSERT INTO pages(capture_id,page,image,original) VALUES(@id,@page,@normalized,@original)", new { id, page, normalized, original }, tx);
        tx.Commit();
        return page;
    }

    public void ReplacePage(string id, int page, byte[] normalized, int rotation)
    {
        using var cn = Open();
        cn.Execute("UPDATE pages SET image=@normalized, rotation=@rotation WHERE capture_id=@id AND page=@page", new { id, page, normalized, rotation });
    }

    public (byte[] Original, int Rotation) Original(string id, int page)
    {
        using var cn = Open();
        return cn.QueryFirstOrDefault<(byte[], int)?>("SELECT original, rotation FROM pages WHERE capture_id=@id AND page=@page", new { id, page })
            ?? throw HqException.NotFound("CAPTURE_PAGE_NOT_FOUND", "That page is not in this capture");
    }

    public void RemovePage(string id, int page)
    {
        using var cn = Open();
        using var tx = cn.BeginTransaction();
        Guard.Require(cn.ExecuteScalar<int>("SELECT COUNT(*) FROM pages WHERE capture_id=@id", new { id }, tx) > 1, "CAPTURE_PAGES", "A capture needs at least one page; discard it instead");
        cn.Execute("DELETE FROM pages WHERE capture_id=@id AND page=@page; UPDATE pages SET page=page-1 WHERE capture_id=@id AND page>@page", new { id, page }, tx);
        tx.Commit();
    }

    public IReadOnlyList<byte[]> Images(string id)
    {
        using var cn = Open();
        var pages = cn.Query<byte[]>("SELECT image FROM pages WHERE capture_id=@id ORDER BY page", new { id }).ToList();
        Guard.Require(pages.Count > 0, "CAPTURE_NOT_FOUND", "That photo is no longer here", FailureKind.NotFound);
        return pages;
    }

    public byte[] Image(string id, int page)
    {
        using var cn = Open();
        return cn.ExecuteScalar<byte[]?>("SELECT image FROM pages WHERE capture_id=@id AND page=@page", new { id, page })
            ?? throw HqException.NotFound("CAPTURE_PAGE_NOT_FOUND", "That page is not in this capture");
    }

    public CaptureRecord? Get(string id)
    {
        using var cn = Open();
        return cn.QueryFirstOrDefault<CaptureRecord>($"SELECT {Columns} FROM captures c WHERE c.id=@id", new { id });
    }

    public IReadOnlyList<CaptureRecord> List(string firm, int take = 60)
    {
        using var cn = Open();
        return cn.Query<CaptureRecord>($"SELECT {Columns} FROM captures c WHERE c.firm=@firm AND c.status<>'discarded' ORDER BY c.created_at DESC LIMIT @take", new { firm, take }).ToList();
    }

    /// <summary>Captures to read at start-up. A read interrupted by a restart goes back to "received" so it can be claimed again.</summary>
    public IReadOnlyList<string> Pending()
    {
        using var cn = Open();
        cn.Execute("UPDATE captures SET status='received' WHERE status='extracting'");
        return cn.Query<string>("SELECT id FROM captures WHERE status='received' ORDER BY created_at").ToList();
    }

    public string? Extraction(string id)
    {
        using var cn = Open();
        return cn.ExecuteScalar<string?>("SELECT extraction FROM captures WHERE id=@id", new { id });
    }

    /// <summary>Sets the status (and any given fields). With <paramref name="onlyFrom"/>, only if the capture is still in that
    /// status; returns whether the row changed, so state changes are compare-and-set rather than check-then-act.</summary>
    public bool Update(string id, string status, string? model = null, string? extraction = null, string? draftId = null, string? summary = null,
        string? errorCode = null, string? errorMessage = null, long? documentId = null, string? reference = null, bool countAttempt = false, long? elapsedMs = null,
        string? onlyFrom = null)
    {
        using var cn = Open();
        return cn.Execute("""
            UPDATE captures SET status=@status, model=COALESCE(@model,model), extraction=COALESCE(@extraction,extraction), draft_id=COALESCE(@draftId,draft_id),
              summary=COALESCE(@summary,summary), error_code=@errorCode, error_message=@errorMessage, document_id=COALESCE(@documentId,document_id),
              reference=COALESCE(@reference,reference), attempts=attempts+@attempt, elapsed_ms=COALESCE(@elapsedMs,elapsed_ms), updated_at=@now
            WHERE id=@id AND (@onlyFrom IS NULL OR status=@onlyFrom)
            """, new { id, status, model, extraction, draftId, summary, errorCode, errorMessage, documentId, reference, attempt = countAttempt ? 1 : 0, elapsedMs, now = Now(), onlyFrom }) == 1;
    }

    public IReadOnlyDictionary<string, (long ItemId, long UnitId)> Aliases(string firm)
    {
        using var cn = Open();
        return cn.Query<(string Key, long ItemId, long UnitId)>("SELECT key, item_id, unit_id FROM aliases WHERE firm=@firm", new { firm })
            .ToDictionary(x => x.Key, x => (x.ItemId, x.UnitId));
    }

    public void Learn(string firm, IEnumerable<(string Key, long ItemId, long UnitId)> pairs)
    {
        using var cn = Open();
        using var tx = cn.BeginTransaction();
        foreach (var (key, item, unit) in pairs.Where(p => p.Key.Length is > 1 and <= 400))
            cn.Execute("""
                INSERT INTO aliases(firm,key,item_id,unit_id,uses,updated_at) VALUES(@firm,@key,@item,@unit,1,@now)
                ON CONFLICT(firm,key) DO UPDATE SET item_id=@item, unit_id=@unit, uses=uses+1, updated_at=@now
                """, new { firm, key, item, unit, now = Now() }, tx);
        tx.Commit();
    }
}
