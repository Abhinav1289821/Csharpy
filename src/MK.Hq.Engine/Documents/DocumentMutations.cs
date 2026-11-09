using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Documents;

public sealed record MutationPreview(string Hash, long DocumentId, string Reference, string Action, DocumentPreview? Replacement,
    decimal AllocatedBefore, decimal ReleasedAllocations, IReadOnlyList<string> Warnings, IReadOnlyList<string> PlanSteps);

/// <summary>
/// Corrections and deletions of saved documents — HQ's own or ours — through HQ's edit path: the same upsert
/// procedures with the existing ids, HQ's delete procedures, HQ's .HQS edit lock, and bill-wise settlements either
/// carried over to the corrected bill or released, never silently lost.
/// </summary>
public sealed class DocumentMutations(
    FirmRegistry firms, FirmProfiles profiles, DocumentResolver resolver, WriteCoordinator coordinator, Catalogue catalogue, HqOptions options)
{
    private static readonly string[] HeaderMustBeZero = ["Branch_ID", "Doc_Cancel", "TcBook", "Doc_Type", "ZDoc_Id", "ProcRefID"];
    private static readonly string[] LineMustBeZero =
        ["Free", "SchQty", "EstQty", "EstFree", "MultiMrp", "DCRecd_Id", "DCDelv_Id", "IndentRecd_ID", "IndentDelv_ID", "TempRecd_ID", "TempDelv_ID"];

    // ------------------------------------------------------------------ delete

    public async Task<MutationPreview> PreviewDeleteAsync(string firmId, long documentId, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        await using var s = await firm.OpenAsync(ct);
        var (plan, state, change) = await PlanDeleteAsync(s, profile, documentId);
        return new MutationPreview(Combine(state.ContentHash, plan.Hash), documentId, state.Reference, "delete", null,
            state.Ledger.Sum(e => e.Allocated), change.Released,
            change.Released > 0 ? [$"Payments of {Money.Text(change.Released)} settled against this bill will become unallocated (on account)."] : [],
            plan.Steps.Select(x => x.Label).ToList());
    }

    public async Task<MutationReceipt> DeleteAsync(string firmId, DeleteRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        var receipt = await coordinator.RunAsync(new WriteRequest<MutationReceipt>(
            firm, request.OperationId, "document.delete", DocumentService.RequestHash(request), [request.DocumentId],
            async session =>
            {
                var (plan, state, change) = await PlanDeleteAsync(session, profile, request.DocumentId);
                Guard.Require(Combine(state.ContentHash, plan.Hash) == request.ExpectedHash, "REVIEW_CHANGED",
                    "This document or its payments changed since you reviewed the deletion. Review it again.", FailureKind.Conflict);
                return new PreparedWrite<MutationReceipt>(plan, _ => (
                    new MutationReceipt(request.OperationId, request.DocumentId, state.Reference, "deleted", null, change.Released),
                    new WriteEvidence(WriteEvidence.Delete, request.DocumentId, Reference: state.Reference)));
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    private static async Task<(WritePlan, DocumentState, AllocationChange)> PlanDeleteAsync(HqSession s, FirmProfile profile, long documentId)
    {
        profile.RequireWritableProfile();
        var state = await DocumentReader.StateAsync(s, documentId) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "The document is not in HQ");
        RequireMutable(state, profile);
        var kind = state.Kind!.Value;
        var steps = new List<PlanStep>();
        var id = new Args().Set("id", documentId);
        var change = AllocationPlanner.Plan(state.Ledger, state.Related, [], state.Links, AllocationDisposition.Release);
        if (kind == DocumentKind.SalesOrder)
        {
            var supplied = await s.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.IQDoc_Delv WHERE OrdDelv_Id IN @ids", new { ids = state.LineIds.DefaultIfEmpty(0).ToArray() });
            Guard.Require(supplied == 0, "HQ_ORDER_SUPPLIED", "Sales have already been made from this order; delete or correct those first", FailureKind.Conflict);
        }
        steps.AddRange(ReleaseSteps(documentId, change));
        steps.Add(new CallStep("HQ delete procedure", kind switch
        {
            DocumentKind.Sale => "Outward_Delete",
            DocumentKind.Purchase => "Inward_Delete",
            _ => "Orders_Delete",
        }, new Args().Set("mdoc_Id", documentId)));
        if (!kind.IsOrder()) steps.AddRange(DocumentPlanBuilder.StockSteps(state.ItemIds, clearEmpty: true));
        foreach (var t in new[] { "IQDoc_Header", "IQDoc_Delv", "IQDoc_Recd", "IQOrd_Detail", "IQDayBook", "IQCash_TRAN", "IQDOC_VAT", "IQDoc_Chrgs" })
            steps.Add(new VerifyStep($"Verify {t} cleared", $"SELECT COUNT(*) FROM dbo.{t} WITH (READCOMMITTEDLOCK) WHERE Doc_ID=@id", id, 0, "HQ_DELETE_MISMATCH", "HQ's delete left rows behind"));
        return (new WritePlan($"Delete {state.Reference}", steps), state, change);
    }

    private static IEnumerable<PlanStep> ReleaseSteps(long documentId, AllocationChange change)
    {
        yield return new SqlStep("Detach bill-wise settlements",
            "DELETE FROM dbo.ADJUST WHERE EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=@id AND d.SR_NO IN (ADJUST.Sr_No, ADJUST.Rel_Sr_No))",
            new Args().Set("id", documentId));
        foreach (var (entry, allocated) in change.Counters)
            yield return new SqlStep($"Settlement counter {entry}", "UPDATE dbo.IQDayBook SET BL_adjusted=@amount WHERE SR_NO=@entry",
                new Args().Set("entry", entry).Set("amount", allocated));
    }

    // ------------------------------------------------------------------ amend

    public async Task<MutationPreview> PreviewAmendAsync(string firmId, AmendRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        await using var s = await firm.OpenAsync(ct);
        var (plan, state, resolved, change) = await PlanAmendAsync(s, profile, request);
        var preview = await DocumentService.ToPreviewAsync(s, firm, resolved, plan, state.Number);
        var warnings = resolved.Warnings.ToList();
        if (change.Released > 0) warnings.Add($"Payments of {Money.Text(change.Released)} will be detached from this bill.");
        return new MutationPreview(Combine(state.ContentHash, plan.Hash), request.DocumentId, state.Reference, "amend", preview,
            state.Ledger.Sum(e => e.Allocated), change.Released, warnings, plan.Steps.Select(x => x.Label).ToList());
    }

    public async Task<MutationReceipt> AmendAsync(string firmId, AmendRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        var receipt = await coordinator.RunAsync(new WriteRequest<MutationReceipt>(
            firm, request.OperationId, "document.amend", DocumentService.RequestHash(request), [request.DocumentId],
            async session =>
            {
                var (plan, state, resolved, change) = await PlanAmendAsync(session, profile, request);
                Guard.Require(Combine(state.ContentHash, plan.Hash) == request.ExpectedHash, "REVIEW_CHANGED",
                    "This document, its payments or HQ's masters changed since you reviewed the correction. Review it again.", FailureKind.Conflict);
                return new PreparedWrite<MutationReceipt>(plan, _ => (
                    new MutationReceipt(request.OperationId, request.DocumentId, state.Reference, "amended", resolved.Quote.Payable, change.Released),
                    new WriteEvidence(WriteEvidence.Amend, request.DocumentId, state.ModifyCnt + 1, state.Reference)));
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    private async Task<(WritePlan, DocumentState, ResolvedDocument, AllocationChange)> PlanAmendAsync(HqSession s, FirmProfile profile, AmendRequest request)
    {
        var state = await DocumentReader.StateAsync(s, request.DocumentId) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "The document is not in HQ");
        RequireMutable(state, profile);
        var kind = state.Kind!.Value;
        Guard.Require(request.Document.Kind == kind, "AMEND_KIND_CHANGED", "A correction cannot change the document type");
        Guard.Require(request.Document.Kind.TtId(request.Document.Interstate) == state.TtId, "AMEND_TYPE_CHANGED",
            "Changing between local and interstate changes the bill series; delete and re-enter the bill instead");
        Guard.Require(request.LineIds.Count == request.Document.Lines.Count, "INVALID_LINES", "Each line needs its existing line id or null");
        var existing = state.LineIds.ToHashSet();
        var kept = request.LineIds.Where(x => x is not null).Select(x => x!.Value).ToList();
        Guard.Require(kept.All(existing.Contains) && kept.Distinct().Count() == kept.Count, "INVALID_LINES", "A line id does not belong to this document");

        var resolved = await resolver.Excluding(request.DocumentId).ResolveAsync(s, profile, request.Document with
        {
            NumberSuffix = request.Document.NumberSuffix ?? DocumentResolver.Text(state.Header, "TExtn"),
        });
        // Keep the original order links of kept sale lines.
        if (kind == DocumentKind.Sale)
        {
            var links = request.LineIds.Select(id =>
            {
                if (id is null) return 0L;
                var row = state.Lines.First(l => Convert.ToInt64(l["Delv_Id"], CultureInfo.InvariantCulture) == id);
                return row.TryGetValue("OrdDelv_Id", out var o) && o is not null ? Convert.ToInt64(o, CultureInfo.InvariantCulture) : 0L;
            }).ToList();
            resolved = resolved with { OrderLineIds = links };
        }

        var docId = request.DocumentId;
        var number = state.Number;
        var steps = new List<PlanStep>
        {
            new VerifyStep("Document unchanged since review", "SELECT ModifyCnt FROM dbo.IQDoc_Header WHERE Doc_Id=@id", new Args().Set("id", docId),
                state.ModifyCnt, "REVIEW_CHANGED", "The document was edited elsewhere"),
        };

        // Header: HQ's IQDoc_Header_Insert updates in place when given the existing Doc_Id.
        var header = DocumentPlanBuilder.Header(resolved, number, options.HqUserId, options.EffectiveMachineName, state.ModifyCnt + 1);
        header.Set("mDoc_Id", docId)
            .Set("mEnt_Date", state.Header["Ent_Date"]).Set("mEnt_Uid", state.Header["Ent_Uid"]).Set("mEnt_Time", state.Header["Ent_Time"] ?? "")
            .Set("mVisitDate", state.Header["VisitDate"]).Set("mBillEnteredSystem", state.Header["BillEnteredSystem"] ?? "");
        steps.Add(new CallStep("Bill header (edit)", "IQDoc_Header_Insert", header));
        steps.Add(new AssertNumberFreeStep("HQ duplicate-number rule", state.TtId, state.Series, number, resolved.Suffix, docId, resolved.Type.SeriesType == 3));

        // Lines removed from the bill go, as in HQ's grid delete.
        var (table, key) = kind switch
        {
            DocumentKind.Sale => ("IQDoc_Delv", "Delv_Id"),
            DocumentKind.Purchase => ("IQDoc_Recd", "Recd_Id"),
            _ => ("IQOrd_Detail", "OrdDltdID"),
        };
        foreach (var gone in existing.Except(kept).Order())
            steps.Add(new SqlStep($"Remove line {gone}", $"DELETE FROM dbo.{table} WHERE Doc_ID=@doc AND {key}=@line", new Args().Set("doc", docId).Set("line", gone)));
        foreach (var t in new[] { "IQCash_TRAN", "IQDoc_Chrgs", "IQDOC_VAT" })
            steps.Add(new SqlStep($"Clear {t} for rewrite", $"DELETE FROM dbo.{t} WHERE Doc_Id=@id", new Args().Set("id", docId)));

        // Lines (kept ones updated in place), snapshot, charges, VAT — but not the ledger yet.
        var body = DocumentPlanBuilder.Body(resolved, docId, number, options.HqUserId, request.LineIds).ToList();
        steps.AddRange(body.Where(b => b is not CallStep { Procedure: "IQdaybook_insert" } && !IsStock(b)));

        var change = new AllocationChange([], [], 0);
        if (!kind.IsOrder())
        {
            // Ledger: HQ rewrites the whole IQDayBook set; settlements are carried to the new party row or released.
            var placeholder = resolved.Ledger.Select((r, i) => new SettlementEntry(-(i + 1), r.AccountId, r.Type, r.Amount, 0)).ToList();
            change = AllocationPlanner.Plan(state.Ledger, state.Related, placeholder, state.Links, request.Allocations);
            steps.Add(new SqlStep("Detach settlements for rewrite",
                "DELETE FROM dbo.ADJUST WHERE EXISTS (SELECT 1 FROM dbo.IQDayBook d WHERE d.Doc_ID=@id AND d.SR_NO IN (ADJUST.Sr_No, ADJUST.Rel_Sr_No))", new Args().Set("id", docId)));
            steps.Add(new SqlStep("Clear ledger for rewrite", "DELETE FROM dbo.IQDayBook WHERE Doc_ID=@id", new Args().Set("id", docId)));
            var ledgerSteps = DocumentPlanBuilder.LedgerSteps(resolved, docId, number, options.HqUserId).Cast<CallStep>().ToList();
            for (var i = 0; i < ledgerSteps.Count; i++)
                steps.Add(ledgerSteps[i] with { Capture = new Dictionary<string, string> { ["sr_no"] = $"entry{i}" } });
            object Entry(long id) => id < 0 ? new Ref($"entry{-id - 1}") : id;
            foreach (var link in change.Links)
                steps.Add(new SqlStep("Carry settlement", "INSERT INTO dbo.ADJUST (Sr_No, Rel_Sr_No, Amount) VALUES (@source, @target, @amount)",
                    new Args().Set("source", Entry(link.Source)).Set("target", Entry(link.Target)).Set("amount", link.Amount)));
            foreach (var (entry, allocated) in change.Counters)
                steps.Add(new SqlStep("Settlement counter", "UPDATE dbo.IQDayBook SET BL_adjusted=@amount WHERE SR_NO=@entry",
                    new Args().Set("entry", Entry(entry)).Set("amount", allocated)));
            steps.AddRange(DocumentPlanBuilder.StockSteps(state.ItemIds.Concat(resolved.Items.Select(m => m.Item.ItemId)), clearEmpty: true));
        }

        if (kind == DocumentKind.Sale && resolved.OrderLineIds.Any(x => x > 0))
            steps.Add(new SqlStep("Order quantities still within order", """
                IF EXISTS (SELECT 1 FROM dbo.IQOrd_Detail o WHERE o.OrdDltdID IN (SELECT OrdDelv_Id FROM dbo.IQDoc_Delv WHERE Doc_ID=@id)
                  AND o.Qty < (SELECT SUM(d.Qty) FROM dbo.IQDoc_Delv d WHERE d.OrdDelv_Id=o.OrdDltdID))
                  RAISERROR('MK:HQ_ORDER_QUANTITY_EXCEEDED|The corrected sale supplies more than its sales order', 16, 1);
                """, new Args().Set("id", docId)));

        steps.AddRange(DocumentPlanBuilder.Audit(resolved, docId, state.ModifyCnt, request.LineIds));
        steps.AddRange(DocumentPlanBuilder.Verification(resolved, docId));
        var plan = new WritePlan($"Correct {state.Reference}", steps) { HqDocumentLocks = [docId] };
        return (plan, state, resolved, change);
    }

    private static bool IsStock(PlanStep step) =>
        step is CallStep { Procedure: "UpdateClosing_Stock_B+NG" } || step is SqlStep s && s.Label.StartsWith("Cost flag", StringComparison.Ordinal);

    // ------------------------------------------------------------------ shared

    public static void RequireMutable(DocumentState state, FirmProfile profile)
    {
        Guard.Supported(state.Kind is not null, "HQ_DOCUMENT_UNSUPPORTED", "Only sales, purchases and orders can be changed here; use Vouchers for receipts/payments");
        var date = DateOnly.FromDateTime((DateTime)state.Header["Doc_Date"]!);
        profile.Firm.RequireInPeriod(date);
        foreach (var column in HeaderMustBeZero)
            if (state.Header.TryGetValue(column, out var v) && v is not null && Convert.ToDecimal(v, CultureInfo.InvariantCulture) != 0)
                throw HqException.Unsupported("HQ_DOCUMENT_UNSUPPORTED", $"This bill uses an HQ feature ({column}) that only HQ can change");
        var mode = Convert.ToInt32(state.Header["Mode"] ?? 0, CultureInfo.InvariantCulture);
        Guard.Supported(mode is 0 or 1 or 2 or 4 or 5 or 6 or 7, "HQ_DOCUMENT_UNSUPPORTED", "Bills with HQ's split 'Others' payment can only be changed in HQ");
        Guard.Supported(state.OtherFeatureRows == 0, "HQ_DOCUMENT_UNSUPPORTED", "This bill has card-split, cheque, serial-number or e-invoice records; change it in HQ");
        foreach (var line in state.Lines)
            foreach (var column in LineMustBeZero)
                if (line.TryGetValue(column, out var v) && v is not null && Convert.ToDecimal(v, CultureInfo.InvariantCulture) != 0)
                    throw HqException.Unsupported("HQ_DOCUMENT_UNSUPPORTED", $"A line uses an HQ feature ({column}) that only HQ can change");
    }

    private static string Combine(string a, string b) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(a + ":" + b))).ToLowerInvariant();
}
