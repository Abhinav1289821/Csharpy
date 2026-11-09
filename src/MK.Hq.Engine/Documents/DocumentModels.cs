using MK.Hq.Domain.Gst;
using MK.Hq.Domain.Ledger;

namespace MK.Hq.Engine.Documents;

public enum DocumentKind { Sale, Purchase, SalesOrder, PurchaseOrder }

public static class DocumentKinds
{
    public static bool IsOrder(this DocumentKind kind) => kind is DocumentKind.SalesOrder or DocumentKind.PurchaseOrder;
    public static bool IsInward(this DocumentKind kind) => kind is DocumentKind.Purchase or DocumentKind.PurchaseOrder;

    public static int TtId(this DocumentKind kind, bool interstate) => kind switch
    {
        DocumentKind.Sale => interstate ? 507 : 506,
        DocumentKind.Purchase => interstate ? 501 : 500,
        DocumentKind.SalesOrder => 249,
        DocumentKind.PurchaseOrder => 248,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string VouchType(this DocumentKind kind) => kind switch
    {
        DocumentKind.Sale => "Sales",
        DocumentKind.Purchase => "Purchase",
        DocumentKind.SalesOrder => "Sales Order",
        _ => "Purchase Order",
    };

    public static DocumentKind? FromTt(int ttId) => ttId switch
    {
        506 or 507 => DocumentKind.Sale,
        500 or 501 => DocumentKind.Purchase,
        249 => DocumentKind.SalesOrder,
        248 => DocumentKind.PurchaseOrder,
        _ => null,
    };
}

public sealed record LineRequest(long ItemId, long UnitId, decimal Quantity, decimal Rate, decimal DiscountPercent = 0, string? Description = null, long? OrderLineId = null);

/// <summary>The party snapshot printed on the bill (HQ's IQCash_TRAN). For credit bills HQ re-reads these
/// from the account, so only cash and bank bills may differ from the master.</summary>
public sealed record PartySnapshot(string? Name = null, string? Town = null, string? Phone = null, string? Mobile = null,
    string? Gstin = null, int? GstType = null, string? State = null, string? Address = null);

public sealed record SupplierBill(string Number, DateOnly Date);

public sealed record DocumentRequest
{
    public required DocumentKind Kind { get; init; }
    public required DateOnly Date { get; init; }
    /// <summary>HQ Payment_Modes position: 0 cash, 1 credit, 2 card, 4 PhonePe, 5 Google Pay, 6 Paytm, 7 others1.</summary>
    public int PaymentMode { get; init; }
    /// <summary>Credit: the customer/supplier. Bank modes: the bank ledger. Cash: optional known customer.</summary>
    public long? AccountId { get; init; }
    public PartySnapshot? Party { get; init; }
    public bool Interstate { get; init; }
    /// <summary>Overrides the transaction type's default (inclusive/exclusive) for this bill.</summary>
    public TaxBasis? Basis { get; init; }
    public DateOnly? DueDate { get; init; }
    public string? Notes { get; init; }
    public string? Message { get; init; }
    public int? SalespersonId { get; init; }
    public string? NumberSuffix { get; init; }
    public SupplierBill? Supplier { get; init; }
    public long? SourceOrderId { get; init; }
    public required IReadOnlyList<LineRequest> Lines { get; init; }
}

public sealed record PreviewLine(int Number, long ItemId, long UnitId, string Code, string Name, string Unit, string Hsn, string Description, LineQuote Quote, decimal Stock);

public sealed record PreviewParty(long AccountId, long HeadAccountId, string Name, string Town, string Phone, string Gstin, int GstType, string State, string Address);

public sealed record PreviewLedgerRow(long AccountId, string AccountName, char Type, long ContraId, decimal Amount);

public sealed record DocumentPreview(
    string Hash,
    string FirmId,
    DocumentKind Kind,
    int TtId,
    string Series,
    string TypeName,
    int NextNumberHint,
    DateOnly Date,
    DateOnly DueDate,
    int PaymentMode,
    string PaymentCaption,
    PreviewParty Party,
    DocumentQuote Quote,
    IReadOnlyList<PreviewLine> Lines,
    IReadOnlyList<PreviewLedgerRow> Ledger,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> PlanSteps);

public sealed record DocumentReceipt(string OperationId, long DocumentId, DocumentKind Kind, string Series, int Number, string Reference, decimal Total, DateOnly Date);

public sealed record PostRequest(string OperationId, string ExpectedHash, DocumentRequest Document);

public sealed record AmendRequest(string OperationId, long DocumentId, string ExpectedHash, DocumentRequest Document, IReadOnlyList<long?> LineIds,
    AllocationDisposition Allocations = AllocationDisposition.Preserve);

public sealed record DeleteRequest(string OperationId, long DocumentId, string ExpectedHash);

public sealed record MutationReceipt(string OperationId, long DocumentId, string Reference, string Action, decimal? Total, decimal ReleasedAllocations);
