using System.Collections.Concurrent;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Vouchers;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Workflows;

public sealed class ScratchMbm : IAsyncLifetime
{
    public const string Database = "MKHQ_FLOW_MBM";
    public ServiceProvider Engine { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Database, "ISTMBM26.bak");
        Engine = ScratchDatabase.Engine("mbm", Database);
    }
    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

/// <summary>End-to-end behaviour on a scratch restore of MK Building Materials.</summary>
[Trait("Category", "Workflow")]
public sealed class WorkflowTests(ScratchMbm fx, ITestOutputHelper output) : IClassFixture<ScratchMbm>
{
    private const long Customer = 11131, Bank = 10701;
    private static readonly DateOnly Day = new(2026, 11, 8);
    private DocumentService Docs => fx.Engine.GetRequiredService<DocumentService>();
    private DocumentMutations Mutations => fx.Engine.GetRequiredService<DocumentMutations>();
    private VoucherService Vouchers => fx.Engine.GetRequiredService<VoucherService>();
    private static SqlConnection Sql() => new(TestDatabase.ConnectionString(ScratchMbm.Database));

    private static DocumentRequest CreditSale(params LineRequest[] lines) => new()
    {
        Kind = DocumentKind.Sale, Date = Day, PaymentMode = 1, AccountId = Customer, Lines = lines,
    };

    private async Task<DocumentReceipt> PostAsync(DocumentRequest r, string? op = null)
    {
        var p = await Docs.PreviewAsync("mbm", r);
        return await Docs.PostAsync("mbm", new PostRequest(op ?? Guid.NewGuid().ToString(), p.Hash, r));
    }

    [Fact]
    public async Task Amended_bill_is_indistinguishable_from_a_fresh_bill_with_the_same_content()
    {
        var original = await PostAsync(CreditSale(new LineRequest(14, 3, 2, 950), new LineRequest(15, 3, 3, 410, 5), new LineRequest(16, 3, 1, 380)));
        await using var cn = Sql();
        var lineIds = (await cn.QueryAsync<long>("SELECT CAST(Delv_Id AS bigint) FROM dbo.IQDoc_Delv WHERE Doc_ID=@id ORDER BY Ln_No", new { id = original.DocumentId })).ToList();

        // Change line 1's quantity, drop line 2, keep line 3, add a new line.
        var final = CreditSale(new LineRequest(14, 3, 5, 950), new LineRequest(16, 3, 1, 380), new LineRequest(17, 3, 2, 260, 2.5m));
        var amend = new AmendRequest(Guid.NewGuid().ToString(), original.DocumentId, "", final, [lineIds[0], lineIds[2], null]);
        var preview = await Mutations.PreviewAmendAsync("mbm", amend);
        await Mutations.AmendAsync("mbm", amend with { ExpectedHash = preview.Hash });
        var fresh = await PostAsync(final);

        string Shape(string sql, long id) => string.Join("\n", cn.Query(sql, new { id }).Select(r => string.Join("|", ((IDictionary<string, object?>)r).Values)));
        const string lines = "SELECT Ln_No, Item_ID, Qty, Rate, Disc, DiscValue, Value, GrossValue, TaxValue, CGST_TaxAmt, SGST_TaxAmt, NetValue, Act_Id, Tax_Id, SRate, MRP FROM dbo.IQDoc_Delv WHERE Doc_ID=@id ORDER BY Ln_No";
        const string ledger = "SELECT Act_ID, TYPE, Contra_ID, AMOUNT, Narration, Bill_Qty FROM dbo.IQDayBook WHERE Doc_ID=@id ORDER BY Act_ID";
        const string header = "SELECT Bill_Amt, Product_Value, Bill_Qty, Mode, Act_ID, HeadAct_ID, Exclusive, GST_Type, Doc_Duedate FROM dbo.IQDoc_Header WHERE Doc_Id=@id";
        const string misc = "SELECT (SELECT COUNT(*) FROM dbo.IQDoc_Chrgs WHERE Doc_id=@id), (SELECT COUNT(*) FROM dbo.IQDOC_VAT WHERE Doc_Id=@id), (SELECT COUNT(*) FROM dbo.IQCash_TRAN WHERE Doc_Id=@id)";
        Assert.Equal(Shape(lines, fresh.DocumentId), Shape(lines, original.DocumentId));
        Assert.Equal(Shape(header, fresh.DocumentId), Shape(header, original.DocumentId));
        Assert.Equal(Shape(misc, fresh.DocumentId), Shape(misc, original.DocumentId));
        Assert.Equal(Shape(ledger, fresh.DocumentId).Replace("/" + fresh.Number, "/#"), Shape(ledger, original.DocumentId).Replace("/" + original.Number, "/#"));
        // HQ's edit counters: header ModifyCnt 2 → 3; one tracking copy per saved version.
        Assert.Equal(3, await cn.ExecuteScalarAsync<int>("SELECT ModifyCnt FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id = original.DocumentId }));
        Assert.Equal(new[] { 1, 2 }, (await cn.QueryAsync<int>("SELECT CAST(ModifyCnt AS int) FROM dbo.User_IQDOC_Header WHERE Doc_Id=@id ORDER BY ModifyCnt", new { id = original.DocumentId })).ToArray());
        // Kept lines keep their HQ identity (HQ's edit updates them in place).
        var after = (await cn.QueryAsync<long>("SELECT CAST(Delv_Id AS bigint) FROM dbo.IQDoc_Delv WHERE Doc_ID=@id ORDER BY Ln_No", new { id = original.DocumentId })).ToList();
        Assert.Equal(lineIds[0], after[0]);
        Assert.Equal(lineIds[2], after[1]);
    }

    [Fact]
    public async Task Settlements_follow_an_amended_bill_and_are_released_on_delete()
    {
        var sale = await PostAsync(CreditSale(new LineRequest(15, 3, 10, 410)));
        var open = (await Vouchers.OpenItemsAsync("mbm", Customer)).Single(o => o.DocumentId == sale.DocumentId);
        var receiptRequest = new VoucherRequest(VoucherKind.BankReceipt, Day, Bank,
            [new VoucherLineRequest(Customer, 1000m, "part payment", new BankDetails(2, "UTR123", Day), [new AllocationRequest(open.EntryId, 1000m)])]);
        var vp = await Vouchers.PreviewAsync("mbm", receiptRequest);
        var receipt = await Vouchers.PostAsync("mbm", new VoucherPost(Guid.NewGuid().ToString(), vp.Hash, receiptRequest));

        await using var cn = Sql();
        decimal Allocated(long doc) => cn.ExecuteScalar<decimal>("SELECT ISNULL(SUM(BL_adjusted),0) FROM dbo.IQDayBook WHERE Doc_ID=@doc", new { doc });
        Assert.Equal(1000m, Allocated(sale.DocumentId));
        Assert.Equal(1000m, Allocated(receipt.DocumentId));

        // Increase the bill: the payment stays attached to the rewritten party row.
        var lineId = await cn.ExecuteScalarAsync<long>("SELECT CAST(Delv_Id AS bigint) FROM dbo.IQDoc_Delv WHERE Doc_ID=@id", new { id = sale.DocumentId });
        var bigger = new AmendRequest(Guid.NewGuid().ToString(), sale.DocumentId, "", CreditSale(new LineRequest(15, 3, 12, 410)), [lineId]);
        var mp = await Mutations.PreviewAmendAsync("mbm", bigger);
        await Mutations.AmendAsync("mbm", bigger with { ExpectedHash = mp.Hash });
        Assert.Equal(1000m, Allocated(sale.DocumentId));
        Assert.Equal(1000m, Allocated(receipt.DocumentId));
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("""
            SELECT COUNT(*) FROM dbo.ADJUST a JOIN dbo.IQDayBook s ON s.SR_NO=a.Sr_No JOIN dbo.IQDayBook t ON t.SR_NO=a.Rel_Sr_No
            WHERE s.Doc_ID=@r AND t.Doc_ID=@b AND a.Amount=1000
            """, new { r = receipt.DocumentId, b = sale.DocumentId }));

        // Reducing the bill below what is already paid is refused, not silently "fixed".
        var tooSmall = new AmendRequest(Guid.NewGuid().ToString(), sale.DocumentId, "", CreditSale(new LineRequest(15, 3, 1, 410)), [lineId]);
        var error = await Assert.ThrowsAsync<HqException>(() => Mutations.PreviewAmendAsync("mbm", tooSmall));
        Assert.Equal("HQ_ALLOCATION_CONFLICT", error.Code);

        // Deleting the bill releases the payment back to on-account.
        var dp = await Mutations.PreviewDeleteAsync("mbm", sale.DocumentId);
        Assert.Equal(1000m, dp.ReleasedAllocations);
        await Mutations.DeleteAsync("mbm", new DeleteRequest(Guid.NewGuid().ToString(), sale.DocumentId, dp.Hash));
        Assert.Equal(0m, Allocated(receipt.DocumentId));
        Assert.Equal(0, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.ADJUST a JOIN dbo.IQDayBook s ON s.SR_NO=a.Sr_No WHERE s.Doc_ID=@r", new { r = receipt.DocumentId }));
    }

    [Fact]
    public async Task Deleting_a_bill_restores_stock_exactly()
    {
        await using var cn = Sql();
        var before = (await cn.QueryAsync<(long, decimal)>("SELECT CAST(Item_ID AS bigint), CAST(ISNULL(Stk,0) AS decimal(19,4)) FROM dbo.IQItems_Mast WHERE Item_ID IN (16,17) ORDER BY Item_ID")).ToList();
        var sale = await PostAsync(CreditSale(new LineRequest(16, 3, 4, 380), new LineRequest(17, 3, 3, 260)));
        var during = (await cn.QueryAsync<(long, decimal)>("SELECT CAST(Item_ID AS bigint), CAST(ISNULL(Stk,0) AS decimal(19,4)) FROM dbo.IQItems_Mast WHERE Item_ID IN (16,17) ORDER BY Item_ID")).ToList();
        Assert.Equal(before[0].Item2 - 4, during[0].Item2);
        Assert.Equal(before[1].Item2 - 3, during[1].Item2);
        var p = await Mutations.PreviewDeleteAsync("mbm", sale.DocumentId);
        await Mutations.DeleteAsync("mbm", new DeleteRequest(Guid.NewGuid().ToString(), sale.DocumentId, p.Hash));
        var after = (await cn.QueryAsync<(long, decimal)>("SELECT CAST(Item_ID AS bigint), CAST(ISNULL(Stk,0) AS decimal(19,4)) FROM dbo.IQItems_Mast WHERE Item_ID IN (16,17) ORDER BY Item_ID")).ToList();
        Assert.Equal(before, after);
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.User_IQDOC_Header WHERE Doc_Id=@id AND TaskType=1", new { id = sale.DocumentId }));
    }

    [Fact]
    public async Task The_same_operation_posted_twice_at_once_creates_one_bill()
    {
        var request = CreditSale(new LineRequest(14, 3, 1, 950));
        var p = await Docs.PreviewAsync("mbm", request);
        var op = Guid.NewGuid().ToString();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            try { return (await Docs.PostAsync("mbm", new PostRequest(op, p.Hash, request))).DocumentId; }
            catch (HqException e) when (e.Code == "OPERATION_BUSY") { return -1L; }
        }));
        var ids = results.Where(r => r > 0).Distinct().ToList();
        Assert.Single(ids);
        // A later retry returns the stored receipt instead of saving again.
        var again = await Docs.PostAsync("mbm", new PostRequest(op, p.Hash, request));
        Assert.Equal(ids[0], again.DocumentId);
        await using var cn = Sql();
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new { id = ids[0] }));
    }

    [Fact]
    public async Task A_changed_master_invalidates_the_review()
    {
        var request = CreditSale(new LineRequest(14, 3, 1, 950));
        var p = await Docs.PreviewAsync("mbm", request);
        await using var cn = Sql();
        await cn.ExecuteAsync("UPDATE dbo.IQItems_Units SET Unit_MRP = ISNULL(Unit_MRP,0) + 1 WHERE Item_ID=14");
        var error = await Assert.ThrowsAsync<HqException>(() => Docs.PostAsync("mbm", new PostRequest(Guid.NewGuid().ToString(), p.Hash, request)));
        Assert.Equal("REVIEW_CHANGED", error.Code);
    }

    /// <summary>
    /// Two MK terminals post while a simulated HQ PC saves bills with HQ's own client-side algorithm
    /// (MAX+1 read, duplicate check, plain INSERT — no transaction), all in series CS. No number may repeat.
    /// </summary>
    [Fact]
    public async Task Numbers_never_collide_with_HQ_or_another_terminal()
    {
        await using var second = ScratchDatabase.Engine("mbm", ScratchMbm.Database);
        const string duplicateSql = "SELECT COUNT(*) FROM (SELECT TNo FROM dbo.IQDoc_Header WHERE TSeries='CS' GROUP BY TNo, ISNULL(TExtn,'') HAVING COUNT(*)>1) d";
        int baseline;
        await using (var pre = Sql()) baseline = await pre.ExecuteScalarAsync<int>(duplicateSql); // HQ's own numbers, suffix included, are unique at the start
        var terminals = new[] { Docs, second.GetRequiredService<DocumentService>() };
        using var stop = new CancellationTokenSource();
        var hqNumbers = new ConcurrentBag<int>();
        var hq = Task.Run(async () =>
        {
            await using var cn = Sql();
            await cn.OpenAsync();
            while (!stop.IsCancellationRequested)
            {
                var next = await cn.ExecuteScalarAsync<int>("SELECT ISNULL(MAX(TNo),0)+1 FROM dbo.IQDoc_Header WHERE TSeries='CS' AND Tt_Id=506");
                await Task.Delay(Random.Shared.Next(1, 15));
                if (await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQDoc_Header WHERE TSeries='CS' AND TNo=@n", new { n = next }) > 0) continue;
                await cn.ExecuteAsync("INSERT INTO dbo.IQDoc_Header (Tt_Id, TSeries, TNo, TExtn, Doc_Date, Mode, Act_ID, HeadAct_ID, Bill_Amt, BillEnteredSystem) VALUES (506,'CS',@n,'',@d,0,6,6,1,'HQ-SIM')",
                    new { n = next, d = Day.ToDateTime(TimeOnly.MinValue) });
                hqNumbers.Add(next);
            }
        });
        var posted = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            var r = new DocumentRequest { Kind = DocumentKind.Sale, Date = Day, PaymentMode = 0, Party = new PartySnapshot(Name: "Walk-in " + i), Lines = [new LineRequest(15, 3, 1, 410)] };
            var t = terminals[i % 2];
            var p = await t.PreviewAsync("mbm", r);
            return await t.PostAsync("mbm", new PostRequest(Guid.NewGuid().ToString(), p.Hash, r));
        }));
        stop.Cancel();
        await hq;
        await using var check = Sql();
        var duplicates = await check.ExecuteScalarAsync<int>(duplicateSql) - baseline;
        output.WriteLine($"MK posted {posted.Length}, simulated HQ PC posted {hqNumbers.Count}, new duplicates {duplicates} (HQ had {baseline} before)");
        Assert.Equal(0, duplicates);
        Assert.Equal(posted.Length, posted.Select(p => p.Number).Distinct().Count());
        await check.ExecuteAsync("DELETE FROM dbo.IQDoc_Header WHERE BillEnteredSystem='HQ-SIM'");
    }
}
