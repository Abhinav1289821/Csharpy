using System.Collections.Concurrent;
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

public sealed record CatalogueSnapshot(string Stamp, IReadOnlyList<CatalogueItem> Items, IReadOnlyList<CatalogueParty> Parties, DateTime LoadedAt);

/// <summary>
/// Item and account masters, cached per firm and invalidated by a cheap change stamp (row counts and
/// BINARY_CHECKSUM aggregates over the columns that matter), so HQ-side edits appear within seconds while
/// typing in the browser never waits for SQL Server.
/// </summary>
public sealed class Catalogue(TimeProvider time)
{
    private static readonly TimeSpan StampInterval = TimeSpan.FromSeconds(3);
    private readonly ConcurrentDictionary<string, (CatalogueSnapshot Snapshot, DateTimeOffset CheckedAt)> _cache = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    public const string StampSql = """
        SELECT CONCAT(
          (SELECT CONCAT(COUNT(*),'.',ISNULL(MAX(Item_ID),0),'.',ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Item_ID,Item_Name,Item_Code,CGST,SGST,IGST,HSNCODE,Item_Stat,Stk)),0)) FROM dbo.IQItems_Mast), '|',
          (SELECT CONCAT(COUNT(*),'.',ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Item_ID,UNit_Id,Unit_Srate,Unit_Prate,Unit_MRP,PP_SaleRate,ReportPack)),0)) FROM dbo.IQItems_Units), '|',
          (SELECT CONCAT(COUNT(*),'.',ISNULL(MAX(Act_Id),0),'.',ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Act_Id,Act_Name,Act_Type,Act_Usage,Bill2Bill,Act_OPbal)),0)) FROM dbo.IQAccmst), '|',
          (SELECT CONCAT(COUNT(*),'.',ISNULL(CHECKSUM_AGG(BINARY_CHECKSUM(Act_Id,Act_Town,Act_Ph1,Act_Cell,GSTIN,GSTTaxType,Act_state)),0)) FROM dbo.IQAccAdr), '|',
          (SELECT CONCAT(COUNT(*),'.',ISNULL(MAX(SR_NO),0),'.',ISNULL(SUM(CAST(AMOUNT AS decimal(19,2))),0)) FROM dbo.IQDayBook))
        """;

    public async Task<CatalogueSnapshot> GetAsync(HqFirm firm, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(firm.Id, out var hit) && time.GetUtcNow() - hit.CheckedAt < StampInterval) return hit.Snapshot;
        var gate = _gates.GetOrAdd(firm.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(firm.Id, out hit) && time.GetUtcNow() - hit.CheckedAt < StampInterval) return hit.Snapshot;
            await using var s = await firm.OpenAsync(ct);
            var stamp = await s.ScalarAsync<string>(StampSql);
            if (hit.Snapshot is not null && hit.Snapshot.Stamp == stamp)
            {
                _cache[firm.Id] = (hit.Snapshot, time.GetUtcNow());
                return hit.Snapshot;
            }
            var snapshot = new CatalogueSnapshot(stamp, await ItemsAsync(s), await PartiesAsync(s), DateTime.UtcNow);
            _cache[firm.Id] = (snapshot, time.GetUtcNow());
            return snapshot;
        }
        finally { gate.Release(); }
    }

    public void Invalidate(string firmId) => _cache.TryRemove(firmId, out _);

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

    public static async Task<IReadOnlyList<CatalogueParty>> PartiesAsync(HqSession s, long? accountId = null) =>
        await s.QueryAsync<CatalogueParty>($"""
            SELECT CAST(a.Act_Id AS bigint) AccountId, LTRIM(RTRIM(ISNULL(a.Act_Name,''))) Name, LTRIM(RTRIM(ISNULL(a.Act_Type,''))) Type,
              LTRIM(RTRIM(ISNULL(a.Act_Code,''))) Code, LTRIM(RTRIM(ISNULL(ad.Act_Town,''))) Town, LTRIM(RTRIM(ISNULL(ad.Act_Ph1,''))) Phone,
              LTRIM(RTRIM(ISNULL(ad.Act_Cell,''))) Mobile, UPPER(LTRIM(RTRIM(ISNULL(ad.GSTIN,'')))) Gstin, CAST(ISNULL(ad.GSTTaxType,2) AS int) GstType,
              LTRIM(RTRIM(ISNULL(ad.Act_state,''))) State, CAST(ISNULL(a.Bill2Bill,0) AS bit) BillWise,
              CAST(CASE WHEN ISNULL(a.Act_Usage,0)=0 THEN 1 ELSE 0 END AS bit) Active,
              CAST({MK.Hq.Engine.Reports.ReportService.Opening("a")} + ISNULL(b.Movement,0) AS decimal(19,2)) Balance,
              LTRIM(RTRIM(ISNULL(ad.Add_Line1,'') + ' ' + ISNULL(ad.Add_Line2,''))) Address
            FROM dbo.IQAccmst a
            LEFT JOIN dbo.IQAccAdr ad ON ad.Act_Id = a.Act_Id
            LEFT JOIN (SELECT Act_ID, SUM(CASE WHEN TYPE IN ('D','P') THEN AMOUNT ELSE -AMOUNT END) Movement FROM dbo.IQDayBook GROUP BY Act_ID) b ON b.Act_ID = a.Act_Id
            WHERE a.Act_Cat='A' AND (@accountId IS NULL OR a.Act_Id=@accountId)
            ORDER BY a.Act_Name
            """, new { accountId });
}
