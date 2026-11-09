using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using MK.Hq.Domain;
using MK.Hq.Engine.Capture;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using QRCoder;

namespace MK.Hq.Api;

/// <summary>Runs Gemma extractions in the background (two at a time), so uploads return immediately.</summary>
public sealed class CaptureWorker(CaptureService captures, ILogger<CaptureWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        captures.RequeuePending();
        await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Consume(stoppingToken)));
    }

    private async Task Consume(CancellationToken ct)
    {
        await foreach (var (id, model) in captures.Queue.ReadAllAsync(ct))
        {
            try { await captures.ProcessAsync(id, model, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogError(e, "Capture worker failed on {Id}", id); }
        }
    }
}

/// <summary>Short-lived phone upload links. A code allows 40 photos for 20 minutes into one firm and bill type; nothing else.</summary>
public sealed class PhonePairing(HqOptions options)
{
    private sealed record Pair(string Firm, string Kind, DateTime ExpiresAt, int Remaining);
    private readonly ConcurrentDictionary<string, Pair> _pairs = new();
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(20);

    public IPAddress? LanAddress { get; } = FindLanAddress();
    public int Port => options.PhoneUploadPort;
    public bool Enabled => Port > 0 && LanAddress is not null;

    public (string Url, string Svg, DateTime ExpiresAt) Create(string firm, string kind)
    {
        Guard.Supported(Enabled, "PHONE_UPLOAD_OFF", "Phone upload needs this PC on the shop Wi-Fi/LAN");
        foreach (var (k, p) in _pairs) if (p.ExpiresAt < DateTime.UtcNow) _pairs.TryRemove(k, out _);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var expires = DateTime.UtcNow + Lifetime;
        _pairs[code] = new Pair(firm, kind, expires, 40);
        var url = $"http://{LanAddress}:{Port}/m/{code}";
        using var data = new QRCodeGenerator().CreateQrCode(url, QRCodeGenerator.ECCLevel.M);
        var svg = new SvgQRCode(data).GetGraphic(6);
        return (url, svg, expires);
    }

    public (string Firm, string Kind) Use(string code)
    {
        while (true)
        {
            if (!_pairs.TryGetValue(code, out var p) || p.ExpiresAt < DateTime.UtcNow || p.Remaining <= 0)
                throw HqException.NotFound("PHONE_LINK_EXPIRED", "This phone link has expired. Show a new QR code on the PC.");
            if (_pairs.TryUpdate(code, p with { Remaining = p.Remaining - 1 }, p)) return (p.Firm, p.Kind);
        }
    }

    public bool Valid(string code) => _pairs.TryGetValue(code, out var p) && p.ExpiresAt > DateTime.UtcNow && p.Remaining > 0;

    private static IPAddress? FindLanAddress() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => n.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => a.Address))
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsPrivate(a));

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }
}

public static class CaptureEndpoints
{
    private static async Task<byte[]> Body(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);
            var file = form.Files.FirstOrDefault() ?? throw HqException.Invalid("IMAGE_REQUIRED", "Choose a photo");
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, ct);
        Guard.Require(body.Length > 0, "IMAGE_REQUIRED", "Choose a photo");
        return body.ToArray();
    }

    public static void Map(RouteGroupBuilder firm, RouteGroupBuilder api)
    {
        var c = firm.MapGroup("/captures");
        c.MapGet("", (string firm, CaptureStore store) => store.List(firm));
        c.MapPost("", async (string firm, string kind, HttpRequest req, CaptureService s, CancellationToken ct) => s.Add(firm, kind, "desktop", await Body(req, ct)));
        c.MapGet("/{id}", (string firm, string id, CaptureStore store) => store.Get(id) is { } r && r.Firm == firm ? Results.Ok(r) : Results.NotFound());
        c.MapGet("/{id}/pages/{page:int}", (string firm, string id, int page, CaptureStore store) =>
            store.Get(id) is { } r && r.Firm == firm ? Results.File(store.Image(id, page), "image/jpeg") : Results.NotFound());
        c.MapPost("/{id}/pages", async (string firm, string id, HttpRequest req, CaptureService s, CancellationToken ct) => s.AddPage(firm, id, await Body(req, ct)));
        c.MapDelete("/{id}/pages/{page:int}", (string firm, string id, int page, CaptureService s) => s.RemovePage(firm, id, page));
        c.MapPost("/{id}/pages/{page:int}/rotate", (string firm, string id, int page, int degrees, bool? reread, CaptureService s) => s.Rotate(firm, id, page, degrees, reread ?? true));
        c.MapPost("/{id}/retry", (string firm, string id, string? model, CaptureService s, CaptureStore store) =>
        {
            Guard.Require(store.Get(id)?.Firm == firm, "CAPTURE_NOT_FOUND", "That photo is no longer here", FailureKind.NotFound);
            return s.Retry(id, model);
        });
        c.MapGet("/{id}/extraction", (string firm, string id, CaptureStore store) =>
            store.Get(id)?.Firm == firm && store.Extraction(id) is { } json ? Results.Content(json, "application/json") : Results.NotFound());
        c.MapPost("/{id}/learn", (string firm, string id, LearnRequest body, CaptureService s) => { s.Learn(firm, id, body); return Results.NoContent(); });
        c.MapDelete("/{id}", (string firm, string id, CaptureService s) => { s.Discard(firm, id); return Results.NoContent(); });
        c.MapPost("/pair", (string firm, string kind, PhonePairing pairing, FirmRegistry firms) =>
        {
            firms.Get(firm);
            Guard.Require(CaptureService.Kinds.Contains(kind), "CAPTURE_KIND", "Choose purchase, sale or estimate");
            var (url, svg, expires) = pairing.Create(firm, kind);
            return new { url, svg, expiresAt = expires };
        });

        api.MapGet("/settings/ai", (AiSettings s, PhonePairing p) => new { ai = s.View(), phone = new { enabled = p.Enabled, address = p.LanAddress?.ToString(), port = p.Port } });
        api.MapPut("/settings/ai", (AiUpdate body, AiSettings s) => { s.Update(body.ApiKey, body.Model); return s.View(); });
    }

    /// <summary>The only routes served on the LAN port: the phone page and its upload. Everything else is 404.</summary>
    public static void MapPhone(WebApplication app, int port)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.LocalPort == port && !ctx.Request.Path.StartsWithSegments("/m"))
            {
                ctx.Response.StatusCode = 404;
                return;
            }
            await next();
        });
        app.MapGet("/m/{code}", (string code, PhonePairing pairing, FirmRegistry firms) =>
            Results.Content(pairing.Valid(code) ? PhonePage : ExpiredPage, "text/html; charset=utf-8"));
        app.MapPost("/m/{code}/upload", async (string code, HttpRequest req, PhonePairing pairing, CaptureService s, CancellationToken ct) =>
        {
            var (firm, kind) = pairing.Use(code);
            var record = s.Add(firm, kind, "phone", await Body(req, ct));
            return new { id = record.Id };
        }).DisableAntiforgery();
    }

    public sealed record AiUpdate(string? ApiKey, string? Model);

    private const string ExpiredPage = """
        <!doctype html><meta name=viewport content="width=device-width,initial-scale=1"><title>MK photo upload</title>
        <body style="font:18px system-ui;padding:24px"><h2>Link expired</h2><p>Show a new QR code on the shop PC and scan it again.</p>
        """;

    private const string PhonePage = """
        <!doctype html><html lang=en><meta charset=utf-8><meta name=viewport content="width=device-width,initial-scale=1"><title>MK photo upload</title>
        <style>body{font:17px system-ui;margin:0;padding:18px;background:#f4f6f8;color:#14202b}h2{margin:0 0 6px}
        label.big{display:block;background:#0f766e;color:#fff;text-align:center;padding:22px;border-radius:12px;font-size:20px;font-weight:600;margin:16px 0}
        input{display:none}.item{background:#fff;border-radius:10px;padding:10px;margin:8px 0;display:flex;gap:10px;align-items:center}
        .item img{width:56px;height:56px;object-fit:cover;border-radius:6px}.ok{color:#1a7f37}.err{color:#b42318}small{color:#5d6b78}</style>
        <h2>MK Billing</h2><small>Photos go to the shop PC. One bill per photo; for a long bill add its next page on the PC.</small>
        <label class=big>📷 Take photo of bill<input id=cam type=file accept="image/*" capture=environment></label>
        <label class=big style="background:#334155">🖼 Choose from gallery<input id=gal type=file accept="image/*" multiple></label>
        <div id=list></div>
        <script>
        const list=document.getElementById('list');
        async function send(file){const row=document.createElement('div');row.className='item';const img=document.createElement('img');img.src=URL.createObjectURL(file);
          const t=document.createElement('div');t.textContent='Sending…';row.append(img,t);list.prepend(row);
          try{const r=await fetch(location.pathname+'/upload',{method:'POST',body:file,headers:{'Content-Type':file.type||'application/octet-stream'}});
            if(!r.ok){const e=await r.json().catch(()=>({}));throw new Error(e.message||('Error '+r.status));}
            t.innerHTML='<b class=ok>Sent ✓</b><br><small>Reading on the PC…</small>';}catch(e){t.innerHTML='<b class=err>Not sent</b><br><small>'+e.message+'</small>';}}
        for(const id of ['cam','gal'])document.getElementById(id).addEventListener('change',e=>{for(const f of e.target.files)send(f);e.target.value='';});
        </script>
        """;
}
