using System.Globalization;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Engine.Masters;
using MK.Hq.Tests.Support;
using Xunit.Abstractions;

namespace MK.Hq.Tests.Parity;

public sealed class ScratchMasters : IAsyncLifetime
{
    public const string Database = "MKHQ_MASTERS_MBM";
    public ServiceProvider Engine { get; private set; } = null!;
    public async Task InitializeAsync()
    {
        await ScratchDatabase.RestoreAsync(Database, "ISTMBM26.bak");
        Engine = await ScratchDatabase.EngineAsync("mbm", Database);
    }
    public async Task DisposeAsync() => await Engine.DisposeAsync();
}

/// <summary>
/// Items and accounts written by the engine against the ones HQ's own screens wrote. Oracles: (1) a clone of a recent
/// HQ-made master, entered with the same inputs, equals the original in every column except identity/name/code/order and
/// the save date; (2) an HQ master re-saved unchanged comes back identical except what HQ's own re-save changes.
/// </summary>
[Trait("Category", "Parity")]
public sealed class MasterParityTests(ScratchMasters fx, ITestOutputHelper output) : IClassFixture<ScratchMasters>
{
    private ItemMasterService Items => fx.Engine.GetRequiredService<ItemMasterService>();
    private static SqlConnection Sql() => new(TestDatabase.ConnectionString(ScratchMasters.Database));

    private static readonly HashSet<string> ItemIdentity = new(StringComparer.OrdinalIgnoreCase)
    {
        "Item_ID", "Item_Name", "Item_Code", "CodeAlias", "Item_Ord", "Ent_Date", "ItemUnitsDtldID", "Ic_Id",
        // maintained by HQ's stock procedures after the first transaction, not by the item screen
        "Stk", "Cost_Calculated", "LastPLUCode", "Rpt_Sel",
    };

    [Fact]
    public async Task New_items_equal_HQ_made_items_with_the_same_inputs()
    {
        await using var cn = Sql();
        // The newest items: created by HQ's current form (older ids include Dec-2023 import rows that HQ merely re-saved).
        var ids = (await cn.QueryAsync<long>("SELECT TOP 25 CAST(Item_ID AS bigint) FROM dbo.IQItems_Mast ORDER BY Item_ID DESC")).ToList();
        var diffs = new List<string>();
        var n = 0;
        foreach (var id in ids)
        {
            var original = await Items.LoadAsync("mbm", id);
            var clone = original with { ItemId = null, Name = (original.Name.Length > 64 ? original.Name[..64] : original.Name) + " MKCL", Code = "MK" + id.ToString(CultureInfo.InvariantCulture) };
            var preview = await Items.PreviewAsync("mbm", clone);
            var receipt = await Items.SaveAsync("mbm", new ItemPost(Guid.NewGuid().ToString(), preview.Hash, clone));
            n++;
            await CompareItemAsync(cn, id, receipt.ItemId, ItemIdentity, diffs, $"clone of {id}");
        }
        Report(diffs, $"{n} items cloned");
        Assert.True(n >= 20);
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task Hq_items_resaved_unchanged_come_back_identical()
    {
        await using var cn = Sql();
        var ids = (await cn.QueryAsync<long>("SELECT TOP 25 CAST(Item_ID AS bigint) FROM dbo.IQItems_Mast WHERE Ent_Date >= 20261001 ORDER BY Item_ID")).ToList();
        var diffs = new List<string>();
        foreach (var id in ids)
        {
            var before = await ItemRowsAsync(cn, id);
            var item = await Items.LoadAsync("mbm", id);
            var preview = await Items.PreviewAsync("mbm", item);
            await Items.SaveAsync("mbm", new ItemPost(Guid.NewGuid().ToString(), preview.Hash, item));
            var after = await ItemRowsAsync(cn, id);
            Diff(before, after, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ent_Date", "ItemUnitsDtldID" }, diffs, $"item {id}");
        }
        Report(diffs, $"{ids.Count} items re-saved");
        Assert.True(ids.Count >= 20);
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task Changing_an_items_gst_rate_rechooses_its_accounts_like_hq()
    {
        await using var cn = Sql();
        // An HQ 5% item and an HQ 18% item: switching the first to 18% must give the second's accounts.
        var five = await cn.ExecuteScalarAsync<long>("SELECT TOP 1 CAST(Item_ID AS bigint) FROM dbo.IQItems_Mast WHERE VAT=5 AND Inc_Tax=1 ORDER BY Item_ID DESC");
        var eighteen = await cn.ExecuteScalarAsync<long>("SELECT TOP 1 CAST(Item_ID AS bigint) FROM dbo.IQItems_Mast WHERE VAT=18 AND Inc_Tax=1 ORDER BY Item_ID DESC");
        Assert.True(five > 0 && eighteen > 0);
        var item = (await Items.LoadAsync("mbm", five)) with { GstRate = 18 };
        var preview = await Items.PreviewAsync("mbm", item);
        Assert.Contains(preview.Warnings, w => w.Contains("GST rate changed"));
        await Items.SaveAsync("mbm", new ItemPost(Guid.NewGuid().ToString(), preview.Hash, item));
        var accounts = "SELECT VIP_ID, VOP_ID, CGSTVIP_ID, CGSTVOP_ID, SGSTVIP_ID, SGSTVOP_ID, CGST, SGST, IGST, VAT FROM dbo.IQItems_Mast WHERE Item_ID=@id";
        var a = (IDictionary<string, object?>)await cn.QueryFirstAsync(accounts, new { id = five });
        var b = (IDictionary<string, object?>)await cn.QueryFirstAsync(accounts, new { id = eighteen });
        foreach (var (k, v) in b) Assert.True(Same(v, a[k]), $"{k}: {a[k]} vs HQ's {v}");
        var ttype = "SELECT CAST(TT_ID AS int) TtId, CAST(Act_ID AS bigint) ActId FROM dbo.Ttype_Acnt WHERE Ic_Id=@id ORDER BY TT_ID";
        Assert.Equal((await cn.QueryAsync<(int, long)>(ttype, new { id = eighteen })).ToList(), (await cn.QueryAsync<(int, long)>(ttype, new { id = five })).ToList());
    }

    [Fact]
    public async Task Items_refuse_what_HQ_would_corrupt_or_reject()
    {
        var group = await Sql().ExecuteScalarAsync<long>("SELECT TOP 1 CAST(Grp_ID AS bigint) FROM dbo.IQItems_Mast ORDER BY Item_ID DESC");
        var basic = new ItemRequest { Name = "MK TEST", GroupId = group, GstRate = 18, PurchaseRate = 10, SaleRate = 12 };
        async Task<string> Code(ItemRequest r) => (await Assert.ThrowsAsync<HqException>(() => Items.PreviewAsync("mbm", r))).Code;
        Assert.Equal("MASTER_FIELD_CHARACTERS", await Code(basic with { Name = "పైపు" }));
        Assert.Equal("MASTER_FIELD_TOO_LONG", await Code(basic with { Name = new string('A', 71) }));
        Assert.Equal("ITEM_GST_RATE", await Code(basic with { GstRate = 7 }));
        var existing = await Sql().QueryFirstAsync<(string Name, long Group, string Code)>("SELECT TOP 1 LTRIM(RTRIM(Item_Name)), CAST(Grp_ID AS bigint), LTRIM(RTRIM(Item_Code)) FROM dbo.IQItems_Mast ORDER BY Item_ID DESC");
        Assert.Equal("ITEM_DUPLICATE_NAME", await Code(basic with { Name = existing.Name.ToLowerInvariant(), GroupId = existing.Group }));
        Assert.Equal("ITEM_DUPLICATE_CODE", await Code(basic with { Code = existing.Code }));
    }

    [Theory]
    [InlineData("349.575", "349.57")]
    [InlineData("3645.725", "3645.72")]
    [InlineData("100.125", "100.13")]
    [InlineData("262", "262")]
    public void Rates_sent_as_float_round_like_SQL_Server(string rate, string stored) =>
        Assert.Equal(decimal.Parse(stored, CultureInfo.InvariantCulture), HqForm.FloatToNumeric(decimal.Parse(rate, CultureInfo.InvariantCulture), 2));

    // ------------------------------------------------------------------ accounts

    private AccountMasterService Accounts => fx.Engine.GetRequiredService<AccountMasterService>();

    private static readonly HashSet<string> AccountIdentity = new(StringComparer.OrdinalIgnoreCase)
    {
        "Act_Id", "actID", "Act_ID", "Act_Name", "Act_Code", "Rec_Index", "Rec_Order", "Ent_Date", "Act_FoodLICExpDate", "SR_NO",
        // IQDayBook.Salesman_ID: NULL before HQ's 4-Nov-2026 build, 0 since (the procedure default changed)
        "Salesman_ID",
        // set to 0 for every row by HQ's report screens later
        "Rpt_Sel",
    };

    [Fact]
    public async Task New_accounts_equal_HQ_made_accounts_with_the_same_inputs()
    {
        await using var cn = Sql();
        var ids = (await cn.QueryAsync<long>("""
            SELECT TOP 20 CAST(Act_Id AS bigint) FROM dbo.IQAccmst WHERE Act_Cat='A' AND Act_Type IN ('Customer','Supplier') AND ISNULL(Act_OpSplit,0)=0
              AND Act_Name NOT LIKE '%MKCL%' AND Ent_Date < 20261109 ORDER BY Act_Id DESC
            """)).ToList();
        var diffs = new List<string>();
        foreach (var id in ids)
        {
            // The inputs exactly as the user typed them (HQ keeps trailing blanks on create; its edit screen trims them).
            var original = await Accounts.LoadAsync("mbm", id);
            var raw = (IDictionary<string, object?>)await cn.QueryFirstAsync("SELECT * FROM dbo.IQAccAdr WHERE Act_Id=@id", new { id });
            string? Raw(string column) => raw[column] is string v && v.Trim().Length > 0 ? v : null;
            var clone = original with
            {
                AccountId = null, Name = original.Name.TrimEnd() + " MKCL" + (original.Name.Length - original.Name.TrimEnd().Length > 0 ? " " : ""),
                Address1 = Raw("Add_Line1"), Address2 = Raw("Add_Line2"), Town = Raw("Act_Town"), District = Raw("Act_District"), Pin = Raw("Act_PinNo"),
                Phone = Raw("Act_Ph1"), Mobile = Raw("Act_Cell"), Email = Raw("Act_Email"), Person = Raw("person"),
            };
            var preview = await Accounts.PreviewAsync("mbm", clone);
            var receipt = await Accounts.SaveAsync("mbm", new AccountPost(Guid.NewGuid().ToString(), preview.Hash, clone));
            Diff(await AccountRowsAsync(cn, id), await AccountRowsAsync(cn, receipt.AccountId), AccountIdentity, diffs, $"clone of {id}");
        }
        Report(diffs, $"{ids.Count} accounts cloned");
        await AssertOpeningDifferenceBalancesAsync(cn);
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task Hq_accounts_resaved_unchanged_come_back_identical()
    {
        await using var cn = Sql();
        // Accounts HQ's edit screen saved in November (older accounts; 11317+ were only created) — already in its normalised shape.
        var ids = (await cn.QueryAsync<long>("""
            SELECT CAST(Act_Id AS bigint) FROM dbo.IQAccmst WHERE Act_Cat='A' AND Act_Type IN ('Customer','Supplier') AND Ent_Date BETWEEN 20261101 AND 20261108
              AND Act_Id > 8 AND Act_Id < 11317 AND ISNULL(Act_OpSplit,0)=0 ORDER BY Act_Id
            """)).ToList();
        var order = await cn.QueryAsync<(long, int)>("SELECT CAST(Act_Id AS bigint), CAST(ISNULL(Rec_Order,0) AS int) FROM dbo.IQAccmst ORDER BY Act_Id");
        var diffs = new List<string>();
        foreach (var id in ids)
        {
            var before = await AccountRowsAsync(cn, id);
            var account = await Accounts.LoadAsync("mbm", id);
            var preview = await Accounts.PreviewAsync("mbm", account);
            await Accounts.SaveAsync("mbm", new AccountPost(Guid.NewGuid().ToString(), preview.Hash, account));
            // Adjusted is recomputed by HQ's outstanding reports; HQ's own re-save also writes 0 there.
            Diff(before, await AccountRowsAsync(cn, id), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ent_Date", "Act_FoodLICExpDate", "SR_NO", "Salesman_ID", "Adjusted" }, diffs, $"account {id}");
        }
        Report(diffs, $"{ids.Count} accounts re-saved");
        Assert.True(ids.Count >= 6, $"only {ids.Count} accounts to re-save");
        Assert.Equal(order.ToList(), (await cn.QueryAsync<(long, int)>("SELECT CAST(Act_Id AS bigint), CAST(ISNULL(Rec_Order,0) AS int) FROM dbo.IQAccmst ORDER BY Act_Id")).ToList());
        await AssertOpeningDifferenceBalancesAsync(cn);
        Assert.Empty(diffs);
    }

    [Fact]
    public async Task A_new_registered_supplier_takes_its_state_from_the_gstin_and_books_its_opening_balance()
    {
        await using var cn = Sql();
        var request = new AccountRequest
        {
            Name = "mk test traders", Type = "Supplier", Address2 = "MAIN ROAD", Town = "RAJAHMUNDRY", Mobile = "9876543210",
            GstType = 0, Gstin = "36aabcu9603r1zm", OpeningBalance = 1500.50m,
        };
        var preview = await Accounts.PreviewAsync("mbm", request);
        Assert.Equal("36-Telangana", preview.State);
        var receipt = await Accounts.SaveAsync("mbm", new AccountPost(Guid.NewGuid().ToString(), preview.Hash, request));
        var row = await cn.QueryFirstAsync<(string Name, string Type, long Group, string Sign, decimal Opbal, string Code)>(
            "SELECT Act_Name, Act_Type, CAST(Rec_Higher AS bigint), Act_OpSign, CAST(Act_OPbal AS decimal(19,2)), Act_Code FROM dbo.IQAccmst WHERE Act_Id=@id", new { id = receipt.AccountId });
        Assert.Equal("Mk test traders", row.Name); // HQ capitalises the first letter
        Assert.Equal(407, row.Group);
        Assert.Equal(("C", 1500.50m), (row.Sign, row.Opbal));
        Assert.Equal("A" + (1000 + receipt.AccountId), row.Code);
        var adr = await cn.QueryFirstAsync<(string Gstin, int Type, string State, string Line1)>(
            "SELECT GSTIN, CAST(GSTTaxType AS int), Act_state, Add_Line1 FROM dbo.IQAccAdr WHERE Act_Id=@id", new { id = receipt.AccountId });
        Assert.Equal(("36AABCU9603R1ZM", 0, "36-Telangana", " "), adr);
        Assert.Equal(1500.50m, await cn.ExecuteScalarAsync<decimal>("SELECT CAST(AMOUNT AS decimal(19,2)) FROM dbo.IQDayBook WHERE Act_ID=@id AND Doc_ID=-1 AND TYPE='C' AND VTYPE='opbal'", new { id = receipt.AccountId }));
        await AssertOpeningDifferenceBalancesAsync(cn);

        async Task<string> Code(AccountRequest r) => (await Assert.ThrowsAsync<HqException>(() => Accounts.PreviewAsync("mbm", r))).Code;
        Assert.Equal("ACCOUNT_DUPLICATE_NAME", await Code(request with { Name = "MK TEST TRADERS" }));
        Assert.Equal("ACCOUNT_GSTIN", await Code(request with { Name = "Another", Gstin = null }));
        Assert.Equal("ACCOUNT_GSTIN", await Code(request with { Name = "Another", GstType = 2 }));
        Assert.Equal("MASTER_FIELD_CHARACTERS", await Code(request with { Name = "Ramu's shop" }));
    }

    /// <summary>Account 5 must offset every other opening row exactly, as UpdateOpeningBalanceDifferenceAccount leaves it.</summary>
    private static async Task AssertOpeningDifferenceBalancesAsync(SqlConnection cn)
    {
        var total = await cn.ExecuteScalarAsync<decimal>("SELECT CAST(SUM(CASE WHEN TYPE IN ('D','d') THEN AMOUNT ELSE -AMOUNT END) AS decimal(19,2)) FROM dbo.IQDayBook WHERE Doc_ID=-1");
        Assert.Equal(0m, total);
        Assert.Equal(1, await cn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQDayBook WHERE Act_ID=5"));
    }

    private static async Task<Dictionary<string, List<IDictionary<string, object?>>>> AccountRowsAsync(SqlConnection cn, long id) => new()
    {
        ["IQAccmst"] = (await cn.QueryAsync("SELECT * FROM dbo.IQAccmst WHERE Act_Id=@id", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        ["IQAccAdr"] = (await cn.QueryAsync("SELECT * FROM dbo.IQAccAdr WHERE Act_Id=@id", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        ["IQAccOthers"] = (await cn.QueryAsync("SELECT * FROM dbo.IQAccOthers WHERE actID=@id ORDER BY LnNo", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        ["IQDayBook"] = (await cn.QueryAsync("SELECT * FROM dbo.IQDayBook WHERE Act_ID=@id AND Doc_ID=-1 ORDER BY SR_NO", new { id })).Cast<IDictionary<string, object?>>().ToList(),
    };

    // ------------------------------------------------------------------ helpers

    private static async Task<Dictionary<string, List<IDictionary<string, object?>>>> ItemRowsAsync(SqlConnection cn, long id) => new()
    {
        ["IQItems_Mast"] = (await cn.QueryAsync("SELECT * FROM dbo.IQItems_Mast WHERE Item_ID=@id", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        ["IQItems_Units"] = (await cn.QueryAsync("SELECT * FROM dbo.IQItems_Units WHERE Item_ID=@id ORDER BY Recd_ID, UNit_Id", new { id })).Cast<IDictionary<string, object?>>().ToList(),
        ["Ttype_Acnt"] = (await cn.QueryAsync("SELECT * FROM dbo.Ttype_Acnt WHERE Ic_Id=@id ORDER BY TT_ID", new { id })).Cast<IDictionary<string, object?>>().ToList(),
    };

    private static async Task CompareItemAsync(SqlConnection cn, long hq, long mine, HashSet<string> ignore, List<string> diffs, string label)
    {
        var a = await ItemRowsAsync(cn, hq);
        var b = await ItemRowsAsync(cn, mine);
        // HQ's Ttype rows carry its own save date; the TT_ID set can differ for items saved before a type existed.
        Diff(a, b, ignore, diffs, label);
    }

    private static void Diff(Dictionary<string, List<IDictionary<string, object?>>> a, Dictionary<string, List<IDictionary<string, object?>>> b,
        HashSet<string> ignore, List<string> diffs, string label)
    {
        foreach (var (table, rows) in a)
        {
            var other = b[table];
            if (rows.Count != other.Count) { diffs.Add($"{label} {table}: {rows.Count} rows vs {other.Count}"); continue; }
            for (var i = 0; i < rows.Count; i++)
                foreach (var (column, value) in rows[i])
                    if (!ignore.Contains(column) && !Same(value, other[i][column]))
                        diffs.Add($"{label} {table}[{i}].{column}: HQ '{value}' engine '{other[i][column]}'");
        }
    }

    private void Report(List<string> diffs, string summary)
    {
        output.WriteLine(summary);
        foreach (var g in diffs.GroupBy(d => System.Text.RegularExpressions.Regex.Replace(d, @"^.*? (\w+)\[\d+\]\.(\w+):.*$", "$1.$2")).OrderBy(g => g.Key))
        {
            output.WriteLine($"DIFF {g.Key}: {g.Count()}");
            foreach (var e in g.Take(3)) output.WriteLine("    " + e);
        }
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
