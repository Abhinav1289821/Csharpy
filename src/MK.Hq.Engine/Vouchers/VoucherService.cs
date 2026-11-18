using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Vouchers;

public sealed record AllocationRequest(long EntryId, decimal Amount);

/// <summary>Cheque/transfer details HQ keeps in IQbnktran. Method: HQ's ChqType (0 cheque, 2 transfer/NEFT, 4 other).</summary>
public sealed record BankDetails(int Method = 2, string? Reference = null, DateOnly? Date = null, string? Bank = null, string? Place = null, DateOnly? DepositDate = null);

public sealed record VoucherLineRequest(long AccountId, decimal Amount, string? Notes = null, BankDetails? Bank = null, IReadOnlyList<AllocationRequest>? Allocations = null);

public sealed record VoucherRequest(VoucherKind Kind, DateOnly Date, long HeaderAccountId, IReadOnlyList<VoucherLineRequest> Lines,
    string? Notes = null, IReadOnlyList<AllocationRequest>? HeaderAllocations = null);

public sealed record VoucherPreview(string Hash, VoucherKind Kind, int TtId, string Series, int NextNumberHint, decimal Total, VoucherAccount Header,
    IReadOnlyList<VoucherAccount> Accounts, IReadOnlyList<PreviewLedgerRow> Ledger, IReadOnlyList<string> Warnings, IReadOnlyList<string> PlanSteps);

public sealed record VoucherPost(string OperationId, string ExpectedHash, VoucherRequest Voucher);

/// <summary>An HQ voucher read back into the editor's request shape, with the hash of what was read.</summary>
public sealed record VoucherEdit(long DocumentId, string Reference, int Number, string ContentHash, VoucherRequest Voucher, IReadOnlyList<string> Warnings);

public sealed record VoucherAmendPost(string OperationId, string ExpectedHash, VoucherRequest Voucher);

public sealed record VoucherReceipt(string OperationId, long DocumentId, VoucherKind Kind, string Series, int Number, string Reference, decimal Total);

/// <summary>An unsettled ledger entry a payment can be allocated against (an open bill, or an unallocated receipt).</summary>
public sealed record OpenItem
{
    public long EntryId { get; init; }
    public long DocumentId { get; init; }
    public string Reference { get; init; } = "";
    public string TypeName { get; init; } = "";
    public DateOnly Date { get; init; }
    public DateOnly? DueDate { get; init; }
    public char Side { get; init; }
    public decimal Amount { get; init; }
    public decimal Allocated { get; init; }
    public decimal Open { get; init; }
    public int AgeDays { get; init; }
}

/// <summary>Receipts, payments and notes through FrmFin_Vouchers' native path (Insert_Voudetails, IQdaybook_insert, IQbnktran_insert),
/// with bill-wise settlement (ADJUST + BL_adjusted) under row locks.</summary>
public sealed class VoucherService(FirmRegistry firms, FirmProfiles profiles, WriteCoordinator coordinator, Catalogue catalogue, HqOptions options)
{
    private sealed record Resolved(VoucherRequest Request, VoucherSpec Spec, TransactionType Type, VoucherAccount Header, IReadOnlyList<VoucherAccount> Accounts,
        IReadOnlyList<VoucherLedgerRow> Ledger, decimal Total, FirmProfile Profile, List<string> Warnings)
    {
        /// <summary>FrmFin_Vouchers saves the series in capitals (TTypes says "Cre"; HQ's rows say "CRE").</summary>
        public string Series => Type.Series.ToUpperInvariant();
    }

    public async Task<VoucherPreview> PreviewAsync(string firmId, VoucherRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        await using var s = await firm.OpenAsync(ct);
        var r = await ResolveAsync(s, profile, request);
        var plan = BuildCreate(r, auditRevision: 0);
        var next = await s.ScalarAsync<int>("SELECT ISNULL(MAX(TNo),0)+1 FROM dbo.IQDoc_Header WHERE Tt_Id=@tt AND TSeries=@series", new { tt = r.Spec.TtId, series = r.Series });
        return await ToPreviewAsync(s, r, plan, next);
    }

    public async Task<VoucherReceipt> PostAsync(string firmId, VoucherPost post, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<VoucherReceipt>(
            firm, post.OperationId, "voucher.create." + post.Voucher.Kind, DocumentService.RequestHash(post), [],
            async session =>
            {
                // Settings read inside the write transaction: a cached copy could be up to 30 s old, and the plan-hash check
                // could not then notice a settings change (round-off type, charge accounts) made since the review.
                var profile = await FirmProfiles.LoadAsync(firm, session);
                var r = await ResolveAsync(session, profile, post.Voucher);
                var plan = BuildCreate(r, auditRevision: 0);
                Guard.Require(plan.Hash == post.ExpectedHash, "REVIEW_CHANGED", "Accounts or open bills changed since review. Review the voucher again.", FailureKind.Conflict);
                return new PreparedWrite<VoucherReceipt>(plan, outcome =>
                {
                    var docId = outcome.Long(DocumentPlanBuilder.DocId);
                    var number = (int)outcome.Long(DocumentPlanBuilder.Number);
                    var reference = $"{r.Series}/{number}";
                    return (new VoucherReceipt(post.OperationId, docId, r.Spec.Kind, r.Series, number, reference, r.Total),
                        new WriteEvidence(WriteEvidence.Create, docId, Reference: reference));
                });
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    /// <summary>Deletes a voucher as FrmFin_Vouchers does, releasing its settlements first.</summary>
    public async Task<MutationPreview> PreviewDeleteAsync(string firmId, long documentId, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var (plan, state, change) = await PlanDeleteAsync(s, firm, documentId);
        return new MutationPreview(Hash(state.ContentHash, plan.Hash), documentId, state.Reference, "delete", null, state.Ledger.Sum(e => e.Allocated), change.Released,
            change.Released > 0 ? [$"{Money.Text(change.Released)} settled against bills will be detached; those bills become due again."] : [],
            plan.Steps.Select(x => x.Label).ToList());
    }

    public async Task<MutationReceipt> DeleteAsync(string firmId, DeleteRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<MutationReceipt>(
            firm, request.OperationId, "voucher.delete", DocumentService.RequestHash(request), [request.DocumentId],
            async session =>
            {
                var (plan, state, change) = await PlanDeleteAsync(session, firm, request.DocumentId);
                Guard.Require(Hash(state.ContentHash, plan.Hash) == request.ExpectedHash, "REVIEW_CHANGED", "The voucher changed since review", FailureKind.Conflict);
                return new PreparedWrite<MutationReceipt>(plan, _ => (
                    new MutationReceipt(request.OperationId, request.DocumentId, state.Reference, "deleted", null, change.Released),
                    new WriteEvidence(WriteEvidence.Delete, request.DocumentId, Reference: state.Reference)));
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    // ------------------------------------------------------------------ correction in place (FrmFin_Vouchers edit mode)

    /// <summary>Reads an HQ voucher back as the request that would create it, including its settlements.</summary>
    public async Task<VoucherEdit> LoadAsync(string firmId, long documentId, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        var state = await DocumentReader.StateAsync(s, documentId) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "The voucher is not in HQ");
        var spec = VoucherSpec.ForTt(state.TtId) ?? throw HqException.Unsupported("HQ_DOCUMENT_UNSUPPORTED", "This is not a receipt/payment/note voucher");
        var (request, warnings) = await ReadRequestAsync(s, state, spec);
        return new VoucherEdit(documentId, state.Reference, state.Number, state.ContentHash, request, warnings);
    }

    public async Task<VoucherPreview> PreviewAmendAsync(string firmId, long documentId, VoucherRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        await using var s = await firm.OpenAsync(ct);
        var (plan, state, r, change) = await PlanAmendAsync(s, firm, profile, documentId, request);
        var preview = await ToPreviewAsync(s, r, plan, state.Number);
        var warnings = r.Warnings.ToList();
        if (change.Released > 0) warnings.Add($"{Money.Text(change.Released)} settled by this voucher is released and settled again as shown.");
        return preview with { Hash = Hash(state.ContentHash, plan.Hash), Warnings = warnings };
    }

    public async Task<VoucherReceipt> AmendAsync(string firmId, long documentId, VoucherAmendPost post, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<VoucherReceipt>(
            firm, post.OperationId, "voucher.amend." + post.Voucher.Kind, DocumentService.RequestHash(new { documentId, post }), [documentId],
            async session =>
            {
                // Settings read inside the write transaction: a cached copy could be up to 30 s old, and the plan-hash check
                // could not then notice a settings change (round-off type, charge accounts) made since the review.
                var profile = await FirmProfiles.LoadAsync(firm, session);
                var (plan, state, r, _) = await PlanAmendAsync(session, firm, profile, documentId, post.Voucher);
                Guard.Require(Hash(state.ContentHash, plan.Hash) == post.ExpectedHash, "REVIEW_CHANGED",
                    "The voucher, its bills or the accounts changed since you reviewed the correction. Review it again.", FailureKind.Conflict);
                var after = state.Ledger.Count == 0 ? 0 : state.Ledger.Max(e => e.Id);
                return new PreparedWrite<VoucherReceipt>(plan, _ => (
                    new VoucherReceipt(post.OperationId, documentId, r.Spec.Kind, r.Series, state.Number, state.Reference, r.Total),
                    new WriteEvidence(WriteEvidence.VoucherAmend, documentId, Reference: state.Reference) { AfterEntry = after }));
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    private async Task<(WritePlan Plan, DocumentState State, Resolved Resolved, AllocationChange Change)> PlanAmendAsync(
        HqSession s, HqFirm firm, FirmProfile profile, long documentId, VoucherRequest request)
    {
        var state = await DocumentReader.StateAsync(s, documentId) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "The voucher is not in HQ");
        var spec = VoucherSpec.ForTt(state.TtId) ?? throw HqException.Unsupported("HQ_DOCUMENT_UNSUPPORTED", "This is not a receipt/payment/note voucher");
        Guard.Require(spec.Kind == request.Kind, "HQ_VOUCHER_KIND_CHANGE", $"A {spec.VouchType} voucher stays a {spec.VouchType} voucher; delete it and enter a new one instead");
        firm.RequireInPeriod(DateOnly.FromDateTime((DateTime)state.Header["Doc_Date"]!));
        // HQ carries a bank row's reconciliation status and an imported collection's id into the re-inserted rows. Neither
        // is used in this shop's data; rather than guess how to pair old and new lines, such vouchers stay HQ-only.
        var carried = await s.ScalarAsync<int>("""
            SELECT (SELECT COUNT(*) FROM dbo.IQbnktran WHERE Doc_ID=@id AND (ISNULL(LTRIM(Status),'0') NOT IN ('0','') OR ISNULL(HRdate,0)<>0))
                 + (SELECT COUNT(*) FROM dbo.IQDayBook WHERE Doc_ID=@id AND ISNULL(ImportCollection_DtldID,0)<>0)
            """, new { id = documentId });
        Guard.Supported(carried == 0, "HQ_VOUCHER_RECONCILED", "This voucher is bank-reconciled or was imported; correct it in HQ");
        // Only the voucher's own rows settle bills (in HQ data no voucher row is ever a settlement target).
        Guard.Supported(state.Links.All(l => state.Ledger.Any(e => e.Id == l.Source) && !state.Ledger.Any(e => e.Id == l.Target)),
            "HQ_VOUCHER_SETTLED_BY_OTHERS", "Another document settles this voucher; correct it in HQ");

        var r = await ResolveAsync(s, profile, request, documentId);
        var change = AllocationPlanner.Plan(state.Ledger, state.Related, [], state.Links, AllocationDisposition.Release);
        var id = new Args().Set("id", documentId);
        var steps = new List<PlanStep>
        {
            // The number is kept (HQ skips ChangeSeriesandNo in edit mode); the audit copy reads it like a new voucher's.
            new SqlStep("Keep voucher number", "SELECT CAST(TNo AS int) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", id, DocumentPlanBuilder.Number),
            // Step A of HQ's save: DeleteBilltoBillAdjustment for every old row (the bills become open again).
            new SqlStep("Release settlements", "DELETE FROM dbo.ADJUST WHERE EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=@id AND d.SR_NO=ADJUST.Sr_No)", id),
        };
        foreach (var (entry, allocated) in change.Counters.Where(c => state.Ledger.All(e => e.Id != c.EntryId)))
            steps.Add(new SqlStep($"Settlement counter {entry}", "UPDATE dbo.IQDayBook SET BL_adjusted=@amount WHERE SR_NO=@entry", new Args().Set("entry", entry).Set("amount", allocated)));

        var notes = string.IsNullOrEmpty(r.Request.Notes) ? " " : r.Request.Notes!;
        var header = new Args()
            .Set("Tt_id", r.Spec.TtId).Set("TSeries", r.Series).Set("Tno", state.Number).Set("TExtn", " ").Set("Mode", r.Spec.Mode)
            .Set("Doc_Date", r.Request.Date).Set("Doc_Duedate", r.Request.Date)
            .Set("Branch_ID", Convert.ToInt32(state.Header["Branch_ID"] ?? 0, CultureInfo.InvariantCulture))
            .Set("Salesman_Id", Convert.ToInt32(state.Header["SM_Id"] ?? 0, CultureInfo.InvariantCulture))
            .Zero("Doc_Type Db_Save TcBook")
            .Set("Bill_Amt", r.Total).Set("Act_id", r.Header.Id).Set("Ent_Date", new Clock(ClockField.Day)).Set("Ent_Uid", options.HqUserId)
            .Set("Narration", notes);
        steps.Add(new CallStep("Voucher header (same Doc_Id)", "Update_Voudetails", new Args { ["mDH_VouchType"] = r.Spec.VouchType, ["Doc_id"] = documentId }.With(header)));
        steps.Add(new SqlStep("Remove old bank register", "DELETE FROM dbo.IQbnktran WHERE Doc_Id=@id", id));
        steps.Add(new SqlStep("Remove old ledger", "DELETE FROM dbo.IQDayBook WHERE Doc_Id=@id", id));
        steps.AddRange(Body(r, documentId, state.Number));
        if (r.Profile.AuditEnabled) steps.AddRange(Audit(r, documentId, header, 0));
        steps.Add(new VerifyStep("Verify voucher amount", "SELECT CAST(Bill_Amt AS decimal(19,2)) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", id, r.Total,
            "HQ_SAVE_MISMATCH", "HQ stored a different voucher amount"));
        steps.Add(new VerifyStep("Verify ledger rows", "SELECT COUNT(*) FROM dbo.IQDayBook WHERE Doc_ID=@id", id, r.Ledger.Count, "HQ_SAVE_MISMATCH", "HQ stored a different voucher ledger"));
        var released = string.Join(",", change.Counters.Select(c => c.EntryId.ToString(CultureInfo.InvariantCulture)).DefaultIfEmpty("-1"));
        steps.Add(new VerifyStep("Verify settlements reconcile", $"""
            SELECT COUNT(*) FROM dbo.IQDayBook d
            WHERE (d.Doc_ID=@id OR d.SR_NO IN (SELECT a.Rel_Sr_No FROM dbo.ADJUST a JOIN dbo.IQDayBook x ON x.SR_NO=a.Sr_No WHERE x.Doc_ID=@id) OR d.SR_NO IN ({released}))
              AND ROUND(ISNULL(d.BL_adjusted,0),2) <> ROUND(ISNULL((SELECT SUM(Amount) FROM dbo.ADJUST WHERE Sr_No=d.SR_NO),0)+ISNULL((SELECT SUM(Amount) FROM dbo.ADJUST WHERE Rel_Sr_No=d.SR_NO),0),2)
            """, id, 0, "HQ_SAVE_MISMATCH", "Bill settlements no longer add up; nothing was saved"));
        return (new WritePlan($"Correct voucher {state.Reference}", steps) { HqDocumentLocks = [documentId] }, state, r, change);
    }

    /// <summary>The request that reproduces an HQ voucher: line rows (lnno 0) in ledger order, the header party row (lnno -1)
    /// for notes, bank details from IQbnktran, and each row's settlements from ADJUST.</summary>
    private static async Task<(VoucherRequest, List<string>)> ReadRequestAsync(HqSession s, DocumentState state, VoucherSpec spec)
    {
        var headerAccount = Convert.ToInt64(state.Header["Act_ID"], CultureInfo.InvariantCulture);
        var rows = await s.QueryAsync<(long Sr, long Act, long Contra, decimal Amount, string? Narration, int Ln)>(
            "SELECT CAST(SR_NO AS bigint), CAST(Act_ID AS bigint), CAST(ISNULL(Contra_ID,0) AS bigint), CAST(AMOUNT AS decimal(19,2)), Narration, CAST(ISNULL(lnno,0) AS int) FROM dbo.IQDayBook WHERE Doc_ID=@id AND ISNULL(VType,'')<>'D' ORDER BY SR_NO",
            new { id = state.DocumentId });
        var bankRows = await s.QueryAsync<(long Sr, int Type, string? No, int Date, string? Bank, string? Place, int Deposit)>(
            "SELECT CAST(Sr_no AS bigint), CAST(ISNULL(ChqType,2) AS int), chqno, CAST(ISNULL(chqdate,0) AS int), bname, BPlace, CAST(ISNULL(DepositDate,0) AS int) FROM dbo.IQbnktran WHERE Doc_ID=@id",
            new { id = state.DocumentId });
        // IQbnktran is keyed by its own id only; two cheque rows on one ledger line cannot be shown as one line's details.
        Guard.Supported(bankRows.GroupBy(b => b.Sr).All(g => g.Count() == 1), "HQ_VOUCHER_BANK_ROWS_UNSUPPORTED",
            "This voucher has two bank-register rows for one line; correct it in HQ");
        var banks = bankRows.ToDictionary(b => b.Sr);
        var linksBySource = state.Links.GroupBy(l => l.Source).ToDictionary(g => g.Key, g => g.Select(l => new AllocationRequest(l.Target, l.Amount)).ToList());
        static DateOnly? Day(int compact) => compact > 19000101 ? DateOnly.ParseExact(compact.ToString(CultureInfo.InvariantCulture), "yyyyMMdd", CultureInfo.InvariantCulture) : null;
        var lines = rows.Where(r => r.Ln >= 0).Select(r =>
        {
            var transfer = r.Act == headerAccount; // HQ's cash-transfer row books the header with the line account as contra
            var bank = banks.TryGetValue(r.Sr, out var b) ? b : default;
            return new VoucherLineRequest(transfer ? r.Contra : r.Act, r.Amount, r.Narration?.Trim() is { Length: > 0 } n ? n : null,
                spec.IsBank ? new BankDetails(bank.Sr == 0 ? 2 : bank.Type, bank.No?.Trim() is { Length: > 0 } no ? no : null, Day(bank.Date),
                    bank.Bank?.Trim() is { Length: > 0 } bn ? bn : null, bank.Place?.Trim() is { Length: > 0 } pl ? pl : null, Day(bank.Deposit)) : null,
                linksBySource.GetValueOrDefault(r.Sr) ?? []);
        }).ToList();
        var headerRow = rows.FirstOrDefault(r => r.Ln < 0);
        var headerAllocations = headerRow.Sr != 0 ? linksBySource.GetValueOrDefault(headerRow.Sr) ?? [] : [];
        var narration = Convert.ToString(state.Header["Narration"], CultureInfo.InvariantCulture)?.Trim();
        var request = new VoucherRequest(spec.Kind, DateOnly.FromDateTime((DateTime)state.Header["Doc_Date"]!), headerAccount, lines,
            string.IsNullOrEmpty(narration) ? null : narration, headerAllocations);
        return (request, []);
    }

    /// <param name="exceptDocument">A voucher being corrected: what it settles counts as open again, and its own rows are left out.</param>
    public async Task<IReadOnlyList<OpenItem>> OpenItemsAsync(string firmId, long accountId, long? exceptDocument = null, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        await using var s = await firm.OpenAsync(ct);
        return await OpenItemsAsync(s, accountId, exceptDocument);
    }

    public static async Task<IReadOnlyList<OpenItem>> OpenItemsAsync(HqSession s, long accountId, long? exceptDocument = null) =>
        await s.QueryAsync<OpenItem>("""
            SELECT CAST(d.SR_NO AS bigint) EntryId, CAST(d.Doc_ID AS bigint) DocumentId,
              CASE WHEN d.Doc_ID=-1 THEN 'Opening balance' ELSE RTRIM(ISNULL(d.Tseries,''))+'/'+CAST(ISNULL(d.Tno,0) AS varchar(12)) END Reference, RTRIM(ISNULL(t.Ttype,'Opening')) TypeName,
              DATEADD(day,DATEDIFF(day,0,d.trdate),0) Date, DATEADD(day,DATEDIFF(day,0,h.Doc_Duedate),0) DueDate, LEFT(LTRIM(d.TYPE),1) Side,
              CAST(d.AMOUNT AS decimal(19,2)) Amount, CAST(ISNULL(d.BL_adjusted,0)-r.Released AS decimal(19,2)) Allocated,
              CAST(d.AMOUNT-ISNULL(d.BL_adjusted,0)+r.Released AS decimal(19,2)) [Open], DATEDIFF(day, d.trdate, GETDATE()) AgeDays
            FROM dbo.IQDayBook d LEFT JOIN dbo.IQDoc_Header h ON h.Doc_Id=d.Doc_ID LEFT JOIN dbo.TTypes t ON t.TT_ID=h.Tt_Id
            CROSS APPLY (SELECT ISNULL((SELECT SUM(a.Amount) FROM dbo.ADJUST a JOIN dbo.IQDayBook src ON src.SR_NO=a.Sr_No
                WHERE a.Rel_Sr_No=d.SR_NO AND src.Doc_ID=@except),0) Released) r
            WHERE d.Act_ID=@accountId AND d.TYPE IN ('D','C') AND d.AMOUNT-ISNULL(d.BL_adjusted,0)+r.Released > 0.004 AND d.Doc_ID<>@except
            ORDER BY d.trdate, d.SR_NO
            """, new { accountId, except = exceptDocument ?? -999 });

    // ------------------------------------------------------------------ resolution

    /// <param name="amending">The voucher being corrected: its own settlements are released by the correction, so they count as open.</param>
    /// <summary>Checks on the request alone, before HQ is read: cheque details HQ's IQbnktran can hold, and settlements in
    /// whole paise. A longer cheque number was refused only at the final save (after review); 10.005 settled went into ADJUST
    /// and BL_adjusted with a third decimal no HQ screen shows.</summary>
    public static void RequireWellFormed(VoucherRequest r)
    {
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var line = r.Lines[i];
            if (line.Bank is { } b)
            {
                Guard.Require(b.Method is 0 or 2 or 4, "BANK_DETAILS", $"Line {i + 1}: choose cheque, transfer or other");
                foreach (var (value, what) in new[] { (b.Reference, "cheque/reference number"), (b.Bank, "bank name"), (b.Place, "bank place") })
                    Guard.Require((value ?? "").Length <= 50, "BANK_DETAILS", $"Line {i + 1}: the {what} can be at most 50 characters");
            }
            foreach (var a in line.Allocations ?? [])
                Guard.Require(a.Amount > 0 && Money.Scale(a.Amount) <= 2, "INVALID_AMOUNT", $"Line {i + 1}: settle a positive amount in rupees and paise");
        }
        foreach (var a in r.HeaderAllocations ?? [])
            Guard.Require(a.Amount > 0 && Money.Scale(a.Amount) <= 2, "INVALID_AMOUNT", "Settle a positive amount in rupees and paise");
    }

    private static async Task<Resolved> ResolveAsync(HqSession s, FirmProfile profile, VoucherRequest r, long? amending = null)
    {
        RequireWellFormed(r);
        profile.RequireWritableProfile();
        profile.Firm.RequireInPeriod(r.Date);
        var spec = VoucherSpec.For(r.Kind);
        var type = profile.Type(spec.TtId);
        Guard.Supported(string.Equals(type.VouchType, spec.VouchType, StringComparison.OrdinalIgnoreCase) && type.Trmode == spec.Mode
            && type.SubType == 0 && type.Status == 0 && type.BranchDefault == 0 && type.SeriesType == 3 && type.Series.Length is > 0 and <= 8,
            "HQ_VOUCHER_PROFILE_UNSUPPORTED", "HQ's voucher type is configured in a way this engine has not verified");
        Guard.Require(r.Lines.Count is > 0 and <= 100, "INVALID_LINES", "A voucher needs 1 to 100 lines");
        Guard.Require((r.Notes ?? "").Length <= 255, "NARRATION_TOO_LONG", "Voucher narration can be at most 255 characters");

        async Task<VoucherAccount> Account(long id) =>
            await s.QueryFirstOrDefaultAsync<VoucherAccount>("""
                SELECT CAST(Act_Id AS bigint) Id, LTRIM(RTRIM(Act_Name)) Name, LTRIM(RTRIM(ISNULL(Act_Type,''))) Type, CAST(ISNULL(Bill2Bill,0) AS bit) BillWise
                FROM dbo.IQAccmst WHERE Act_Id=@id AND Act_Cat='A' AND ISNULL(Act_Usage,0)=0
                """, new { id }) ?? throw HqException.NotFound("HQ_ACCOUNT_UNAVAILABLE", $"Account {id} is missing or inactive in HQ");

        var header = await Account(r.HeaderAccountId);
        if (spec.IsBank) Guard.Require(header.Type == "Bank", "HQ_BANK_ACCOUNT_REQUIRED", "Bank vouchers need a bank account at the top");
        // FrmFin_Vouchers lists only parties as the header of a credit or debit note.
        if (spec.IsNote) Guard.Require(header.Type is "Customer" or "Supplier" or "Customer-Supplier", "HQ_PARTY_REQUIRED", "A credit or debit note is issued to a customer or supplier");
        if (r.Kind is VoucherKind.CashReceipt or VoucherKind.CashPayment)
            Guard.Require(header.IsCash && header.Id == InvoiceLedger.HqCashAccountId, "HQ_CASH_ACCOUNT_REQUIRED", "Cash vouchers use the Cash account at the top");

        var accounts = new List<VoucherAccount>();
        var warnings = new List<string>();
        for (var i = 0; i < r.Lines.Count; i++)
        {
            var line = r.Lines[i];
            Guard.Require(line.Amount > 0 && Money.Scale(line.Amount) <= 2, "INVALID_AMOUNT", $"Line {i + 1}: enter a positive amount in rupees and paise");
            Guard.Require(line.AccountId != r.HeaderAccountId, "HQ_DATA_INVALID", $"Line {i + 1}: the line account cannot be the header account");
            Guard.Require((line.Notes ?? "").Length <= 250, "NARRATION_TOO_LONG", $"Line {i + 1}: note too long");
            Guard.Require(spec.IsBank == (line.Bank is not null), "BANK_DETAILS", spec.IsBank ? $"Line {i + 1}: enter the cheque/transfer details" : $"Line {i + 1}: bank details do not belong to a cash voucher");
            var account = await Account(line.AccountId);
            accounts.Add(account);
            var allocations = line.Allocations ?? [];
            Guard.Require(allocations.Sum(a => a.Amount) <= line.Amount, "ALLOCATION_TOO_LARGE", $"Line {i + 1}: allocated more than the line amount");
            Guard.Require(allocations.Select(a => a.EntryId).Distinct().Count() == allocations.Count, "ALLOCATION_DUPLICATE", $"Line {i + 1}: a bill is allocated twice");
            if (allocations.Count > 0)
            {
                Guard.Supported(profile.Config.BillWise == 1 && account.BillWise, "HQ_ACCOUNT_NOT_BILLWISE", $"{account.Name} is not bill-wise in HQ");
                Guard.Supported(account.Type is "Customer" or "Customer-Supplier" ? r.Kind is VoucherKind.CashReceipt or VoucherKind.BankReceipt or VoucherKind.DebitNote
                        : account.Type == "Supplier" && r.Kind is VoucherKind.CashPayment or VoucherKind.BankPayment or VoucherKind.CreditNote,
                    "HQ_ALLOCATION_DIRECTION_UNSUPPORTED", $"Bills of {account.Name} cannot be settled by this voucher type");
            }
            else if (account.BillWise && profile.Config.BillWise == 1 && r.Kind is not (VoucherKind.CreditNote or VoucherKind.DebitNote))
                warnings.Add($"{account.Name} is bill-wise but nothing is allocated; the amount stays on account.");
        }
        var ledger = VoucherLedger.Plan(spec, header, accounts, r.Lines.Select(l => new VoucherLine(l.AccountId, l.Amount, l.Notes ?? "")).ToList());
        var total = r.Lines.Sum(l => l.Amount);
        if ((r.HeaderAllocations ?? []).Count > 0)
            Guard.Supported(profile.Config.BillWise == 1 && header.BillWise, "HQ_HEADER_ALLOCATION_UNSUPPORTED", "The header account is not bill-wise");

        // Validate allocation targets now (they are re-checked under row locks when posting).
        var settling = r.Lines.SelectMany(l => l.Allocations ?? []).Concat(r.HeaderAllocations ?? [])
            .GroupBy(a => a.EntryId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
        async Task Target(AllocationRequest a, VoucherLedgerRow row, string where)
        {
            var target = await s.QueryFirstOrDefaultAsync<(long Id, long AccountId, string Type, decimal Amount, decimal Allocated, long DocumentId, decimal Releasing)?>(
                """
                SELECT CAST(d.SR_NO AS bigint), CAST(d.Act_ID AS bigint), LEFT(LTRIM(d.TYPE),1), CAST(d.AMOUNT AS decimal(19,2)), CAST(ISNULL(d.BL_adjusted,0) AS decimal(19,2)),
                  CAST(d.Doc_ID AS bigint),
                  CAST(ISNULL((SELECT SUM(a.Amount) FROM dbo.ADJUST a JOIN dbo.IQDayBook src ON src.SR_NO=a.Sr_No WHERE a.Rel_Sr_No=d.SR_NO AND src.Doc_ID=@amending),0) AS decimal(19,2))
                FROM dbo.IQDayBook d WHERE d.SR_NO=@id
                """, new { id = a.EntryId, amending = amending ?? 0 })
                ?? throw HqException.NotFound("HQ_ALLOCATION_TARGET_MISSING", "A bill chosen for settlement no longer exists");
            var t = target;
            Guard.Require(t.DocumentId != amending, "HQ_ALLOCATION_ACCOUNT_MISMATCH", $"{where}: a voucher cannot settle its own rows");
            Guard.Require(t.AccountId == row.AccountId && (t.Type is "D" or "P") != (row.Type is 'D' or 'P'), "HQ_ALLOCATION_ACCOUNT_MISMATCH",
                $"{where}: the chosen bill belongs to another account or side");
            var open = t.Amount - t.Allocated + t.Releasing;
            // Two lines (or a line and the header) may settle the same bill; together they may not exceed it. Checked one
            // by one, both passed review and the second then failed at saving as "settled elsewhere meanwhile".
            Guard.Require(a.Amount > 0 && settling[a.EntryId] <= open, "HQ_ALLOCATION_EXCEEDS_OUTSTANDING",
                $"{where}: more than the bill's open amount ({Money.Text(open)}{(settling[a.EntryId] != a.Amount ? ", counting the other lines settling it" : "")})", FailureKind.Conflict);
        }
        foreach (var (line, index) in r.Lines.Select((l, i) => (l, i)))
            foreach (var a in line.Allocations ?? [])
                await Target(a, ledger.First(x => x.Line == index), $"Line {index + 1}");
        if ((r.HeaderAllocations ?? []).Count > 0)
        {
            var headerRow = ledger.FirstOrDefault(x => x.Line < 0)
                ?? throw HqException.Unsupported("HQ_HEADER_ALLOCATION_UNSUPPORTED", "This voucher has no party row at the top to settle bills with");
            var headerAllocations = r.HeaderAllocations!;
            // Capped by the party row being settled, not the voucher total (which also counts cash-transfer lines).
            Guard.Require(headerAllocations.Sum(a => a.Amount) <= headerRow.Amount, "ALLOCATION_TOO_LARGE", "Allocated more than the party's amount on this voucher");
            Guard.Require(headerAllocations.Select(a => a.EntryId).Distinct().Count() == headerAllocations.Count, "ALLOCATION_DUPLICATE", "A bill is allocated twice");
            foreach (var a in headerAllocations) await Target(a, headerRow, header.Name);
        }
        return new Resolved(r, spec, type, header, accounts, ledger, total, profile, warnings);
    }

    // ------------------------------------------------------------------ plans

    private WritePlan BuildCreate(Resolved r, int auditRevision)
    {
        var steps = new List<PlanStep>();
        var docId = new Ref(DocumentPlanBuilder.DocId);
        var number = new Ref(DocumentPlanBuilder.Number);
        var notes = string.IsNullOrEmpty(r.Request.Notes) ? " " : r.Request.Notes!;
        steps.Add(new AllocateNumberStep("Allocate voucher number", r.Spec.TtId, r.Series, "", DocumentPlanBuilder.Number));
        var header = new Args()
            .Set("Tt_id", r.Spec.TtId).Set("TSeries", r.Series).Set("Tno", number).Set("TExtn", " ").Set("Mode", r.Spec.Mode)
            .Set("Doc_Date", r.Request.Date).Set("Doc_Duedate", r.Request.Date).Zero("Branch_ID Salesman_Id Doc_Type Db_Save TcBook")
            .Set("Bill_Amt", r.Total).Set("Act_id", r.Header.Id).Set("Ent_Date", new Clock(ClockField.Day)).Set("Ent_Uid", options.HqUserId)
            .Set("Narration", notes);
        steps.Add(new CallStep("Voucher header", "Insert_Voudetails", new Args { ["mDH_VouchType"] = r.Spec.VouchType }.With(header),
            new Dictionary<string, string> { ["Doc_id"] = DocumentPlanBuilder.DocId }));
        steps.Add(new AssertNumberFreeStep("HQ duplicate-number rule", r.Spec.TtId, r.Series, number, "", docId, true));
        steps.AddRange(Body(r, docId, number));
        if (r.Profile.AuditEnabled) steps.AddRange(Audit(r, docId, header, auditRevision));
        steps.Add(new VerifyStep("Verify voucher amount", "SELECT CAST(Bill_Amt AS decimal(19,2)) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new Args().Set("id", docId), r.Total,
            "HQ_SAVE_MISMATCH", "HQ stored a different voucher amount"));
        steps.Add(new VerifyStep("Verify ledger rows", "SELECT COUNT(*) FROM dbo.IQDayBook WHERE Doc_ID=@id", new Args().Set("id", docId), r.Ledger.Count,
            "HQ_SAVE_MISMATCH", "HQ stored a different voucher ledger"));
        steps.Add(new VerifyStep("Verify narration kept", "SELECT LTRIM(RTRIM(ISNULL(Narration,''))) FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new Args().Set("id", docId),
            (r.Request.Notes ?? "").Trim(), "HQ_FIELD_MISMATCH", "HQ did not keep the narration as typed"));
        return new WritePlan($"{r.Type.Name} voucher", steps);
    }

    private IEnumerable<PlanStep> Body(Resolved r, object docId, object number)
    {
        foreach (var row in r.Ledger)
        {
            var entry = row.Line >= 0 ? $"entry{row.Line}" : "entryH";
            yield return new CallStep($"Ledger {row.Type} {row.AccountId}", "IQdaybook_insert", LedgerArgs(r, row, docId, number)
                .Set("VType", row.Line >= 0 && r.Accounts[row.Line].IsCash ? "1" : "DPAY").Set("lnno", row.Line >= 0 ? 0 : -1),
                new Dictionary<string, string> { ["sr_no"] = entry });
            if (row.Line >= 0 && r.Spec.IsBank)
                yield return new CallStep("Bank register", "IQbnktran_insert", BankArgs(r, row.Line, docId, new Ref(entry)));
        }
        for (var i = 0; i < r.Request.Lines.Count; i++)
            foreach (var a in r.Request.Lines[i].Allocations ?? [])
                yield return Allocate(new Ref($"entry{i}"), a);
        foreach (var a in r.Request.HeaderAllocations ?? [])
            yield return Allocate(new Ref("entryH"), a);
    }

    private Args LedgerArgs(Resolved r, VoucherLedgerRow row, object docId, object number) => new Args()
        .Set("trdate", r.Request.Date).Set("Act_id", row.AccountId).Set("Amount", row.Amount).Set("type", row.Type.ToString()).Set("Contra_id", row.ContraId)
        .Zero("BL_Adjusted Adjusted ARS ARS_Ord Div_no days_from days_to int_credit discount doc_type Bill_Qty adj_id")
        .Set("Doc_ID", docId).Set("Tseries", r.Series).Set("Tno", number).Set("Textn", "")
        // The live ledger keeps the trimmed note; HQ's audit copy keeps it as typed (set by the audit step).
        .Set("Narration", row.Notes.Trim())
        .Set("ent_Uid", options.HqUserId).Set("mDiscRow", "");

    private static Args BankArgs(Resolved r, int line, object docId, object entry)
    {
        var b = r.Request.Lines[line].Bank!;
        static int Compact(DateOnly? d) => d is { } v ? int.Parse(v.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture) : 0;
        return new Args().Set("mdoc_ID", docId).Set("mSr_no", entry).Set("mChqType", b.Method)
            .Set("mchqno", string.IsNullOrWhiteSpace(b.Reference) ? " " : b.Reference)
            .Set("mchqdate", Compact(b.Date)).Set("mbname", r.Spec.Kind == VoucherKind.BankPayment ? r.Header.Name : string.IsNullOrWhiteSpace(b.Bank) ? " " : b.Bank)
            .Set("mBPlace", string.IsNullOrWhiteSpace(b.Place) ? " " : b.Place).Set("mDepositDate", Compact(b.DepositDate)).Set("mStatus", "0").Set("mHRdate", 0);
    }

    /// <summary>Bill-wise settlement exactly as HQ stores it (ADJUST + both BL_adjusted counters), guarded under row locks.</summary>
    public static SqlStep Allocate(object source, AllocationRequest a) => new("Settle bill", """
        DECLARE @ok int = (SELECT COUNT(*) FROM dbo.IQDayBook s WITH (UPDLOCK, HOLDLOCK) JOIN dbo.IQDayBook t WITH (UPDLOCK, HOLDLOCK) ON t.SR_NO=@target
          WHERE s.SR_NO=@source AND s.Act_ID=t.Act_ID
            AND (CASE WHEN s.TYPE IN ('D','P') THEN 1 ELSE 0 END) <> (CASE WHEN t.TYPE IN ('D','P') THEN 1 ELSE 0 END)
            AND s.AMOUNT-ISNULL(s.BL_adjusted,0) >= @amount AND t.AMOUNT-ISNULL(t.BL_adjusted,0) >= @amount);
        IF @ok <> 1 RAISERROR('MK:HQ_ALLOCATION_EXCEEDS_OUTSTANDING|A bill was settled elsewhere meanwhile; review the allocation again', 16, 1);
        INSERT INTO dbo.ADJUST (Sr_No, Rel_Sr_No, Amount) VALUES (@source, @target, @amount);
        UPDATE dbo.IQDayBook SET BL_adjusted=ISNULL(BL_adjusted,0)+@amount WHERE SR_NO IN (@source, @target);
        """, new Args().Set("source", source).Set("target", a.EntryId).Set("amount", a.Amount));

    private IEnumerable<PlanStep> Audit(Resolved r, object docId, Args header, int revision)
    {
        yield return new CallStep("Audit: voucher header", "User_Insert_Voudetails", new Args().With(header).Set("Doc_id", docId).Set("mModifyCnt", revision));
        var lines = r.Request.Lines.Select(l => new VoucherLine(l.AccountId, l.Amount, l.Notes ?? "")).ToList();
        foreach (var (row, counter) in VoucherLedger.AuditRows(r.Spec, r.Header, r.Accounts, lines))
        {
            yield return new CallStep("Audit: voucher ledger", "User_IQdaybook_insert",
                LedgerArgs(r, row, docId, new Ref(DocumentPlanBuilder.Number)).Set("Narration", row.Notes).Set("sr_no", 0).Set("VType", r.Accounts[row.Line].IsCash ? "1" : "0")
                    .Set("lnno", counter).Set("mDiscRow", counter == 1 ? "D" : ""));
            if (r.Spec.IsBank && counter == 0 && !string.IsNullOrWhiteSpace(r.Request.Lines[row.Line].Bank?.Reference))
                yield return new CallStep("Audit: bank register", "User_IQbnktran_insert", BankArgs(r, row.Line, docId, 0).Set("mBnkTranID", 0));
        }
    }

    private static async Task<(WritePlan, DocumentState, AllocationChange)> PlanDeleteAsync(HqSession s, HqFirm firm, long documentId)
    {
        var state = await DocumentReader.StateAsync(s, documentId) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "The voucher is not in HQ");
        Guard.Supported(VoucherSpec.ForTt(state.TtId) is not null, "HQ_DOCUMENT_UNSUPPORTED", "This is not a receipt/payment/note voucher");
        firm.RequireInPeriod(DateOnly.FromDateTime((DateTime)state.Header["Doc_Date"]!));
        // The same guards as a voucher correction and a bill delete: HQ settings MK has not verified (e.g. the change audit
        // kept in a separate database, where the deleted-mark below would then be missing), and bank-reconciled or
        // imported vouchers, which stay HQ-only.
        (await FirmProfiles.LoadAsync(firm, s)).RequireWritableProfile();
        var carried = await s.ScalarAsync<int>("""
            SELECT (SELECT COUNT(*) FROM dbo.IQbnktran WHERE Doc_ID=@id AND (ISNULL(LTRIM(Status),'0') NOT IN ('0','') OR ISNULL(HRdate,0)<>0))
                 + (SELECT COUNT(*) FROM dbo.IQDayBook WHERE Doc_ID=@id AND ISNULL(ImportCollection_DtldID,0)<>0)
            """, new { id = documentId });
        Guard.Supported(carried == 0, "HQ_VOUCHER_RECONCILED", "This voucher is bank-reconciled or was imported; delete it in HQ");
        var change = AllocationPlanner.Plan(state.Ledger, state.Related, [], state.Links, AllocationDisposition.Release);
        var id = new Args().Set("id", documentId);
        var steps = new List<PlanStep>
        {
            new SqlStep("Detach settlements", "DELETE FROM dbo.ADJUST WHERE EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=@id AND d.SR_NO IN (ADJUST.Sr_No, ADJUST.Rel_Sr_No))", id),
        };
        foreach (var (entry, allocated) in change.Counters)
            steps.Add(new SqlStep($"Settlement counter {entry}", "UPDATE dbo.IQDayBook SET BL_adjusted=@amount WHERE SR_NO=@entry", new Args().Set("entry", entry).Set("amount", allocated)));
        steps.Add(new SqlStep("Delete bank register", "DELETE FROM dbo.IQbnktran WHERE Doc_Id=@id", id));
        steps.Add(new SqlStep("Delete ledger", "DELETE FROM dbo.IQDayBook WHERE Doc_Id=@id", id));
        steps.Add(new SqlStep("Delete header", "DELETE FROM dbo.IQDoc_Header WHERE Doc_Id=@id", id));
        // HQ's Update_UserDeleteStatus, verbatim.
        steps.Add(new SqlStep("Mark audit deleted", "UPDATE dbo.User_IQDOC_Header SET TaskType=1 WHERE Doc_ID=@id AND ModifyCnt=(SELECT COUNT(Doc_Id) FROM dbo.User_IQDOC_Header WHERE Doc_ID=@id)", id));
        steps.Add(new VerifyStep("Verify voucher removed", "SELECT COUNT(*) FROM dbo.IQDoc_Header WITH (READCOMMITTEDLOCK) WHERE Doc_Id=@id", id, 0, "HQ_DELETE_MISMATCH", "The voucher is still in HQ"));
        return (new WritePlan($"Delete voucher {state.Reference}", steps) { HqDocumentLocks = [documentId] }, state, change);
    }

    private static async Task<VoucherPreview> ToPreviewAsync(HqSession s, Resolved r, WritePlan plan, int next)
    {
        var ids = r.Ledger.Select(l => l.AccountId).Distinct().ToArray();
        var names = (await s.QueryAsync<(long, string)>("SELECT CAST(Act_Id AS bigint), LTRIM(RTRIM(Act_Name)) FROM dbo.IQAccmst WHERE Act_Id IN @ids", new { ids }))
            .ToDictionary(x => x.Item1, x => x.Item2);
        return new VoucherPreview(plan.Hash, r.Spec.Kind, r.Spec.TtId, r.Series, next, r.Total, r.Header, r.Accounts,
            r.Ledger.Select(l => new PreviewLedgerRow(l.AccountId, names.GetValueOrDefault(l.AccountId, "#" + l.AccountId), l.Type, l.ContraId, l.Amount)).ToList(),
            r.Warnings, plan.Steps.Select(x => x.Label).ToList());
    }

    private static string Hash(string a, string b) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(a + ":" + b))).ToLowerInvariant();
}

internal static class ArgsExtensions
{
    public static Args With(this Args target, IReadOnlyDictionary<string, object?> source)
    {
        foreach (var (k, v) in source) target[k] = v;
        return target;
    }
}
