using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Masters;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;
using MK.Hq.Engine.Vouchers;
using MK.Hq.Tests.Support;

namespace MK.Hq.Tests.Documents;

/// <summary>Database-free checks of the bill, voucher and master rules: everything here runs without SQL Server.</summary>
public sealed class DocumentsRulesTests
{
    // ------------------------------------------------------------------ fixtures

    private static readonly HqOptions Options = new()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "mkhq-docs-tests"),
        Firms = [new FirmOptions { Id = "mbm", Name = "MBM", Database = "MKHQ_NONE_MBM", PeriodStart = new DateOnly(2026, 4, 1), PeriodEnd = new DateOnly(2027, 3, 31) }],
    };

    private static readonly HqFirm Firm = new(Options.Firms[0], Options, new ProcedureCatalog());

    private static TransactionType Type(int tt, string vouch, string name = "GST Sales", string series = "CS") =>
        new(tt, name, vouch, series, SeriesType: 3, Firstor: tt is 501 or 507 ? 0 : 1, IncTax: 0, RoundedOffType: 0, RoundedOffTo: 0, ChargeSet: 0,
            StockPointId: 1, DefaultDays: 0, BranchDefault: 0, Trmode: 0, SubType: 0, Status: 0, Message: "", RestartDaily: 0);

    private static FirmProfile Profile(int skipAudit = 0) => new()
    {
        Firm = Firm,
        Config = new HqConfig(skipAudit, 0, 1, 0, 0, 0, 0, 0, 0),
        Types = new[] { Type(506, "Sales"), Type(500, "Purchase", "GST Purchase", "PU"), Type(249, "Sales Order", "Estimate", "SO") }.ToDictionary(t => t.TtId),
        ChargeSets = new Dictionary<int, IReadOnlyList<ChargeRow>>(),
        PaymentModes = [new PaymentMode(0, "Cash", "Cash", 0, 0), new PaymentMode(1, "Credit", "Credit", 1, 0)],
        Salespeople = [],
        StateCaption = "37-Andhra Pradesh",
        LoadedAt = DateTime.UtcNow,
    };

    private static DocumentRequest Request(DocumentKind kind = DocumentKind.Sale, int mode = 1) => new()
    {
        Kind = kind, Date = new DateOnly(2026, 11, 18), PaymentMode = mode, AccountId = 11155,
        Lines = [new LineRequest(100, 3, 2m, 50m)],
    };

    /// <summary>A resolved credit sale built by hand, as the resolver would return it for one 18% line.</summary>
    private static ResolvedDocument Resolved(DocumentRequest? request = null, FirmProfile? profile = null)
    {
        var r = request ?? Request();
        var type = (profile ?? Profile()).Type(r.Kind.TtId(false));
        var item = new CatalogueItem(100, 3, "P1", "PVC PIPE", "PCS", "3917", 50m, 40m, 60m, 9m, 9m, 18m, true, 10m, true, "G", "");
        var mapping = new ItemMapping(item, 3, 1, 1m, 1, 60m, 10364, 10341, 10343, 0, 0, 0, 40m, 45m, 50m, 0m, 18m, "", 0, 18m, 2500m);
        var quote = GstCalculator.Price(r.Lines.Select(l => new LineInput(l.Quantity, l.Rate, l.DiscountPercent, item.Taxes)).ToList(),
            type.Policy(), TaxMode.Local, 4, 4);
        var ledger = r.Kind.IsOrder() ? [] : InvoiceLedger.Plan(r.Kind.IsInward(), Settlement.Credit, 11155,
            [(10364, quote.Lines[0].Taxable), (10341, quote.Lines[0].Cgst), (10343, quote.Lines[0].Sgst)], [], quote.Payable);
        var address = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Act_Town"] = "RAJAHMUNDRY", ["Act_state"] = "37-Andhra Pradesh" };
        return new ResolvedDocument(profile ?? Profile(), r, type, Settlement.Credit, 1,
            new PartyAccount(11155, "UPRIGHT TRADERS", "Customer", "A12156", false, 0, 0, 0), 11155, address,
            new PreviewParty(11155, 11155, "UPRIGHT TRADERS", "RAJAHMUNDRY", "", "", 2, "37-Andhra Pradesh", ""), "RAJAHMUNDRY",
            [mapping], quote, ledger, [], null, null, r.Date, "", [], []);
    }

    private static async Task<HqException> Refused(DocumentRequest r) =>
        // Every check here runs before the resolver touches SQL Server, so no session is needed.
        await Assert.ThrowsAsync<HqException>(() => new DocumentResolver(Options).ResolveAsync(null!, Profile(), r));

    // ------------------------------------------------------------------ bill number suffix and typed fields

    [Fact]
    public async Task A_suffix_longer_than_HQs_one_character_is_refused_at_review()
    {
        // IQDoc_Header.TExtn is nvarchar(1): "AB" used to pass review and fail inside the save with a truncation error.
        Assert.Equal("INVALID_SUFFIX", (await Refused(Request() with { NumberSuffix = "AB" })).Code);
        Assert.Equal("INVALID_SUFFIX", (await Refused(Request() with { NumberSuffix = "ABC" })).Code);
    }

    [Fact]
    public void Suffix_is_saved_in_capitals_like_HQs_box()
    {
        var d = Resolved(Request() with { NumberSuffix = " a " });
        Assert.Equal("A", d.Suffix);
        var plan = DocumentPlanBuilder.Create(d, 1, "MK");
        Assert.Equal("A", plan.Steps.OfType<AllocateNumberStep>().Single().Extension);
        Assert.Equal("A", plan.Steps.OfType<CallStep>().Single(s => s.Procedure == "IQDoc_Header_Insert").Args["mTExtn"]);
        Assert.Equal("A", plan.Steps.OfType<AssertNumberFreeStep>().Single().Extension);
    }

    [Fact]
    public async Task A_message_HQs_procedure_would_cut_is_refused_at_review()
    {
        Assert.Equal("MESSAGE_TOO_LONG", (await Refused(Request() with { Message = new string('x', 256) })).Code);
    }

    [Theory]
    [InlineData("name", 101)]
    [InlineData("town", 51)]
    [InlineData("phone", 51)]
    [InlineData("mobile", 51)]
    [InlineData("address", 256)]
    [InlineData("gstin", 16)]
    public async Task Typed_party_text_longer_than_the_bill_snapshot_holds_is_refused_at_review(string field, int length)
    {
        var text = new string('A', length);
        var party = field switch
        {
            "name" => new PartySnapshot(Name: text),
            "town" => new PartySnapshot(Name: "RAVI", Town: text),
            "phone" => new PartySnapshot(Name: "RAVI", Phone: text),
            "mobile" => new PartySnapshot(Name: "RAVI", Mobile: text),
            "address" => new PartySnapshot(Name: "RAVI", Address: text),
            _ => new PartySnapshot(Name: "RAVI", Gstin: text),
        };
        Assert.Equal("PARTY_FIELD_TOO_LONG", (await Refused(Request(mode: 0) with { AccountId = null, Party = party })).Code);
    }

    [Fact]
    public void Typed_party_text_at_the_limits_passes()
    {
        DocumentResolver.RequireTypedParty(new PartySnapshot(new string('A', 100), new string('B', 50), new string('1', 50), new string('2', 50),
            " 37AAACK8470B1ZP ", 0, "37-Andhra Pradesh", new string('C', 255)));
        DocumentResolver.RequireTypedParty(null);
    }

    [Fact]
    public async Task Dates_outside_the_financial_year_are_refused()
    {
        Assert.Equal("DATE_OUTSIDE_PERIOD", (await Refused(Request() with { Date = new DateOnly(2026, 3, 31) })).Code);
        Assert.Equal("DATE_OUTSIDE_PERIOD", (await Refused(Request() with { Date = new DateOnly(2027, 4, 1) })).Code);
    }

    // ------------------------------------------------------------------ party state

    [Theory]
    [InlineData("Jammu and Kashmir", "01-Jammu & Kashmir")]
    [InlineData("jammu & kashmir", "01-Jammu & Kashmir")]
    [InlineData("Puducherry", "34-Pondicherry")]
    [InlineData("Pondicherry", "34-Pondicherry")]
    [InlineData("Lakshadweep", "31-Lakshdweep")]
    [InlineData("Andaman and Nicobar Islands", "35-Andaman & Nicobar Islands")]
    [InlineData("Andaman & Nicobar", "35-Andaman & Nicobar Islands")]
    [InlineData("Dadra and Nagar Haveli and Daman and Diu", "26-Dadra & Nagar Haveli")]
    [InlineData("Daman & Diu", "25-Daman & Diu")]
    [InlineData("Telengana", "36-Telangana")]
    [InlineData(" telangana ", "36-Telangana")]
    [InlineData("ANDHRA PRADESH", "37-Andhra Pradesh")]
    [InlineData("Orissa", "21-Odisha")]
    [InlineData("33", "33-Tamil Nadu")]
    [InlineData("37-AP", "37-Andhra Pradesh")]
    [InlineData("Atlantis", "Atlantis")]
    public void A_typed_state_becomes_HQs_caption(string typed, string caption) =>
        Assert.Equal(caption, DocumentResolver.NormaliseState(typed));

    [Fact]
    public void Account_screen_states_are_HQs_drop_down_exactly()
    {
        // FrmAccounts / FrmInwards' list, copied from the decompiled forms; the account screen now derives it from Gstin.States.
        string[] hq =
        [
            "01-Jammu & Kashmir", "02-Himachal Pradesh", "03-Punjab", "04-Chandigarh", "05-Uttarakhand", "06-Haryana", "07-Delhi", "08-Rajasthan", "09-Uttar Pradesh", "10-Bihar",
            "11-Sikkim", "12-Arunachal Pradesh", "13-Nagaland", "14-Manipur", "15-Mizoram", "16-Tripura", "17-Meghalaya", "18-Assam", "19-West Bengal", "20-Jharkhand",
            "21-Odisha", "22-Chhattisgarh", "23-Madhya Pradesh", "24-Gujarat", "25-Daman & Diu", "26-Dadra & Nagar Haveli", "27-Maharashtra", "29-Karnataka", "30-Goa", "31-Lakshdweep",
            "32-Kerala", "33-Tamil Nadu", "34-Pondicherry", "35-Andaman & Nicobar Islands", "36-Telangana", "37-Andhra Pradesh", "97-Other Territory",
        ];
        Assert.Equal(hq, AccountMasterService.States);
        Assert.All(Gstin.States, s => Assert.Equal(Gstin.HqStateCaption(s.Key), DocumentResolver.NormaliseState(s.Value)));
    }

    // ------------------------------------------------------------------ plan builder

    [Fact]
    public void Supplier_bill_braces_in_the_narration_are_not_plan_variables()
    {
        var d = Resolved(Request(DocumentKind.Purchase) with { Supplier = new SupplierBill(" INV{12} ", new DateOnly(2026, 11, 2)) });
        var party = Assert.IsType<Interp>(DocumentPlanBuilder.Narration(d, new Ref("number"), partyRow: true));
        Assert.Equal("GST Purchase Supp. Bill No. INV(12) Dt. 02/11/2026", party.Template);
        var other = Assert.IsType<Interp>(DocumentPlanBuilder.Narration(d, new Ref("number"), partyRow: false));
        Assert.Equal("GST Purchase From UPRIGHT TRADERS, RAJAHMUNDRY Supp. Bill No. INV(12) Dt. 02/11/2026", other.Template);
    }

    [Fact]
    public void Sale_narration_carries_the_bill_number_variable_and_typed_notes_win()
    {
        var d = Resolved();
        Assert.Equal("GST Sales Bill No CS/{number}", Assert.IsType<Interp>(DocumentPlanBuilder.Narration(d, new Ref("number"), true)).Template);
        Assert.Equal("GST Sales To UPRIGHT TRADERS, RAJAHMUNDRY Bill No CS/7", Assert.IsType<Interp>(DocumentPlanBuilder.Narration(d, 7, false)).Template);
        // Typed notes are plain text: a brace in them is stored as typed, never read as a variable.
        Assert.Equal("paid {cash}", DocumentPlanBuilder.Narration(Resolved(Request() with { Notes = "  paid {cash} " }), new Ref("number"), true));
    }

    [Fact]
    public void Invoice_plan_verifies_ledger_and_party_and_an_order_plan_does_not_post_a_ledger()
    {
        var sale = DocumentPlanBuilder.Create(Resolved(), 1, "MK");
        Assert.Contains(sale.Steps, s => s is CallStep { Procedure: "IQdaybook_insert" });
        Assert.Equal(["Verify bill amount", "Verify lines", "Verify line total", "Verify ledger rows", "Verify ledger total", "Verify party name kept"],
            sale.Steps.OfType<VerifyStep>().Select(v => v.Label));
        var ledgerTotal = sale.Steps.OfType<VerifyStep>().Single(v => v.Label == "Verify ledger total");
        Assert.Equal(0m, ledgerTotal.Expected);

        var order = DocumentPlanBuilder.Create(Resolved(Request(DocumentKind.SalesOrder)), 1, "MK");
        Assert.DoesNotContain(order.Steps, s => s is CallStep { Procedure: "IQdaybook_insert" or "UpdateClosing_Stock_B+NG" });
        Assert.Equal(["Verify bill amount", "Verify lines", "Verify line total", "Verify party name kept"], order.Steps.OfType<VerifyStep>().Select(v => v.Label));
    }

    [Fact]
    public void Audit_copies_follow_HQs_setting()
    {
        Assert.Contains(DocumentPlanBuilder.Create(Resolved(), 1, "MK").Steps, s => s.Label.StartsWith("Audit:", StringComparison.Ordinal));
        Assert.DoesNotContain(DocumentPlanBuilder.Create(Resolved(profile: Profile(skipAudit: 1)), 1, "MK").Steps, s => s.Label.StartsWith("Audit:", StringComparison.Ordinal));
    }

    [Fact]
    public void Bill_quantity_in_the_ledger_is_bankers_rounded_like_HQ()
    {
        var d = Resolved(Request() with { Lines = [new LineRequest(100, 3, 76.5m, 10m)] });
        var rows = DocumentPlanBuilder.LedgerSteps(d, new Ref("docId"), new Ref("number"), 1).Cast<CallStep>().ToList();
        Assert.All(rows, r => Assert.Equal(76m, r.Args["Bill_Qty"]));
    }

    [Fact]
    public void Sale_ship_to_state_falls_back_to_the_firms_state()
    {
        var header = DocumentPlanBuilder.Header(Resolved(), 1, 1, "MK", 2);
        Assert.Equal("37-Andhra Pradesh", header["mShip_state"]);
        var purchase = DocumentPlanBuilder.Header(Resolved(Request(DocumentKind.Purchase) with { Supplier = new SupplierBill("9", new DateOnly(2026, 11, 1)) }), 1, 1, "MK", 2);
        Assert.Equal("", purchase["mShip_state"]);
    }

    // ------------------------------------------------------------------ masters: HqForm and the account boxes

    [Fact]
    public void HqForm_text_rules()
    {
        Assert.Equal("   ", HqForm.Text("   ", "town", 25)); // a box left as spaces is blank, not "starts with a space"
        Assert.Equal(" ", HqForm.Spaced(HqForm.Text("   ", "town", 25)));
        Assert.Equal("", HqForm.Trimmed(HqForm.Text(null, "PAN", 25)));
        Assert.Equal("MASTER_FIELD_CHARACTERS", Assert.Throws<HqException>(() => HqForm.Text(" RAVI", "name", 100)).Code);
        Assert.Equal("MASTER_FIELD_REQUIRED", Assert.Throws<HqException>(() => HqForm.Text("   ", "name", 100, required: true)).Code);
        Assert.Equal("MASTER_FIELD_TOO_LONG", Assert.Throws<HqException>(() => HqForm.Text(new string('A', 71), "item name", 70)).Code);
        Assert.Equal("MASTER_FIELD_CHARACTERS", Assert.Throws<HqException>(() => HqForm.Text("O'NEIL", "name", 100)).Code);
        Assert.Equal("1/2'' PIPE", HqForm.Text("1/2'' PIPE", "item name", 70, apostrophe: true));
        Assert.Equal("MASTER_FIELD_CHARACTERS", Assert.Throws<HqException>(() => HqForm.Text("రవి", "name", 100)).Code);
        Assert.Equal(349.57m, HqForm.FloatToNumeric(349.575m, 2));
    }

    [Theory]
    [InlineData("Act_Town", 25)]
    [InlineData("Act_District", 25)]
    [InlineData("Add_Line1", 60)]
    [InlineData("Add_Line2", 60)]
    [InlineData("Act_Cell", 12)]
    [InlineData("Act_Ph1", 35)]
    [InlineData("Act_PinNo", 10)]
    [InlineData("person", 35)]
    [InlineData("Act_PAN", 25)]
    [InlineData("Act_Email", 100)]
    public void Account_address_boxes_take_what_HQs_form_takes(string column, int max)
    {
        Assert.Equal(max, AccountMasterService.AddressBox(new string('A', max), column).Length);
        // A 26-character town used to be saved, and every later bill for the party then failed (IQCash_TRAN holds 50).
        Assert.Equal("MASTER_FIELD_TOO_LONG", Assert.Throws<HqException>(() => AccountMasterService.AddressBox(new string('A', max + 1), column)).Code);
    }

    // ------------------------------------------------------------------ vouchers

    private static VoucherRequest Voucher(BankDetails? bank = null, IReadOnlyList<AllocationRequest>? allocations = null, IReadOnlyList<AllocationRequest>? header = null) =>
        new(VoucherKind.BankReceipt, new DateOnly(2026, 11, 18), 10701, [new VoucherLineRequest(11178, 1000m, null, bank ?? new BankDetails(), allocations)], null, header);

    [Fact]
    public void Voucher_bank_details_must_fit_HQs_bank_register()
    {
        VoucherService.RequireWellFormed(Voucher(new BankDetails(0, new string('1', 50), null, new string('B', 50), new string('P', 50))));
        Assert.Equal("BANK_DETAILS", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(new BankDetails(2, new string('1', 51))))).Code);
        Assert.Equal("BANK_DETAILS", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(new BankDetails(2, Bank: new string('B', 51))))).Code);
        Assert.Equal("BANK_DETAILS", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(new BankDetails(3)))).Code);
    }

    [Fact]
    public void Voucher_settlements_are_in_whole_paise()
    {
        VoucherService.RequireWellFormed(Voucher(allocations: [new AllocationRequest(1, 10.05m)]));
        Assert.Equal("INVALID_AMOUNT", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(allocations: [new AllocationRequest(1, 10.005m)]))).Code);
        Assert.Equal("INVALID_AMOUNT", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(allocations: [new AllocationRequest(1, 0m)]))).Code);
        Assert.Equal("INVALID_AMOUNT", Assert.Throws<HqException>(() => VoucherService.RequireWellFormed(Voucher(header: [new AllocationRequest(1, -5m)]))).Code);
    }
}

/// <summary>The item clean-up's cleanup.db side (import, answers, what waits to be written) without HQ: HQ's group list is
/// passed in, and nothing here applies to HQ.</summary>
public sealed class DocumentsCleanupTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mkhq-docs-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ServiceProvider _engine;
    private static readonly Dictionary<string, long> Groups = new() { ["PIPES"] = 5, ["PAINTS"] = 7 };

    public DocumentsCleanupTests() => _engine = ScratchDatabase.Engine("mbm", "MKHQ_NONE_MBM", _dir);

    public void Dispose()
    {
        _engine.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private ItemCleanupService Cleanup => _engine.GetRequiredService<ItemCleanupService>();

    private static CleanupProposal Item(long id, string? proposed = null, string? check = "Review", Dictionary<string, string>? answers = null) => new()
    {
        Id = id, Code = "C" + id, Group = "PIPES", GroupId = 5, Current = "ITEM " + id, Proposed = proposed ?? "ITEM " + id + " NEW", Check = check,
        Changes = ["order"], Hsn = "3917", Gst = 18m, Answers = answers,
    };

    private static CleanupFile File(params CleanupProposal[] items) => new("mbm", "ISTMBM26", "2026-11-18T10:00:00", new Dictionary<string, string>(), items);

    [Fact]
    public void Items_missing_from_a_fresh_list_are_withdrawn_and_never_written()
    {
        Cleanup.Import("mbm", File(Item(1), Item(2), Item(3)), Groups);
        Cleanup.Decide("mbm", [(1, new CleanupDecision { Name = "ONE" }), (2, new CleanupDecision { Name = "TWO" }), (3, new CleanupDecision { Retire = false })]);
        Assert.Equal(2, Cleanup.Summary("mbm").ReadyToApply);

        // Item 2 was fixed in HQ meanwhile: the fresh list leaves it out. Its old answer must not be written.
        var summary = Cleanup.Import("mbm", File(Item(1), Item(3)), Groups);
        Assert.Equal(1, summary.ReadyToApply);
        Assert.Equal(2, summary.Items);
        Assert.Equal([1L], Cleanup.List("mbm", "pending").Select(i => i.Proposal.Id));
        Assert.DoesNotContain(Cleanup.List("mbm", "review"), i => i.Proposal.Id == 2);
        Assert.Equal("CLEANUP_WITHDRAWN", Assert.Throws<HqException>(() => Cleanup.Decide("mbm", [(2, new CleanupDecision { Name = "AGAIN" })])).Code);

        // Back in a later list, it is open again (its old answer kept, as the proposal is the same).
        Cleanup.Import("mbm", File(Item(1), Item(2), Item(3)), Groups);
        var back = Cleanup.List("mbm", "pending").Single(i => i.Proposal.Id == 2);
        Assert.Equal("TWO", back.Pending!.Name);
    }

    [Fact]
    public void Excel_answers_override_screen_answers_field_by_field()
    {
        Cleanup.Import("mbm", File(Item(1)), Groups);
        Cleanup.Decide("mbm", [(1, new CleanupDecision { Name = "MY WORDING", Retire = true, Hsn = "39172390" })]);

        // The sheet only says to move the group (and leaves Approve blank): his on-screen name, retire and HSN stay.
        Cleanup.Import("mbm", File(Item(1, answers: new() { ["approve"] = " ", ["moveToGroup"] = "paints" })), Groups);
        var d = Cleanup.List("mbm", "review").Single().Decision!;
        Assert.Equal(("MY WORDING", true, 7L, "39172390"), (d.Name, d.Retire, d.GroupId, d.Hsn));

        // A sheet "N" on the name does replace the on-screen name.
        Cleanup.Import("mbm", File(Item(1, answers: new() { ["approve"] = "n" })), Groups);
        d = Cleanup.List("mbm", "review").Single().Decision!;
        Assert.Equal((null, true, true), (d.Name, d.KeepName, d.Retire));
    }

    [Fact]
    public void Excel_answers_MK_cannot_use_stop_the_import_naming_the_item()
    {
        foreach (var (key, value) in new[] { ("setHsn", "39172"), ("setGst", "7"), ("moveToGroup", "PIPEZ"), ("approve", "OK"), ("ownName", new string('A', 71)) })
        {
            var e = Assert.Throws<HqException>(() => Cleanup.Import("mbm", File(Item(1, answers: new() { [key] = value })), Groups));
            Assert.Contains("C1", e.Message);
        }
        Assert.Equal(18m, ItemCleanupService.FromExcel(Item(1, answers: new() { ["setGst"] = "18.0%" }), Groups)!.Gst);
        Assert.Equal("39172390", ItemCleanupService.FromExcel(Item(1, answers: new() { ["setHsn"] = "39172390" }), Groups)!.Hsn);
        Assert.Null(ItemCleanupService.FromExcel(Item(1, answers: new() { ["approve"] = "", ["ownName"] = "  " }), Groups));
    }

    [Fact]
    public void Screen_answers_are_checked_by_the_item_saves_rules()
    {
        Cleanup.Import("mbm", File(Item(1)), Groups);
        Assert.Equal("MASTER_FIELD_CHARACTERS", Assert.Throws<HqException>(() => Cleanup.Decide("mbm", [(1, new CleanupDecision { Name = "పైపు" })])).Code);
        Assert.Equal("MASTER_FIELD_TOO_LONG", Assert.Throws<HqException>(() => Cleanup.Decide("mbm", [(1, new CleanupDecision { Name = new string('A', 71) })])).Code);
        Cleanup.Decide("mbm", [(1, new CleanupDecision { Name = "  pvc pipe 1/2'' " })]);
        Assert.Equal("PVC PIPE 1/2''", Cleanup.List("mbm", "review").Single().Decision!.Name);
    }

    [Fact]
    public void Merge_keeps_screen_answers_the_sheet_leaves_blank()
    {
        var screen = new CleanupDecision { Name = "A", GroupLeave = true, DataLeave = true, Retire = false };
        Assert.Same(screen, ItemCleanupService.Merge(screen, null));
        var excel = new CleanupDecision { Gst = 12m, Source = "excel" };
        var merged = ItemCleanupService.Merge(screen, excel)!;
        Assert.Equal(("A", true, false, 12m, false), (merged.Name, merged.GroupLeave, merged.DataLeave, merged.Gst, merged.Retire!.Value));
    }
}
