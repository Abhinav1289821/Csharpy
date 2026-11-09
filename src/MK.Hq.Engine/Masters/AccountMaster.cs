using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Masters;

/// <summary>What the shop enters for a customer or supplier (docs/hq-spec-accounts.md §3.2).</summary>
public sealed record AccountRequest
{
    /// <summary>Null for a new account; the HQ Act_Id to change one.</summary>
    public long? AccountId { get; init; }
    public string Name { get; init; } = "";
    /// <summary>"Customer" or "Supplier".</summary>
    public string Type { get; init; } = "Customer";
    public string? Address1 { get; init; }
    public string? Address2 { get; init; }
    public string? Town { get; init; }
    public string? District { get; init; }
    public string? Pin { get; init; }
    public string? Phone { get; init; }
    public string? Mobile { get; init; }
    public string? Email { get; init; }
    public string? Person { get; init; }
    /// <summary>HQ's GSTTaxType: 0 registered, 1 composition, 2 unregistered, 3 consumer.</summary>
    public int GstType { get; init; } = 2;
    public string? Gstin { get; init; }
    public string? Pan { get; init; }
    /// <summary>One of HQ's state captions ("37-Andhra Pradesh"); taken from the GSTIN for registered parties.</summary>
    public string? State { get; init; }
    public decimal OpeningBalance { get; init; }
    /// <summary>'D' (they owe us) or 'C' (we owe them); default by type.</summary>
    public char? OpeningSide { get; init; }
    public bool BillWise { get; init; }
    public bool Active { get; init; } = true;
}

public sealed record AccountPost(string OperationId, string ExpectedHash, AccountRequest Account);

public sealed record AccountPreview(string Hash, long? AccountId, string Code, string State, IReadOnlyList<string> Warnings, IReadOnlyList<string> PlanSteps);

public sealed record AccountReceipt(string OperationId, long AccountId, string Name, string Code, bool Created);

/// <summary>
/// Creates and changes customers and suppliers exactly as HQ's account screen (FrmAccounts.SaveData) does: IQaccmst_insert
/// (which files the account in the chart of accounts and renumbers its order), the address, the ten contact rows, the
/// opening-balance ledger row and the "Opening Balance Difference" account it balances — as one transaction.
/// </summary>
public sealed class AccountMasterService(FirmRegistry firms, WriteCoordinator coordinator, Catalogue catalogue)
{
    /// <summary>FrmAccounts' state list (the captions HQ stores; no 28 or 38).</summary>
    public static readonly IReadOnlyList<string> States =
    [
        "01-Jammu & Kashmir", "02-Himachal Pradesh", "03-Punjab", "04-Chandigarh", "05-Uttarakhand", "06-Haryana", "07-Delhi", "08-Rajasthan", "09-Uttar Pradesh", "10-Bihar",
        "11-Sikkim", "12-Arunachal Pradesh", "13-Nagaland", "14-Manipur", "15-Mizoram", "16-Tripura", "17-Meghalaya", "18-Assam", "19-West Bengal", "20-Jharkhand",
        "21-Odisha", "22-Chhattisgarh", "23-Madhya Pradesh", "24-Gujarat", "25-Daman & Diu", "26-Dadra & Nagar Haveli", "27-Maharashtra", "29-Karnataka", "30-Goa", "31-Lakshdweep",
        "32-Kerala", "33-Tamil Nadu", "34-Pondicherry", "35-Andaman & Nicobar Islands", "36-Telangana", "37-Andhra Pradesh", "97-Other Territory",
    ];

    /// <summary>HQ writes Ent_Uid 1 on accounts whoever is logged in (ClsAccounts.Insert_Accmst).</summary>
    private const int HqAccountUser = 1;
    private const string CompanyState = "37-Andhra Pradesh";
    private const string AccountIdVariable = "actId", CodeVariable = "actCode", DiffAmount = "opDiffAmount", DiffSide = "opDiffSide", Fingerprint = "fingerprint";

    public async Task<AccountRequest> LoadAsync(string firmId, long accountId, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var (a, d, opening) = await ReadAsync(s, firm, accountId);
        string T(string column) => HqForm.ReloadedTrimmed(d?[column]);
        return new AccountRequest
        {
            AccountId = accountId, Name = Convert.ToString(a["Act_Name"], CultureInfo.InvariantCulture)!, Type = HqForm.ReloadedTrimmed(a["Act_Type"]),
            Address1 = T("Add_Line1"), Address2 = T("Add_Line2"), Town = T("Act_Town"), District = T("Act_District"), Pin = T("Act_PinNo"), Phone = T("Act_Ph1"),
            Mobile = T("Act_Cell"), Email = T("Act_Email"), Person = T("person"), GstType = (int)HqForm.Number(d?["GSTTaxType"] ?? 2), Gstin = T("GSTIN").ToUpperInvariant(),
            Pan = T("Act_PAN"), State = T("Act_state"), OpeningBalance = Math.Abs(opening),
            OpeningSide = opening > 0 ? 'D' : opening < 0 ? 'C' : HqForm.ReloadedTrimmed(a["Act_OpSign"]) is { Length: > 0 } sign ? sign[0] : null,
            BillWise = HqForm.Number(a["Bill2Bill"]) == 1, Active = HqForm.Number(a["Act_Usage"]) == 0,
        };
    }

    public async Task<AccountPreview> PreviewAsync(string firmId, AccountRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var p = await PlanAsync(s, firm, request);
        return new AccountPreview(p.Plan.Hash, request.AccountId, p.Code, p.State, p.Warnings, p.Plan.Steps.Select(x => x.Label).ToList());
    }

    public async Task<AccountReceipt> SaveAsync(string firmId, AccountPost post, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<AccountReceipt>(
            firm, post.OperationId, post.Account.AccountId is null ? "account.create" : "account.update", DocumentService.RequestHash(post), [],
            async session =>
            {
                var p = await PlanAsync(session, firm, post.Account);
                Guard.Require(p.Plan.Hash == post.ExpectedHash, "REVIEW_CHANGED", "The account changed in HQ since review. Review it again.", FailureKind.Conflict);
                return new PreparedWrite<AccountReceipt>(p.Plan, outcome =>
                {
                    var id = post.Account.AccountId ?? outcome.Long(AccountIdVariable);
                    var code = post.Account.AccountId is null ? Convert.ToString(outcome.Variables[CodeVariable], CultureInfo.InvariantCulture)! : p.Code;
                    return (new AccountReceipt(post.OperationId, id, p.Name, code, post.Account.AccountId is null),
                        post.Account.AccountId is null
                            ? new WriteEvidence(WriteEvidence.AccountCreate, id, Reference: code)
                            : new WriteEvidence(WriteEvidence.AccountUpdate, id, Reference: code)
                            {
                                Fingerprint = Convert.ToString(outcome.Variables[Fingerprint], CultureInfo.InvariantCulture),
                                PreviousFingerprint = p.PreviousFingerprint,
                            });
                });
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    // ------------------------------------------------------------------ planning

    private sealed record Planned(WritePlan Plan, string Code, string State, List<string> Warnings, string? PreviousFingerprint, string Name);

    private static async Task<(IReadOnlyDictionary<string, object?> Account, IReadOnlyDictionary<string, object?>? Address, decimal Opening)> ReadAsync(HqSession s, HqFirm firm, long id)
    {
        var a = (await s.RowsAsync("SELECT * FROM dbo.IQAccmst WHERE Act_Id=@id", new { id })).FirstOrDefault()
            ?? throw HqException.NotFound("HQ_ACCOUNT_UNAVAILABLE", "That account is not in HQ");
        var d = (await s.RowsAsync("SELECT * FROM dbo.IQAccAdr WHERE Act_Id=@id", new { id })).FirstOrDefault();
        // EditAccount: the opening balance is the sum of the account's opening rows (Doc_Id -1), not the master figure.
        var opening = await s.ScalarAsync<decimal>("SELECT CAST(ISNULL(SUM(CASE WHEN TYPE IN ('D','d') THEN AMOUNT ELSE -AMOUNT END),0) AS decimal(19,2)) FROM dbo.IQDayBook WHERE Act_ID=@id AND Doc_ID=-1 AND trdate<@start",
            new { id, start = firm.PeriodStart.ToDateTime(TimeOnly.MinValue) });
        return (a, d, opening);
    }

    private static async Task<Planned> PlanAsync(HqSession s, HqFirm firm, AccountRequest r)
    {
        var config = (await s.RowsAsync("SELECT Hide_ActImagesTAB_Accounts, Bill2Bill_Comp FROM dbo.Configs")).Single();
        Guard.Supported(HqForm.Number(config["Hide_ActImagesTAB_Accounts"]) == 0, "HQ_ACCOUNT_PROFILE_UNSUPPORTED", "HQ's account settings differ from the verified ones; use HQ");
        Guard.Supported(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Branch") == 0, "HQ_ACCOUNT_PROFILE_UNSUPPORTED", "HQ branches are in use; create accounts in HQ");
        Guard.Require(r.Type is "Customer" or "Supplier", "ACCOUNT_TYPE", "An account here is a Customer or a Supplier");
        var name = HqForm.Text(r.Name, "name", 100, required: true);
        // FrmAccounts' key handler capitalises a lowercase first letter as it is typed.
        if (char.IsLower(name[0])) name = char.ToUpperInvariant(name[0]) + name[1..];
        var editing = r.AccountId is not null;
        var warnings = new List<string>();

        IReadOnlyDictionary<string, object?>? stored = null, storedAddress = null;
        decimal storedOpening = 0;
        if (editing)
        {
            (stored, storedAddress, storedOpening) = await ReadAsync(s, firm, r.AccountId!.Value);
            Guard.Supported(r.AccountId > 8, "HQ_ACCOUNT_LOCKED", "HQ's built-in accounts cannot be changed");
            var type = HqForm.ReloadedTrimmed(stored!["Act_Type"]);
            Guard.Supported(type is "Customer" or "Supplier" && HqForm.ReloadedTrimmed(stored["Act_Cat"]) == "A", "HQ_ACCOUNT_TYPE_UNSUPPORTED",
                $"{type} accounts are changed in HQ");
            Guard.Supported(HqForm.Number(stored["Act_OpSplit"]) == 0, "HQ_ACCOUNT_BILLWISE_OPENING", "This account has a bill-wise opening balance; change it in HQ");
            Guard.Supported(storedAddress is not null, "HQ_ACCOUNT_NO_ADDRESS", "This account has no address row in HQ; change it in HQ");
        }

        // GST registration (FrmAccounts.IS_Valid_GSTIN / Verify_GSTIN).
        var gstin = HqForm.Text(r.Gstin, "GSTIN", 15).Trim().ToUpperInvariant();
        Guard.Require(r.GstType is >= 0 and <= 3, "ACCOUNT_GST_TYPE", "Choose the GST registration");
        if (r.GstType is 0 or 1) Guard.Require(gstin.Length == 15, "ACCOUNT_GSTIN", "A registered party needs its 15-character GSTIN");
        else Guard.Require(gstin.Length == 0, "ACCOUNT_GSTIN", "Only a registered (or composition) party has a GSTIN");
        if (gstin.Length > 0 && !Gstin.IsValid(gstin)) warnings.Add($"{gstin} fails the official GSTIN check digit; GST returns will reject it. HQ saves it anyway.");
        string state;
        if (r.GstType is 0 or 1)
        {
            state = States.FirstOrDefault(x => x.StartsWith(gstin[..2] + "-", StringComparison.Ordinal))
                ?? throw HqException.Invalid("ACCOUNT_GSTIN", $"HQ has no state with code {gstin[..2]}");
        }
        else state = string.IsNullOrWhiteSpace(r.State) ? (editing ? HqForm.ReloadedTrimmed(storedAddress!["Act_state"]) : CompanyState) : r.State!;
        Guard.Require(States.Contains(state), "ACCOUNT_STATE", "Choose the state from HQ's list");

        var opening = r.OpeningBalance;
        Guard.Require(opening >= 0 && Money.Scale(opening) <= 2 && opening < 100_000_000, "ACCOUNT_OPENING", "Enter the opening balance in rupees and paise");
        // FrmAccounts: Customer defaults to Dr, Supplier to Cr; a zero balance on a change is saved as Dr (Amount >= 0 → Dr).
        var side = r.OpeningSide ?? (r.Type == "Customer" ? 'D' : 'C');
        if (editing && opening == 0) side = 'D';
        Guard.Require(side is 'D' or 'C', "ACCOUNT_OPENING", "Opening balance side is D or C");
        if (r.BillWise) Guard.Supported(HqForm.Number(config["Bill2Bill_Comp"]) == 1, "HQ_BILLWISE_OFF", "Bill-wise settlement is switched off in HQ");

        var groupName = r.Type == "Customer" ? "SUNDRY DEBTORS" : "SUNDRY CREDITORS";
        var group = await s.ScalarAsync<long?>("SELECT CAST(Act_Id AS bigint) FROM dbo.IQAccmst WHERE Act_Cat='G' AND Act_Name=@groupName", new { groupName })
            ?? throw HqException.Unsupported("HQ_ACCOUNT_GROUP_MISSING", $"HQ has no '{groupName}' group");
        Guard.Supported(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQAccmst WHERE Rec_higher=@group", new { group }) > 0, "HQ_ACCOUNT_GROUP_EMPTY",
            "HQ would file the account with an empty chart position; create the first one in HQ");
        long recHigher = group;
        if (editing && HqForm.ReloadedTrimmed(stored!["Act_Type"]) == r.Type) recHigher = HqForm.Id(stored["Rec_Higher"]);

        var self = r.AccountId ?? 0;
        Guard.Require(await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQAccMst WHERE Act_name=@name AND Act_Id<>@self", new { name, self }) == 0,
            "ACCOUNT_DUPLICATE_NAME", $"An account named '{name.Trim()}' already exists", FailureKind.Conflict);
        var code = editing ? HqForm.ReloadedTrimmed(stored!["Act_Code"]) : "A" + (1001 + await s.ScalarAsync<long>("SELECT ISNULL(MAX(Act_Id),0) FROM dbo.IQAccMst")).ToString(CultureInfo.InvariantCulture);

        // ---- the plan
        var steps = new List<PlanStep>();
        var idArg = r.AccountId is { } eid ? (object)eid : new Ref(AccountIdVariable);
        var fyStart = firm.PeriodStart.ToDateTime(TimeOnly.MinValue);
        if (!editing)
        {
            steps.Add(new SqlStep("Account code (A + 1001 + highest id)", "SELECT 'A'+CAST(1001+ISNULL(MAX(Act_Id),0) AS varchar(12)) FROM dbo.IQAccMst", new Args(), CodeVariable));
            steps.Add(new SqlStep("HQ duplicate check (name, code)", """
                IF EXISTS (SELECT 1 FROM dbo.IQAccMst WITH (UPDLOCK, HOLDLOCK) WHERE Act_name=@name) RAISERROR('MK:ACCOUNT_DUPLICATE_NAME|An account with this name was just created', 16, 1);
                IF EXISTS (SELECT 1 FROM dbo.IQAccMst WITH (UPDLOCK, HOLDLOCK) WHERE Act_Code=@code) RAISERROR('MK:ACCOUNT_DUPLICATE_CODE|This account code is already used', 16, 1);
                """, new Args().Set("name", name).Set("code", new Ref(CodeVariable))));
        }
        else
            steps.Add(new SqlStep("HQ duplicate check (name)", """
                IF EXISTS (SELECT 1 FROM dbo.IQAccMst WITH (UPDLOCK, HOLDLOCK) WHERE Act_name=@name AND Act_Id<>@self) RAISERROR('MK:ACCOUNT_DUPLICATE_NAME|An account with this name was just created', 16, 1);
                """, new Args().Set("name", name).Set("self", self)));

        decimal Stored(string column) => HqForm.Number(stored![column]);
        var master = new Args()
            .Set("Act_id", r.AccountId ?? 0).Set("act_Name", name).Set("Act_type", r.Type).Set("Rec_higher", recHigher)
            .Set("Act_Code", editing ? code : new Ref(CodeVariable)).Set("Act_Opbal", opening).Set("Act_opSign", side.ToString())
            .Set("Act_Cat", "A").Set("Act_TT", r.Type).Set("Act_CA", 0).Set("Act_OpSplit", 0)
            .Set("Act_St", r.BillWise ? 1 : 0).Set("mBill2Bill", r.BillWise ? 1 : 0)
            .Set("Ent_Date", new Clock(ClockField.CompactDay)).Set("Ent_UId", HqAccountUser).Set("Act_usage", r.Active ? 0 : 1)
            .Set("Act_BillMode", editing ? Stored("Act_BillMode") : 1).Set("Act_ControlID", editing ? Stored("Act_ControlID") : 0)
            .Set("mCust_Type", editing ? Stored("Cust_Type") : 0).Set("mSalesmanID", editing ? Stored("SalesmanID") : 0)
            .Set("mIsStaff", editing ? Stored("IsStaff") : 0).Set("mShift_ID", editing ? Stored("Shift_ID") : 0)
            .Set("mActTaxType", editing ? Stored("ActTaxType") : 2).Set("mIsTCSAPPLY", editing ? Stored("IsTCSAPPLY") : 0)
            .Set("mTCSApplyAmt", editing ? Stored("TCSApplyAmt") : 0m);
        steps.Add(editing
            ? new CallStep("Account master (update)", "IQaccmst_update", master)
            : new CallStep("Account master (files it in the chart of accounts)", "IQaccmst_insert", master, new Dictionary<string, string> { ["Act_id"] = AccountIdVariable }));

        // ---- address: rebuilt from the form (HQ reloads every box with .Trim(); blanks are " " or "" per box).
        object? A(string column) => storedAddress?[column];
        string Box(string? typed, string column) => editing && typed is null ? HqForm.Reloaded(A(column)) : HqForm.Spaced(HqForm.Text(typed, column, 100));
        string TrimBox(string? typed, string column) => editing && typed is null ? HqForm.ReloadedTrimmed(A(column)) : HqForm.Trimmed(HqForm.Text(typed, column, 100));
        var billMode = editing ? (int)Stored("Act_BillMode") : 1;
        var adr = new Args()
            .Set("Act_Id", idArg).Set("person", Box(r.Person, "person")).Set("Add_Line1", Box(r.Address1, "Add_Line1")).Set("Add_Line2", Box(r.Address2, "Add_Line2"))
            .Set("Act_Town", Box(r.Town, "Act_Town")).Set("Act_District", Box(r.District, "Act_District")).Set("Act_state", state)
            .Set("Act_PinNo", Box(r.Pin, "Act_PinNo")).Set("Act_Ph1", Box(r.Phone, "Act_Ph1")).Set("Act_Cell", Box(r.Mobile, "Act_Cell")).Set("Act_Email", Box(r.Email, "Act_Email"))
            .Set("Act_PAN", TrimBox(r.Pan, "Act_PAN")).Set("mGSTIN", gstin).Set("mGSTTaxType", r.GstType)
            .Set("Act_Mode", billMode != 0).Set("Act_Photo", "").Set("mAct_FoodLICExpDate", new Clock(ClockField.Day));
        foreach (var column in new[] { "Act_Ph2", "Act_STD", "Act_CSTNo", "Act_SSTNo", "Act_Transport", "Act_VAT_TINNO", "Act_DL20b", "Act_DL21B",
                     "ship_Add_Line1", "ship_Add_Line2", "ship_Act_Town", "ship_Act_District", "ship_Act_PinNo", "ship_Act_Ph1", "ship_Act_Ph2", "ship_Act_STD",
                     "ship_Act_Cell", "ship_Act_Email", "ship_Act_GSIN", "ship_Act_PANNO" })
            adr.Set(column.StartsWith("ship_", StringComparison.Ordinal) ? "m" + column : column, editing ? HqForm.Reloaded(A(column)) : " ");
        foreach (var column in new[] { "Act_TAN", "ship_Person", "AadharNo", "ActFoodLICNO", "Act_Note1", "Act_Note2", "Act_Note3", "Act_Note4", "Act_Note5", "Act_Note6",
                     "Act_Image1", "Act_Image2", "Act_Image3", "Act_Image4" })
            adr.Set(column.StartsWith("Act_T", StringComparison.Ordinal) ? column : "m" + column, editing ? HqForm.ReloadedTrimmed(A(column)) : "");
        adr.Set("mship_Act_state", editing ? HqForm.ReloadedTrimmed(A("ship_Act_state")) is { Length: > 0 } ship ? ship : CompanyState : CompanyState)
            .Set("Act_Credit", editing ? (double)HqForm.Number(A("Act_Credit")) : 0d)
            .Set("Act_CrDays", editing && int.TryParse(Convert.ToString(A("Act_CrDays"), CultureInfo.InvariantCulture)?.Trim(), out var days) ? days : 0)
            .Set("Area_Id", editing ? HqForm.Number(A("Area_Id")) : 0).Set("Act_Rate", editing ? HqForm.Number(A("Act_Rate")) : 1m)
            .Set("mAct_CrCtrlDays", editing ? HqForm.Number(A("Act_CrCtrlDays")) : 0);
        steps.Add(new SqlStep("Replace address", "DELETE FROM dbo.IQAccadr WHERE Act_Id=@id", new Args().Set("id", idArg)));
        steps.Add(new CallStep("Address, GST and state", "IQAccAdr_insert", adr));

        // ---- the contact grid: always ten rows (blank ones included), rewritten on every save.
        var contacts = editing
            ? (await s.QueryAsync<(int Ln, string? Degn, string? Name, string? Phone, string? Email)>("SELECT CAST(LnNo AS int), ActDegn, ActName, ActPhone, ActEmailID FROM dbo.IQAccOthers WHERE actID=@id", new { id = r.AccountId }))
                .ToDictionary(c => c.Ln)
            : [];
        steps.Add(new SqlStep("Replace contacts", "DELETE FROM dbo.IQAccOthers WHERE actID=@id", new Args().Set("id", idArg)));
        for (var ln = 0; ln < 10; ln++)
        {
            var c = contacts.GetValueOrDefault(ln);
            steps.Add(new CallStep($"Contact row {ln}", "IQAccOthers_insert", new Args().Set("mActID", idArg).Set("mLnNo", ln)
                .Set("mActDegn", HqForm.Trimmed(c.Degn)).Set("mActName", HqForm.Trimmed(c.Name)).Set("mActPhone", HqForm.Trimmed(c.Phone)).Set("mActEmailID", HqForm.Trimmed(c.Email))));
        }

        // ---- opening balance row (Doc_ID -1, dated the day before the year), written even when zero.
        var openingRows = editing
            ? await s.QueryAsync<(long Sr, decimal Allocated, int Links)>("""
                SELECT CAST(d.SR_NO AS bigint), CAST(ISNULL(d.BL_adjusted,0) AS decimal(19,2)), (SELECT COUNT(*) FROM dbo.ADJUST a WHERE d.SR_NO IN (a.Sr_No, a.Rel_Sr_No))
                FROM dbo.IQDayBook d WHERE d.Act_ID=@id AND d.trdate<@start AND ISNULL(d.Div_no,0)=0
                """, new { id = r.AccountId, start = fyStart })
            : [];
        var settled = openingRows.Where(o => o.Links > 0).ToList();
        if (settled.Count == 0)
        {
            steps.Add(new SqlStep("Replace opening row", "DELETE FROM dbo.IQDayBook WHERE Act_ID=@id AND Div_no=0 AND trdate<@start", new Args().Set("id", idArg).Set("start", fyStart)));
            steps.Add(new CallStep("Opening balance row", "IQdaybook_insert", OpeningRow(idArg, opening, side, "opbal", fyStart)));
        }
        else
        {
            // HQ would delete and re-insert this row, leaving the payments settled against it pointing at nothing. MK keeps the
            // row (and its settlements) and changes it in place instead.
            Guard.Supported(settled.Count == 1 && openingRows.Count == 1, "HQ_ACCOUNT_OPENING_SETTLED", "This account's opening balance is split and settled; change it in HQ");
            Guard.Require(opening >= settled[0].Allocated, "HQ_ACCOUNT_OPENING_SETTLED",
                $"{Money.Text(settled[0].Allocated)} of the opening balance is already settled by payments; it cannot go below that");
            if (opening != Math.Abs(storedOpening) || side != (storedOpening >= 0 ? 'D' : 'C'))
                warnings.Add("Payments are settled against this opening balance; MK keeps them attached (HQ itself would detach them).");
            steps.Add(new SqlStep("Opening balance row (kept, settlements stay attached)", "UPDATE dbo.IQDayBook SET AMOUNT=@amount, TYPE=@side WHERE SR_NO=@sr",
                new Args().Set("amount", opening).Set("side", side.ToString()).Set("sr", settled[0].Sr)));
        }

        // ---- UpdateOpeningBalanceDifferenceAccount: account 5 balances all opening rows, rewritten on every account save.
        const string net = "(SELECT ISNULL(SUM(CASE WHEN d.TYPE IN ('D','d') THEN d.AMOUNT ELSE (-1)*d.AMOUNT END),0) FROM dbo.IQDayBook d INNER JOIN dbo.IQAccMst a ON d.Act_ID=a.Act_ID WHERE d.TYPE IN ('D','d','C','c') AND a.Act_ID<>5 AND d.Doc_ID=-1)";
        steps.Add(new SqlStep("Opening difference amount", $"SELECT CAST(ABS({net}) AS decimal(18,2))", new Args(), DiffAmount));
        steps.Add(new SqlStep("Opening difference side", $"SELECT CASE WHEN {net} > 0 THEN 'C' ELSE 'D' END", new Args(), DiffSide));
        steps.Add(new SqlStep("Opening difference account", "UPDATE dbo.IQAccMst SET Act_Opbal=@amount, Act_opSign=@side WHERE Act_Id=5",
            new Args().Set("amount", new Ref(DiffAmount)).Set("side", new Ref(DiffSide))));
        steps.Add(new SqlStep("Replace opening difference row", "DELETE FROM dbo.IQDayBook WHERE Act_Id=5", new Args()));
        steps.Add(new CallStep("Opening difference row", "IQdaybook_insert", OpeningRow(5L, new Ref(DiffAmount), new Ref(DiffSide), "opDif", fyStart)));

        steps.Add(new VerifyStep("Verify account saved", "SELECT COUNT(*) FROM dbo.IQAccmst a JOIN dbo.IQAccAdr d ON d.Act_Id=a.Act_Id WHERE a.Act_Id=@id AND a.Act_Name=@name",
            new Args().Set("id", idArg).Set("name", name), 1, "HQ_SAVE_MISMATCH", "HQ did not store the account as entered"));
        steps.Add(new VerifyStep("Verify ten contact rows", "SELECT COUNT(*) FROM dbo.IQAccOthers WHERE actID=@id", new Args().Set("id", idArg), 10, "HQ_SAVE_MISMATCH", "HQ stored a different contact grid"));
        steps.Add(new SqlStep("Fingerprint", WriteEvidence.AccountFingerprintSql, new Args().Set("id", idArg), Fingerprint));

        var previous = editing ? await s.ScalarAsync<string?>(WriteEvidence.AccountFingerprintSql, new { id = r.AccountId }) : null;
        return new Planned(new WritePlan(editing ? $"Change account {name.Trim()}" : $"New {r.Type.ToLowerInvariant()} {name.Trim()}", steps), code, state, warnings, previous, name.Trim());
    }

    private static Args OpeningRow(object accountId, object amount, object side, string vtype, DateTime fyStart) => new Args()
        .Set("trdate", fyStart.AddDays(-1)).Set("Act_id", accountId).Set("Amount", amount).Set("type", side is char c ? c.ToString() : side).Set("Contra_id", 0)
        .Zero("BL_Adjusted Adjusted ARS ARS_Ord Div_no days_from days_to int_credit Tno discount doc_type Bill_Qty adj_id lnno mImportCollection_DtldID")
        .Set("Doc_ID", -1).Set("VType", vtype).Set("Tseries", "").Set("Textn", "").Set("Narration", "").Set("ent_Uid", HqAccountUser).Set("mDiscRow", "");
}
