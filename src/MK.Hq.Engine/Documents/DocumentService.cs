using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MK.Hq.Domain;
using MK.Hq.Domain.Ledger;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Documents;

/// <summary>Preview → post for sales, purchases, sales orders (estimates) and purchase orders.</summary>
public sealed class DocumentService(
    FirmRegistry firms,
    FirmProfiles profiles,
    DocumentResolver resolver,
    WriteCoordinator coordinator,
    Catalogue catalogue,
    HqOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Prices and validates a bill against live HQ data and returns the exact plan hash to post.</summary>
    public async Task<DocumentPreview> PreviewAsync(string firmId, DocumentRequest request, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var profile = await profiles.GetAsync(firm, ct);
        await using var s = await firm.OpenAsync(ct);
        var resolved = await resolver.ResolveAsync(s, profile, request);
        var plan = DocumentPlanBuilder.Create(resolved, options.HqUserId, options.EffectiveMachineName);
        var next = await s.ScalarAsync<int>("SELECT ISNULL(MAX(TNo),0)+1 FROM dbo.IQDoc_Header WHERE Tt_Id=@tt AND TSeries=@series",
            new { tt = resolved.Type.TtId, series = resolved.Type.Series });
        return await ToPreviewAsync(s, firm, resolved, plan, next);
    }

    public async Task<DocumentReceipt> PostAsync(string firmId, PostRequest post, CancellationToken ct = default)
    {
        var firm = firms.Get(firmId);
        var receipt = await coordinator.RunAsync(new WriteRequest<DocumentReceipt>(
            firm, post.OperationId, "document.create." + post.Document.Kind, RequestHash(post), [],
            async session =>
            {
                // Settings read inside the write transaction: a cached copy could be up to 30 s old, and the plan-hash check
                // could not then notice a settings change (round-off type, charge accounts) made since the review.
                var profile = await FirmProfiles.LoadAsync(firm, session);
                var resolved = await resolver.ResolveAsync(session, profile, post.Document);
                var plan = DocumentPlanBuilder.Create(resolved, options.HqUserId, options.EffectiveMachineName);
                Guard.Require(plan.Hash == post.ExpectedHash, "REVIEW_CHANGED",
                    "Something in HQ changed since this bill was reviewed (rates, accounts, settings). Review it again.", FailureKind.Conflict);
                return new PreparedWrite<DocumentReceipt>(plan, outcome =>
                {
                    var docId = outcome.Long(DocumentPlanBuilder.DocId);
                    var number = (int)outcome.Long(DocumentPlanBuilder.Number);
                    var reference = $"{resolved.Type.Series}/{number}{resolved.Suffix}";
                    return (new DocumentReceipt(post.OperationId, docId, post.Document.Kind, resolved.Type.Series, number, reference, resolved.Quote.Payable, post.Document.Date),
                        new WriteEvidence(WriteEvidence.Create, docId, Reference: reference));
                });
            }), ct);
        catalogue.Invalidate(firm.Id);
        return receipt;
    }

    internal static async Task<DocumentPreview> ToPreviewAsync(HqSession s, HqFirm firm, ResolvedDocument d, WritePlan plan, int nextNumber)
    {
        var accountIds = d.Ledger.Select(l => l.AccountId).Distinct().ToArray();
        var names = accountIds.Length == 0
            ? new Dictionary<long, string>()
            : (await s.QueryAsync<(long Id, string Name)>("SELECT CAST(Act_Id AS bigint), LTRIM(RTRIM(Act_Name)) FROM dbo.IQAccmst WHERE Act_Id IN @ids", new { ids = accountIds }))
                .ToDictionary(x => x.Id, x => x.Name);
        var modeCaption = d.Kind.IsOrder() ? "Order" : d.Profile.PaymentModes.ElementAtOrDefault(d.Mode)?.Caption ?? d.Mode.ToString();
        return new DocumentPreview(
            plan.Hash, firm.Id, d.Kind, d.Type.TtId, d.Type.Series, d.Type.Name, nextNumber, d.Request.Date, d.DueDate, d.Mode, modeCaption,
            d.Snapshot, d.Quote,
            d.Items.Select((m, i) => new PreviewLine(i + 1, m.Item.ItemId, m.Item.UnitId, m.Item.Code, m.Item.Name, m.Item.Unit, m.Item.Hsn,
                d.Request.Lines[i].Description ?? m.ItemDescription, d.Quote.Lines[i], m.Item.Stock)).ToList(),
            d.Ledger.Select(l => new PreviewLedgerRow(l.AccountId, names.GetValueOrDefault(l.AccountId, "#" + l.AccountId), l.Type, l.ContraId, l.Amount)).ToList(),
            d.Warnings,
            plan.Steps.Select(st => st.Label).ToList());
    }

    internal static string RequestHash(object request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json)))).ToLowerInvariant();
}
