using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MK.Hq.Engine.Configuration;

namespace MK.Hq.Api.Agent;

/// <summary>A revocable credential for external clients, independent of browser and ACP chat keys.</summary>
public sealed class McpAccess(HqOptions options)
{
    private readonly string _token = ApiToken.LoadOrCreate(options.DataDirectory, "mcp.token");

    public bool Matches(string supplied) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(_token), Encoding.UTF8.GetBytes(supplied));
}

/// <summary>Stateless Streamable HTTP. ACP chat keys and persistent external keys share the same tool implementations.</summary>
public sealed class McpServer(McpAccess access, AgentHub hub, AgentTools tools, ExternalBillingTools billing, ILogger<McpServer> log)
{
    private static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26"];
    public const string ExternalInstructions = "Complete this workflow entirely inside Antigravity. Read the returned image, ask questions, " +
        "and show lookup results and saved receipts in this conversation. Do not launch a browser or send the user to the billing UI. " +
        "Use scan to get a page image, lookup_items for all written items at once, " +
        "and post_bill to save a sales estimate, sales invoice or supplier purchase invoice (kind purchase). firm is mbm or mp. " +
        "For purchases use the printed supplier reference, date, GSTIN and total. Resolve payment, tax basis and all charge lines before posting. " +
        "Reuse operationId with the identical bill payload when retrying a post. " +
        "Keep replies short in Telenglish, show a compact monochrome inline preview, and use native questions for unresolved choices. " +
        "Review candidate notes; do not invent substitutions or change rates/discounts to force totals. " +
        "For catalogue full-bar PCS, CPVC/UPVC pipe is 10 feet per bar and ordinary PVC is 20 feet; 90 feet UPVC is 9 PCS. " +
        "Resolve partial bars and unit conflicts before explicit posting approval. These tools cannot amend a saved bill; never duplicate one to apply an edit.";

    public static void Map(WebApplication app)
    {
        app.MapMethods("/mcp", ["POST", "GET", "DELETE"],
            (HttpContext ctx, McpServer server, CancellationToken ct) => server.HandleAsync(ctx, ct));
        app.MapGet("/.well-known/{**rest}", () => Results.NotFound());
    }

    public async Task<IResult> HandleAsync(HttpContext ctx, CancellationToken ct)
    {
        // The LAN photo listener must never expose MCP, even if the main listener is reconfigured.
        if (!Loopback(ctx.Request.Host.Host) || ctx.Connection.RemoteIpAddress is { } remote && !IPAddress.IsLoopback(remote))
            return Error(null, -32001, "MCP is available on this PC's loopback address.", 403);
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || !Loopback(uri.Host)
            || !string.Equals(uri.GetLeftPart(UriPartial.Authority), $"{ctx.Request.Scheme}://{ctx.Request.Host}", StringComparison.OrdinalIgnoreCase)))
            return Error(null, -32001, "Origin is not allowed.", 403);

        var authorization = ctx.Request.Headers.Authorization.ToString();
        var key = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..] : "";
        var external = access.Matches(key);
        var chat = external ? null : hub.ChatByKey(key);
        if (!external && chat is null)
        {
            ctx.Response.Headers.WWWAuthenticate = "Bearer realm=\"MK Billing MCP\"";
            return Error(null, -32001, "An MK MCP bearer token or active assistant chat key is required.", 401);
        }
        if (!HttpMethods.IsPost(ctx.Request.Method))
        {
            ctx.Response.Headers.Allow = "POST";
            return Results.StatusCode(405);
        }
        var protocol = ctx.Request.Headers["MCP-Protocol-Version"].ToString();
        if (protocol.Length > 0 && !Versions.Contains(protocol))
            return Error(null, -32600, "Unsupported MCP-Protocol-Version.", 400);
        if (!ctx.Request.HasJsonContentType()) return Error(null, -32600, "Content-Type must be application/json.", 415);

        JsonNode? id = null;
        try
        {
            JsonNode? node;
            try { node = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct); }
            catch (JsonException) { return Error(null, -32700, "Invalid JSON.", 400); }
            if (node is not JsonObject message || Text(message["jsonrpc"]) != "2.0")
                return Error(null, -32600, "Expected a JSON-RPC 2.0 message.", 400);
            var hasId = message.TryGetPropertyValue("id", out id);
            if (hasId && (id is not JsonValue value || !(value.TryGetValue<string>(out _) || value.TryGetValue<long>(out _))))
                return Error(null, -32600, "Request id must be a string or integer.", 400);
            var method = Text(message["method"]);
            if (method is null)
                return hasId && (message.ContainsKey("result") || message.ContainsKey("error"))
                    ? Results.Accepted() : Error(id, -32600, "A request needs a method.", 400);
            if (message["params"] is not null and not JsonObject)
                return Error(id, -32602, "params must be an object.");
            if (!hasId) return method.StartsWith("notifications/", StringComparison.Ordinal)
                ? Results.Accepted() : Error(null, -32600, "A request needs an id.", 400);
            var args = message["params"] as JsonObject ?? new JsonObject();
            JsonNode? result;
            switch (method)
            {
                case "initialize":
                    var requested = Text(args["protocolVersion"]);
                    if (requested is null) return Error(id, -32602, "initialize needs protocolVersion.");
                    result = new JsonObject
                    {
                        ["protocolVersion"] = Versions.Contains(requested) ? requested : Versions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = AgentTools.ServerName, ["title"] = "MK Billing", ["version"] = "1.0.0" },
                        ["instructions"] = external ? ExternalInstructions : "Read HQ, prepare drafts, and navigate the billing UI. The user saves reviewed drafts.",
                    };
                    break;
                case "ping": result = new JsonObject(); break;
                case "tools/list": result = new JsonObject { ["tools"] = external ? ExternalBillingTools.List() : tools.List() }; break;
                case "tools/call":
                    var name = Text(args["name"]);
                    if (name is null || args["arguments"] is not null and not JsonObject)
                        return Error(id, -32602, "tools/call needs a name and object arguments.");
                    if (!(external ? ExternalBillingTools.List() : tools.List()).Any(t => Text(t?["name"]) == name))
                        return Error(id, -32602, "Unknown or unavailable tool.");
                    result = external
                        ? await billing.CallAsync(name, args["arguments"] as JsonObject ?? new JsonObject(), ct)
                        : await tools.CallAsync(chat!, name, args["arguments"] as JsonObject ?? new JsonObject(), ct);
                    break;
                default: return Error(id, -32601, "Method not found.");
            }
            return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            log.LogError(e, "MCP request failed");
            return Error(id, -32603, "MK could not complete this request. Check the app log.");
        }
    }

    private static string? Text(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var text) ? text : null;
    private static bool Loopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    private static IResult Error(JsonNode? id, int code, string message, int status = 200) => Results.Json(
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }, statusCode: status);
}
