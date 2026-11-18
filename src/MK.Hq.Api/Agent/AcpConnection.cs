using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MK.Hq.Api.Agent;

/// <summary>An ACP error reply from the agent (JSON-RPC error object).</summary>
public sealed class AcpException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
    public bool AuthRequired => Code == -32000 && Message.Contains("Authentication", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One <c>agy acp</c> process: newline-delimited JSON-RPC 2.0 over stdin/stdout (Agent Client Protocol v1).
/// We are the client. Requests the agent sends us (permission prompts, file and terminal access) go to
/// <see cref="OnRequest"/>; notifications (streamed message chunks, tool calls, plans) go to <see cref="OnNotification"/>.
/// </summary>
public sealed class AcpConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _nextId;
    private int _stopped;

    // Process.HasExited throws once the Process is disposed, and a reader that stopped on a pipe error leaves the process
    // running but deaf. Track "stopped" ourselves so Alive is always safe to read and always means "can answer".
    private volatile bool _down;

    public Func<string, JsonNode?, Task<JsonNode?>>? OnRequest { get; set; }
    public Action<string, JsonNode?>? OnNotification { get; set; }
    public event Action? Exited;
    public JsonNode? Initialize { get; private set; }
    public bool Alive => !_down;

    private AcpConnection(Process process, ILogger log)
    {
        _process = process;
        _log = log;
    }

    public static async Task<AcpConnection> StartAsync(string exe, IEnumerable<string> args, string workingDirectory, ILogger log, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start " + exe);
        process.StandardInput.AutoFlush = false;
        var c = new AcpConnection(process, log);
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => c.OnExit();
        _ = Task.Run(c.ReadLoop);
        _ = Task.Run(c.ErrorLoop);
        try
        {
        c.Initialize = await c.RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = 1,
            // We never let the agent touch this PC's files or run commands: it works only through MK's own tools.
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject { ["name"] = "mk-hq", ["title"] = "MK Billing", ["version"] = typeof(AcpConnection).Assembly.GetName().Version?.ToString() ?? "1" },
        }, TimeSpan.FromSeconds(60), ct);
        }
        catch
        {
            // Without this an agent that never answered initialize (or a request that was cancelled) stays running, orphaned.
            await c.DisposeAsync();
            throw;
        }
        return c;
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(timeout);
        try
        {
            // The timeout covers the write too: an agent that stops reading stdin would otherwise block this write (and,
            // through the write lock, every other chat) forever. A write that cannot finish means the agent is wedged: stop it.
            var send = SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            try { await send.WaitAsync(timer.Token); }
            catch (OperationCanceledException) when (!send.IsCompleted)
            {
                if (!ct.IsCancellationRequested) Stop("write timed out");
                throw;
            }
            await using (timer.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The agent did not answer {method} within {timeout.TotalSeconds:0} s");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public async Task NotifyAsync(string method, JsonNode? parameters)
    {
        // Bounded like RequestAsync's write: an agent that stopped reading stdin must not hang the Stop button forever.
        var send = SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });
        try { await send.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (TimeoutException) { Stop("write timed out"); throw; }
    }

    private async Task SendAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        await _write.WaitAsync();
        try
        {
            if (_down) throw new AcpException(-1, "The Antigravity agent has stopped");
            await _process.StandardInput.WriteAsync(line + "\n");
            await _process.StandardInput.FlushAsync();
        }
        finally { _write.Release(); }
    }

    private async Task ReadLoop()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { _log.LogDebug("agy: {Line}", line); continue; }
                if (msg is not JsonObject o) continue;
                // One odd message (a non-integer error code, a string id) must not stop the reader for good.
                try { Handle(o); }
                catch (Exception e) { _log.LogWarning(e, "agy acp: could not handle {Line}", line.Length > 300 ? line[..300] : line); }
            }
        }
        catch (Exception e) { _log.LogWarning(e, "agy acp: reading stopped"); }
        finally { Stop("output closed"); }
    }

    private void Handle(JsonObject o)
    {
        var method = o["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        var hasId = o.TryGetPropertyValue("id", out var idNode) && idNode is not null;
        if (method is not null && hasId) _ = Task.Run(() => AnswerAsync(idNode!.DeepClone(), method, o["params"]));
        else if (method is not null) Dispatch(method, o["params"]);
        else if (hasId && idNode is JsonValue v && v.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var tcs))
        {
            if (o["error"] is JsonObject err)
            {
                var code = err["code"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : -1;
                var message = err["message"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "Agent error";
                tcs.TrySetException(new AcpException(code, message));
            }
            else tcs.TrySetResult(o["result"]);
        }
    }

    private void Dispatch(string method, JsonNode? parameters)
    {
        try { OnNotification?.Invoke(method, parameters); }
        catch (Exception e) { _log.LogWarning(e, "agy acp: notification {Method} failed", method); }
    }

    private async Task AnswerAsync(JsonNode id, string method, JsonNode? parameters)
    {
        JsonObject reply;
        try
        {
            var result = OnRequest is null ? throw new AcpException(-32601, "Not supported") : await OnRequest(method, parameters);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JsonObject() };
        }
        catch (AcpException e)
        {
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = e.Code, ["message"] = e.Message } };
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "agy acp: answering {Method} failed", method);
            reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32603, ["message"] = e.Message } };
        }
        try { await SendAsync(reply); } catch (Exception e) { _log.LogDebug(e, "agy acp: reply lost"); }
    }

    private async Task ErrorLoop()
    {
        try
        {
            while (await _process.StandardError.ReadLineAsync() is { } line)
                if (!string.IsNullOrWhiteSpace(line)) _log.LogDebug("agy: {Line}", line);
        }
        catch { /* process gone */ }
    }

    private void OnExit() => Stop("process exited");

    /// <summary>Runs once, whichever comes first: the process exits, its output closes, a write wedges, or we dispose.
    /// Every waiting request fails at once instead of hanging until its own timeout.</summary>
    private void Stop(string why)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;
        _down = true;
        _log.LogInformation("agy acp stopped: {Why}", why);
        try { _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        foreach (var p in _pending.Values) p.TrySetException(new AcpException(-1, "The Antigravity agent has stopped"));
        try { Exited?.Invoke(); } catch (Exception e) { _log.LogWarning(e, "agy acp: exit handler failed"); }
    }

    public ValueTask DisposeAsync()
    {
        Stop("disposed");
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
