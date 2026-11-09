using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Vouchers;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Parity;

/// <summary>Real HQ receipts and payments, rebuilt from their own rows, re-posted and compared column by column.</summary>
[Trait("Category", "Parity")]
public sealed class VoucherParityTests(ITestOutputHelper output)
{
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase)
    {
        "Doc_Id", "Doc_ID", "TNo", "Tno", "SR_NO", "Sr_No", "BnkTranID", "Ent_Date", "Ent_Time", "Bill_Time", "Modify_Date", "TaskType",
        "BL_adjusted", "Adjusted", "Rpt_Sel", "CID", "UserDtldID",
        // Written as NULL before HQ's 4-Nov-2026 update and 0 since; vouchers sampled here span both builds.
        "Salesman_ID",
    };

    private sealed record Head(long Doc_Id, int Tt_Id, DateTime Doc_Date, long Act_ID, string? Narration);
    private sealed record Book(long SR_NO, long Act_ID, string TYPE, long Contra_ID, decimal AMOUNT, string? Narration, int lnno);
    private sealed record Bank(long Sr_no, int ChqType, string? chqno, int chqdate, string? bname, string? BPlace, int DepositDate);

    [Theory]
    [InlineData("mbm", "MKHQ_VPARITY_MBM", "ISTMBM26.bak")]
    public async Task Engine_writes_vouchers_as_HQ_does(string firmId, string database, string backup)
    {
        await ScratchDatabase.RestoreAsync(database, backup);
        await using var provider = ScratchDatabase.Engine(firmId, database);
        var vouchers = provider.GetRequiredService<VoucherService>();
        await using var cn = new SqlConnection(TestDatabase.ConnectionString(database));
        var heads = (await cn.QueryAsync<Head>("""
            WITH r AS (SELECT h.*, ROW_NUMBER() OVER (PARTITION BY h.Tt_Id ORDER BY h.Doc_Id DESC) rn FROM dbo.IQDoc_Header h WHERE h.Tt_Id IN (204,205,206,207,208,209)
              AND (SELECT COUNT(*) FROM dbo.User_IQDOC_Header u WHERE u.Doc_Id=h.Doc_Id) <= 1)
            SELECT CAST(Doc_Id AS bigint) Doc_Id, CAST(Tt_Id AS int) Tt_Id, Doc_Date, CAST(Act_ID AS bigint) Act_ID, Narration FROM r WHERE rn <= 10 ORDER BY Tt_Id, Doc_Id
            """)).ToList();

        var diffs = new Dictionary<string, List<string>>();
        var refused = new List<string>();
        var compared = 0;
        foreach (var h in heads)
        {
            var spec = VoucherSpec.ForTt(h.Tt_Id)!;
            var books = (await cn.QueryAsync<Book>("SELECT CAST(SR_NO AS bigint) SR_NO, CAST(Act_ID AS bigint) Act_ID, TYPE, CAST(ISNULL(Contra_ID,0) AS bigint) Contra_ID, CAST(AMOUNT AS decimal(19,2)) AMOUNT, Narration, CAST(ISNULL(lnno,0) AS int) lnno FROM dbo.IQDayBook WHERE Doc_ID=@id ORDER BY SR_NO", new { id = h.Doc_Id })).ToList();
            var banks = (await cn.QueryAsync<Bank>("SELECT CAST(Sr_no AS bigint) Sr_no, CAST(ChqType AS int) ChqType, chqno, CAST(ISNULL(chqdate,0) AS int) chqdate, bname, BPlace, CAST(ISNULL(DepositDate,0) AS int) DepositDate FROM dbo.IQbnktran WHERE Doc_ID=@id", new { id = h.Doc_Id })).ToDictionary(b => b.Sr_no);
            var lines = books.Where(b => b.lnno >= 0).Select(b =>
            {
                var transfer = b.Act_ID == h.Act_ID; // HQ's cash-transfer row books the header account with the line account as contra
                var bank = banks.GetValueOrDefault(b.SR_NO);
                return new VoucherLineRequest(transfer ? b.Contra_ID : b.Act_ID, b.AMOUNT, b.Narration,
                    spec.IsBank ? new BankDetails(bank?.ChqType ?? 2, bank?.chqno?.Trim(), Day(bank?.chqdate ?? 0), bank?.bname?.Trim(), bank?.BPlace?.Trim(), Day(bank?.DepositDate ?? 0)) : null);
            }).ToList();
            var request = new VoucherRequest(spec.Kind, DateOnly.FromDateTime(h.Doc_Date), h.Act_ID, lines, h.Narration?.Trim() is { Length: > 0 } n ? n : null);
            var tables = new[] { "IQDoc_Header", "IQDayBook", "IQbnktran", "User_IQDOC_Header", "User_IQDayBook", "User_IQbnktran" };
            var before = new Dictionary<string, List<IDictionary<string, object?>>>();
            foreach (var t in tables) before[t] = (await cn.QueryAsync(Select(t), new { id = h.Doc_Id })).Cast<IDictionary<string, object?>>().ToList();
            long newId;
            try
            {
                var preview = await vouchers.PreviewAsync(firmId, request);
                newId = (await vouchers.PostAsync(firmId, new VoucherPost(Guid.NewGuid().ToString(), preview.Hash, request))).DocumentId;
            }
            catch (HqException e)
            {
                refused.Add($"doc {h.Doc_Id} tt {h.Tt_Id}: {e.Code} {e.Message}");
                continue;
            }
            compared++;
            foreach (var t in tables)
            {
                var after = (await cn.QueryAsync(Select(t), new { id = newId })).Cast<IDictionary<string, object?>>().ToList();
                if (before[t].Count != after.Count) { Note(diffs, $"{t}: row count", $"doc {h.Doc_Id} tt {h.Tt_Id}: HQ {before[t].Count} engine {after.Count}"); continue; }
                for (var i = 0; i < after.Count; i++)
                    foreach (var (column, hq) in before[t][i])
                    {
                        if (Ignored.Contains(column)) continue;
                        var mine = after[i][column];
                        // A replay only knows the trimmed note HQ kept in its ledger, not surrounding spaces typed into the audit copy.
                        if (t == "User_IQDayBook" && column == "Narration" && Same((hq as string)?.Trim(), (mine as string)?.Trim())) continue;
                        if (!Same(hq, mine)) Note(diffs, $"{t}.{column}", $"doc {h.Doc_Id} tt {h.Tt_Id} row {i}: HQ '{hq}' engine '{mine}'");
                    }
            }
        }
        output.WriteLine($"{database}: {heads.Count} vouchers, {compared} compared, {refused.Count} refused");
        foreach (var r in refused) output.WriteLine("REFUSED " + r);
        foreach (var (k, list) in diffs.OrderBy(d => d.Key))
        {
            output.WriteLine($"DIFF {k}: {list.Count}");
            foreach (var e in list.Take(3)) output.WriteLine("    " + e);
        }
        Assert.True(compared > 0);
        Assert.True(diffs.Count == 0, $"{diffs.Count} voucher columns differ: {string.Join(", ", diffs.Keys)}");
    }

    private static DateOnly? Day(int compact) => compact > 19000101 ? DateOnly.ParseExact(compact.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture) : null;

    private static string Select(string t) => t switch
    {
        "IQDayBook" => "SELECT * FROM dbo.IQDayBook WHERE Doc_ID=@id ORDER BY SR_NO",
        "IQbnktran" => "SELECT * FROM dbo.IQbnktran WHERE Doc_ID=@id ORDER BY Sr_no",
        "User_IQDayBook" => "SELECT * FROM dbo.User_IQDayBook WHERE Doc_ID=@id ORDER BY lnno, Act_ID, TYPE",
        "User_IQbnktran" => "SELECT * FROM dbo.User_IQbnktran WHERE Doc_ID=@id ORDER BY chqno",
        _ => $"SELECT * FROM dbo.{t} WHERE Doc_Id=@id",
    };

    private static void Note(Dictionary<string, List<string>> d, string k, string e) { if (!d.TryGetValue(k, out var l)) d[k] = l = []; l.Add(e); }

    private static bool Same(object? a, object? b)
    {
        if (a is null or DBNull) return b is null or DBNull;
        if (b is null or DBNull) return false;
        if (a is string sa && b is string sb) return sa == sb;
        if (a is DateTime da && b is DateTime db) return da == db;
        try { return Convert.ToDecimal(a, CultureInfo.InvariantCulture) == Convert.ToDecimal(b, CultureInfo.InvariantCulture); }
        catch { return Equals(a, b); }
    }
}
