using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Masters;

/// <summary>What the shop enters for an item (docs/hq-spec-items.md §5). Everything else is HQ's form default or derived.</summary>
public sealed record ItemRequest
{
    /// <summary>Null for a new item; the HQ Item_ID to change an existing one.</summary>
    public long? ItemId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Blank = HQ's default code (COUNT+1).</summary>
    public string? Code { get; init; }
    public long GroupId { get; init; }
    public long CategoryId { get; init; }
    public decimal GstRate { get; init; }
    public string? Hsn { get; init; }
    public decimal PurchaseRate { get; init; }
    public decimal SaleRate { get; init; }
    public decimal Mrp { get; init; }
    public string? Description { get; init; }
    public bool Active { get; init; } = true;
    /// <summary>Purchase/sale pack text (KGS, PCS, METER…). New items only; blank = HQ's default (the last item's pack).
    /// HQ locks the pack once an item is used, so a change keeps the stored one.</summary>
    public string? Pack { get; init; }
}

public sealed record ItemPost(string OperationId, string ExpectedHash, ItemRequest Item);

public sealed record ItemPreview(string Hash, long? ItemId, string Code, IReadOnlyList<string> Warnings, IReadOnlyList<ItemAccountRow> Accounts, IReadOnlyList<string> PlanSteps);

public sealed record ItemAccountRow(string Use, long AccountId, string Account);

public sealed record ItemReceipt(string OperationId, long ItemId, string Name, string Code, bool Created);

public sealed record MasterOption(long Id, string Name);

public sealed record ItemOptions(IReadOnlyList<MasterOption> Groups, IReadOnlyList<MasterOption> Categories, IReadOnlyList<decimal> GstRates, string DefaultCode, string Pack);

/// <summary>
/// Creates and changes items exactly as HQ's Items screen (FrmItems.CmdSave_Click) does: IQItems_Mast_insert (insert or,
/// with an id, its update branch), the images call, the one unit row, and one Ttype_Acnt row per transaction type with
/// the GST rate's accounts — but as one transaction.
/// </summary>
public sealed class ItemMasterService(FirmRegistry firms, WriteCoordinator coordinator, Catalogue catalogue, HqOptions options)
{
    /// <summary>GST rates HQ's item screen maps to accounts (Set_GSTAccounts); 0 maps to the exempt accounts.</summary>
    public static readonly IReadOnlyList<decimal> Rates = [0m, 0.1m, 3m, 5m, 12m, 18m, 28m, 40m];

    private static readonly string[] PurchaseSide = ["Purchase", "Purchase Return", "Purchase Order", "Transfer In"];

    public async Task<ItemOptions> OptionsAsync(string firmId, CancellationToken ct = default)
    {
        await using var s = await firms.Get(firmId).OpenAsync(ct);
        var groups = await s.QueryAsync<MasterOption>("SELECT CAST(Grp_ID AS bigint) Id, LTRIM(RTRIM(Grp_Name)) Name FROM dbo.IQGroups ORDER BY Grp_Name");
        var categories = await s.QueryAsync<MasterOption>("SELECT CAST(Item_CatID AS bigint) Id, LTRIM(RTRIM(Item_CatName)) Name FROM dbo.IQItems_Cat ORDER BY Item_CatName");
        var code = await s.ScalarAsync<int>("SELECT COUNT(Item_ID)+1 FROM dbo.IQItems_Mast");
        var pack = await s.ScalarAsync<string?>("SELECT TOP 1 Item_Pack FROM dbo.IQItems_Mast ORDER BY Item_ID DESC") ?? "";
        return new ItemOptions(groups, categories, Rates, code.ToString(CultureInfo.InvariantCulture), pack.Trim());
    }

    /// <summary>The stored item in the request shape the editor uses.</summary>
    public async Task<ItemRequest> LoadAsync(string firmId, long itemId, CancellationToken ct = default)
    {
        await using var s = await firms.Get(firmId).OpenAsync(ct);
        var (item, unit) = await ReadAsync(s, itemId);
        return new ItemRequest
        {
            ItemId = itemId, Name = HqForm.ReloadedTrimmed(item["Item_Name"]), Code = HqForm.ReloadedTrimmed(item["Item_Code"]),
            GroupId = HqForm.Id(item["Grp_ID"]), CategoryId = HqForm.Id(item["Cat_ID"]), GstRate = HqForm.Number(item["VAT"]),
            Hsn = HqForm.ReloadedTrimmed(item["HSNCODE"]), PurchaseRate = HqForm.Number(unit["Unit_Prate"]), SaleRate = HqForm.Number(unit["Unit_WSrate"]),
            Mrp = HqForm.Number(unit["Unit_MRP"]), Description = HqForm.ReloadedTrimmed(item["Item_Desc"]), Active = HqForm.Number(item["Item_Stat"]) == 0,
            Pack = HqForm.ReloadedTrimmed(item["Item_Pack"]),
        };
    }

    public async Task<ItemPreview> PreviewAsync(string firmId, ItemRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var p = await PlanAsync(s, request);
        return new ItemPreview(p.Plan.Hash, request.ItemId, p.Code, p.Warnings, p.Accounts, p.Plan.Steps.Select(x => x.Label).ToList());
    }

    public async Task<ItemReceipt> SaveAsync(string firmId, ItemPost post, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<ItemReceipt>(
            firm, post.OperationId, post.Item.ItemId is null ? "item.create" : "item.update", DocumentService.RequestHash(post), [],
            async session =>
            {
                var p = await PlanAsync(session, post.Item);
                Guard.Require(p.Plan.Hash == post.ExpectedHash, "REVIEW_CHANGED", "The item or HQ's item settings changed since review. Review it again.", FailureKind.Conflict);
                return new PreparedWrite<ItemReceipt>(p.Plan, outcome =>
                {
                    var id = post.Item.ItemId ?? outcome.Long(ItemIdVariable);
                    return (new ItemReceipt(post.OperationId, id, post.Item.Name, p.Code, post.Item.ItemId is null),
                        post.Item.ItemId is null
                            ? new WriteEvidence(WriteEvidence.ItemCreate, id, Reference: p.Code)
                            : new WriteEvidence(WriteEvidence.ItemUpdate, id, Reference: p.Code) { AfterEntry = p.PreviousUnitRow });
                });
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    // ------------------------------------------------------------------ planning

    private const string ItemIdVariable = "itemId", ItemOrdVariable = "itemOrd";

    private static string TaxUse(string column) => column switch
    {
        "VIP_ID" => "IGST input", "VOP_ID" => "IGST output", "CGSTVIP_ID" => "CGST input", "CGSTVOP_ID" => "CGST output",
        "SGSTVIP_ID" => "SGST input", "SGSTVOP_ID" => "SGST output", _ => column,
    };

    private sealed record Planned(WritePlan Plan, string Code, List<string> Warnings, List<ItemAccountRow> Accounts, long PreviousUnitRow);

    private static async Task<(IReadOnlyDictionary<string, object?> Item, IReadOnlyDictionary<string, object?> Unit)> ReadAsync(HqSession s, long itemId)
    {
        var item = (await s.RowsAsync("SELECT * FROM dbo.IQItems_Mast WHERE Item_ID=@id", new { id = itemId })).FirstOrDefault()
            ?? throw HqException.NotFound("HQ_ITEM_NOT_FOUND", "That item is not in HQ");
        var units = await s.RowsAsync("SELECT * FROM dbo.IQItems_Units WHERE Item_ID=@id AND ISNULL(Recd_ID,0)=0", new { id = itemId });
        Guard.Supported(units.Count == 1, "HQ_ITEM_UNITS_UNSUPPORTED", "This item has several unit rows in HQ; change it in HQ");
        return (item, units[0]);
    }

    private async Task<Planned> PlanAsync(HqSession s, ItemRequest r)
    {
        var config = (await s.RowsAsync("SELECT Default_HSNUQC, Hide_ItemImagesTAB_Items, ItemCode_AutoGenerateType, ShowCodeAlias, GST, Enable_BOM, Enable_PLUCode FROM dbo.Configs")).Single();
        Guard.Supported(HqForm.Number(config["GST"]) > 0 && HqForm.Number(config["Hide_ItemImagesTAB_Items"]) == 0 && HqForm.Number(config["ShowCodeAlias"]) == 0
            && HqForm.Number(config["Enable_BOM"]) == 0 && HqForm.Number(config["Enable_PLUCode"]) == 0 && HqForm.Number(config["ItemCode_AutoGenerateType"]) == 0,
            "HQ_ITEM_PROFILE_UNSUPPORTED", "HQ's item settings differ from the ones this engine was verified against; create items in HQ");

        var name = HqForm.Text(r.Name, "item name", 70, required: true, apostrophe: true).Trim();
        var hsn = HqForm.Text(r.Hsn, "HSN code", 50).Trim();
        var description = HqForm.Text(r.Description, "description", 500, apostrophe: true).Trim();
        Guard.Require(Rates.Contains(r.GstRate), "ITEM_GST_RATE", "Choose a GST rate HQ knows: " + string.Join(", ", Rates.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture) + "%")));
        foreach (var (v, label) in new[] { (r.PurchaseRate, "purchase rate"), (r.SaleRate, "sale rate"), (r.Mrp, "MRP") })
            Guard.Require(v >= 0 && v < 10_000_000 && Money.Scale(v) <= 4, "ITEM_RATE", $"Enter a valid {label}");
        Guard.Require(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQGroups WHERE Grp_ID=@g", new { g = r.GroupId }) == 1, "ITEM_GROUP", "Choose the item's group (company)");
        if (r.CategoryId != 0)
            Guard.Require(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQItems_Cat WHERE Item_CatID=@c", new { c = r.CategoryId }) == 1, "ITEM_CATEGORY", "That category is not in HQ");

        var editing = r.ItemId is { } existingId;
        IReadOnlyDictionary<string, object?>? stored = null, storedUnit = null;
        if (editing) (stored, storedUnit) = await ReadAsync(s, r.ItemId!.Value);
        var defaultCode = (await s.ScalarAsync<int>("SELECT COUNT(Item_ID)+1 FROM dbo.IQItems_Mast")).ToString(CultureInfo.InvariantCulture);
        var code = HqForm.Text(string.IsNullOrWhiteSpace(r.Code) ? (editing ? HqForm.ReloadedTrimmed(stored!["Item_Code"]) : defaultCode) : r.Code, "item code", 30).Trim().ToUpperInvariant();
        var warnings = new List<string>();

        // HQ's IsDuplicateFound: the name within its group, the code across all items (case-insensitive, trailing blanks ignored).
        var self = r.ItemId ?? 0;
        Guard.Require(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQItems_Mast WHERE Grp_ID=@g AND Item_Name=@name AND Item_ID<>@self", new { g = r.GroupId, name, self }) == 0,
            "ITEM_DUPLICATE_NAME", $"An item named '{name}' already exists in this group", FailureKind.Conflict);
        Guard.Require(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQItems_Mast WHERE Item_Code=@code AND Item_ID<>@self", new { code, self }) == 0,
            "ITEM_DUPLICATE_CODE", $"Item code {code} is already used", FailureKind.Conflict);

        // Accounts for the GST rate (Set_GSTAccounts): looked up by HQ's trade-type label, newest account first.
        async Task<long> ByLabel(string label) =>
            await s.ScalarAsync<long?>("SELECT TOP 1 CAST(Act_Id AS bigint) FROM dbo.IQAccmst WHERE Act_Tt=@label ORDER BY Act_Id DESC", new { label }) ?? 0;
        var rateChanged = !editing || HqForm.Number(stored!["VAT"]) != r.GstRate;
        var known = r.GstRate != 0;
        string Label(decimal x) => x == 0.1m ? "0.10" : x.ToString("0.##", CultureInfo.InvariantCulture);
        var half = Label(r.GstRate / 2 == 0.05m ? 0.05m : r.GstRate / 2);
        var taxIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, label) in new[] { ("VIP_ID", $"IGST Input {Label(r.GstRate)}%"), ("VOP_ID", $"IGST Output {Label(r.GstRate)}%"),
                     ("CGSTVIP_ID", $"CGST Input {half}%"), ("CGSTVOP_ID", $"CGST Output {half}%"), ("SGSTVIP_ID", $"SGST Input {half}%"), ("SGSTVOP_ID", $"SGST Output {half}%") })
        {
            if (!rateChanged) { taxIds[column] = HqForm.Id(stored![column]); continue; }
            var id = known ? await ByLabel(label) : 0;
            Guard.Supported(!known || id > 0, "HQ_GST_ACCOUNT_MISSING", $"HQ has no '{label}' account; create the item in HQ");
            taxIds[column] = id;
        }

        var ttypes = await s.QueryAsync<(string VouchType, int TtId, string Name, int Firstor)>("""
            SELECT RTRIM(vouchtype), CAST(tt_id AS int), RTRIM(ttype), CAST(ISNULL(Firstor,0) AS int) FROM dbo.TTypes
            WHERE vouchtype IN ('Purchase','Sales','Purchase Return','Sales Return','Purchase Order','Sales Order','Transfer In','Transfer Out')
            GROUP BY vouchtype, tt_id, ttype, Firstor ORDER BY Vouchtype, tt_id
            """);
        var storedTtype = editing
            ? (await s.QueryAsync<(int TtId, long ActId)>("SELECT CAST(TT_ID AS int), CAST(Act_ID AS bigint) FROM dbo.Ttype_Acnt WHERE Ic_Id=@id", new { id = r.ItemId })).ToDictionary(x => x.TtId, x => x.ActId)
            : [];
        var ttypeAccounts = new List<(int TtId, long ActId, string Use)>();
        foreach (var t in ttypes)
        {
            var purchase = PurchaseSide.Contains(t.VouchType, StringComparer.OrdinalIgnoreCase);
            if (!rateChanged && storedTtype.TryGetValue(t.TtId, out var keep) && keep > 0) { ttypeAccounts.Add((t.TtId, keep, t.Name)); continue; }
            var label = !known ? (purchase ? "Purchase – Exempted" : "Sales – Exempt")
                : $"{(t.Firstor == 1 ? "GST" : "IGST")} {(purchase ? "Purchase" : "Sales")} {Label(r.GstRate)}%";
            var id = await ByLabel(label);
            Guard.Supported(id > 0, "HQ_GST_ACCOUNT_MISSING", $"HQ has no '{label}' account; create the item in HQ");
            ttypeAccounts.Add((t.TtId, id, t.Name));
        }

        // ---- IQItems_Mast_insert arguments: HQ's form defaults for a new item, the stored row (as HQ's form reloads it) for a change.
        var cess = await s.ScalarAsync<long?>("SELECT TOP 1 CAST(Act_Id AS bigint) FROM dbo.IQAccmst WHERE (Act_Type='Income' OR Act_Type='Expense') AND Act_Cat='A' ORDER BY Act_name") ?? 0;
        var last = (await s.RowsAsync("SELECT TOP 1 Item_Pack, Sale_PACK FROM dbo.IQItems_Mast ORDER BY Item_ID DESC")).FirstOrDefault();
        object? Old(string column) => stored![column];
        var m = new Args();
        if (!editing)
        {
            var pack = string.IsNullOrWhiteSpace(r.Pack) ? null : HqForm.Text(r.Pack, "pack", 15, apostrophe: true).Trim();
            m.Set("mItem_Pack", pack ?? HqForm.Spaced(Convert.ToString(last?["Item_Pack"], CultureInfo.InvariantCulture)))
             .Set("mSale_PACK", pack ?? HqForm.Trimmed(Convert.ToString(last?["Sale_PACK"], CultureInfo.InvariantCulture)))
             .Set("mCodeAlias", code)
             .Zero("mGen_ID mAllow_Disc mMin_Qty mMax_Qty mDis_Recd mDis_Issd mAllow_Batch mbrand_ID mDept_ID mSetDiscMRP_SaleRate mWgt mAllow_PLUCode mNoOfVeh_Servs mAllow_Serialno mGSTCess mGSTAddLCessRate mGoods_Or_Serv mRet_Disc1Per mRet_Disc2Per mIsWgtBarcode mEnableSetNewGSTRate_On_ItemPriceAbove_TextileItems mIsTobaccoItem mMaxCheckSaleDiscPer")
             .Set("mRack_No", " ").Set("mSch_Drug", " ").Set("mInc_Tax", 1)
             .Set("mPrimary_PackID", 3).Set("mPur_PackID", 3).Set("mSal_PackId", 3).Set("mRpt_PackID", 3)
             .Blank("mSizeCol mTelItem_Name mUniformCode")
             .Set("mGSTCess_ActID", cess).Set("mGSTAddlCess_ActID", cess).Set("mUQC", HqForm.Trimmed(Convert.ToString(config["Default_HSNUQC"], CultureInfo.InvariantCulture)))
             .Set("mNewGSTRate_TextileItems", 18m).Set("mItemPriceAbove_TextitleItems", 2500m)
             .Set("mItem_Ord", new Ref(ItemOrdVariable));
        }
        else
        {
            m.Set("mItem_Pack", HqForm.Reloaded(Old("Item_Pack"))).Set("mSale_PACK", HqForm.ReloadedTrimmed(Old("Sale_PACK")))
             .Set("mRack_No", HqForm.Reloaded(Old("Rack_No"))).Set("mSch_Drug", HqForm.Reloaded(Old("Sch_Drug")))
             .Set("mSizeCol", HqForm.ReloadedTrimmed(Old("SizeCol"))).Set("mTelItem_Name", HqForm.ReloadedTrimmed(Old("TelItem_Name")))
             .Set("mUniformCode", HqForm.ReloadedTrimmed(Old("UniformCode"))).Set("mUQC", HqForm.ReloadedTrimmed(Old("UQC")))
             .Set("mItem_Ord", (int)HqForm.Number(Old("Item_Ord")))
             // txtBarcode is loaded from the row; it follows the code only when the code is retyped.
             .Set("mCodeAlias", code == HqForm.ReloadedTrimmed(Old("Item_Code")) ? HqForm.ReloadedTrimmed(Old("CodeAlias")) : code);
            foreach (var column in new[] { "Gen_ID", "Allow_Disc", "Min_Qty", "Max_Qty", "Dis_Recd", "Dis_Issd", "Allow_Batch", "brand_ID", "Dept_ID", "SetDiscMRP_SaleRate",
                         "Wgt", "Allow_PLUCode", "NoOfVeh_Servs", "Allow_Serialno", "GSTCess", "GSTAddLCessRate", "Goods_Or_Serv", "Ret_Disc1Per", "Ret_Disc2Per", "IsWgtBarcode",
                         "EnableSetNewGSTRate_On_ItemPriceAbove_TextileItems", "IsTobaccoItem", "MaxCheckSaleDiscPer", "Inc_Tax", "GSTCess_ActID", "GSTAddlCess_ActID" })
                m.Set("m" + column, HqForm.Number(Old(column)));
            foreach (var column in new[] { "Primary_PackID", "Pur_PackID", "Sal_PackId", "Rpt_PackID" })
                m.Set("m" + column, HqForm.Number(Old(column)) is var pack && pack > 0 ? pack : 3); // GetPackIsZero_Set3
            m.Set("mNewGSTRate_TextileItems", Old("NewGSTRate_TextileItems") is null ? 18m : HqForm.Number(Old("NewGSTRate_TextileItems")))
             .Set("mItemPriceAbove_TextitleItems", Old("ItemPriceAbove_TextitleItems") is null ? 2500m : HqForm.Number(Old("ItemPriceAbove_TextitleItems")));
        }
        m.Set("mItem_ID", r.ItemId ?? 0).Set("mItem_Name", name).Set("mItem_Code", code)
         .Set("mGrp_ID", r.GroupId).Set("mCat_ID", r.CategoryId).Set("mVAT", r.GstRate).Set("mCST", 0m)
         .Zero("mCST_PActID mCST_SAcID mPur_ActID mPurRet_ActID mSal_ActID mSalRet_ActID mDeActDate mRpt_Sel")
         .Set("mDeActReason", "").Set("mItem_Stat", r.Active ? 0 : 1)
         .Set("mEnt_Date", new Clock(ClockField.CompactDay)).Set("mEnt_Uid", options.HqUserId)
         .Set("mItem_Desc", description).Set("mHSNCODE", hsn)
         .Set("mCGST", r.GstRate / 2).Set("mSGST", r.GstRate / 2).Set("mIGST", r.GstRate);
        foreach (var (column, id) in taxIds) m.Set("m" + column, id);

        // ---- IQItems_Units_insert: one row, unit 3/3, sale rate in WSrate; hidden fields reloaded on a change.
        object? OldUnit(string column) => storedUnit![column];
        var u = new Args()
            .Set("mRecd_ID", 0).Set("mItem_ID", r.ItemId is { } uid ? uid : new Ref(ItemIdVariable)).Set("mUNit_Id", 3).Set("mUnit_PrimaryID", 3).Set("mUnit_Qty", 1).Set("mUnit_Equ", 1)
            .Set("mUnit_Prate", r.PurchaseRate).Set("mUnit_WSrate", r.SaleRate).Set("mUnit_MRP", r.Mrp)
            .Set("mLn_No", 0).Set("mChrg_Set", 0).Set("mWs_Disc", 0m).Set("mRs_Disc", 0m).Set("mPLBExcTYPE", "0")
            .Set("mSP_PurRate", HqForm.FloatToNumeric(r.PurchaseRate, 2)).Set("mPP_SaleRate", HqForm.FloatToNumeric(r.SaleRate, 2))
            .Set("mThirdConv", editing ? HqForm.Number(storedUnit!["ThirdConv"]) : 0m);
        var hidden = new[] { "PGRATE", "PLBExcise", "PDRATE", "PDTYPE", "PEXCISE", "PETYPE", "PCTAX", "PCTYPE", "PSTAX", "PSTYPE", "PADD", "PLESS", "PDIFF",
            "SGRATE", "SDRATE", "SDTYPE", "SEXCISE", "SETYPE", "SCTAX", "SCTYPE", "SSTAX", "SSTYPE", "SADD", "SLESS", "SDIFF", "PADDTYPE", "PLESSTYPE", "SADDTYPE", "SLESSTYPE",
            "SPLRate1", "SPLRate2", "SPLRate3", "SPLRate4", "SPLRate5", "SPLRate6", "SPLRate7", "SPLRate8", "SPLRate9", "SPLRate10" };
        foreach (var column in hidden) u.Set("m" + column, editing ? HqForm.Number(OldUnit(column)) : 0m);
        if (editing)
        {
            u.Set("mUnit_Srate", HqForm.Number(OldUnit("Unit_Srate"))).Set("mReportPack", HqForm.ReloadedTrimmed(OldUnit("ReportPack")))
             .Set("mIssueConv", HqForm.Number(OldUnit("IssueConv")) is var conv && conv > 0 ? conv : 1m).Set("mStockConv", HqForm.Number(OldUnit("StockConv")));
            Guard.Supported(HqForm.Number(OldUnit("IssueConv")) is 0 or 1, "HQ_ITEM_UNITS_UNSUPPORTED", "This item converts between two packs in HQ; change it in HQ");
        }
        else u.Set("mUnit_Srate", 0m).Set("mReportPack", "").Set("mIssueConv", 1m).Set("mStockConv", 1);

        // ---- the plan
        var steps = new List<PlanStep>();
        var idArg = r.ItemId is { } eid ? (object)eid : new Ref(ItemIdVariable);
        var guard = new Args().Set("g", r.GroupId).Set("name", name).Set("code", code).Set("self", self);
        steps.Add(new SqlStep("HQ duplicate check (name in group, code)", """
            IF EXISTS (SELECT 1 FROM dbo.IQItems_Mast WITH (UPDLOCK, HOLDLOCK) WHERE Grp_ID=@g AND Item_Name=@name AND Item_ID<>@self)
              RAISERROR('MK:ITEM_DUPLICATE_NAME|An item with this name was just created in this group', 16, 1);
            IF EXISTS (SELECT 1 FROM dbo.IQItems_Mast WITH (UPDLOCK, HOLDLOCK) WHERE Item_Code=@code AND Item_ID<>@self)
              RAISERROR('MK:ITEM_DUPLICATE_CODE|This item code was just used', 16, 1);
            """, guard));
        if (!editing)
            steps.Add(new SqlStep("Item order in group", "SELECT CAST(ISNULL(MAX(ISNULL(Item_Ord,0))+1,0) AS smallint) FROM dbo.IQItems_Mast WHERE Grp_ID=@g", new Args().Set("g", r.GroupId), ItemOrdVariable));
        steps.Add(new CallStep(editing ? "Item master (update)" : "Item master", "IQItems_Mast_insert", m,
            editing ? null : new Dictionary<string, string> { ["mItem_ID"] = ItemIdVariable }));
        var images = editing ? new[] { 1, 2, 3, 4 }.Select(i => HqForm.ReloadedTrimmed(Old("Item_Image" + i))).ToArray() : ["", "", "", ""];
        steps.Add(new CallStep("Item images", "IQItems_Mast_Update_ItemImages", new Args().Set("mItem_ID", idArg)
            .Set("mItem_Image1", images[0]).Set("mItem_Image2", images[1]).Set("mItem_Image3", images[2]).Set("mItem_Image4", images[3])));
        steps.Add(new SqlStep("Replace unit row", "DELETE FROM dbo.IQItems_Units WHERE Recd_ID=0 AND Item_ID=@id", new Args().Set("id", idArg)));
        steps.Add(new CallStep("Unit and rates", "IQItems_Units_insert", u));
        foreach (var (ttId, actId, use) in ttypeAccounts)
            steps.Add(new CallStep($"Account for {use}", "ttype_acnt_insert", new Args().Set("ic_id", idArg).Set("tt_id", ttId).Set("act_id", actId)
                .Set("set_no", 0).Set("ent_date", new Clock(ClockField.Day)).Set("ent_uid", options.HqUserId).Set("edmode", 0)));
        steps.Add(new VerifyStep("Verify item saved", "SELECT COUNT(*) FROM dbo.IQItems_Mast WHERE Item_ID=@id AND Item_Name=@name AND Item_Code=@code",
            new Args().Set("id", idArg).Set("name", name).Set("code", code), 1, "HQ_SAVE_MISMATCH", "HQ did not store the item as entered"));
        steps.Add(new VerifyStep("Verify one unit row", "SELECT COUNT(*) FROM dbo.IQItems_Units WHERE Item_ID=@id AND Recd_ID=0", new Args().Set("id", idArg), 1,
            "HQ_SAVE_MISMATCH", "HQ stored a different unit row"));

        if (editing && rateChanged) warnings.Add("The GST rate changed: all of the item's tax accounts are re-chosen for the new rate, as HQ does.");
        if (!editing && !string.IsNullOrWhiteSpace(r.Code) && code != defaultCode) warnings.Add($"HQ would suggest code {defaultCode}; {code} is used.");
        var names = (await s.QueryAsync<(long, string)>("SELECT CAST(Act_Id AS bigint), LTRIM(RTRIM(Act_Name)) FROM dbo.IQAccmst WHERE Act_Id IN @ids",
            new { ids = ttypeAccounts.Select(t => t.ActId).Concat(taxIds.Values).Distinct().ToArray() })).ToDictionary(x => x.Item1, x => x.Item2);
        var accounts = ttypeAccounts.Select(t => new ItemAccountRow(t.Use, t.ActId, names.GetValueOrDefault(t.ActId, "#" + t.ActId)))
            .Concat(taxIds.Where(t => t.Value > 0).Select(t => new ItemAccountRow(TaxUse(t.Key), t.Value, names.GetValueOrDefault(t.Value, "#" + t.Value)))).ToList();
        var previousUnit = editing ? HqForm.Id(storedUnit!["ItemUnitsDtldID"]) : 0;
        return new Planned(new WritePlan(editing ? $"Change item {name}" : $"New item {name}", steps), code, warnings, accounts, previousUnit);
    }
}
