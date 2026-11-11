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

    public Func<string, JsonNode?, Task<JsonNode?>>? OnRequest { get; set; }
    public Action<string, JsonNode?>? OnNotification { get; set; }
    public event Action? Exited;
    public JsonNode? Initialize { get; private set; }
    public bool Alive => !_process.HasExited;

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
        return c;
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters, TimeSpan timeout, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timer.CancelAfter(timeout);
            await using (timer.Token.Register(() => tcs.TrySetCanceled()))
                return await tcs.Task;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The agent did not answer {method} within {timeout.TotalSeconds:0} s");
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, JsonNode? parameters) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

    private async Task SendAsync(JsonObject message)
    {
        var line = message.ToJsonString();
        await _write.WaitAsync();
        try
        {
            if (_process.HasExited) throw new AcpException(-1, "The Antigravity agent has stopped");
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
                var method = o["method"]?.GetValue<string>();
                var hasId = o.TryGetPropertyValue("id", out var idNode) && idNode is not null;
                if (method is not null && hasId) _ = Task.Run(() => AnswerAsync(idNode!.DeepClone(), method, o["params"]));
                else if (method is not null) Dispatch(method, o["params"]);
                else if (hasId && idNode!.GetValueKind() == JsonValueKind.Number && _pending.TryGetValue(idNode.GetValue<long>(), out var tcs))
                {
                    if (o["error"] is JsonObject err)
                        tcs.TrySetException(new AcpException(err["code"]?.GetValue<int>() ?? -1, err["message"]?.GetValue<string>() ?? "Agent error"));
                    else tcs.TrySetResult(o["result"]);
                }
            }
        }
        catch (Exception e) { _log.LogWarning(e, "agy acp: reading stopped"); }
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

    private void OnExit()
    {
        foreach (var p in _pending.Values) p.TrySetException(new AcpException(-1, "The Antigravity agent has stopped"));
        Exited?.Invoke();
    }

    public ValueTask DisposeAsync()
    {
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _process.Dispose();
        return ValueTask.CompletedTask;
    }
}
