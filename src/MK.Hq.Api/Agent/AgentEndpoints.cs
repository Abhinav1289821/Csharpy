using System.Text.Json;
using System.Text.Json.Nodes;

namespace MK.Hq.Api.Agent;

public sealed record AgentScreenBody(string? Firm, string? Hash, string? Title);
public sealed record AgentPromptBody(string Text, string? Firm, string? Hash, string? Title);
public sealed record AgentAnswerBody(string Id, string? Answer);
public sealed record AgentPermissionBody(string Id, string? OptionId);
public sealed record AgentOptionBody(string ConfigId, string Value);

/// <summary>
/// /mcp: MK's tools for embedded and external Antigravity clients over Streamable HTTP.
/// /api/agent: the browser's side of the assistant: chats, messages, the live event stream, permission answers.
/// </summary>
public static class AgentEndpoints
{
    public static void Map(WebApplication app, RouteGroupBuilder api)
    {
        McpServer.Map(app);

        // ---- browser API ----
        var agent = api.MapGroup("/agent");
        agent.MapGet("", (AgentHub hub) => hub.Status());
        agent.MapPost("/login", (AgentHub hub) => Run(() => { hub.StartSignIn(); return Results.Ok(new { started = true }); }));
        agent.MapPost("/chats", async (AgentScreenBody body, AgentHub hub, CancellationToken ct) =>
            await RunAsync(async () =>
            {
                var chat = await hub.NewChatAsync(new ScreenContext(body.Firm, body.Hash, body.Title), ct);
                return Results.Ok(new { chat.Id, chat.Title });
            }));
        agent.MapPost("/chats/{id}/prompt", (string id, AgentPromptBody body, AgentHub hub) =>
        {
            if (hub.Chat(id) is not { } chat) return Gone();
            if (string.IsNullOrWhiteSpace(body.Text)) return Results.BadRequest(new { code = "EMPTY", message = "Type a message", kind = "Invalid" });
            hub.Prompt(chat, body.Text.Trim(), new ScreenContext(body.Firm, body.Hash, body.Title));
            return Results.Accepted();
        });
        agent.MapPost("/chats/{id}/screen", (string id, AgentScreenBody body, AgentHub hub) =>
        {
            if (hub.Chat(id) is not { } chat) return Gone();
            chat.Screen = new ScreenContext(body.Firm, body.Hash, body.Title);
            return Results.NoContent();
        });
        agent.MapPost("/chats/{id}/cancel", async (string id, AgentHub hub) =>
        {
            if (hub.Chat(id) is not { } chat) return Gone();
            await hub.CancelAsync(chat);
            return Results.NoContent();
        });
        agent.MapPost("/chats/{id}/answer", (string id, AgentAnswerBody body, AgentHub hub) =>
        {
            if (body.Answer is { Length: > 1000 }) return Results.BadRequest(new { message = "Keep the answer under 1,000 characters" });
            return hub.Chat(id) is { } chat && chat.Questions.TryGetValue(body.Id, out var question)
                && question.TrySetResult(body.Answer?.Trim()) ? Results.NoContent() : Gone();
        });
        agent.MapPost("/chats/{id}/permission", (string id, AgentPermissionBody body, AgentHub hub) =>
            hub.Chat(id) is { } chat && hub.Answer(chat, body.Id, body.OptionId) ? Results.NoContent() : Gone());
        agent.MapPost("/chats/{id}/option", async (string id, AgentOptionBody body, AgentHub hub) =>
        {
            if (hub.Chat(id) is not { } chat) return Gone();
            return await RunAsync(async () => Results.Ok(await hub.SetOptionAsync(chat, body.ConfigId, body.Value)));
        });
        agent.MapDelete("/chats/{id}", async (string id, AgentHub hub) =>
        {
            if (hub.Chat(id) is { } chat) await hub.CloseAsync(chat);
            return Results.NoContent();
        });
        agent.MapGet("/chats/{id}/events", async (string id, long? after, HttpContext ctx, AgentHub hub, CancellationToken ct) =>
        {
            if (hub.Chat(id) is not { } chat) { ctx.Response.StatusCode = 404; return; }
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            var from = after ?? (long.TryParse(ctx.Request.Headers["Last-Event-ID"], out var last) ? last : 0);
            var (backlog, live) = chat.Subscribe(from);
            try
            {
                async Task Write(JsonObject e) => await ctx.Response.WriteAsync($"id: {e["seq"]}\ndata: {e.ToJsonString()}\n\n", ct);
                foreach (var e in backlog) await Write(e);
                await ctx.Response.Body.FlushAsync(ct);
                while (!ct.IsCancellationRequested)
                {
                    using var ping = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ping.CancelAfter(TimeSpan.FromSeconds(20));
                    try
                    {
                        var e = await live.Reader.ReadAsync(ping.Token);
                        await Write(e);
                        while (live.Reader.TryRead(out var more)) await Write(more);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { await ctx.Response.WriteAsync(": ping\n\n", ct); }
                    await ctx.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { /* browser went away */ }
            finally { chat.Unsubscribe(live); }
        });
    }

    private static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;


    private static IResult Gone() => Results.Json(new { code = "AGENT_CHAT_GONE", message = "This conversation has ended. Start a new one.", kind = "NotFound" }, statusCode: 404);

    private static IResult Fail(AcpException e) => Results.Json(new
    {
        code = e.AuthRequired ? "AGENT_SIGN_IN" : e.Code == -2 ? "AGENT_NOT_INSTALLED" : "AGENT_UNAVAILABLE",
        message = e.AuthRequired ? "Antigravity needs you to sign in on this PC." : e.Message,
        kind = "Unavailable",
    }, statusCode: 503);

    private static IResult Run(Func<IResult> f)
    {
        try { return f(); }
        catch (AcpException e) { return Fail(e); }
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> f)
    {
        try { return await f(); }
        catch (AcpException e) { return Fail(e); }
        catch (TimeoutException e) { return Fail(new AcpException(-4, e.Message)); }
        catch (System.ComponentModel.Win32Exception e) { return Fail(new AcpException(-2, "Could not start the Antigravity CLI: " + e.Message)); }
    }
}
