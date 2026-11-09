using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Vouchers;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Workflows;

public sealed class ScratchVoucherMbm : IAsyncLifetime
{
    public const string Database = "MKHQ_VAMEND_MBM";
    public ServiceProvider Engine { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Database, "ISTMBM26.bak");
        Engine = ScratchDatabase.Engine("mbm", Database);
    }
    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

/// <summary>
/// Correcting a voucher in place, as FrmFin_Vouchers' edit mode does: same Doc_Id and number, header updated, ledger and
/// bank rows re-inserted, settlements re-applied, one more audit set. Oracle: HQ's own vouchers (including the 8 HQ
/// itself modified and the credit/debit note), re-saved unchanged, must come back byte-identical except where HQ's edit
/// also changes them.
/// </summary>
[Trait("Category", "Workflow")]
public sealed class VoucherAmendTests(ScratchVoucherMbm fx, ITestOutputHelper output) : IClassFixture<ScratchVoucherMbm>
{
    private VoucherService Vouchers => fx.Engine.GetRequiredService<VoucherService>();
    private static SqlConnection Sql() => new(TestDatabase.ConnectionString(ScratchVoucherMbm.Database));

    /// <summary>What HQ's edit changes by design: new identities, today's entry date, the live NULL→0 Salesman_ID since the
    /// 4-Nov build, and Adjusted (recomputed by HQ's outstanding reports, never written by the voucher form).</summary>
    private static readonly HashSet<string> Changes = new(StringComparer.OrdinalIgnoreCase)
    {
        "SR_NO", "Sr_No", "BnkTranID", "Ent_Date", "Ent_Uid", "Salesman_ID", "Adjusted",
    };

    [Fact]
    public async Task Hq_vouchers_resaved_unchanged_come_back_identical()
    {
        await using var cn = Sql();
        var ids = (await cn.QueryAsync<long>("""
            SELECT CAST(Doc_Id AS bigint) FROM (
              SELECT h.Doc_Id, ROW_NUMBER() OVER (PARTITION BY h.Tt_Id ORDER BY h.Doc_Id DESC) rn,
                (SELECT COUNT(*) FROM dbo.User_IQDOC_Header u WHERE u.Doc_Id=h.Doc_Id) audits
              FROM dbo.IQDoc_Header h WHERE h.Tt_Id BETWEEN 204 AND 209 AND h.Doc_Date >= '2026-04-01') x
            WHERE rn <= 4 OR audits > 1 ORDER BY Doc_Id
            """)).ToList();
        Assert.Contains(9941L, ids); // the credit note
        Assert.Contains(10183L, ids); // saved three times in HQ

        var diffs = new List<string>();
        var refused = new List<string>();
        var compared = 0;
        foreach (var id in ids)
        {
            var before = await SnapshotAsync(cn, id);
            var edit = await Vouchers.LoadAsync("mbm", id);
            VoucherPreview preview;
            try { preview = await Vouchers.PreviewAmendAsync("mbm", id, edit.Voucher); }
            catch (HqException e) { refused.Add($"{id}: {e.Code} {e.Message}"); continue; }
            await Vouchers.AmendAsync("mbm", id, new VoucherAmendPost(Guid.NewGuid().ToString(), preview.Hash, edit.Voucher));
            var after = await SnapshotAsync(cn, id);
            compared++;
            foreach (var (table, rows) in before.Tables)
            {
                var now = after.Tables[table];
                if (rows.Count != now.Count) { diffs.Add($"{id} {table}: {rows.Count} rows → {now.Count}"); continue; }
                for (var i = 0; i < rows.Count; i++)
                    foreach (var (column, value) in rows[i])
                        if (!Changes.Contains(column) && !Same(value, now[i][column]))
                            diffs.Add($"{id} {table}[{i}].{column}: '{value}' → '{now[i][column]}'");
            }
            if (!before.Settlements.SequenceEqual(after.Settlements))
                diffs.Add($"{id} settlements: [{string.Join(", ", before.Settlements)}] → [{string.Join(", ", after.Settlements)}]");
            if (after.Audits != before.Audits + 1) diffs.Add($"{id} audit headers: {before.Audits} → {after.Audits}");
            if (after.Ledger.Min() <= before.Ledger.Max()) diffs.Add($"{id}: ledger rows were not re-inserted");
        }
        output.WriteLine($"{ids.Count} vouchers, {compared} re-saved, {refused.Count} refused");
        foreach (var r in refused) output.WriteLine("REFUSED " + r);
        foreach (var d in diffs.Take(40)) output.WriteLine("DIFF " + d);
        Assert.True(compared >= 20, $"only {compared} compared");
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task Correcting_amount_and_settlement_moves_the_payment_between_bills()
    {
        await using var cn = Sql();
        // A customer with at least two open bills, settled by a fresh cash receipt against the older one.
        var (customer, older, newer) = await cn.QueryFirstAsync<(long, long, long)>("""
            SELECT TOP 1 CAST(d.Act_ID AS bigint), CAST(MIN(d.SR_NO) AS bigint), CAST(MAX(d.SR_NO) AS bigint)
            FROM dbo.IQDayBook d JOIN dbo.IQAccmst a ON a.Act_Id=d.Act_ID
            WHERE a.Bill2Bill=1 AND a.Act_Type='Customer' AND d.TYPE='D' AND d.AMOUNT-ISNULL(d.BL_adjusted,0) >= 500 AND d.Doc_ID>0
            GROUP BY d.Act_ID HAVING COUNT(*) >= 2 ORDER BY d.Act_ID
            """);
        var day = new DateOnly(2026, 11, 8);
        VoucherRequest Receipt(decimal amount, long bill) => new(VoucherKind.CashReceipt, day, 6,
            [new VoucherLineRequest(customer, amount, "test", null, [new AllocationRequest(bill, amount)])]);
        var first = Receipt(300, older);
        var created = await Vouchers.PostAsync("mbm", new VoucherPost(Guid.NewGuid().ToString(), (await Vouchers.PreviewAsync("mbm", first)).Hash, first));
        var olderOpenBefore = await OpenAsync(cn, older);

        var corrected = Receipt(450, newer);
        var preview = await Vouchers.PreviewAmendAsync("mbm", created.DocumentId, corrected);
        var receipt = await Vouchers.AmendAsync("mbm", created.DocumentId, new VoucherAmendPost(Guid.NewGuid().ToString(), preview.Hash, corrected));

        Assert.Equal(created.Number, receipt.Number);
        Assert.Equal(olderOpenBefore + 300, await OpenAsync(cn, older));
        var links = (await cn.QueryAsync<(long Target, decimal Amount)>(
            "SELECT CAST(a.Rel_Sr_No AS bigint), CAST(a.Amount AS decimal(19,2)) FROM dbo.ADJUST a JOIN dbo.IQDayBook d ON d.SR_NO=a.Sr_No WHERE d.Doc_ID=@id",
            new { id = created.DocumentId })).ToList();
        Assert.Equal([(newer, 450m)], links);
        Assert.Equal(450m, await cn.ExecuteScalarAsync<decimal>("SELECT CAST(Bill_Amt AS decimal(19,2)) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id = created.DocumentId }));
        // An unchanged replay of the same request is refused: the review hash no longer matches.
        var stale = await Assert.ThrowsAsync<HqException>(() => Vouchers.AmendAsync("mbm", created.DocumentId, new VoucherAmendPost(Guid.NewGuid().ToString(), preview.Hash, corrected)));
        Assert.Equal("REVIEW_CHANGED", stale.Code);
    }

    private static Task<decimal> OpenAsync(SqlConnection cn, long entry) =>
        cn.ExecuteScalarAsync<decimal>("SELECT CAST(AMOUNT-ISNULL(BL_adjusted,0) AS decimal(19,2)) FROM dbo.IQDayBook WHERE SR_NO=@entry", new { entry });

    private sealed record Snapshot(Dictionary<string, List<IDictionary<string, object?>>> Tables, List<string> Settlements, int Audits, List<long> Ledger);

    private static async Task<Snapshot> SnapshotAsync(SqlConnection cn, long id)
    {
        var tables = new Dictionary<string, List<IDictionary<string, object?>>>
        {
            ["IQDoc_Header"] = (await cn.QueryAsync("SELECT * FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id })).Cast<IDictionary<string, object?>>().ToList(),
            ["IQDayBook"] = (await cn.QueryAsync("SELECT * FROM dbo.IQDayBook WHERE Doc_ID=@id ORDER BY SR_NO", new { id })).Cast<IDictionary<string, object?>>().ToList(),
            ["IQbnktran"] = (await cn.QueryAsync("SELECT b.* FROM dbo.IQbnktran b JOIN dbo.IQDayBook d ON d.SR_NO=b.Sr_no WHERE b.Doc_ID=@id ORDER BY d.SR_NO", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        };
        var ledger = tables["IQDayBook"].Select(r => Convert.ToInt64(r["SR_NO"], CultureInfo.InvariantCulture)).ToList();
        // Settlements by the voucher row's position, so new identities compare equal.
        var settlements = (await cn.QueryAsync<(long Source, long Target, decimal Amount)>(
                "SELECT CAST(a.Sr_No AS bigint), CAST(a.Rel_Sr_No AS bigint), CAST(a.Amount AS decimal(19,2)) FROM dbo.ADJUST a JOIN dbo.IQDayBook d ON d.SR_NO=a.Sr_No WHERE d.Doc_ID=@id", new { id }))
            .Select(l => $"row{ledger.IndexOf(l.Source)}→{l.Target}:{l.Amount.ToString("0.00", CultureInfo.InvariantCulture)}").Order().ToList();
        var audits = await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.User_IQDOC_Header WHERE Doc_Id=@id", new { id });
        return new Snapshot(tables, settlements, audits, ledger);
    }

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
