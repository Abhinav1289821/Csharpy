using System.Data;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Journal;
using MK.Hq.Engine.Reports;

namespace MK.Hq.Api.Agent;

/// <summary>A tool failure the agent should read and act on (not a server fault).</summary>
public sealed class ToolError(string message) : Exception(message);

/// <summary>
/// MK's tools for the Antigravity agent, served as an MCP server at /mcp. They read HQ (through the same services the
/// screens use, or a guarded read-only query), move the user's screen, and prepare bill drafts. None of them writes to
/// HQ: a prepared bill opens in the normal editor and is saved only when the user presses Save.
/// </summary>
public sealed partial class AgentTools(
    FirmRegistry firms,
    Catalogue catalogue,
    FirmProfiles profiles,
    ReportService reports,
    DocumentService documents,
    EngineStore store,
    ILogger<AgentTools> log)
{
    public const string ServerName = "mk";

    public const string Instructions = """
        You are the assistant inside MK Billing, the billing program of two shops in Peddapuram owned by one family:
        MK Building Materials (firm id "mbm": pipes, fittings, sanitary, hardware) and MK Paints (firm id "mp").
        MK Billing writes into HQ Business+NG's accounting database. The person typing is the owner or shop staff.

        How to work:
        - Reply in the user's language (English, Telugu or a mix), short and plain. Amounts like ₹1,23,456.00 (Indian grouping). Dates as 11-11-2026.
        - Use ONLY the mk tools. Never run commands, read or write files, browse or search the web.
        - Each message starts with <context> saying which firm and screen the user has open. Use that firm unless the user names the other shop.
        - Never guess ids, names, rates or balances: look them up (find_parties, find_items) first. If several match, ask with a short numbered list.
        - Show, don't just tell: after answering about a party, bill, ledger or report, call open_screen so the user sees it.
        - You cannot save anything to HQ. To make a bill, estimate, purchase or order, call prepare_bill: it opens a draft on screen.
          Then tell the user to check it and press F10 (Save). Never claim a bill is saved.
          Use the item's own rate unless the user gives one. For purchases you need the supplier's bill number.
        - Receipts, payments, item or party changes and deletions: open the right screen with open_screen and tell the user what to enter.
        - db_query is a read-only SQL Server SELECT for questions the other tools cannot answer; call db_tables first to learn columns.
          Main HQ tables: IQDoc_Header (bills: Doc_Id, Tt_Id, TSeries, TNo, Doc_Date, Bill_Amt, Mode 0 cash/1 credit),
          IQDayBook (ledger rows: Doc_ID, Act_ID, TYPE 'D'/'P' debit else credit, AMOUNT), IQAccmst (accounts: Act_Id, Act_Name, Act_Type),
          IQAccAdr (addresses, GSTIN), IQItems_Mast (items: Item_ID, Item_Name, Stk, HSNCODE, CGST, SGST), IQItems_Units (rates).
          Tt_Id: 506/507 sale, 500/501 purchase, 249 estimate/sales order, 248 purchase order.
        - If a tool returns an error, explain it plainly; do not work around business rules (firm, payment mode, tax).
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record Tool(string Name, string Description, JsonObject Schema, Func<AgentChat, JsonObject, CancellationToken, Task<object?>> Run);

    private IReadOnlyList<Tool>? _tools;
    private IReadOnlyList<Tool> Tools => _tools ??= Build();

    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        "list_firms", "find_parties", "find_items", "party_ledger", "outstanding", "list_bills", "get_bill", "day_summary", "day_book",
        "stock", "gst_summary", "db_tables", "db_query", "current_screen", "open_screen", "prepare_bill",
    };

    public JsonArray List() => new(Tools.Select(t => (JsonNode?)new JsonObject
    {
        ["name"] = t.Name, ["description"] = t.Description, ["inputSchema"] = t.Schema.DeepClone(),
    }).ToArray());

    public async Task<JsonObject> CallAsync(AgentChat chat, string name, JsonObject args, CancellationToken ct)
    {
        var tool = Tools.FirstOrDefault(t => t.Name == name);
        string text;
        var isError = false;
        log.LogInformation("Agent tool {Tool} {Args}", name, args.ToJsonString());
        try
        {
            if (tool is null) throw new ToolError($"Unknown tool {name}");
            var result = await tool.Run(chat, args, ct);
            text = result as string ?? JsonSerializer.Serialize(result, Json);
            chat.Push(new JsonObject { ["type"] = "tool", ["name"] = name, ["ok"] = true });
        }
        catch (Exception e) when (e is ToolError or HqException or SqlException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            isError = true;
            text = e is HqException h ? $"{h.Code}: {h.Message}" : e.Message;
            chat.Push(new JsonObject { ["type"] = "tool", ["name"] = name, ["ok"] = false, ["message"] = text });
            log.LogInformation("Agent tool {Tool} failed: {Message}", name, text);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Anything else is a bug in a tool (or a timeout inside one). The agent still gets an ordinary tool error to
            // explain, instead of an HTTP 500 that the MCP client reports as "the mk server is broken" for the whole chat.
            isError = true;
            text = "MK Billing could not run this tool (an internal error; details are in MK's log). Tell the user, and do not retry it in a loop.";
            chat.Push(new JsonObject { ["type"] = "tool", ["name"] = name, ["ok"] = false, ["message"] = text });
            log.LogError(e, "Agent tool {Tool} crashed", name);
        }
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = isError,
        };
    }

    // ------------------------------------------------------------------ schema helpers
    private static JsonObject Schema(params (string Name, string Type, string Description, bool Required)[] props)
    {
        var p = new JsonObject();
        foreach (var (n, t, d, _) in props)
            p[n] = t switch
            {
                "lines" => new JsonObject
                {
                    ["type"] = "array", ["description"] = d,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["itemId"] = new JsonObject { ["type"] = "integer", ["description"] = "from find_items" },
                            ["unitId"] = new JsonObject { ["type"] = "integer", ["description"] = "from find_items (optional when the item has one unit)" },
                            ["quantity"] = new JsonObject { ["type"] = "number" },
                            ["rate"] = new JsonObject { ["type"] = "number", ["description"] = "omit to use the item's own rate" },
                            ["discountPercent"] = new JsonObject { ["type"] = "number" },
                            ["description"] = new JsonObject { ["type"] = "string", ["description"] = "optional text printed instead of the item name" },
                        },
                        ["required"] = new JsonArray("itemId", "quantity"),
                    },
                },
                _ when t.StartsWith("enum:", StringComparison.Ordinal) => new JsonObject
                {
                    ["type"] = "string", ["description"] = d,
                    ["enum"] = new JsonArray(t[5..].Split('|').Select(x => (JsonNode?)x).ToArray()),
                },
                _ => new JsonObject { ["type"] = t, ["description"] = d },
            };
        return new JsonObject
        {
            ["type"] = "object", ["properties"] = p,
            ["required"] = new JsonArray(props.Where(x => x.Required).Select(x => (JsonNode?)x.Name).ToArray()),
        };
    }

    private const string FirmHelp = "mbm (MK Building Materials) or mp (MK Paints); default: the firm open on screen";
    private static (string, string, string, bool) FirmArg => ("firm", "string", FirmHelp, false);

    private static string? Str(JsonObject a, string k) => a[k] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    private static long? Long(JsonObject a, string k) => a[k] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<double>(out var d) => (long)d,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, out var p) => p,
        _ => null,
    };
    private static decimal? Dec(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<decimal>(out var d) => d,
        JsonValue v when v.TryGetValue<double>(out var f) => (decimal)f,
        JsonValue v when v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };
    private static int Int(JsonObject a, string k, int fallback, int max) => (int)Math.Clamp(Long(a, k) ?? fallback, 1, max);
    private static DateOnly? Date(JsonObject a, string k) =>
        Str(a, k) is { } s ? DateOnly.TryParseExact(s, ["yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "d-M-yyyy", "d/M/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : throw new ToolError($"{k}: write dates as yyyy-mm-dd") : null;
    private static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    private HqFirm Firm(AgentChat chat, JsonObject a)
    {
        var wanted = Str(a, "firm") ?? chat.Screen.Firm;
        if (wanted is null) return firms.All.First();
        var hit = firms.All.FirstOrDefault(f => f.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                  ?? firms.All.FirstOrDefault(f => f.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                  ?? (wanted.Contains("paint", StringComparison.OrdinalIgnoreCase) ? firms.All.FirstOrDefault(f => f.Id == "mp")
                      : wanted.Contains("build", StringComparison.OrdinalIgnoreCase) ? firms.All.FirstOrDefault(f => f.Id == "mbm") : null);
        return hit ?? throw new ToolError($"No firm '{wanted}'. Firms: {string.Join(", ", firms.All.Select(f => $"{f.Id} ({f.Name})"))}");
    }

    /// <summary>Every word must appear somewhere; names starting with the first word come first.</summary>
    private static IEnumerable<T> Search<T>(IEnumerable<T> rows, string? query, Func<T, string> name, Func<T, string> rest)
    {
        if (string.IsNullOrWhiteSpace(query)) return rows;
        var words = Regex.Split(query.ToUpperInvariant(), @"[\s,]+").Where(w => w.Length > 0).ToArray();
        return rows
            .Select(r => (r, n: name(r).ToUpperInvariant(), all: (name(r) + " " + rest(r)).ToUpperInvariant()))
            .Where(x => words.All(w => x.all.Contains(w, StringComparison.Ordinal)))
            .OrderBy(x => x.n.StartsWith(words[0], StringComparison.Ordinal) ? 0 : x.n.Contains(words[0], StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(x => x.n.Length)
            .Select(x => x.r);
    }

    // ------------------------------------------------------------------ the tools
    private List<Tool> Build() =>
    [
        new("list_firms", "The shop's firms (HQ databases) and their financial years.", Schema(), async (_, _, _) =>
            await Task.FromResult<object?>(firms.All.Select(f => new { f.Id, f.Name, f.PeriodStart, f.PeriodEnd }))),

        new("find_parties", "Find customers, suppliers or banks by name, town, phone, GSTIN or code. Balance > 0: they owe the shop; < 0: the shop owes them.",
            Schema(FirmArg, ("query", "string", "words to match; empty lists the largest balances", false),
                ("type", "enum:any|customer|supplier|bank", "filter", false), ("take", "integer", "max rows (default 15)", false)),
            async (chat, a, ct) =>
            {
                var snap = await catalogue.GetAsync(Firm(chat, a), ct);
                var type = Str(a, "type") ?? "any";
                var rows = snap.Parties.Where(p => p.Active && type switch { "customer" => p.IsCustomer, "supplier" => p.IsSupplier, "bank" => p.IsBank, _ => true });
                var q = Str(a, "query");
                var found = q is null ? rows.OrderByDescending(p => Math.Abs(p.Balance)) : Search(rows, q, p => p.Name, p => $"{p.Code} {p.Town} {p.Mobile} {p.Phone} {p.Gstin}");
                return found.Take(Int(a, "take", 15, 50)).Select(p => new { p.AccountId, p.Name, p.Type, p.Town, p.Mobile, p.Gstin, p.Balance, p.BillWise });
            }),

        new("find_items", "Find items by name, code, group or HSN. Rates are per unit; gst is the total GST %. Stock is HQ's current stock.",
            Schema(FirmArg, ("query", "string", "words to match, e.g. '1 inch elbow' or 'asian royale 4 ltr'", true), ("take", "integer", "max rows (default 15)", false),
                ("includeInactive", "boolean", "also items marked not in use", false)),
            async (chat, a, ct) =>
            {
                var snap = await catalogue.GetAsync(Firm(chat, a), ct);
                var all = a["includeInactive"]?.GetValueKind() == JsonValueKind.True;
                return Search(snap.Items.Where(i => all || i.Active), Str(a, "query"), i => i.Name, i => $"{i.Code} {i.Group} {i.Category} {i.Hsn}")
                    .Take(Int(a, "take", 15, 50))
                    .Select(i => new { i.ItemId, i.UnitId, i.Name, i.Code, i.Unit, i.SaleRate, i.PurchaseRate, i.Mrp, gst = i.Igst, i.Hsn, i.Stock, i.Group, i.Active });
            }),

        new("party_ledger", "An account's ledger: opening, totals, closing and the latest entries.",
            Schema(FirmArg, ("accountId", "integer", "from find_parties", true), ("from", "string", "yyyy-mm-dd (default: start of the year)", false),
                ("to", "string", "yyyy-mm-dd (default: today)", false), ("take", "integer", "latest entries to return (default 30)", false)),
            async (chat, a, ct) =>
            {
                var f = Firm(chat, a);
                var r = await reports.LedgerAsync(f.Id, Long(a, "accountId") ?? throw new ToolError("accountId is required"), Date(a, "from"), Date(a, "to"), ct);
                var take = Int(a, "take", 30, 200);
                return new { r.AccountId, r.AccountName, r.AccountType, r.From, r.To, r.Opening, r.Debit, r.Credit, r.Closing, entries = r.Lines.Count,
                    latest = r.Lines.Skip(Math.Max(0, r.Lines.Count - take)) };
            }),

        new("outstanding", "Who owes the shop (receivable) or whom the shop owes (payable), with ageing in days.",
            Schema(FirmArg, ("side", "enum:receivable|payable", "default receivable", false), ("take", "integer", "max rows (default 25)", false)),
            async (chat, a, ct) =>
            {
                var rows = await reports.OutstandingAsync(Firm(chat, a).Id, Str(a, "side") != "payable", ct);
                return new { parties = rows.Count, total = rows.Sum(r => r.Balance), top = rows.OrderByDescending(r => Math.Abs(r.Balance)).Take(Int(a, "take", 25, 200)) };
            }),

        new("list_bills", "List bills (sales, purchases, estimates, orders) newest first.",
            Schema(FirmArg, ("kind", "enum:any|sale|purchase|estimate|purchaseOrder", "default any", false),
                ("from", "string", "yyyy-mm-dd", false), ("to", "string", "yyyy-mm-dd", false), ("accountId", "integer", "only this party", false),
                ("search", "string", "bill number or party name", false), ("take", "integer", "max rows (default 25)", false)),
            async (chat, a, ct) =>
            {
                var f = Firm(chat, a);
                await using var s = await f.OpenAsync(ct);
                List<int>? tts = Str(a, "kind") switch
                {
                    "sale" => [506, 507], "purchase" => [500, 501], "estimate" => [249], "purchaseOrder" => [248], _ => [506, 507, 500, 501, 249, 248],
                };
                return await DocumentReader.ListAsync(s, new DocumentFilter
                {
                    TtIds = tts, From = Date(a, "from"), To = Date(a, "to"), AccountId = Long(a, "accountId"), Search = Str(a, "search"), Take = Int(a, "take", 25, 100),
                });
            }),

        new("get_bill", "One bill with its lines, taxes and ledger.", Schema(FirmArg, ("documentId", "integer", "from list_bills", true)),
            async (chat, a, ct) =>
            {
                var f = Firm(chat, a);
                var profile = await profiles.GetAsync(f, ct);
                await using var s = await f.OpenAsync(ct);
                return await DocumentReader.ViewAsync(s, profile, Long(a, "documentId") ?? throw new ToolError("documentId is required"))
                       ?? throw new ToolError("That document is not in HQ (it may have been deleted)");
            }),

        new("day_summary", "The day's figures: sales, bills, cash, receipts, payments.", Schema(FirmArg, ("day", "string", "yyyy-mm-dd (default today)", false)),
            async (chat, a, ct) => await reports.DashboardAsync(Firm(chat, a).Id, Date(a, "day") ?? Today, ct)),

        new("day_book", "Every bill and voucher between two dates.",
            Schema(FirmArg, ("from", "string", "yyyy-mm-dd (default today)", false), ("to", "string", "yyyy-mm-dd (default = from)", false), ("take", "integer", "max rows (default 60)", false)),
            async (chat, a, ct) =>
            {
                var from = Date(a, "from") ?? Today;
                var rows = await reports.DayBookAsync(Firm(chat, a).Id, from, Date(a, "to") ?? from, ct);
                return new { count = rows.Count, total = rows.Sum(r => r.Amount), rows = rows.Take(Int(a, "take", 60, 300)) };
            }),

        new("stock", "Stock on hand with value and last sale date.",
            Schema(FirmArg, ("query", "string", "words to match (optional)", false), ("only", "enum:all|negative|zero|positive", "default all", false),
                ("sort", "enum:lowest|highest|value|name|lastSale", "default: lowest stock first for negative, else name", false), ("take", "integer", "max rows (default 30)", false)),
            async (chat, a, ct) =>
            {
                var rows = await reports.StockAsync(Firm(chat, a).Id, ct);
                IEnumerable<StockRow> r = Str(a, "only") switch
                {
                    "negative" => rows.Where(x => x.Stock < 0), "zero" => rows.Where(x => x.Stock == 0), "positive" => rows.Where(x => x.Stock > 0), _ => rows,
                };
                var list = Search(r, Str(a, "query"), x => x.Name, x => $"{x.Code} {x.Group}").ToList();
                list = (Str(a, "sort") ?? (Str(a, "only") == "negative" ? "lowest" : null)) switch
                {
                    "lowest" => list.OrderBy(x => x.Stock).ToList(), "highest" => list.OrderByDescending(x => x.Stock).ToList(),
                    "value" => list.OrderByDescending(x => Math.Abs(x.Value)).ToList(), "name" => list.OrderBy(x => x.Name).ToList(),
                    "lastSale" => list.OrderByDescending(x => x.LastSale).ToList(), _ => list,
                };
                return new { items = list.Count, value = list.Sum(x => x.Value), rows = list.Take(Int(a, "take", 30, 200)) };
            }),

        new("gst_summary", "Sales or purchase register totals for a period: GST by rate (B2B/B2C) and HSN summary.",
            Schema(FirmArg, ("kind", "enum:sales|purchases", "default sales", false), ("from", "string", "yyyy-mm-dd (default 1st of this month)", false), ("to", "string", "yyyy-mm-dd (default today)", false)),
            async (chat, a, ct) =>
            {
                var r = await reports.RegisterAsync(Firm(chat, a).Id, Str(a, "kind") != "purchases", Date(a, "from") ?? new DateOnly(Today.Year, Today.Month, 1), Date(a, "to") ?? Today, ct);
                return new { r.Kind, r.From, r.To, bills = r.Rows.Count, taxable = r.Rows.Sum(x => x.Taxable), cgst = r.Rows.Sum(x => x.Cgst), sgst = r.Rows.Sum(x => x.Sgst),
                    igst = r.Rows.Sum(x => x.Igst), total = r.Rows.Sum(x => x.Total), r.Slabs, hsn = r.Hsn.Take(60) };
            }),

        new("db_tables", "Columns of HQ tables whose name contains the filter (e.g. 'IQDoc', 'Items').",
            Schema(FirmArg, ("filter", "string", "part of a table name", true)),
            async (chat, a, ct) =>
            {
                await using var s = await Firm(chat, a).OpenAsync(ct);
                var rows = await s.QueryAsync<(string Table, string Column, string Type)>(
                    "SELECT TOP 400 TABLE_NAME, COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME LIKE @f ORDER BY TABLE_NAME, ORDINAL_POSITION",
                    new { f = "%" + (Str(a, "filter") ?? "") + "%" });
                return rows.GroupBy(r => r.Table).Take(25).ToDictionary(g => g.Key, g => string.Join(", ", g.Select(r => $"{r.Column} {r.Type}")));
            }),

        new("db_query", "Run ONE read-only SELECT (or WITH … SELECT) on HQ's SQL Server database. Max 200 rows, 20 seconds. No writes, no semicolons.",
            Schema(FirmArg, ("sql", "string", "the SELECT statement", true)),
            async (chat, a, ct) => await QueryAsync(Firm(chat, a), Str(a, "sql") ?? throw new ToolError("sql is required"), ct)),

        new("current_screen", "What the user has open in MK Billing right now.", Schema(), (chat, _, _) =>
            Task.FromResult<object?>(new { chat.Screen.Firm, screen = chat.Screen.Title, route = chat.Screen.Hash })),

        new("open_screen", "Show a screen to the user (it changes what they see). Use after answering, and to take them where a task is done.",
            Schema(FirmArg,
                ("screen", "enum:dashboard|sale|estimate|purchase|purchaseOrder|receipt|bankReceipt|payment|bankPayment|creditNote|debitNote|bills|bill|ledger|outstanding|reports|stock|drafts|draft|items|item|newItem|parties|party|newCustomer|newSupplier|scan|health|settings",
                    "bill/ledger/item/party/draft need id", true),
                ("id", "string", "documentId, accountId, itemId or draft id", false), ("side", "enum:receivable|payable", "for outstanding", false)),
            (chat, a, _) =>
            {
                var f = Firm(chat, a);
                var id = Str(a, "id") ?? Long(a, "id")?.ToString(CultureInfo.InvariantCulture);
                string Need() => id ?? throw new ToolError("This screen needs an id");
                var hash = Str(a, "screen") switch
                {
                    "dashboard" => "#/", "sale" => "#/new/sale", "estimate" => "#/new/salesOrder", "purchase" => "#/new/purchase", "purchaseOrder" => "#/new/purchaseOrder",
                    "receipt" => "#/voucher/cashReceipt", "bankReceipt" => "#/voucher/bankReceipt", "payment" => "#/voucher/cashPayment", "bankPayment" => "#/voucher/bankPayment",
                    "creditNote" => "#/voucher/creditNote", "debitNote" => "#/voucher/debitNote",
                    "bills" => "#/documents", "bill" => "#/doc/" + Need(), "ledger" => id is null ? "#/ledger" : "#/ledger/" + id,
                    "outstanding" => "#/outstanding/" + (Str(a, "side") ?? "receivable"), "reports" => "#/reports", "stock" => "#/stock",
                    "drafts" => "#/drafts", "draft" => "#/draft/" + Need(), "items" => "#/items", "item" => "#/items/" + Need(), "newItem" => "#/items/new",
                    "parties" => "#/accounts", "party" => "#/accounts/" + Need(), "newCustomer" => "#/accounts/new/Customer", "newSupplier" => "#/accounts/new/Supplier",
                    "scan" => "#/capture", "health" => "#/health", "settings" => "#/settings",
                    var other => throw new ToolError($"Unknown screen {other}"),
                };
                if (id is not null && !Regex.IsMatch(id, "^[A-Za-z0-9-]{1,40}$")) throw new ToolError("Bad id");
                Navigate(chat, f.Id, hash);
                return Task.FromResult<object?>($"Opened {hash} for {f.Name}.");
            }),

        new("prepare_bill", "Make a DRAFT sale bill, estimate, purchase or purchase order and open it on screen for the user to check and save (F10). Nothing is saved to HQ. Returns HQ's own priced preview (totals, GST) and any warnings.",
            Schema(FirmArg,
                ("kind", "enum:sale|estimate|purchase|purchaseOrder", "what to make", true),
                ("accountId", "integer", "the customer/supplier from find_parties (required for credit)", false),
                ("customerName", "string", "for a walk-in cash customer without an account", false),
                ("paymentMode", "enum:cash|credit|card|phonepe|gpay|paytm", "default: cash for sales, credit for purchases", false),
                ("date", "string", "yyyy-mm-dd (default today)", false),
                ("supplierBillNumber", "string", "purchases: the supplier's invoice number", false),
                ("supplierBillDate", "string", "purchases: yyyy-mm-dd", false),
                ("notes", "string", "narration", false),
                ("lines", "lines", "the bill lines", true)),
            PrepareBillAsync),
    ];

    private void Navigate(AgentChat chat, string firm, string hash)
    {
        chat.Screen = chat.Screen with { Firm = firm, Hash = hash };
        chat.Push(new JsonObject { ["type"] = "ui", ["action"] = "navigate", ["firm"] = firm, ["hash"] = hash });
    }

    private async Task<object?> PrepareBillAsync(AgentChat chat, JsonObject a, CancellationToken ct)
    {
        var f = Firm(chat, a);
        var snap = await catalogue.GetAsync(f, ct);
        var (kind, editorKind) = Str(a, "kind") switch
        {
            "sale" => (DocumentKind.Sale, "sale"), "estimate" => (DocumentKind.SalesOrder, "salesOrder"),
            "purchase" => (DocumentKind.Purchase, "purchase"), "purchaseOrder" => (DocumentKind.PurchaseOrder, "purchaseOrder"),
            _ => throw new ToolError("kind must be sale, estimate, purchase or purchaseOrder"),
        };
        var inward = kind.IsInward();
        var mode = kind.IsOrder() ? 0 : Str(a, "paymentMode") switch
        {
            null => inward ? 1 : 0, "cash" => 0, "credit" => 1, "card" => 2, "phonepe" => 4, "gpay" => 5, "paytm" => 6,
            var other => throw new ToolError($"Unknown payment mode {other}"),
        };
        var accountId = Long(a, "accountId");
        CatalogueParty? party = null;
        if (accountId is { } aid) party = snap.Parties.FirstOrDefault(p => p.AccountId == aid) ?? throw new ToolError($"No account {aid}; use find_parties");
        if (mode == 1 && party is null) throw new ToolError("A credit bill needs accountId (find_parties)");
        var date = Date(a, "date") ?? Today;

        var lines = new List<(CatalogueItem Item, decimal Qty, decimal Rate, decimal Disc, string? Description)>();
        foreach (var node in a["lines"] as JsonArray ?? throw new ToolError("lines is required"))
        {
            if (node is not JsonObject l) continue;
            var itemId = Long(l, "itemId") ?? throw new ToolError("Each line needs itemId (find_items)");
            var unitId = Long(l, "unitId");
            var item = snap.Items.FirstOrDefault(i => i.ItemId == itemId && (unitId is null || i.UnitId == unitId))
                       ?? throw new ToolError($"No item {itemId}{(unitId is null ? "" : "/" + unitId)}; use find_items");
            var qty = Dec(l["quantity"]) ?? throw new ToolError($"Line for {item.Name}: quantity is required");
            if (qty <= 0) throw new ToolError($"Line for {item.Name}: quantity must be more than 0");
            lines.Add((item, qty, Dec(l["rate"]) ?? (inward ? item.PurchaseRate : item.SaleRate), Dec(l["discountPercent"]) ?? 0, Str(l, "description")));
        }
        if (lines.Count == 0) throw new ToolError("Give at least one line");

        var name = party?.Name ?? Str(a, "customerName") ?? "";
        var supplierNo = Str(a, "supplierBillNumber") ?? "";
        var supplierDate = Date(a, "supplierBillDate") ?? date;
        var iso = (DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string Plain(decimal d) => d.ToString("0.####", CultureInfo.InvariantCulture);

        // The draft has exactly the bill editor's shape (as photo drafts do), so it opens like any other draft.
        var payload = new
        {
            kind = editorKind, date = iso(date), paymentMode = mode, accountId = party?.AccountId,
            party = new { name, town = party?.Town ?? "", phone = party?.Mobile is { Length: > 0 } m ? m : party?.Phone ?? "", mobile = "", gstin = party?.Gstin ?? "", address = party?.Address ?? "" },
            interstate = false, basis = (string?)null, dueDate = (string?)null, notes = Str(a, "notes") ?? "", message = (string?)null, salespersonId = (int?)null,
            numberSuffix = "", supplier = new { number = supplierNo, date = iso(supplierDate) }, sourceOrderId = (long?)null,
            lines = lines.Select(l => new
            {
                itemId = l.Item.ItemId, unitId = l.Item.UnitId, name = l.Item.Name, quantity = Plain(l.Qty), rate = Plain(l.Rate),
                discountPercent = l.Disc > 0 ? Plain(l.Disc) : "", description = l.Description, lineId = (long?)null, orderLineId = (long?)null,
            }),
            assistant = new { chat = chat.Id },
        };

        object? preview = null;
        string? problem = null;
        try
        {
            var p = await documents.PreviewAsync(f.Id, new DocumentRequest
            {
                Kind = kind, Date = date, PaymentMode = mode, AccountId = party?.AccountId,
                Party = mode == 1 ? null : new PartySnapshot(Name: name, Town: party?.Town, Phone: party?.Phone, Mobile: party?.Mobile,
                    Gstin: mode > 1 || string.IsNullOrEmpty(party?.Gstin) ? null : party!.Gstin, GstType: mode > 1 || string.IsNullOrEmpty(party?.Gstin) ? null : 0, Address: party?.Address),
                Notes = Str(a, "notes"),
                Supplier = kind == DocumentKind.Purchase ? new SupplierBill(supplierNo, supplierDate) : null,
                Lines = lines.Select(l => new LineRequest(l.Item.ItemId, l.Item.UnitId, l.Qty, l.Rate, l.Disc, l.Description)).ToList(),
            }, ct);
            preview = new { series = p.Series, nextNumber = p.NextNumberHint, type = p.TypeName, payment = p.PaymentCaption, party = p.Party.Name, quote = p.Quote,
                lines = p.Lines.Select(x => new { x.Name, x.Unit, x.Stock, x.Quote }), p.Warnings };
        }
        catch (HqException e) { problem = $"{e.Code}: {e.Message}"; }

        var id = Guid.NewGuid().ToString();
        var label = kind switch { DocumentKind.Sale => "Sale", DocumentKind.SalesOrder => "Estimate", DocumentKind.Purchase => "Purchase", _ => "Purchase order" };
        store.SaveDraft(id, f.Id, editorKind, $"Assistant · {label} · {(name.Length > 0 ? name : "cash")} · {lines.Count} lines", JsonSerializer.Serialize(payload, Json), 0);
        Navigate(chat, f.Id, "#/draft/" + id);
        return new
        {
            draftId = id, opened = true, saved = false,
            note = "Draft opened on screen. The user must check it and press F10 to save it into HQ.",
            preview, problem = problem is null ? null : "HQ cannot save it as it is: " + problem + ". Tell the user what to fix (they can fix it on screen).",
        };
    }

    // ------------------------------------------------------------------ read-only SQL
    // Strings, quoted identifiers and comments are recognised in ONE left-to-right pass, the way SQL Server reads them.
    // Stripping comments first let "SELECT '--'; COMMIT; UPDATE ..." hide everything after the quote from the checks
    // below while SQL Server ran all of it. The pass must end every token exactly where SQL Server does, or text the
    // guard thinks is a comment or a string runs as code. Two such gaps were found in a regex version (both checked on
    // this SQL Server): a "--" comment also ends at a lone carriage return, and /* comments nest (a quote inside one is
    // not a string). Anything whose reading is doubtful (nesting, stray control characters, an unclosed quote or
    // comment) is refused outright rather than guessed at.
    [GeneratedRegex(@"\b(insert|update|delete|merge|drop|alter|create|truncate|exec|execute|grant|revoke|deny|into|openrowset|opendatasource|openquery|openxml|bulk|backup|restore|dbcc|shutdown|kill|waitfor|use|declare|set|begin|commit|rollback|save|writetext|updatetext|enable|disable|send|receive|reconfigure|checkpoint|setuser|revert|raiserror|goto|xp_\w*|sp_\w*" +
        // Lock hints: a question runs at READ UNCOMMITTED so it never holds up an HQ PC that is saving; TABLOCKX & co. would.
        @"|tablock|tablockx|xlock|updlock|holdlock|paglock|serializable|repeatableread|readcommittedlock)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Forbidden();

    /// <summary>The statement with strings blanked to '' and quoted identifiers to [x] and comments removed, or the reason
    /// it cannot be read safely.</summary>
    private static (string? Bare, string? Problem) Lex(string sql)
    {
        var bare = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        // Ends a quoted token at its closing character; a doubled closer is an escaped character inside it.
        int Close(int from, char closer)
        {
            for (var j = from; j < sql.Length; j++)
            {
                if (sql[j] != closer) continue;
                if (j + 1 < sql.Length && sql[j + 1] == closer) { j++; continue; }
                return j;
            }
            return -1;
        }
        while (i < sql.Length)
        {
            var ch = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (ch < ' ' && ch is not ('\t' or '\r' or '\n')) return (null, "Control characters are not allowed in a query");
            if (ch is '\'' or '[' or '"')
            {
                var end = Close(i + 1, ch == '[' ? ']' : ch);
                if (end < 0) return (null, "Unclosed quote or bracket");
                bare.Append(ch == '\'' ? "''" : "[x]");
                i = end + 1;
            }
            else if (ch == '-' && next == '-')
            {
                // SQL Server ends a line comment at CR as well as LF.
                while (i < sql.Length && sql[i] is not ('\r' or '\n')) i++;
                bare.Append(' ');
            }
            else if (ch == '/' && next == '*')
            {
                // Scanned character by character as SQL Server does, so "/*/" opens a nested comment rather than closing one.
                var j = i + 2;
                while (j < sql.Length && !(sql[j] == '*' && j + 1 < sql.Length && sql[j + 1] == '/'))
                {
                    if (sql[j] == '/' && j + 1 < sql.Length && sql[j + 1] == '*') return (null, "Nested /* comments are not allowed");
                    j++;
                }
                if (j >= sql.Length) return (null, "Unclosed /* comment");
                bare.Append(' ');
                i = j + 2;
            }
            else { bare.Append(ch); i++; }
        }
        return (bare.ToString().Trim(), null);
    }

    /// <summary>Why <paramref name="sql"/> is not a single read-only SELECT, or null when db_query may run it.</summary>
    public static string? RejectReason(string sql)
    {
        var (bare, problem) = Lex(sql);
        if (problem is not null) return problem;
        if (!Regex.IsMatch(bare!, @"^(select|with)\b", RegexOptions.IgnoreCase)) return "Only a SELECT (or WITH … SELECT) is allowed";
        if (bare!.TrimEnd(';', ' ', '\n', '\r', '\t').Contains(';')) return "One statement only (no semicolons)";
        if (Forbidden().Match(bare) is { Success: true } bad) return $"'{bad.Value}' is not allowed in a read-only query";
        return null;
    }

    private static async Task<object> QueryAsync(HqFirm firm, string sql, CancellationToken ct)
    {
        if (RejectReason(sql) is { } why) throw new ToolError(why);

        await using var s = await firm.OpenAsync(ct);
        // Dirty reads: a question must never wait for (or hold up) an HQ PC that is saving. Rolled back regardless.
        await s.BeginAsync(IsolationLevel.ReadUncommitted);
        try
        {
            await using var cmd = new SqlCommand(sql.TrimEnd(';', ' ', '\n', '\r', '\t'), s.Connection, s.Transaction) { CommandTimeout = 20 };
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
            var rows = new List<object?[]>();
            var more = false;
            while (await reader.ReadAsync(ct))
            {
                // Cancel before the reader is disposed, or SqlClient first drains the rest of a huge result set.
                if (rows.Count == 200) { more = true; cmd.Cancel(); break; }
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i) switch { string t => t.Trim(), DateTime d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), byte[] => "(binary)", var v => v };
                rows.Add(row);
            }
            return new { columns, rows, truncated = more };
        }
        finally { await s.RollbackAsync(); }
    }
}
