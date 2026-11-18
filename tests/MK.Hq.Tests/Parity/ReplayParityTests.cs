using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Documents;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Parity;

/// <summary>
/// The write oracle. Real bills that HQ saved (and nobody edited since) are rebuilt as requests and posted
/// through the engine into a scratch restore. Every column of every row the engine wrote is then compared
/// with the row HQ wrote for the original bill — header, lines, party snapshot, charges, VAT summary, ledger
/// and the User_IQ* audit copies. Only identities, numbers and capture timestamps are excluded.
/// </summary>
[Trait("Category", "Parity")]
public sealed class ReplayParityTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Doc_Id", "Doc_ID", "TNo", "Tno", "Delv_Id", "Recd_Id", "OrdDltdID", "SR_NO", "Sr_No", "Bill_Time", "Ent_Time", "Ent_Date", "Modify_Date",
        "VisitDate", "MfgDate", "ExpDate", "SchDate", "BillEnteredSystem", "Narration", "Rpt_Sel", "Modify_By", "ModifyCnt", "User_ID", "UserID",
        "SysDate", "EntDate", "CreditCardDtldID", "RecdOrDelv_ID", "BL_adjusted", "Cost_Calculated", "Stk", "TaskType", "UserDtldID", "User_DtldID",
        // Database name of the scratch copy differs by construction.
        "CID",
        // Settlement and costing state that HQ (or later receipts) change after the save.
        "Adjusted", "Item_Cost",
    };

    /// <summary>
    /// Informational line fields HQ copies from the item master at save time (sale rate, disabled textile settings).
    /// A replay copies today's master, so these can only match when the master is unchanged; they never affect
    /// amounts, tax or ledger. Counted and reported, not failed.
    /// </summary>
    private static readonly HashSet<string> MasterCopied = new(StringComparer.OrdinalIgnoreCase)
    {
        "SRate", "NewGSTRateTextileItems", "ItemPriceAboveTextitleItems", "PTS", "PTR", "PTD", "HSNCODE",
    };

    /// <summary>
    /// Columns whose value changed with an HQ update, and the first entry day of the installed build's behaviour
    /// (read from HQ's own bills): ship-to state defaulting from 30-Oct-2026, ledger Salesman_ID=0 from 4-Nov-2026.
    /// </summary>
    private static readonly Dictionary<string, DateTime> BuildChanges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ship_state"] = new DateTime(2026, 10, 30),
        ["Salesman_ID"] = new DateTime(2026, 11, 4),
    };

    /// <summary>Account 10970 gained a mobile number in HQ on 3-Nov-2026, after these bills were saved.</summary>
    private static readonly HashSet<(long, string)> KnownMasterChanges = [(10216, "MobNo"), (10216, "Act_Mob"), (10256, "MobNo"), (10256, "Act_Mob")];

    /// <summary>
    /// HQ-side anomalies that a correct writer must not reproduce: orders 9655 and 10242 were saved by HQ without their
    /// audit, VAT and charge rows (its save is not transactional); order 10178 has a gap in line numbers from a deleted
    /// grid row; 10334's audit copy kept a trailing space its own line dropped; 10172/10325 carry leftover form state
    /// in Ship_PinNo for a walk-in customer.
    /// </summary>
    private static readonly HashSet<long> KnownHqAnomalies = [9655, 10242, 10178, 10334, 10172, 10325];

    private sealed record Source(long Doc_Id, int Tt_Id, DateTime Doc_Date, DateTime Ent_Date, DateTime? Doc_Duedate, int Mode, long Act_ID, long HeadAct_ID, int Exclusive,
        string? Narration, string? Message, int SM_Id, string? TExtn, string? Supp_Billno, DateTime? Supp_Billdate,
        string? Act_Name, string? Act_Town, string? Act_PH1, string? Act_Mob, string? GSTIN, int? GSTTaxType, string? Actstate, string? Address);

    private sealed record SourceLine(long ItemId, long UnitId, decimal Qty, decimal Rate, decimal Disc, string? ItemDesc, string? MasterDesc);

    [Theory]
    [InlineData("mbm", "MKHQ_PARITY_MBM", "ISTMBM26.bak", 12)]
    [InlineData("mp", "MKHQ_PARITY_MP", "ISTMP26.bak", 12)]
    public async Task Engine_writes_what_HQ_writes(string firmId, string database, string backup, int perProfile)
    {
        await ScratchDatabase.RestoreAsync(database, backup);
        await using var provider = await ScratchDatabase.EngineAsync(firmId, database);
        var documents = provider.GetRequiredService<DocumentService>();
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(database));

        var sources = (await cn.QueryAsync<Source>($"""
            WITH ranked AS (
              SELECT h.*, ROW_NUMBER() OVER (PARTITION BY h.Tt_Id, h.Mode ORDER BY h.Doc_Id DESC) rn
              FROM dbo.IQDoc_Header h
              WHERE h.Tt_Id IN (248,249,500,501,506,507) AND ISNULL(h.ModifyCnt,0)=2 AND h.BillEnteredSystem='MK' AND ISNULL(h.Doc_Cancel,0)=0
                AND NOT EXISTS (SELECT 1 FROM dbo.IQDoc_Delv d WHERE d.Doc_ID=h.Doc_Id AND ISNULL(d.OrdDelv_Id,0)>0))
            SELECT CAST(h.Doc_Id AS bigint) Doc_Id, CAST(h.Tt_Id AS int) Tt_Id, h.Doc_Date, h.Ent_Date, h.Doc_Duedate, CAST(h.Mode AS int) Mode, CAST(h.Act_ID AS bigint) Act_ID,
              CAST(h.HeadAct_ID AS bigint) HeadAct_ID, CAST(ISNULL(h.Exclusive,0) AS int) Exclusive, h.Narration, h.Message, CAST(ISNULL(h.SM_Id,0) AS int) SM_Id,
              h.TExtn, h.Supp_Billno, h.Supp_Billdate, c.Act_Name, c.Act_Town, c.Act_PH1, c.Act_Mob, c.GSTIN, CAST(c.GSTTaxType AS int) GSTTaxType, c.Actstate, c.Address
            FROM ranked h LEFT JOIN dbo.IQCash_TRAN c ON c.Doc_Id=h.Doc_Id
            WHERE h.rn <= {perProfile} ORDER BY h.Tt_Id, h.Mode, h.Doc_Id
            """)).ToList();
        Assert.NotEmpty(sources);

        var diffs = new Dictionary<string, List<string>>();
        var refused = new List<string>();
        var compared = 0;
        var masterDrift = 0;
        var olderBuild = 0;
        foreach (var src in sources.Where(s => !(database.EndsWith("MBM", StringComparison.Ordinal) && KnownHqAnomalies.Contains(s.Doc_Id))))
        {
            var kind = DocumentKinds.FromTt(src.Tt_Id)!.Value;
            var table = kind.IsOrder() ? "IQOrd_Detail" : kind == DocumentKind.Sale ? "IQDoc_Delv" : "IQDoc_Recd";
            var lines = (await cn.QueryAsync<SourceLine>($"""
                SELECT CAST(l.Item_ID AS bigint) ItemId, CAST(l.UnitID AS bigint) UnitId, CAST(l.Qty AS decimal(19,4)) Qty, CAST(l.Rate AS decimal(19,4)) Rate,
                  CAST(ISNULL(l.Disc,0) AS decimal(19,4)) Disc, l.ItemDesc, ISNULL(im.Item_Desc,'') MasterDesc
                FROM dbo.{table} l JOIN dbo.IQItems_Mast im ON im.Item_ID=l.Item_ID WHERE l.Doc_ID=@id ORDER BY l.Ln_No
                """, new { id = src.Doc_Id })).ToList();
            var request = Rebuild(src, kind, lines);
            var tables = new[] { "IQDoc_Header", table, "IQCash_TRAN", "IQDoc_Chrgs", "IQDOC_VAT", "IQDayBook",
                "User_IQDOC_Header", "User_IQCash_TRAN", "User_IQDOC_VAT", "User_IQDoc_Chrgs", kind == DocumentKind.Sale ? "User_IQDOC_Delv" : "User_IQDOC_Recd" };
            var originals = new Dictionary<string, List<IDictionary<string, object?>>>();
            foreach (var t in tables)
                originals[t] = (await cn.QueryAsync(Select(t), new { id = src.Doc_Id })).Cast<IDictionary<string, object?>>().ToList();
            if (kind == DocumentKind.Purchase)
                // The original is still in the scratch copy; move its reference aside so the duplicate guard lets the replay through.
                await cn.ExecuteAsync("UPDATE dbo.IQDoc_Header SET Supp_Billno=LEFT(Supp_Billno,18)+'~' WHERE Doc_Id=@id", new { id = src.Doc_Id });
            long newId;
            try
            {
                var preview = await documents.PreviewAsync(firmId, request);
                var receipt = await documents.PostAsync(firmId, new PostRequest(Guid.NewGuid().ToString(), preview.Hash, request));
                newId = receipt.DocumentId;
            }
            catch (HqException e)
            {
                refused.Add($"doc {src.Doc_Id} tt {src.Tt_Id} mode {src.Mode}: {e.Code} {e.Message}");
                continue;
            }
            compared++;
            foreach (var t in tables)
            {
                var before = originals[t];
                var after = (await cn.QueryAsync(Select(t), new { id = newId })).Cast<IDictionary<string, object?>>().ToList();
                if (before.Count != after.Count)
                {
                    Note(diffs, $"{t}: row count", $"doc {src.Doc_Id}: HQ {before.Count} engine {after.Count}");
                    continue;
                }
                for (var i = 0; i < before.Count; i++)
                    foreach (var (column, hqValue) in before[i])
                    {
                        if (Ignored.Contains(column)) continue;
                        // Outside purchases this is the entry day (capture time), like Ent_Date.
                        if (column.Equals("Supp_Billdate", StringComparison.OrdinalIgnoreCase) && kind != DocumentKind.Purchase) continue;
                        var mine = after[i][column];
                        if (!Same(hqValue, mine) && BuildChanges.TryGetValue(column, out var since) && src.Ent_Date < since)
                        {
                            olderBuild++;
                            continue;
                        }
                        if (!Same(hqValue, mine) && MasterCopied.Contains(column))
                        {
                            masterDrift++;
                            continue;
                        }
                        if (!Same(hqValue, mine) && KnownMasterChanges.Contains((src.Doc_Id, column)))
                        {
                            masterDrift++;
                            continue;
                        }
                        if (!Same(hqValue, mine))
                            Note(diffs, $"{t}.{column}", $"doc {src.Doc_Id} tt {src.Tt_Id} m{src.Mode} row {i}: HQ '{Show(hqValue)}' engine '{Show(mine)}'");
                    }
            }
        }

        output.WriteLine($"{database}: {sources.Count} HQ bills replayed, {compared} compared, {refused.Count} refused, {masterDrift} values differ only because an item/account master changed after HQ saved the bill, {olderBuild} values written by an older HQ build");
        foreach (var r in refused) output.WriteLine("REFUSED " + r);
        foreach (var (key, list) in diffs.OrderBy(d => d.Key))
        {
            output.WriteLine($"DIFF {key}: {list.Count}");
            foreach (var example in list.Take(3)) output.WriteLine("    " + example);
        }
        Assert.True(compared > 0, "No bill could be replayed");
        Assert.True(diffs.Count == 0, $"{diffs.Count} columns differ from HQ: {string.Join(", ", diffs.Keys.Take(20))}");
    }

    private static string Select(string t)
    {
        var order = t switch
        {
            "IQDayBook" => "Act_ID, Contra_ID",
            "IQDoc_Chrgs" or "User_IQDoc_Chrgs" or "IQDoc_Delv" or "IQDoc_Recd" or "IQOrd_Detail" or "User_IQDOC_Delv" or "User_IQDOC_Recd" => "Ln_No",
            _ => "1",
        };
        var where = t is "User_IQDOC_Header" or "User_IQDOC_Delv" or "User_IQDOC_Recd" ? "Doc_Id=@id AND ModifyCnt=1" : "Doc_Id=@id";
        return $"SELECT * FROM dbo.{t} WHERE {where} ORDER BY {order}";
    }

    private static DocumentRequest Rebuild(Source s, DocumentKind kind, IReadOnlyList<SourceLine> lines) => new()
    {
        Kind = kind,
        Date = DateOnly.FromDateTime(s.Doc_Date),
        PaymentMode = s.Mode,
        AccountId = s.Mode >= 2 ? s.Act_ID : s.HeadAct_ID,
        Party = s.Mode == 1 ? null : new PartySnapshot(
            Name: s.Act_Name, Town: s.Act_Town, Phone: s.Act_PH1, Mobile: s.Act_Mob,
            Gstin: s.Mode == 0 ? s.GSTIN?.Trim() : null, GstType: s.Mode == 0 ? s.GSTTaxType : null, State: s.Mode == 0 ? s.Actstate?.Trim() : null,
            Address: s.Address),
        Interstate = s.Tt_Id is 501 or 507,
        Basis = s.Exclusive == 1 ? TaxBasis.Inclusive : TaxBasis.Exclusive,
        DueDate = s.Doc_Duedate is { } due ? DateOnly.FromDateTime(due) : null,
        Notes = s.Narration?.Trim(),
        Message = s.Message?.Trim(),
        SalespersonId = s.SM_Id > 0 ? s.SM_Id : null,
        NumberSuffix = s.TExtn?.Trim(),
        Supplier = kind == DocumentKind.Purchase ? new SupplierBill(s.Supp_Billno?.Trim() ?? "", DateOnly.FromDateTime(s.Supp_Billdate ?? s.Doc_Date)) : null,
        Lines = lines.Select(l => new LineRequest(l.ItemId, l.UnitId, l.Qty, l.Rate, l.Disc,
            l.ItemDesc is null || l.ItemDesc == l.MasterDesc || (l.ItemDesc == " " && string.IsNullOrEmpty(l.MasterDesc)) ? null : l.ItemDesc)).ToList(),
    };

    private static void Note(Dictionary<string, List<string>> diffs, string key, string example)
    {
        if (!diffs.TryGetValue(key, out var list)) diffs[key] = list = [];
        list.Add(example);
    }

    private static bool Same(object? a, object? b)
    {
        if (a is null or DBNull) return b is null or DBNull;
        if (b is null or DBNull) return false;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is DateTime da && b is DateTime db) return da == db;
        if (IsNumber(a) && IsNumber(b)) return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture);
        return Equals(a, b) || string.Equals(Convert.ToString(a, CultureInfo.InvariantCulture), Convert.ToString(b, CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool IsNumber(object o) => o is decimal or double or float or int or long or short or byte or bool;
    private static string Show(object? v) => v is null or DBNull ? "NULL" : v is DateTime d ? d.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture)!;
}
