using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Json;
using MK.Hq.Api;
using MK.Hq.Domain;
using MK.Hq.Engine;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Journal;
using MK.Hq.Engine.Masters;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Reports;
using MK.Hq.Engine.Vouchers;

var builder = WebApplication.CreateBuilder(args);
var hq = builder.Configuration.GetSection("Hq").Get<HqOptions>() ?? new HqOptions();
hq.DataDirectory = Path.GetFullPath(hq.DataDirectory, builder.Environment.ContentRootPath);
builder.Services.AddHqEngine(hq);
builder.Services.Configure<JsonOptions>(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
});
builder.Services.AddProblemDetails();
// Bill photos can be large; JSON bodies stay small in practice.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 30 * 1024 * 1024);
var pairing = new PhonePairing(hq);
builder.Services.AddSingleton(pairing);
builder.Services.AddHostedService<CaptureWorker>();
// The assistant: Google Antigravity (agy acp) with MK's tools served at /mcp.
var agentOptions = builder.Configuration.GetSection("Agent").Get<MK.Hq.Api.Agent.AgentOptions>() ?? new MK.Hq.Api.Agent.AgentOptions();
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<MK.Hq.Api.Agent.AgentHub>();
builder.Services.AddSingleton<MK.Hq.Api.Agent.AgentTools>();
// Bill photos are read by Antigravity as well (the shop's Google sign-in, no API key); ReadPhotos=false keeps Gemma 4.
if (agentOptions.ReadPhotos) builder.Services.AddSingleton<MK.Hq.Engine.Capture.IBillReader, MK.Hq.Api.Agent.AntigravityBillReader>();
var mainUrls = builder.Configuration["urls"] ?? "http://127.0.0.1:47320";
// Phone uploads: a second listener on the shop LAN that serves ONLY the photo page (see CaptureEndpoints.MapPhone).
builder.WebHost.UseUrls(pairing.Enabled ? $"{mainUrls};http://{pairing.LanAddress}:{pairing.Port}" : mainUrls);

var app = builder.Build();
var token = ApiToken.LoadOrCreate(hq.DataDirectory);
app.Services.GetRequiredService<MK.Hq.Api.Agent.AgentHub>().ToolsBaseUrl =
    app.Services.GetRequiredService<MK.Hq.Api.Agent.AgentOptions>().ToolsBaseUrl ?? mainUrls.Split(';')[0];
// The access key stays out of the log (logs get copied around); the launcher reads it from var\api.token.
app.Logger.LogInformation("MK HQ Billing on {Url} (access key in {File})", mainUrls.Split(';')[0], Path.Combine(hq.DataDirectory, "api.token"));
if (pairing.Enabled) app.Logger.LogInformation("Phone uploads on http://{Lan}:{Port}/m/… (QR codes only)", pairing.LanAddress, pairing.Port);

// ---- startup: settle any interrupted save, pin/verify HQ's procedure contract ----
using (var scope = app.Services.CreateScope())
{
    var contract = app.Services.GetRequiredService<HqContract>();
    foreach (var hqFirm in app.Services.GetRequiredService<FirmRegistry>().All)
    {
        try
        {
            var status = await contract.CheckAsync(hqFirm);
            app.Logger.LogInformation("{Firm}: {Summary}", hqFirm.Name, status.Summary);
        }
        catch (Exception e) { app.Logger.LogError(e, "{Firm}: HQ database not reachable at start", hqFirm.Name); }
    }
    try { await app.Services.GetRequiredService<WriteCoordinator>().RecoverAsync(app.Services.GetRequiredService<FirmRegistry>()); }
    catch (Exception e) { app.Logger.LogError(e, "Recovery pass failed; it will run again on the next start"); }
}

if (pairing.Enabled) CaptureEndpoints.MapPhone(app, pairing.Port);
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });

// ---- errors: business failures become {code, message, kind}; nothing leaks stack traces ----
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (HqException e)
    {
        ctx.Response.StatusCode = e.Kind switch
        {
            FailureKind.Invalid => 400, FailureKind.NotFound => 404, FailureKind.Conflict => 409, FailureKind.Unsupported => 422,
            FailureKind.Unavailable => 503, FailureKind.Uncertain => 409, _ => 500,
        };
        await ctx.Response.WriteAsJsonAsync(new { code = e.Code, message = e.Message, kind = e.Kind.ToString(), detail = e.Detail });
    }
    catch (BadHttpRequestException e)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { code = "BAD_REQUEST", message = e.Message, kind = "Invalid" });
    }
    catch (JsonException e)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { code = "BAD_JSON", message = "The request could not be read: " + e.Message, kind = "Invalid" });
    }
    catch (FormatException e)
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { code = "BAD_REQUEST", message = "A value in the request is not a valid number or date: " + e.Message, kind = "Invalid" });
    }
    catch (Exception e) when (e is not OperationCanceledException && !ctx.Response.HasStarted)
    {
        // Anything else is an engine bug: log it with its stack, answer in the same {code, message} shape the client expects.
        app.Logger.LogError(e, "Unhandled error on {Method} {Path}", ctx.Request.Method, ctx.Request.Path);
        ctx.Response.StatusCode = 500;
        await ctx.Response.WriteAsJsonAsync(new { code = "INTERNAL", message = "Something went wrong in MK Billing. Details are in the log.", kind = "Internal" });
    }
});

// ---- bearer token for every /api route except health ----
app.Use(async (ctx, next) =>
{
    // Only the health probe itself is open. StartsWithSegments("/api/health") also opened every /api/{firm}/... route
    // with firm "health": /api/health/drafts/{id} (PUT/DELETE), /api/health/cleanup/..., all without the key.
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !ApiAuth.IsOpen(path))
    {
        if (!ApiAuth.Accepts(ctx.Request.Headers.Authorization.ToString(), ctx.Request.Query["token"].ToString(), token))
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { code = "UNAUTHORIZED", message = "Open MK Billing from its launcher (the link carries the access key).", kind = "Invalid" });
            return;
        }
    }
    await next();
});

var api = app.MapGroup("/api");
api.MapGet("/health", () => new { app = "mk-hq", version = typeof(Program).Assembly.GetName().Version?.ToString(), time = DateTime.UtcNow });

api.MapGet("/firms", async (FirmRegistry firms, FirmProfiles profiles, HqContract contract, CancellationToken ct) =>
{
    var list = new List<object>();
    foreach (var f in firms.All)
    {
        try
        {
            var p = await profiles.GetAsync(f, ct);
            list.Add(new
            {
                f.Id, f.Name, f.Database, f.PeriodStart, f.PeriodEnd, letterhead = f.Options.Letterhead, gstin = f.Options.Gstin, online = true,
                contract = contract.Status(f.Id),
                paymentModes = p.PaymentModes.Select(m => new { m.Id, m.Caption, active = m.Status == 0 }),
                salespeople = p.Salespeople,
                types = p.Types.Values.Where(t => t.TtId is 204 or 205 or 206 or 207 or 208 or 209 or 248 or 249 or 500 or 501 or 506 or 507)
                    .Select(t => new { t.TtId, t.Name, t.Series, t.VouchType, basis = t.IncTax == 1 ? "inclusive" : "exclusive", t.RoundedOffTo, t.DefaultDays }),
            });
        }
        catch (HqException e) { list.Add(new { f.Id, f.Name, f.Database, online = false, error = e.Message }); }
    }
    return list;
});

var firm = api.MapGroup("/{firm}");

firm.MapGet("/catalogue", async (string firm, HttpContext ctx, Catalogue catalogue, FirmRegistry firms, CancellationToken ct) =>
{
    var snapshot = await catalogue.GetAsync(firms.Get(firm), ct);
    var etag = $"\"{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(snapshot.Stamp)))[..20]}\"";
    if (ctx.Request.Headers.IfNoneMatch == etag) return Results.StatusCode(304);
    ctx.Response.Headers.ETag = etag;
    return Results.Ok(new { stamp = etag, items = snapshot.Items, parties = snapshot.Parties });
});

// ---- documents ----
firm.MapPost("/documents/preview", (string firm, DocumentRequest body, DocumentService s, CancellationToken ct) => s.PreviewAsync(firm, body, ct));
firm.MapPost("/documents", (string firm, PostRequest body, DocumentService s, CancellationToken ct) => s.PostAsync(firm, body, ct));
firm.MapGet("/documents", async (string firm, string? tt, DateOnly? from, DateOnly? to, long? account, string? q, long? before, int? take, FirmRegistry firms, CancellationToken ct) =>
{
    await using var s = await firms.Get(firm).OpenAsync(ct);
    return await DocumentReader.ListAsync(s, new DocumentFilter
    {
        TtIds = string.IsNullOrWhiteSpace(tt) ? null : ApiAuth.Ids<int>(tt, "tt"),
        From = from, To = to, AccountId = account, Search = q, BeforeId = before, Take = take ?? 100,
    });
});
firm.MapGet("/documents/{id:long}", async (string firm, long id, FirmRegistry firms, FirmProfiles profiles, CancellationToken ct) =>
{
    var f = firms.Get(firm);
    var profile = await profiles.GetAsync(f, ct);
    await using var s = await f.OpenAsync(ct);
    return await DocumentReader.ViewAsync(s, profile, id) ?? throw HqException.NotFound("HQ_DOCUMENT_NOT_FOUND", "That document is not in HQ (it may have been deleted)");
});
firm.MapPost("/documents/{id:long}/amend/preview", (string firm, long id, AmendRequest body, DocumentMutations m, CancellationToken ct) => m.PreviewAmendAsync(firm, body with { DocumentId = id }, ct));
firm.MapPost("/documents/{id:long}/amend", (string firm, long id, AmendRequest body, DocumentMutations m, CancellationToken ct) => m.AmendAsync(firm, body with { DocumentId = id }, ct));
firm.MapPost("/documents/{id:long}/delete/preview", (string firm, long id, DocumentMutations m, CancellationToken ct) => m.PreviewDeleteAsync(firm, id, ct));
firm.MapPost("/documents/{id:long}/delete", (string firm, long id, DeleteRequest body, DocumentMutations m, CancellationToken ct) => m.DeleteAsync(firm, body with { DocumentId = id }, ct));

// ---- vouchers ----
firm.MapPost("/vouchers/preview", (string firm, VoucherRequest body, VoucherService s, CancellationToken ct) => s.PreviewAsync(firm, body, ct));
firm.MapPost("/vouchers", (string firm, VoucherPost body, VoucherService s, CancellationToken ct) => s.PostAsync(firm, body, ct));
firm.MapGet("/vouchers/{id:long}", (string firm, long id, VoucherService s, CancellationToken ct) => s.LoadAsync(firm, id, ct));
firm.MapPost("/vouchers/{id:long}/amend/preview", (string firm, long id, VoucherRequest body, VoucherService s, CancellationToken ct) => s.PreviewAmendAsync(firm, id, body, ct));
firm.MapPost("/vouchers/{id:long}/amend", (string firm, long id, VoucherAmendPost body, VoucherService s, CancellationToken ct) => s.AmendAsync(firm, id, body, ct));
firm.MapPost("/vouchers/{id:long}/delete/preview", (string firm, long id, VoucherService s, CancellationToken ct) => s.PreviewDeleteAsync(firm, id, ct));
firm.MapPost("/vouchers/{id:long}/delete", (string firm, long id, DeleteRequest body, VoucherService s, CancellationToken ct) => s.DeleteAsync(firm, body with { DocumentId = id }, ct));
firm.MapGet("/accounts/{id:long}/open-items", (string firm, long id, long? except, VoucherService s, CancellationToken ct) => s.OpenItemsAsync(firm, id, except, ct));

// ---- masters: items and accounts, written exactly as HQ's own screens write them ----
firm.MapGet("/masters/items/options", (string firm, ItemMasterService s, CancellationToken ct) => s.OptionsAsync(firm, ct));
firm.MapGet("/masters/items/{id:long}", (string firm, long id, ItemMasterService s, CancellationToken ct) => s.LoadAsync(firm, id, ct));
firm.MapPost("/masters/items/preview", (string firm, ItemRequest body, ItemMasterService s, CancellationToken ct) => s.PreviewAsync(firm, body, ct));
firm.MapPost("/masters/items", (string firm, ItemPost body, ItemMasterService s, CancellationToken ct) => s.SaveAsync(firm, body, ct));
firm.MapGet("/masters/accounts/states", () => AccountMasterService.States);
firm.MapGet("/masters/accounts/{id:long}", (string firm, long id, AccountMasterService s, CancellationToken ct) => s.LoadAsync(firm, id, ct));
firm.MapPost("/masters/accounts/preview", (string firm, AccountRequest body, AccountMasterService s, CancellationToken ct) => s.PreviewAsync(firm, body, ct));
firm.MapPost("/masters/accounts", (string firm, AccountPost body, AccountMasterService s, CancellationToken ct) => s.SaveAsync(firm, body, ct));

// ---- item clean-up: the owner's guided review of proposed names; nothing reaches HQ until "apply" ----
firm.MapPost("/cleanup/import", (string firm, CleanupFile body, ItemCleanupService s, CancellationToken ct) => s.ImportAsync(firm, body, ct));
firm.MapGet("/cleanup", (string firm, ItemCleanupService s) => s.Summary(firm));
firm.MapGet("/cleanup/items", (string firm, string stage, string? ids, int? take, ItemCleanupService s) =>
    s.List(firm, stage, string.IsNullOrWhiteSpace(ids) ? null : ApiAuth.Ids<long>(ids, "ids"), take ?? 200));
firm.MapPost("/cleanup/decide", (string firm, CleanupAnswer[] body, ItemCleanupService s) => s.Decide(firm, body.Select(a => (a.ItemId, a.Decision)).ToList()));
firm.MapPost("/cleanup/rules", (string firm, CleanupRuleAnswer body, ItemCleanupService s) => s.DecideRule(firm, body.Rule, body.Decision));
firm.MapPost("/cleanup/apply", (string firm, CleanupRun body, ItemCleanupService s, CancellationToken ct) => s.ApplyAsync(firm, body.BatchId, body.Take ?? 20, ct));

// ---- reports ----
firm.MapGet("/reports/dashboard", (string firm, DateOnly? day, ReportService r, CancellationToken ct) => r.DashboardAsync(firm, day ?? DateOnly.FromDateTime(DateTime.Now), ct));
firm.MapGet("/reports/ledger/{id:long}", (string firm, long id, DateOnly? from, DateOnly? to, ReportService r, CancellationToken ct) => r.LedgerAsync(firm, id, from, to, ct));
firm.MapGet("/reports/outstanding", (string firm, string? side, ReportService r, CancellationToken ct) => r.OutstandingAsync(firm, side != "payable", ct));
firm.MapGet("/reports/daybook", (string firm, DateOnly? from, DateOnly? to, ReportService r, CancellationToken ct) =>
    r.DayBookAsync(firm, from ?? DateOnly.FromDateTime(DateTime.Now), to ?? from ?? DateOnly.FromDateTime(DateTime.Now), ct));
firm.MapGet("/reports/register", (string firm, string? kind, DateOnly? from, DateOnly? to, ReportService r, FirmRegistry firms, CancellationToken ct) =>
{
    var f = firms.Get(firm);
    var today = DateOnly.FromDateTime(DateTime.Now);
    return r.RegisterAsync(firm, kind != "purchases", from ?? new DateOnly(today.Year, today.Month, 1), to ?? today, ct);
});
firm.MapGet("/reports/stock", (string firm, ReportService r, CancellationToken ct) => r.StockAsync(firm, ct));
firm.MapGet("/integrity", (string firm, FirmRegistry firms, HqContract contract, CancellationToken ct) => Integrity.CheckAsync(firms.Get(firm), contract, ct));
// ---- data health: audited checks; automatic repairs go through the same write path as bills ----
firm.MapGet("/health/checks", (string firm, string? area, MK.Hq.Engine.Integrity.IntegrityService s, CancellationToken ct) => s.RunAsync(firm, area, ct));
firm.MapPost("/health/{check}/preview", (string firm, string check, MK.Hq.Engine.Integrity.IntegrityService s, CancellationToken ct) => s.PreviewRepairAsync(firm, check, ct));
firm.MapPost("/health/{check}/repair", (string firm, string check, RepairRun body, MK.Hq.Engine.Integrity.IntegrityService s, CancellationToken ct) =>
    s.RepairAsync(firm, check, body.OperationId, body.ExpectedHash, body.Decided, body.Key, body.Choice, ct));
firm.MapGet("/health/log", (string firm, MK.Hq.Engine.Integrity.IntegrityService s) => s.Log(firm));
firm.MapPost("/contract/accept", async (string firm, FirmRegistry firms, HqContract contract, CancellationToken ct) => await contract.CheckAsync(firms.Get(firm), acceptCurrent: true, ct));

// ---- drafts (work in progress, never in HQ) ----
firm.MapGet("/drafts", (string firm, EngineStore store) => store.Drafts(firm));
firm.MapGet("/drafts/{id}", (string firm, string id, EngineStore store) => store.Draft(id) is { } d && d.Firm == firm ? Results.Ok(d) : Results.NotFound());
firm.MapPut("/drafts/{id}", (string firm, string id, DraftBody body, EngineStore store) =>
    store.SaveDraft(id, firm, body.Kind, body.Title, body.Payload.GetRawText(), body.Version));
firm.MapDelete("/drafts/{id}", (string firm, string id, EngineStore store) => store.DeleteDraft(id, firm) ? Results.NoContent() : Results.NotFound());

CaptureEndpoints.Map(firm, api);
api.MapGet("/operations", (string? firm, int? take, EngineStore store) => store.Operations(firm, take ?? 100));

// ---- live change feed: the browser refreshes the moment anyone (HQ or MK) saves ----
firm.MapGet("/events", async (string firm, HttpContext ctx, FirmRegistry firms, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var f = firms.Get(firm);
    string? last = null;
    while (!ct.IsCancellationRequested)
    {
        try
        {
            await using var s = await f.OpenAsync(ct);
            var stamp = await s.ScalarAsync<string>(Catalogue.StampSql + " + '|' + (SELECT CONCAT(COUNT(*),'.',ISNULL(MAX(Doc_Id),0),'.',ISNULL(SUM(CAST(ModifyCnt AS bigint)),0)) FROM dbo.IQDoc_Header)");
            if (stamp != last)
            {
                if (last is not null) await ctx.Response.WriteAsync($"event: changed\ndata: {{\"at\":\"{DateTime.UtcNow:O}\"}}\n\n", ct);
                last = stamp;
            }
            else await ctx.Response.WriteAsync(": ping\n\n", ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
        catch (OperationCanceledException) { break; }
        catch (HqException) { await ctx.Response.WriteAsync("event: offline\ndata: {}\n\n", ct); }
        try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch (OperationCanceledException) { break; }
    }
});

MK.Hq.Api.Agent.AgentEndpoints.Map(app, api);

app.MapFallbackToFile("index.html");
app.Run();

public sealed record DraftBody(string Kind, string Title, JsonElement Payload, int Version);

public sealed record CleanupAnswer(long ItemId, CleanupDecision Decision);
public sealed record CleanupRuleAnswer(string Rule, string? Decision);
public sealed record CleanupRun(string? BatchId, int? Take);
public sealed record RepairRun(string OperationId, string ExpectedHash, bool Decided = false, string? Key = null, string? Choice = null);

public partial class Program;
