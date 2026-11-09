using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;

namespace MK.Hq.Engine.Capture;

public sealed record AiSettingsView(bool KeyConfigured, string KeyHint, string Model, IReadOnlyList<string> Models, string Source);

/// <summary>
/// Google Gemini API key and Gemma model choice. The key is encrypted with Windows DPAPI (CurrentUser) at rest and is
/// never returned to a browser. GEMINI_API_KEY in the environment overrides the stored key.
/// </summary>
public sealed class AiSettings
{
    public static readonly IReadOnlyList<string> Models = ["gemma-4-26b-a4b-it", "gemma-4-31b-it"];
    private readonly string _path;
    private readonly object _gate = new();
    private string? _key;
    private string _model = Models[0];
    private string _source = "none";

    public AiSettings(HqOptions options)
    {
        _path = Path.Combine(options.DataDirectory, "ai-settings.json");
        Load(options);
    }

    public string? ApiKey { get { lock (_gate) return _key; } }
    public string Model { get { lock (_gate) return _model; } }

    public AiSettingsView View()
    {
        lock (_gate)
            return new AiSettingsView(_key is not null, _key is null ? "" : "…" + _key[^4..], _model, Models, _source);
    }

    public void Update(string? apiKey, string? model)
    {
        lock (_gate)
        {
            if (model is not null)
            {
                Guard.Require(Models.Contains(model), "MODEL_INVALID", $"Choose one of: {string.Join(", ", Models)}");
                _model = model;
            }
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                apiKey = apiKey.Trim();
                Guard.Require(apiKey.Length is >= 20 and <= 200 && apiKey.All(c => c is > ' ' and < '\u007f'), "API_KEY_INVALID", "That does not look like a Google API key");
                _key = apiKey;
                _source = "settings";
            }
            Save();
        }
    }

    private void Load(HqOptions options)
    {
        if (File.Exists(_path))
        {
            var stored = JsonSerializer.Deserialize<Stored>(File.ReadAllText(_path));
            if (stored is not null)
            {
                if (Models.Contains(stored.Model)) _model = stored.Model;
                if (stored.ProtectedKey is { Length: > 0 }) { _key = Unprotect(stored.ProtectedKey); _source = "settings"; }
            }
        }
        else if (options.ImportAiSettingsFrom is { } legacy && File.Exists(legacy))
        {
            // One-time import from the previous capture app on this PC (same Windows user, same key).
            using var doc = JsonDocument.Parse(File.ReadAllText(legacy));
            if (doc.RootElement.TryGetProperty("apiKey", out var k) && k.GetString() is { Length: > 0 } key) { _key = key; _source = "imported"; }
            if (doc.RootElement.TryGetProperty("model", out var m) && Models.Contains(m.GetString() ?? "")) _model = m.GetString()!;
            if (_key is not null) Save();
        }
        if (Environment.GetEnvironmentVariable("GEMINI_API_KEY") is { Length: > 0 } env) { _key = env; _source = "environment"; }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(new Stored(_model, _key is null ? null : Protect(_key))));
    }

    private static string Protect(string value) => OperatingSystem.IsWindows()
        ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser))
        : "plain:" + value;

    private static string? Unprotect(string value)
    {
        if (value.StartsWith("plain:", StringComparison.Ordinal)) return value[6..];
        try
        {
            return OperatingSystem.IsWindows()
                ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser))
                : null;
        }
        catch (CryptographicException) { return null; } // stored by another Windows user: ask for the key again
    }

    private static readonly byte[] Entropy = "MK.Hq.AiSettings.v1"u8.ToArray();
    private sealed record Stored(string Model, string? ProtectedKey);
}
