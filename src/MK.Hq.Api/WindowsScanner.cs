using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MK.Hq.Domain;

namespace MK.Hq.Api;

public sealed record ScannerDevice(string Id, string Name);
public sealed record ScannerSelection(string DeviceId);

/// <summary>Runs Windows Image Acquisition in a separate STA process so a driver timeout can be interrupted.</summary>
public sealed class WindowsScanner(ILogger<WindowsScanner> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _transfer = new(1, 1);

    public async Task<ScannerDevice[]> ListAsync(CancellationToken ct) =>
        JsonSerializer.Deserialize<ScannerDevice[]>(await RunAsync(["list"], TimeSpan.FromSeconds(15), ct), Json) ?? [];

    public async Task<byte[]> ScanAsync(string? deviceId, CancellationToken ct)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(deviceId) && deviceId.Length <= 1024,
            "SCANNER_REQUIRED", "Choose a connected scanner");
        Guard.Require(await _transfer.WaitAsync(0, ct), "SCANNER_BUSY", "A scan is already in progress", FailureKind.Conflict);
        var path = Path.Combine(Path.GetTempPath(), $"mk-hq-scan-{Guid.NewGuid():N}.png");
        try
        {
            await RunAsync(["scan", deviceId!, path], TimeSpan.FromSeconds(90), ct);
            var file = new FileInfo(path);
            Guard.Require(file.Exists && file.Length > 0, "SCAN_EMPTY", "The scanner did not return a page", FailureKind.Unavailable);
            Guard.Require(file.Length <= 30L * 1024 * 1024, "SCAN_TOO_LARGE", "The scan is too large. Lower the scanner resolution and try again.");
            return await File.ReadAllBytesAsync(path, ct);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException e) { log.LogWarning(e, "Could not remove the temporary scanner image"); }
            catch (UnauthorizedAccessException e) { log.LogWarning(e, "Could not remove the temporary scanner image"); }
            finally { _transfer.Release(); }
        }
    }

    private async Task<string> RunAsync(string[] arguments, TimeSpan timeout, CancellationToken ct)
    {
        var executable = Environment.ProcessPath;
        Guard.Require(OperatingSystem.IsWindows() && executable is not null,
            "SCANNER_UNAVAILABLE", "Windows Image Acquisition is unavailable", FailureKind.Unavailable);
        var info = new ProcessStartInfo(executable!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        info.Environment.Remove("Hq__Password");
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(WindowsScanner).Assembly.GetName().Name + ".dll"));
        info.ArgumentList.Add("--scanner-worker");
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (Win32Exception e)
        {
            throw new HqException("SCANNER_UNAVAILABLE", "Windows could not start the scanner helper", FailureKind.Unavailable, inner: e);
        }

        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            await process.WaitForExitAsync();
            ct.ThrowIfCancellationRequested();
            throw new HqException("SCANNER_TIMEOUT", "The scanner did not finish. Check the scanner and close any other scanning app before retrying.", FailureKind.Unavailable);
        }

        var error = (await errors).Trim();
        if (process.ExitCode != 0)
        {
            log.LogWarning("Windows scanner helper failed: {ScannerError}", error);
            throw new HqException("SCANNER_FAILED", "Windows could not scan the page. Check the scanner connection and close any other scanning app before retrying.", FailureKind.Unavailable);
        }
        return (await output).Trim();
    }
}
