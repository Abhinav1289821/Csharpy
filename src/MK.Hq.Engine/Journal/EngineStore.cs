using Dapper;
using Microsoft.Data.Sqlite;
using MK.Hq.Domain;

namespace MK.Hq.Engine.Journal;

public enum OperationStatus { Pending, Committing, Committed, Rejected, Uncertain }

public sealed record OperationRecord(
    string Id, string Firm, string Kind, string RequestHash, OperationStatus Status,
    long? DocumentId, string? Evidence, string? Result, string? ErrorCode, string? ErrorMessage,
    string CreatedAt, string UpdatedAt);

public sealed record DraftRecord
{
    public string Id { get; init; } = "";
    public string Firm { get; init; } = "";
    public string Kind { get; init; } = "";
    public int Version { get; init; }
    public string Title { get; init; } = "";
    public string Payload { get; init; } = "";
    public string CreatedAt { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
}

/// <summary>
/// The engine's own durable state, in a local SQLite file (WAL, synchronous=FULL) — never in HQ's database.
/// <list type="bullet">
/// <item><b>operations</b>: an idempotency and crash-recovery journal. Evidence is written durably *before*
/// SQL Server COMMIT, so after a crash a single HQ read decides whether the write happened.</item>
/// <item><b>drafts</b>: bills being prepared (typed, scanned or AI-extracted) that are not in HQ yet.</item>
/// </list>
/// </summary>
public sealed class EngineStore
{
    private readonly string _connectionString;

    public EngineStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "mk-hq.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
        }.ToString();
        using var cn = Open();
        cn.Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS operations(
              id TEXT PRIMARY KEY, firm TEXT NOT NULL, kind TEXT NOT NULL, request_hash TEXT NOT NULL,
              status TEXT NOT NULL, document_id INTEGER, evidence TEXT, result TEXT, error_code TEXT, error_message TEXT,
              created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_operations_status ON operations(status);
            CREATE INDEX IF NOT EXISTS ix_operations_created ON operations(created_at);
            CREATE TABLE IF NOT EXISTS drafts(
              id TEXT PRIMARY KEY, firm TEXT NOT NULL, kind TEXT NOT NULL, version INTEGER NOT NULL, title TEXT NOT NULL,
              payload TEXT NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_drafts_firm ON drafts(firm, updated_at);
            """);
    }

    private SqliteConnection Open()
    {
        var cn = new SqliteConnection(_connectionString);
        cn.Open();
        cn.Execute("PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;");
        return cn;
    }

    private static string Now() => DateTime.UtcNow.ToString("O");

    private const string OperationColumns = "id Id, firm Firm, kind Kind, request_hash RequestHash, status Status, document_id DocumentId, evidence Evidence, result Result, error_code ErrorCode, error_message ErrorMessage, created_at CreatedAt, updated_at UpdatedAt";

    public OperationRecord? GetOperation(string id)
    {
        using var cn = Open();
        var row = cn.QueryFirstOrDefault($"SELECT {OperationColumns} FROM operations WHERE id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<OperationRecord> Operations(string? firm, int limit, bool unsettledOnly = false)
    {
        using var cn = Open();
        var where = unsettledOnly ? "status IN ('Pending','Committing','Uncertain')" : "1=1";
        if (firm is not null) where += " AND firm=@firm";
        return cn.Query($"SELECT {OperationColumns} FROM operations WHERE {where} ORDER BY created_at DESC LIMIT @limit", new { firm, limit })
            .Select(Map).ToList();
    }

    /// <summary>Creates a pending record or returns the existing one with the same id.</summary>
    public OperationRecord BeginOperation(string id, string firm, string kind, string requestHash)
    {
        using var cn = Open();
        var now = Now();
        cn.Execute("INSERT OR IGNORE INTO operations(id,firm,kind,request_hash,status,created_at,updated_at) VALUES(@id,@firm,@kind,@requestHash,'Pending',@now,@now)",
            new { id, firm, kind, requestHash, now });
        return GetOperation(id)!;
    }

    public void Update(string id, OperationStatus status, long? documentId = null, string? evidence = null, string? result = null, string? errorCode = null, string? errorMessage = null)
    {
        using var cn = Open();
        cn.Execute("""
            UPDATE operations SET status=@status, document_id=COALESCE(@documentId, document_id), evidence=COALESCE(@evidence, evidence),
              result=COALESCE(@result, result), error_code=@errorCode, error_message=@errorMessage, updated_at=@now WHERE id=@id
            """, new { id, status = status.ToString(), documentId, evidence, result, errorCode, errorMessage, now = Now() });
    }

    private static OperationRecord Map(dynamic r) => new(
        (string)r.Id, (string)r.Firm, (string)r.Kind, (string)r.RequestHash, Enum.Parse<OperationStatus>((string)r.Status),
        (long?)r.DocumentId, (string?)r.Evidence, (string?)r.Result, (string?)r.ErrorCode, (string?)r.ErrorMessage, (string)r.CreatedAt, (string)r.UpdatedAt);

    // ---------------- drafts ----------------

    private const string DraftColumns = "id Id, firm Firm, kind Kind, version Version, title Title, payload Payload, created_at CreatedAt, updated_at UpdatedAt";

    public IReadOnlyList<DraftRecord> Drafts(string firm)
    {
        using var cn = Open();
        return cn.Query<DraftRecord>($"SELECT {DraftColumns} FROM drafts WHERE firm=@firm ORDER BY updated_at DESC LIMIT 200", new { firm }).ToList();
    }

    public DraftRecord? Draft(string id)
    {
        using var cn = Open();
        return cn.QueryFirstOrDefault<DraftRecord>($"SELECT {DraftColumns} FROM drafts WHERE id=@id", new { id });
    }

    /// <summary>Optimistic save: <paramref name="expectedVersion"/> 0 creates; otherwise it must match.</summary>
    public DraftRecord SaveDraft(string id, string firm, string kind, string title, string payload, int expectedVersion)
    {
        using var cn = Open();
        var now = Now();
        var changed = expectedVersion == 0
            ? cn.Execute("INSERT OR IGNORE INTO drafts(id,firm,kind,version,title,payload,created_at,updated_at) VALUES(@id,@firm,@kind,1,@title,@payload,@now,@now)",
                new { id, firm, kind, title, payload, now })
            : cn.Execute("UPDATE drafts SET version=version+1, title=@title, payload=@payload, kind=@kind, updated_at=@now WHERE id=@id AND firm=@firm AND version=@expectedVersion",
                new { id, firm, kind, title, payload, now, expectedVersion });
        Guard.Require(changed == 1, "DRAFT_CHANGED", "This draft was changed elsewhere; reload it", FailureKind.Conflict);
        return Draft(id)!;
    }

    public bool DeleteDraft(string id, string firm)
    {
        using var cn = Open();
        return cn.Execute("DELETE FROM drafts WHERE id=@id AND firm=@firm", new { id, firm }) == 1;
    }
}
