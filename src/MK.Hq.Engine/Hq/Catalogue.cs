using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Hq;

public sealed record CatalogueItem(
    long ItemId, long UnitId, string Code, string Name, string Unit, string Hsn,
    decimal SaleRate, decimal PurchaseRate, decimal Mrp,
    decimal Cgst, decimal Sgst, decimal Igst, bool TaxConfigured,
    decimal Stock, bool Active, string Group, string Category)
{
    public TaxRates Taxes => new(Cgst, Sgst, Igst);
}

public sealed record CatalogueParty(
    long AccountId, string Name, string Type, string Code, string Town, string Phone, string Mobile,
    string Gstin, int GstType, string State, bool BillWise, bool Active, decimal Balance, string Address)
{
    public bool IsCustomer => Type is "Customer" or "Customer-Supplier";
    public bool IsSupplier => Type is "Supplier" or "Customer-Supplier";
    public bool IsBank => Type == "Bank";
}

/// <param name="Stamp">A fingerprint of the content itself (the browser's ETag): it changes exactly when what the browser
/// would show changes, whichever way the change was noticed.</param>
public sealed record CatalogueSnapshot(string Stamp, IReadOnlyList<CatalogueItem> Items, IReadOnlyList<CatalogueParty> Parties, DateTime LoadedAt);

/// <summary>What one look at HQ found: its change stamp, and the masters when they were read (null when the stamp was the
/// one already held, so nothing needed reading).</summary>
public sealed record CatalogueRead(string Stamp, IReadOnlyList<CatalogueItem>? Items, IReadOnlyList<CatalogueParty>? Parties);

/// <summary>Reads HQ for the catalogue. <paramref name="knownStamp"/>: the stamp already held; null forces a full read.</summary>
public delegate Task<CatalogueRead> CatalogueReader(HqFirm firm, string? knownStamp, CancellationToken ct);

/// <summary>
/// Item and account masters, cached per firm and invalidated by a cheap change stamp (row counts and
/// BINARY_CHECKSUM aggregates over the columns that matter), so HQ-side edits appear within seconds while
/// typing in the browser never waits for SQL Server. A change the stamp cannot see (two edits whose checksums cancel)
/// still shows within <see cref="MaxAge"/>, when the masters are read again regardless.
/// </summary>
public sealed class Catalogue
{
    private static readonly TimeSpan StampInterval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _time;
    private readonly CatalogueReader _read;
    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    // Bumped by Invalidate: a read that started before a save must not be stored after it (it would serve the pre-save
    // masters as fresh for up to a minute, e.g. a just-saved party's balance or a new item missing from the list).
    private readonly Dictionary<string, long> _generations = new();
    private readonly object _sync = new();

    private sealed record Entry(CatalogueSnapshot Snapshot, string DbStamp, DateTimeOffset CheckedAt, DateTimeOffset ReadAt);

    public Catalogue(TimeProvider time) : this(time, ReadAsync) { }

    /// <summary>For tests: any reader in place of SQL Server.</summary>
    public Catalogue(TimeProvider time, CatalogueReader read)
    {
        _time = time;
        _read = read;
    }

    /// <summary>
    /// Every column the catalogue shows or derives from (a party's balance comes from its master opening and sign and from
    /// every ledger row's account, side, amount and contra). IQDayBook carries a checksum too: count, last row and the plain
    /// sum of amounts cannot see an entry moved from debit to credit or between accounts. Group and category names are
    /// shown on items, so a rename counts. Measured at about 25 ms on the MBM demo copy, every 3 s while a screen is open.
    /// </summary>
    public const string StampSql = """
        SELECT
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(MAX(Item_ID),0))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Item_ID,Item_Name,Item_Code,Item_Pack,CGST,SGST,IGST,HSNCODE,Item_Stat,Stk,Grp_ID,Cat_ID)),0)) FROM dbo.IQItems_Mast) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Item_ID,UNit_Id,Unit_Srate,Unit_Prate,Unit_MRP,PP_SaleRate,ReportPack)),0)) FROM dbo.IQItems_Units) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Grp_ID,Grp_Name)),0)) FROM dbo.IQGroups) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Item_CatID,Item_CatName)),0)) FROM dbo.IQItems_Cat) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(MAX(Act_Id),0))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Act_Id,Act_Name,Act_Code,Act_Type,Act_Cat,Act_Usage,Bill2Bill,Act_OPbal,Act_OpSign)),0)) FROM dbo.IQAccmst) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Act_Id,Act_Town,Act_Ph1,Act_Cell,GSTIN,GSTTaxType,Act_state,Add_Line1,Add_Line2)),0)) FROM dbo.IQAccAdr) + '|' +
          (SELECT CONVERT(varchar(40),COUNT(*))+'.'+CONVERT(varchar(40),ISNULL(MAX(SR_NO),0))+'.'+CONVERT(varchar(40),ISNULL(SUM(CAST(AMOUNT AS decimal(19,2))),0))+'.'+CONVERT(varchar(40),ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(SR_NO,Act_ID,Contra_ID,TYPE,AMOUNT,Doc_ID,TRDATE)),0)) FROM dbo.IQDayBook)
        """;

    public async Task<CatalogueSnapshot> GetAsync(HqFirm firm, CancellationToken ct = default)
    {
        if (Fresh(firm.Id) is { } quick) return quick;
        var gate = _gates.GetOrAdd(firm.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (Fresh(firm.Id) is { } fresh) return fresh;
            var generation = Generation(firm.Id);
            _cache.TryGetValue(firm.Id, out var hit);
            var now = _time.GetUtcNow();
            var forceRead = hit is null || now - hit.ReadAt >= MaxAge;
            var read = await _read(firm, forceRead ? null : hit!.DbStamp, ct);
            Entry entry;
            if (read.Items is null || read.Parties is null)
            {
                // The stamp is the one already held: nothing changed.
                Guard(hit is not null && hit.DbStamp == read.Stamp, "The catalogue reader skipped the masters although the stamp changed");
                entry = hit! with { CheckedAt = now };
            }
            else
            {
                var snapshot = new CatalogueSnapshot(ContentStamp(read.Items, read.Parties), read.Items, read.Parties, DateTime.UtcNow);
                // Unchanged content keeps its snapshot (and so the browser's ETag).
                if (hit is not null && hit.Snapshot.Stamp == snapshot.Stamp) snapshot = hit.Snapshot;
                entry = new Entry(snapshot, read.Stamp, now, now);
            }
            Store(firm.Id, entry, generation);
            return entry.Snapshot;
        }
        finally { gate.Release(); }
    }

    private CatalogueSnapshot? Fresh(string firmId) =>
        _cache.TryGetValue(firmId, out var hit) && _time.GetUtcNow() - hit.CheckedAt < StampInterval ? hit.Snapshot : null;

    private long Generation(string firmId)
    {
        lock (_sync) return _generations.GetValueOrDefault(firmId);
    }

    /// <summary>Caches what was read unless a save invalidated the firm while it was being read; that caller still gets
    /// what it read (as it would have a moment earlier), and the next one reads again.</summary>
    private void Store(string firmId, Entry entry, long generation)
    {
        lock (_sync)
            if (_generations.GetValueOrDefault(firmId) == generation) _cache[firmId] = entry;
    }

    public void Invalidate(string firmId)
    {
        lock (_sync)
        {
            _generations[firmId] = _generations.GetValueOrDefault(firmId) + 1;
            _cache.TryRemove(firmId, out _);
        }
    }

    private static void Guard(bool condition, string message)
    {
        if (!condition) throw MK.Hq.Domain.HqException.Internal(message);
    }

    /// <summary>A fingerprint of everything the browser receives.</summary>
    public static string ContentStamp(IReadOnlyList<CatalogueItem> items, IReadOnlyList<CatalogueParty> parties)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(items));
        hash.AppendData([0]);
        hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(parties));
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>The SQL Server reader: one connection for the stamp and, when it changed, the masters.</summary>
    public static async Task<CatalogueRead> ReadAsync(HqFirm firm, string? knownStamp, CancellationToken ct)
    {
        await using var s = await firm.OpenAsync(ct);
        var stamp = await s.ScalarAsync<string>(StampSql);
        if (knownStamp is not null && stamp == knownStamp) return new CatalogueRead(stamp, null, null);
        return new CatalogueRead(stamp, await ItemsAsync(s), await PartiesAsync(s));
    }

    public static async Task<IReadOnlyList<CatalogueItem>> ItemsAsync(HqSession s, long? itemId = null) =>
        await s.QueryAsync<CatalogueItem>("""
            SELECT CAST(im.Item_ID AS bigint) ItemId, CAST(iu.UNit_Id AS bigint) UnitId,
              LTRIM(RTRIM(ISNULL(im.Item_Code,''))) Code, LTRIM(RTRIM(ISNULL(im.Item_Name,''))) Name,
              LTRIM(RTRIM(COALESCE(NULLIF(LTRIM(RTRIM(iu.ReportPack)),''), im.Item_Pack, ''))) Unit,
              LTRIM(RTRIM(ISNULL(im.HSNCODE,''))) Hsn,
              CAST(ISNULL(COALESCE(iu.PP_SaleRate, iu.Unit_Srate),0) AS decimal(19,4)) SaleRate,
              CAST(ISNULL(iu.Unit_Prate,0) AS decimal(19,4)) PurchaseRate,
              CAST(ISNULL(iu.Unit_MRP,0) AS decimal(19,4)) Mrp,
              CAST(ISNULL(im.CGST,0) AS decimal(9,4)) Cgst, CAST(ISNULL(im.SGST,0) AS decimal(9,4)) Sgst,
              CAST(COALESCE(im.IGST, ISNULL(im.CGST,0)+ISNULL(im.SGST,0)) AS decimal(9,4)) Igst,
              CAST(CASE WHEN im.CGST IS NULL OR im.SGST IS NULL THEN 0 ELSE 1 END AS bit) TaxConfigured,
              CAST(ISNULL(im.Stk,0) AS decimal(19,4)) Stock,
              CAST(CASE WHEN ISNULL(im.Item_Stat,0)=0 THEN 1 ELSE 0 END AS bit) Active,
              LTRIM(RTRIM(ISNULL(g.Grp_Name,''))) [Group], LTRIM(RTRIM(ISNULL(c.Item_CatName,''))) Category
            FROM dbo.IQItems_Mast im
            JOIN dbo.IQItems_Units iu ON iu.Item_ID = im.Item_ID
            LEFT JOIN dbo.IQGroups g ON g.Grp_ID = im.Grp_ID
            LEFT JOIN dbo.IQItems_Cat c ON c.Item_CatID = im.Cat_ID
            WHERE (@itemId IS NULL OR im.Item_ID=@itemId)
            ORDER BY im.Item_Name, iu.UNit_Id
            """, new { itemId });

    /// <summary>Accounts with their balance as their ledger closes (<see cref="Reports.ReportService.Balance"/>): Cash
    /// included its implicit P/R rows nowhere but in the cash book, so the picker showed Cash lakhs away from the dashboard.</summary>
    public static async Task<IReadOnlyList<CatalogueParty>> PartiesAsync(HqSession s, long? accountId = null) =>
        await s.QueryAsync<CatalogueParty>($"""
            SELECT CAST(a.Act_Id AS bigint) AccountId, LTRIM(RTRIM(ISNULL(a.Act_Name,''))) Name, LTRIM(RTRIM(ISNULL(a.Act_Type,''))) Type,
              LTRIM(RTRIM(ISNULL(a.Act_Code,''))) Code, LTRIM(RTRIM(ISNULL(ad.Act_Town,''))) Town, LTRIM(RTRIM(ISNULL(ad.Act_Ph1,''))) Phone,
              LTRIM(RTRIM(ISNULL(ad.Act_Cell,''))) Mobile, UPPER(LTRIM(RTRIM(ISNULL(ad.GSTIN,'')))) Gstin, CAST(ISNULL(ad.GSTTaxType,2) AS int) GstType,
              LTRIM(RTRIM(ISNULL(ad.Act_state,''))) State, CAST(ISNULL(a.Bill2Bill,0) AS bit) BillWise,
              CAST(CASE WHEN ISNULL(a.Act_Usage,0)=0 THEN 1 ELSE 0 END AS bit) Active,
              CAST({MK.Hq.Engine.Reports.ReportService.Balance("a")} AS decimal(19,2)) Balance,
              LTRIM(RTRIM(ISNULL(ad.Add_Line1,'') + ' ' + ISNULL(ad.Add_Line2,''))) Address
            FROM dbo.IQAccmst a
            LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id = a.Act_Id
            WHERE a.Act_Cat='A' AND (@accountId IS NULL OR a.Act_Id=@accountId)
            ORDER BY a.Act_Name
            """, new { accountId });
}
