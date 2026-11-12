using System.Diagnostics;
using System.Text.RegularExpressions;
using MK.Hq.Domain;
using MK.Hq.Engine.Capture;

namespace MK.Hq.Api.Agent;

/// <summary>
/// Reads bill photos with Google Antigravity (<c>agy acp</c>) on the shop's own Google sign-in, so no Gemini API key is
/// needed. Each read is an ACP session of its own with no tools (<see cref="AgentHub.AskAsync"/>). The prompt and the
/// defensive parsing are the Gemma reader's (<see cref="GemmaExtractor.Prompt"/>, <see cref="GemmaExtractor.Parse"/>),
/// so the contract is unchanged: visible facts only, numbers checked, anything unclear becomes null plus a warning.
/// </summary>
public sealed class AntigravityBillReader(AgentHub hub) : IBillReader
{
    private const string NoTools = "Answer from the attached images only. Do not use any tools, files, terminal or web search.\n";

    public string Name => "antigravity";

    public async Task<ExtractionResult> ExtractAsync(IReadOnlyList<byte[]> pages, string kind, string? model = null, CancellationToken ct = default)
    {
        Guard.Require(pages.Count is > 0 and <= 10, "CAPTURE_PAGES", "A bill needs 1 to 10 pages");
        var clock = Stopwatch.StartNew();
        var nudge = false;
        HqException? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var (text, used) = await AskAsync(NoTools + GemmaExtractor.Prompt(kind, nudge, pages.Count), pages, TimeSpan.FromMinutes(5), ct);
                return new ExtractionResult(used is null ? "Antigravity" : $"Antigravity ({used})", GemmaExtractor.Parse(text), clock.ElapsedMilliseconds, null, null, attempt);
            }
            catch (HqException e) when (e.Code == "AI_REPLY_INVALID") { nudge = true; last = e; }
            catch (HqException e) when (e.Code is "AI_TIMEOUT" or "AI_SERVER" && attempt < 3) { last = e; await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); }
        }
        throw last!;
    }

    /// <summary>Same method as the Gemma reader: the pixels say whether the writing runs sideways; the model only picks
    /// which of the two turned versions reads correctly.</summary>
    public async Task<int> OrientationAsync(byte[] page, CancellationToken ct = default)
    {
        if (!ImageNormalizer.LooksSideways(page)) return 0;
        var a = ImageNormalizer.Normalize(page, 90, maxEdge: 900);
        var b = ImageNormalizer.Normalize(page, 270, maxEdge: 900);
        try
        {
            var (text, _) = await AskAsync(NoTools +
                "The two images (first = A, second = B) are the same page turned two different ways. In which one is the writing the right way up, readable left to right? Reply with exactly one letter: A or B.",
                [a, b], TimeSpan.FromMinutes(2), ct);
            var m = Regex.Matches(text.ToUpperInvariant(), @"\b([AB])\b");
            return m.Count > 0 && m[^1].Value == "B" ? 270 : 90;
        }
        catch (HqException) { return 90; }
    }

    private async Task<(string Text, string? Model)> AskAsync(string prompt, IReadOnlyList<byte[]> images, TimeSpan timeout, CancellationToken ct)
    {
        try { return await hub.AskAsync(prompt, images, timeout, ct); }
        catch (AcpException e) when (e.AuthRequired)
        {
            throw HqException.Unsupported("AI_SIGNIN", "Antigravity is not signed in on this PC. Open the Assistant (Alt+A), press Sign in, then press Read again.");
        }
        catch (AcpException e) when (e.Code is -2 or -3) { throw HqException.Unsupported("AI_NOT_INSTALLED", e.Message); }
        catch (AcpException e) { throw new HqException("AI_SERVER", $"Antigravity could not read the photo ({e.Message}); it is kept", FailureKind.Unavailable); }
        catch (TimeoutException) { throw new HqException("AI_TIMEOUT", "Antigravity took too long reading this photo; it is kept. Turn it upright or press Read again.", FailureKind.Unavailable); }
    }
}
