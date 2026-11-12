namespace MK.Hq.Engine.Capture;

/// <summary>
/// Reads bill photos into <see cref="Extraction"/> facts. The service uses Google Antigravity (the shop's own Google sign-in,
/// see MK.Hq.Api.Agent.AntigravityBillReader); <see cref="GemmaExtractor"/> (Gemini API key) when "Agent": { "ReadPhotos": false }.
/// </summary>
public interface IBillReader
{
    /// <summary>The settings value that names this reader: "antigravity" or "gemma".</summary>
    string Name { get; }

    /// <summary>Reads one bill from its pages (normalised JPEGs, in order).</summary>
    Task<ExtractionResult> ExtractAsync(IReadOnlyList<byte[]> pages, string kind, string? model = null, CancellationToken ct = default);

    /// <summary>How many degrees (clockwise) a page must turn to be upright: 0, 90 or 270.</summary>
    Task<int> OrientationAsync(byte[] page, CancellationToken ct = default);
}
