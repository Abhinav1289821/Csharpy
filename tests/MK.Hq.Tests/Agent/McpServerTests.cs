using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Api;
using MK.Hq.Api.Agent;
using MK.Hq.Engine;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Domain.Gst;

namespace MK.Hq.Tests.Agent;

public sealed class McpServerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mk-mcp-tests-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHqEngine(new HqOptions
        {
            DataDirectory = _directory,
            Firms = [new FirmOptions { Id = "mbm", Name = "Test firm", Database = "MCP_TEST26" }],
        });
        builder.Services.AddSingleton(new AgentOptions());
        builder.Services.AddSingleton<AgentHub>();
        builder.Services.AddSingleton<AgentTools>();
        builder.Services.AddSingleton<WindowsScanner>();
        builder.Services.AddSingleton<McpAccess>();
        builder.Services.AddSingleton<McpServer>();
        builder.Services.AddSingleton<ExternalBillingTools>();
        _app = builder.Build();
        McpServer.Map(_app);
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _client.BaseAddress = new Uri("http://127.0.0.1");
        var token = ApiToken.LoadOrCreate(_directory, "mcp.token");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        // SQLite's pooled handles may outlive disposal. Leave this unique fixture folder to temp cleanup.
    }

    private async Task<JsonObject> Request(string method, JsonObject? args = null)
    {
        var response = await _client.PostAsync("/mcp", new StringContent(new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = "test", ["method"] = method, ["params"] = args ?? new JsonObject(),
        }.ToJsonString(), Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
        return (JsonObject)JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [Fact]
    public async Task ExternalClientInitializesDiscoversAndCallsWithoutAgyOrSql()
    {
        var initialized = await Request("initialize", new JsonObject { ["protocolVersion"] = "2025-11-25" });
        Assert.Equal("2025-11-25", initialized["result"]?["protocolVersion"]?.GetValue<string>());
        Assert.Contains("entirely inside Antigravity", initialized["result"]!["instructions"]!.GetValue<string>());
        Assert.Contains("Do not launch a browser", initialized["result"]!["instructions"]!.GetValue<string>());
        var list = await Request("tools/list");
        var tools = (JsonArray)list["result"]!["tools"]!;
        Assert.Equal(new[] { "scan", "lookup_items", "post_bill" }, tools.Select(t => t!["name"]!.GetValue<string>()));
        Assert.Contains("firm", tools[1]!["inputSchema"]!["required"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Contains("purchase", tools[2]!["inputSchema"]!["properties"]!["kind"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
        var blocked = await Request("tools/call", new JsonObject { ["name"] = "db_query", ["arguments"] = new JsonObject() });
        Assert.Equal(-32602, blocked["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task MalformedBulkAndPostRequestsFailBeforeDatabaseAccess()
    {
        var missing = await Request("tools/call", new JsonObject { ["name"] = "lookup_items", ["arguments"] = new JsonObject { ["firm"] = "mbm", ["queries"] = new JsonArray() } });
        Assert.True(missing["result"]!["isError"]!.GetValue<bool>());
        var response = await Request("tools/call", new JsonObject
        {
            ["name"] = "post_bill", ["arguments"] = new JsonObject { ["firm"] = "mbm", ["kind"] = "invoice", ["lines"] = new JsonArray(new JsonObject { ["itemId"] = 1 }) },
        });
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("estimate", DocumentKind.SalesOrder)]
    [InlineData("invoice", DocumentKind.Sale)]
    public void PostPreservesExplicitPricesTaxAndOperationId(string kind, DocumentKind expected)
    {
        var operation = Guid.NewGuid().ToString();
        var args = JsonNode.Parse("""
            {"firm":"mbm","operationId":"","kind":"","date":"2026-11-12","basis":"inclusive",
             "party":{"name":"Walk-in","phone":"123"},"notes":"From scan",
             "lines":[{"itemId":7,"unitId":3,"quantity":2.5,"rate":118,"discountPercent":10}]}
            """)!.AsObject();
        args["kind"] = kind;
        args["operationId"] = operation;
        var parsed = ExternalBillingTools.ParsePost(args);
        Assert.Equal(operation, parsed.OperationId);
        Assert.Equal(expected, parsed.Document.Kind);
        Assert.Equal(TaxBasis.Inclusive, parsed.Document.Basis);
        Assert.Equal(2.5m, parsed.Document.Lines[0].Quantity);
        Assert.Equal(118m, parsed.Document.Lines[0].Rate);
        Assert.Equal(10m, parsed.Document.Lines[0].DiscountPercent);
        var retry = ExternalBillingTools.ParsePost((JsonObject)args.DeepClone());
        Assert.Equal(parsed.OperationId, retry.OperationId);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(parsed.Document), System.Text.Json.JsonSerializer.Serialize(retry.Document));
    }

    [Fact]
    public void PurchaseDefaultsToPrintedDateAndPreservesReferenceAndTotal()
    {
        var args = JsonNode.Parse("""
            {"firm":"mbm","operationId":"e192c123-973e-499f-9ef3-d364ee875a92","kind":"purchase",
             "supplier":{"number":"003109","date":"2026-11-06"},"party":{"gstin":"37abdcs0279c1zh"},
             "paymentMode":1,"basis":"inclusive","invoiceTotal":118,
             "lines":[{"itemId":7,"unitId":3,"quantity":1,"rate":118}]}
            """)!.AsObject();
        var post = ExternalBillingTools.ParsePost(args);
        Assert.Equal(DocumentKind.Purchase, post.Document.Kind);
        Assert.Equal(new DateOnly(2026, 11, 6), post.Document.Date);
        Assert.Equal(new SupplierBill("003109", post.Document.Date), post.Document.Supplier);
        Assert.Equal("37ABDCS0279C1ZH", post.Document.Party?.Gstin);
        Assert.Equal(118m, post.InvoiceTotal);
        Assert.Equal(1, post.Document.PaymentMode);
        Assert.Null(post.Document.AccountId);
        args["date"] = "2026-11-07";
        Assert.Equal(new DateOnly(2026, 11, 7), ExternalBillingTools.ParsePost(args).Document.Date);
    }

    [Theory]
    [InlineData("supplier")]
    [InlineData("party")]
    [InlineData("paymentMode")]
    [InlineData("basis")]
    [InlineData("invoiceTotal")]
    public async Task IncompletePurchaseFailsBeforeDatabaseAccess(string missing)
    {
        var args = JsonNode.Parse("""
            {"firm":"mbm","operationId":"e192c123-973e-499f-9ef3-d364ee875a92","kind":"purchase",
             "supplier":{"number":"003109","date":"2026-11-06"},"party":{"gstin":"37ABDCS0279C1ZH"},
             "paymentMode":0,"basis":"exclusive","invoiceTotal":118,
             "lines":[{"itemId":7,"unitId":3,"quantity":1,"rate":100}]}
            """)!.AsObject();
        args.Remove(missing);
        var response = await Request("tools/call", new JsonObject { ["name"] = "post_bill", ["arguments"] = args });
        Assert.True(response["result"]!["isError"]!.GetValue<bool>());
        var error = JsonNode.Parse(response["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("INVALID_ARGUMENTS", error!["error"]!.GetValue<string>());
    }

    [Fact]
    public void BulkLookupRetainsEveryQueryAndReturnsUnitAndRates()
    {
        var items = new[] { new CatalogueItem(7, 3, "P75", "75 MM PVC PIPE", "PCS", "3917", 118, 100, 130, 9, 9, 18, true, 10, true, "Pipes", "") };
        var results = JsonSerializerNode(ExternalBillingTools.MatchItems(items, ["75 MM PVC PIPE", "ZZZUNMATCHED"], 5));
        Assert.Equal(2, results.AsArray().Count);
        Assert.Equal("75 MM PVC PIPE", results[0]!["query"]!.GetValue<string>());
        var candidate = results[0]!["candidates"]![0]!;
        Assert.Equal(7, candidate["itemId"]!.GetValue<int>());
        Assert.Equal(3, candidate["unitId"]!.GetValue<int>());
        Assert.Equal(118, candidate["saleRate"]!.GetValue<decimal>());
        Assert.Equal(18, candidate["gst"]!.GetValue<decimal>());
        Assert.Equal(7, results[0]!["chosen"]!["itemId"]!.GetValue<int>());
        Assert.Equal("clear best match", results[0]!["reason"]!.GetValue<string>());
        Assert.Equal("ZZZUNMATCHED", results[1]!["query"]!.GetValue<string>());
        Assert.Null(results[1]!["chosen"]);
        Assert.Equal("no similar item in HQ", results[1]!["reason"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("75 MM PVC PIPE", "110 MM PVC PIPE", "size differs")]
    [InlineData("75 MM PVC PIPE", "75 MM CPVC PIPE", "different material")]
    [InlineData("75 MM PVC ELBOW", "75 MM PVC PIPE", "different product")]
    public void BulkLookupExposesSpecificationConflicts(string query, string catalogueName, string warning)
    {
        var items = new[] { new CatalogueItem(7, 3, "P75", catalogueName, "PCS", "3917", 118, 100, 130, 9, 9, 18, true, 10, true, "Pipes", "") };
        var results = JsonSerializerNode(ExternalBillingTools.MatchItems(items, [query], 5));
        var result = results[0]!;
        Assert.Equal(query, result["query"]!.GetValue<string>());
        Assert.Null(result["chosen"]);
        Assert.Equal("specification differs; choose", result["reason"]!.GetValue<string>());
        var candidate = Assert.Single(result["candidates"]!.AsArray())!;
        Assert.Equal(7, candidate["itemId"]!.GetValue<int>());
        Assert.Equal(3, candidate["unitId"]!.GetValue<int>());
        Assert.Contains(candidate["notes"]!.AsArray(), note => note!.GetValue<string>().StartsWith(warning, StringComparison.Ordinal));
    }

    private static JsonNode JsonSerializerNode(object value) => System.Text.Json.JsonSerializer.SerializeToNode(value,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

    [Theory]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("unknown", "2025-11-25")]
    public async Task InitializeNegotiatesOnlySupportedVersions(string requested, string expected)
    {
        var response = await Request("initialize", new JsonObject { ["protocolVersion"] = requested });
        Assert.Equal(expected, response["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task AuthOriginHostAndProtocolAreValidated()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/mcp")).StatusCode);
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiToken.LoadOrCreate(_directory, "mcp.token"));
        _client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/mcp")).StatusCode);
        _client.DefaultRequestHeaders.Remove("Origin");
        _client.DefaultRequestHeaders.Host = "untrusted.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.GetAsync("/mcp")).StatusCode);
        _client.DefaultRequestHeaders.Host = null;
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _client.GetAsync("/mcp")).StatusCode);
        _client.DefaultRequestHeaders.Add("MCP-Protocol-Version", "unknown");
        var response = await _client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NotificationsAndMalformedRequestsFollowJsonRpc()
    {
        var accepted = await _client.PostAsync("/mcp", new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal("", await accepted.Content.ReadAsStringAsync());
        var unknown = await Request("no-such-method");
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        var invalid = await Request("tools/call", new JsonObject { ["name"] = "list_firms", ["arguments"] = 3 });
        Assert.Equal(-32602, invalid["error"]!["code"]!.GetValue<int>());
        var malformed = await _client.PostAsync("/mcp", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var error = JsonNode.Parse(await malformed.Content.ReadAsStringAsync())!;
        Assert.Equal(-32700, error["error"]!["code"]!.GetValue<int>());
    }
}
