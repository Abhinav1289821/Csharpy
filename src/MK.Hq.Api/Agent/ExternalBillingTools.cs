using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Api.Agent;

/// <summary>The external billing contract: scan, bulk lookup, post.</summary>
public sealed class ExternalBillingTools(WindowsScanner scanner, FirmRegistry firms, Catalogue catalogue, DocumentService documents)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static JsonArray List() => JsonNode.Parse("""
        [
          {
            "name": "scan",
            "description": "Scan one page and return the JPEG image. If several scanners exist, supply scannerName.",
            "inputSchema": { "type": "object", "properties": { "scannerName": { "type": "string" } }, "additionalProperties": false }
          },
          {
            "name": "lookup_items",
            "description": "Look up every written item in one call. Returns candidates with itemId, unitId, rates and GST for each query.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "firm": { "type": "string", "description": "mbm or mp" },
                "queries": { "type": "array", "minItems": 1, "maxItems": 500, "items": { "type": "string", "minLength": 1, "maxLength": 500 } },
                "take": { "type": "integer", "minimum": 1, "maximum": 10, "default": 5 }
              },
              "required": ["firm", "queries"], "additionalProperties": false
            }
          },
          {
            "name": "post_bill",
            "description": "Save a sales estimate, sales invoice, or supplier purchase invoice. For purchases use kind purchase and provide the printed supplier number/date, supplier GSTIN, payment mode, tax basis, and printed total. Returns a saved receipt. Reuse operationId and the identical payload on retry.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "firm": { "type": "string", "description": "mbm or mp" },
                "operationId": { "type": "string", "format": "uuid" },
                "kind": { "type": "string", "enum": ["estimate", "invoice", "purchase"] },
                "date": { "type": "string", "format": "date", "description": "Required for sales. Purchases default to supplier.date; override only on explicit user instruction." },
                "paymentMode": { "type": "integer", "enum": [0, 1, 2, 4, 5, 6, 7], "description": "Purchases require 0 cash or 1 credit. Sales default to cash." },
                "accountId": { "type": "integer", "minimum": 1, "description": "Sales customer/bank ledger. Purchases can resolve the supplier automatically from party.gstin." },
                "supplier": { "type": "object", "description": "Required for purchase. Preserve the printed supplier reference, including leading zeros.", "properties": {
                  "number": { "type": "string", "minLength": 1, "maxLength": 20 }, "date": { "type": "string", "format": "date" }
                }, "required": ["number", "date"], "additionalProperties": false },
                "buyerGstin": { "type": "string", "description": "Printed Bill To GSTIN, when present. It must match the selected firm." },
                "invoiceTotal": { "type": "number", "exclusiveMinimum": 0, "description": "Required for purchase. Printed payable total, including tax, charges and round-off. A mismatch blocks saving." },
                "party": {
                  "type": "object", "properties": {
                    "name": { "type": "string" }, "town": { "type": "string" }, "phone": { "type": "string" },
                    "mobile": { "type": "string" }, "gstin": { "type": "string" }, "gstType": { "type": "integer", "minimum": 0, "maximum": 3 },
                    "state": { "type": "string" }, "address": { "type": "string" }
                  }, "additionalProperties": false
                },
                "interstate": { "type": "boolean", "default": false },
                "basis": { "type": "string", "enum": ["inclusive", "exclusive"] },
                "notes": { "type": "string" },
                "lines": {
                  "type": "array", "minItems": 1, "maxItems": 300,
                  "items": {
                    "type": "object", "properties": {
                      "itemId": { "type": "integer", "minimum": 1 }, "unitId": { "type": "integer", "minimum": 1 },
                      "quantity": { "type": "number", "exclusiveMinimum": 0 }, "rate": { "type": "number", "minimum": 0 },
                      "discountPercent": { "type": "number", "minimum": 0, "maximum": 100 }, "description": { "type": "string" }
                    }, "required": ["itemId", "unitId", "quantity", "rate"], "additionalProperties": false
                  }
                }
              },
              "required": ["firm", "operationId", "kind", "lines"], "additionalProperties": false
            }
          }
        ]
        """)!.AsArray();

    public async Task<JsonObject> CallAsync(string name, JsonObject args, CancellationToken ct)
    {
        try
        {
            switch (name)
            {
                case "scan":
                    var devices = await scanner.ListAsync(ct);
                    var requested = args["scannerName"]?.GetValue<string>();
                    var device = string.IsNullOrWhiteSpace(requested) && devices.Length == 1 ? devices[0]
                        : devices.FirstOrDefault(d => d.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
                    if (device is null) throw new ToolError(devices.Length == 0 ? "No scanner found."
                        : "Specify scannerName: " + string.Join(", ", devices.Select(d => d.Name)));
                    var image = ImageNormalizer.Normalize(await scanner.ScanAsync(device.Id, ct));
                    return new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "image", ["mimeType"] = "image/jpeg", ["data"] = Convert.ToBase64String(image) }),
                        ["isError"] = false,
                    };
                case "lookup_items":
                    var lookup = args.Deserialize<LookupInput>(Json) ?? throw new ToolError("Provide firm and queries.");
                    if (lookup.Queries is null || lookup.Queries.Count is < 1 or > 500
                        || lookup.Queries.Any(q => string.IsNullOrWhiteSpace(q) || q.Length > 500) || lookup.Take is < 1 or > 10)
                        throw new ToolError("Provide 1–500 nonempty queries, each up to 500 characters, and take between 1 and 10.");
                    var firm = firms.Get(lookup.Firm ?? "");
                    var snapshot = await catalogue.GetAsync(firm, ct);
                    return Text(MatchItems(snapshot.Items, lookup.Queries, lookup.Take));
                case "post_bill":
                    var input = ParsePost(args);
                    var target = firms.Get(input.Firm);
                    var buyer = args["buyerGstin"]?.GetValue<string>().Trim().ToUpperInvariant();
                    if (buyer is not null && !string.Equals(buyer, target.Options.Gstin?.Trim(), StringComparison.OrdinalIgnoreCase))
                        throw new ToolError("Printed buyer GSTIN does not match the selected firm. Check Bill To and firm before posting.");
                    var receipt = await documents.PostDirectAsync(target.Id, input.OperationId, input.Document, ct, input.InvoiceTotal);
                    return Text(receipt);
                default: throw new ToolError("Unknown tool.");
            }
        }
        catch (Exception e) when (e is ToolError or HqException or JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return Text(new { error = e is HqException h ? h.Code : "INVALID_ARGUMENTS", message = e.Message, detail = (e as HqException)?.Detail }, true);
        }
    }

    public static object MatchItems(IReadOnlyList<CatalogueItem> items, IReadOnlyList<string> queries, int take)
    {
        var matcher = new ItemMatcher(items);
        var byId = items.GroupBy(i => (i.ItemId, i.UnitId)).ToDictionary(g => g.Key, g => g.First());
        return queries.Select(query =>
        {
            var match = matcher.Match(new ExtractedLine(query, query, null, null, null, null, null, null, null),
                new Dictionary<string, (long, long)>(), take);
            var candidates = match.Candidates.Select(c =>
            {
                var item = byId[(c.ItemId, c.UnitId)];
                return new { item.ItemId, item.UnitId, item.Code, item.Name, item.Unit, item.SaleRate, item.PurchaseRate,
                    item.Hsn, gst = item.Igst, item.Stock, c.Score, c.Notes };
            }).ToArray();
            return new
            {
                query,
                candidates,
                chosen = candidates.FirstOrDefault(c => match.Chosen is { } chosen && c.ItemId == chosen.ItemId && c.UnitId == chosen.UnitId),
                match.Reason,
            };
        }).ToArray();
    }

    public static (string Firm, string OperationId, DocumentRequest Document, decimal? InvoiceTotal) ParsePost(JsonObject args)
    {
        foreach (var line in args["lines"] as JsonArray ?? throw new ToolError("lines is required."))
            if (line is not JsonObject row || row["itemId"] is null || row["unitId"] is null || row["quantity"] is null || row["rate"] is null)
                throw new ToolError("Each line requires itemId, unitId, quantity and rate.");
        var input = args.Deserialize<PostInput>(Json) ?? throw new ToolError("Provide a bill.");
        if (string.IsNullOrWhiteSpace(input.Firm) || !Guid.TryParse(input.OperationId, out _)) throw new ToolError("Provide firm and a GUID operationId.");
        var kind = input.Kind switch
        {
            "estimate" => DocumentKind.SalesOrder, "invoice" => DocumentKind.Sale, "purchase" => DocumentKind.Purchase,
            _ => throw new ToolError("kind must be estimate, invoice or purchase."),
        };
        var date = input.Date;
        var party = input.Party;
        if (kind == DocumentKind.Purchase)
        {
            if (input.Supplier is not { Date: var printedDate, Number: var number } || printedDate == default
                || string.IsNullOrWhiteSpace(number) || number.Trim().Length > 20)
                throw new ToolError("Provide the printed supplier.number (up to 20 characters) and supplier.date.");
            var gstin = party?.Gstin?.Trim().ToUpperInvariant();
            if (!Gstin.IsValid(gstin)) throw new ToolError("Provide the valid printed supplier GSTIN in party.gstin.");
            if (input.PaymentMode is not (0 or 1)) throw new ToolError("Choose purchase paymentMode: 0 cash or 1 credit.");
            if (input.Basis is null || input.InvoiceTotal is not > 0) throw new ToolError("Provide the purchase tax basis and printed invoiceTotal.");
            date ??= printedDate;
            party = new PartySnapshot(Gstin: gstin);
        }
        if (date is null || date == default(DateOnly) || input.Lines is null || input.Lines.Count is < 1 or > 300)
            throw new ToolError("Provide a bill date and 1–300 lines.");
        if (input.Lines.Any(l => l is null || l.ItemId <= 0 || l.UnitId <= 0 || l.Quantity <= 0 || l.Rate < 0 || l.DiscountPercent is < 0 or > 100))
            throw new ToolError("Invalid item, unit, quantity, rate or discount.");
        if (input.Party?.GstType is < 0 or > 3) throw new ToolError("gstType must be 0–3.");
        return (input.Firm, Guid.Parse(input.OperationId).ToString(), new DocumentRequest
        {
            Kind = kind, Date = date.Value, PaymentMode = input.PaymentMode ?? 0, AccountId = input.AccountId, Party = party,
            Interstate = input.Interstate, Basis = input.Basis, Notes = input.Notes, Lines = input.Lines,
            Supplier = kind == DocumentKind.Purchase ? input.Supplier : null,
        }, kind == DocumentKind.Purchase ? input.InvoiceTotal : null);
    }

    private static JsonObject Text(object result, bool error = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = JsonSerializer.Serialize(result, Json) }),
        ["isError"] = error,
    };

    private sealed record LookupInput(string Firm, IReadOnlyList<string> Queries, int Take = 5);
    private sealed record PostInput(string Firm, string OperationId, string Kind, DateOnly? Date, IReadOnlyList<LineRequest> Lines,
        int? PaymentMode = null, long? AccountId = null, PartySnapshot? Party = null, bool Interstate = false, TaxBasis? Basis = null,
        string? Notes = null, SupplierBill? Supplier = null, decimal? InvoiceTotal = null);
}
