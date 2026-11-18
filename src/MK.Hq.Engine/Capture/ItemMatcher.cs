using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MK.Hq.Engine.Hq;

namespace MK.Hq.Engine.Capture;

public sealed record MatchCandidate(long ItemId, long UnitId, string Name, string Unit, decimal SaleRate, decimal PurchaseRate, decimal Stock, double Score, IReadOnlyList<string> Notes);

public sealed record LineMatch(IReadOnlyList<MatchCandidate> Candidates, MatchCandidate? Chosen, string Reason);

/// <summary>
/// Matches written item descriptions to HQ catalogue items. Words are weighted by how rare they are in the catalogue
/// (so "zinc" counts more than "paint"), spelling slips are tolerated, and sizes/packs are treated as facts: a 4 ltr
/// request never silently becomes the 1 ltr tin. Choices the user confirmed before are remembered as aliases.
/// Nothing is chosen unless the best candidate is clearly ahead; the user always reviews.
/// </summary>
public sealed partial class ItemMatcher
{
    private sealed record Entry(CatalogueItem Item, string[] Words, Spec Spec, double Weight);
    private readonly Entry[] _entries;
    private readonly Dictionary<string, double> _idf;

    public ItemMatcher(IEnumerable<CatalogueItem> items)
    {
        var active = items.Where(i => i.Active).ToList();
        var docs = active.Select(i => (Item: i, Words: Words(i.Name + " " + i.Code))).ToList();
        var df = new Dictionary<string, int>();
        foreach (var d in docs) foreach (var w in d.Words.Distinct()) df[w] = df.GetValueOrDefault(w) + 1;
        _idf = df.ToDictionary(kv => kv.Key, kv => Math.Log(1 + (double)docs.Count / kv.Value));
        _entries = docs.Select(d => new Entry(d.Item, d.Words, ParseSpec(d.Item.Name), d.Words.Distinct().Sum(Idf))).ToArray();
    }

    private double Idf(string w) => _idf.TryGetValue(w, out var v) ? v : Math.Log(1 + _entries.Length + 1.0);

    /// <param name="history">How often each item appeared on this party's earlier HQ bills (or, for sales, overall). A strong,
    /// data-driven prior: a supplier keeps selling the same items, whatever their bill calls them.</param>
    public LineMatch Match(ExtractedLine line, IReadOnlyDictionary<string, (long ItemId, long UnitId)> aliases, int take = 5,
        IReadOnlyDictionary<long, int>? history = null)
    {
        foreach (var key in new[] { AliasKey(line.SourceText), AliasKey(line.Name) })
            if (aliases.TryGetValue(key, out var hit) && _entries.FirstOrDefault(e => e.Item.ItemId == hit.ItemId && e.Item.UnitId == hit.UnitId) is { } learned)
            {
                var c = Candidate(learned, 1.0, ["chosen for this wording before"]);
                var rest = Rank(line, take, history).Where(r => r.ItemId != c.ItemId).Take(take - 1);
                return new LineMatch([c, .. rest], c, "learned");
            }

        var ranked = Rank(line, take, history);
        if (ranked.Count == 0) return new LineMatch([], null, "no similar item in HQ");
        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1].Score : 0;
        var conflict = best.Notes.Any(n => n.StartsWith("size differs", StringComparison.Ordinal)
            || n.StartsWith("different product", StringComparison.Ordinal) || n.StartsWith("different material", StringComparison.Ordinal));
        var known = best.Notes.Any(n => n.StartsWith("on this party", StringComparison.Ordinal));
        // A party's usual item is taken on a smaller lead, but only when the words fit it as well as anything else.
        var fits = best.Notes.Contains(BestWording);
        var chosen = !conflict && ((best.Score >= 0.72 && best.Score - second >= 0.12) || (known && fits && best.Score >= 0.5 && best.Score - second >= 0.08)) ? best : null;
        return new LineMatch(ranked, chosen,
            chosen is null ? (conflict ? "specification differs; choose" : "several possible items; choose") : known ? "usual item from this party" : "clear best match");
    }

    private static readonly Regex ChargeWords = new(@"\b(p\s*&\s*f|c\s*&\s*f|packing|packaging|forwarding|freight|transport|transportation|cartage|hamali|loading|unloading|courier|delivery charge)", RegexOptions.IgnoreCase);

    private List<MatchCandidate> Rank(ExtractedLine line, int take, IReadOnlyDictionary<long, int>? history)
    {
        // Charge rows (P&F, C&F, freight…) only ever map to the shop's charge items, never to products.
        if (ChargeWords.IsMatch(line.SourceText) || ChargeWords.IsMatch(line.Name))
            return _entries.Where(e => ChargeWords.IsMatch(e.Item.Name))
                .Select(e => (E: e, Score: 0.6 + Boost(e, history) + (line.GstRate is { } g && g == e.Item.Cgst + e.Item.Sgst ? 0.05 : 0)))
                .OrderByDescending(x => x.Score).Take(take)
                .Select(x => Candidate(x.E, Math.Round(Math.Min(x.Score, 0.99), 3), ["charge row"])).ToList();
        var text = line.Name + " " + (line.Name == line.SourceText ? "" : line.SourceText);
        var q = Words(line.Name);
        if (q.Length == 0) q = Words(line.SourceText);
        if (q.Length == 0) return [];
        var spec = ParseSpec(text);
        var implied = false;
        if (spec.Mm is null && spec.Inch is null && ImpliedSize(line.Name) is { } size)
        {
            spec = size.Mm ? spec with { Mm = Format(size.Value, "0.##") + " mm" } : spec with { Inch = Format(size.Value, "0.###") + " inch" };
            implied = true;
        }
        var qWeight = q.Distinct().Sum(Idf);
        var results = new List<(Entry E, double Score, double Text, List<string> Notes)>();
        var queryTypes = TypeWords(q);
        var queryMaterials = q.Where(Materials.Contains).ToHashSet();
        foreach (var e in _entries)
        {
            double matched = 0, itemMatched = 0;
            foreach (var w in q.Distinct())
            {
                var best = 0.0;
                foreach (var t in e.Words)
                {
                    var s = w == t ? 1.0
                        : Materials.Contains(w) && Materials.Contains(t) ? 0 // upvc is not "nearly" cpvc
                        : w.Length >= 3 && t.Length >= 3 && (t.StartsWith(w, StringComparison.Ordinal) || w.StartsWith(t, StringComparison.Ordinal)) ? 0.85
                        : w.Length >= 4 && t.Length >= 4 ? Similarity(w, t) is var sim && sim >= 0.75 ? sim * 0.8 : 0 : 0;
                    if (s > best) best = s;
                }
                matched += best * Idf(w);
            }
            if (matched <= 0) continue;
            foreach (var t in e.Words.Distinct()) if (q.Contains(t)) itemMatched += Idf(t);
            var recall = matched / qWeight;
            var precision = e.Weight > 0 ? itemMatched / e.Weight : 0;
            var score = 0.72 * recall + 0.28 * precision;
            var notes = new List<string>();
            foreach (var (label, a, b) in new[] { ("volume", spec.Volume, e.Spec.Volume), ("weight", spec.Mass, e.Spec.Mass), ("inch size", spec.Inch, e.Spec.Inch), ("mm size", spec.Mm, e.Spec.Mm) })
            {
                if (a is null || b is null) continue;
                if (a == b) score += 0.08;
                else { score *= 0.3; notes.Add($"size differs: written {a}{(implied && label.EndsWith("size", StringComparison.Ordinal) ? " (read as a size)" : "")}, item is {b} ({label})"); }
            }
            // Written in inches, catalogued in millimetres (or the other way round): compare via nominal sizes.
            if (spec.Inch is { } qi && e.Spec.Inch is null && e.Spec.Mm is { } im && NominalMm.TryGetValue(qi, out var mms))
            {
                if (NominalMatches(qi, im, queryMaterials, mms)) score += 0.08;
                else { score *= 0.3; notes.Add($"size differs: written {qi}, item is {im}"); }
            }
            else if (spec.Mm is { } qm && spec.Inch is null && e.Spec.Mm is null && e.Spec.Inch is { } ii && NominalMm.TryGetValue(ii, out var mms2))
            {
                if (NominalMatches(ii, qm, queryMaterials, mms2)) score += 0.08;
                else { score *= 0.3; notes.Add($"size differs: written {qm}, item is {ii}"); }
            }
            if (line.Hsn is { Length: >= 4 } h && e.Item.Hsn.Length >= 4 && h[..4] == e.Item.Hsn[..4]) score += 0.04;
            if (line.GstRate is { } g && g > 0) score += g == e.Item.Cgst + e.Item.Sgst ? 0.03 : -0.06;
            if (e.Item.Stock > 0) score += 0.01;
            // A product's type outweighs brand words: an "elbow" line never becomes a pipe because both say PRINCE.
            var itemTypes = TypeWords(e.Words);
            var penalty = 1.0;
            if (queryTypes.Count > 0 && !queryTypes.Overlaps(itemTypes))
            {
                penalty = 0.15;
                score *= penalty;
                notes.Add($"different product: {(itemTypes.Count == 0 ? "type not established" : string.Join("/", itemTypes))} vs {string.Join("/", queryTypes)}");
            }
            // So does the material: a UPVC tee is not a CPVC tee, however alike the words look.
            var itemMaterials = e.Words.Where(Materials.Contains).ToHashSet();
            if (queryMaterials.Count > 0 && itemMaterials.Count > 0 && !queryMaterials.Overlaps(itemMaterials))
            {
                penalty *= 0.5;
                score *= 0.5;
                notes.Add($"different material: {string.Join("/", itemMaterials)} vs {string.Join("/", queryMaterials)}");
            }
            var textScore = score;
            var boost = Boost(e, history);
            if (boost > 0)
            {
                score += boost * penalty * (notes.Any(n => n.StartsWith("size differs", StringComparison.Ordinal)) ? 0.3 : 1);
                notes.Add($"on this party's earlier bills ({history![e.Item.ItemId]}×)");
            }
            results.Add((e, Math.Min(score, 0.99), textScore, notes));
        }
        if (results.Count == 0) return [];
        // History breaks ties between plausible items; it must never hide the item the words fit best (a first purchase).
        var bestText = results.Max(r => r.Text);
        var close = results.Where(r => r.Text >= bestText - 0.04).ToList();
        foreach (var r in close)
            r.Notes.Insert(0, close.Count == 1 ? BestWording : $"one of {close.Count} items the words fit equally");
        var top = results.OrderByDescending(r => r.Score).Take(take).ToList();
        var leader = results.OrderByDescending(r => r.Text).ThenByDescending(r => r.Score).First();
        if (!top.Contains(leader)) top[^1] = leader;
        return top.Select(r => Candidate(r.E, Math.Round(r.Score, 3), r.Notes)).ToList();
    }

    private const string BestWording = "fits the written words best";

    /// <summary>Product-type nouns of a hardware, plumbing, electrical and paint shop (singular, as <see cref="Words"/> makes them).</summary>
    private static readonly HashSet<string> ProductTypes =
    [
        "pipe", "elbow", "bend", "tee", "coupler", "coupling", "socket", "union", "reducer", "bush", "bushing", "valve", "tap", "cock", "nipple",
        "adapter", "adaptor", "fta", "mta", "ftaw", "mtaw", "clamp", "cap", "plug", "flange", "trap", "cowl", "wye", "door", "filter", "tank",
        "wire", "cable", "switch", "socket", "holder", "bulb", "tube", "fan", "mcb", "board",
        "primer", "paint", "putty", "enamel", "emulsion", "distemper", "thinner", "varnish", "stainer", "brush", "roller",
        "nail", "screw", "bolt", "nut", "washer", "hinge", "lock", "handle", "cement", "solvent", "tape", "sheet", "rod", "joint", "junction", "gully",
    ];

    /// <summary>Nominal inch size → the millimetre sizes it is sold as (pressure/CPVC and PVC/SWR series differ).</summary>
    private static readonly Dictionary<string, string[]> NominalMm = new[]
    {
        ("0.5", new[] { 15, 16, 20 }), ("0.75", new[] { 20, 25 }), ("1", new[] { 25, 32 }), ("1.25", new[] { 32, 40 }), ("1.5", new[] { 40, 50 }),
        ("2", new[] { 50, 63 }), ("2.5", new[] { 65, 75 }), ("3", new[] { 80, 90 }), ("4", new[] { 100, 110 }), ("5", new[] { 125, 140 }), ("6", new[] { 150, 160 }),
    }.ToDictionary(x => x.Item1 + " inch", x => x.Item2.Select(m => m + " mm").ToArray());

    /// <summary>Pipe/fitting materials; each is a different product line with its own sizes and prices.</summary>
    private static readonly HashSet<string> Materials = ["upvc", "cpvc", "pvc", "gi", "swr", "ppr", "hdpe", "ms"];

    // Shop trade dimensions differ by material; e.g. 1 inch UPVC is 25 mm, PVC is 32 mm.
    // Keep the broader engineering table only for materials without an established shop rule.
    private static bool NominalMatches(string inch, string mm, HashSet<string> materials, string[] fallback)
    {
        var pressure = materials.Overlaps(["upvc", "cpvc", "gi"]);
        var drainage = materials.Overlaps(["pvc", "swr"]);
        if (!pressure && !drainage) return fallback.Contains(mm);
        var expected = inch switch
        {
            "0.5 inch" => pressure ? 15 : 20, "0.75 inch" => pressure ? 20 : 25,
            "1 inch" => pressure ? 25 : 32, "1.25 inch" => pressure ? 32 : 40,
            "1.5 inch" => pressure ? 40 : 50, "2 inch" => pressure ? 50 : 63,
            "2.5 inch" => 75, "3 inch" => 90, "4 inch" => 110, "5 inch" => 140, "6 inch" => 160,
            _ => 0,
        };
        return expected == 0 ? fallback.Contains(mm) : mm == expected + " mm";
    }

    private static HashSet<string> TypeWords(IEnumerable<string> words) =>
        words.Select(w => w switch { "ftaw" => "fta", "mtaw" => "mta", "bushing" => "bush", "coupler" => "coupling", "adaptor" => "adapter", _ => w })
            .Where(ProductTypes.Contains).ToHashSet();

    /// <summary>Up to +0.25 for items this party has billed before (log-scaled by how often).</summary>
    private static double Boost(Entry e, IReadOnlyDictionary<long, int>? history) =>
        history is not null && history.TryGetValue(e.Item.ItemId, out var n) && n > 0 ? 0.15 + 0.10 * Math.Min(1, Math.Log10(n + 1)) : 0;

    private static MatchCandidate Candidate(Entry e, double score, IReadOnlyList<string> notes) =>
        new(e.Item.ItemId, e.Item.UnitId, e.Item.Name, e.Item.Unit, e.Item.SaleRate, e.Item.PurchaseRate, e.Item.Stock, score, notes);

    // ------------------------------------------------------------------ text

    public static string AliasKey(string text) => string.Join(' ', Words(text));

    private static readonly Dictionary<string, string> UnitWords = new()
    {
        ["ltr"] = "l", ["lt"] = "l", ["ltrs"] = "l", ["litre"] = "l", ["litres"] = "l", ["liter"] = "l", ["liters"] = "l", ["lit"] = "l",
        ["kgs"] = "kg", ["kilo"] = "kg", ["kilogram"] = "kg", ["kilograms"] = "kg", ["gm"] = "g", ["gms"] = "g", ["gram"] = "g", ["grams"] = "g", ["grm"] = "g",
        ["mtr"] = "m", ["mtrs"] = "m", ["meter"] = "m", ["meters"] = "m", ["metre"] = "m", ["inches"] = "inch", ["in"] = "inch",
        ["l"] = "l", ["g"] = "g", ["m"] = "m", ["pcs"] = "", ["pc"] = "", ["nos"] = "", ["no"] = "", ["piece"] = "", ["pieces"] = "", ["qty"] = "",
    };

    /// <summary>Same part, different words, on both the catalogue and the written side.</summary>
    private static readonly Dictionary<string, string> Synonyms = new()
    {
        ["coupler"] = "coupling", ["couplers"] = "coupling", ["couplings"] = "coupling", ["couple"] = "coupling",
        ["bushing"] = "bush", ["bushings"] = "bush", ["bushes"] = "bush",
        ["ftaw"] = "fta", ["mtaw"] = "mta",
        ["tees"] = "tee", ["elbows"] = "elbow", ["bends"] = "bend", ["clamps"] = "clamp", ["valves"] = "valve", ["taps"] = "tap",
        ["nipples"] = "nipple", ["plange"] = "flange", ["sealent"] = "sealant",
    };

    public static string[] Words(string text)
    {
        var s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('“', '"').Replace('”', '"').Replace('″', '"');
        s = SplitPvc().Replace(s, "$1pvc"); // "U PVC", "C-PVC"
        s = DigitLetter().Replace(s, "$1 $2");
        s = LetterDigit().Replace(s, "$1 $2");
        s = NonWord().Replace(s, " ");
        var words = new List<string>();
        var previous = "";
        foreach (var raw in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = raw.Trim('.');
            if (w.Length == 0) continue;
            var afterNumber = previous.Length > 0 && char.IsDigit(previous[^1]);
            previous = w;
            // Counter shorthand: "4' pvc door L", "1' T joint". After a number, l stays litres.
            if (w == "l" && !afterNumber) { words.Add("elbow"); continue; }
            if (w == "t") { words.Add("tee"); continue; }
            if (Synonyms.TryGetValue(w, out var syn)) { words.Add(syn); continue; }
            if (UnitWords.TryGetValue(w, out var u)) { if (u.Length > 0) words.Add(u); continue; }
            if (w.Length == 1 && !char.IsDigit(w[0])) continue;
            if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal)) w = w[..^1];
            words.Add(w);
        }
        return [.. words];
    }

    private sealed record Spec(string? Volume, string? Mass, string? Inch, string? Mm);

    private static Spec ParseSpec(string text)
    {
        // In plumbing lists a handwritten 3/4' (or 3/4") means inches.
        // Counter notation for 2½" and 1¼": 2"1/2", 1"1/4 ".
        text = CompoundInch().Replace(text.Replace('”', '"').Replace('“', '"'), "$1 $2\"");
        var inchMarked = InchMark().Replace(text.Replace("\"", " inch "), "$1 inch ");
        var s = " " + string.Join(' ', Words(inchMarked)) + " ";
        string? Find(Regex re, Func<decimal, string, string> format)
        {
            var m = re.Match(s);
            if (!m.Success) return null;
            var n = Fraction(m.Groups[1].Value);
            return n is null ? null : format(n.Value, m.Groups[2].Value);
        }
        return new Spec(
            Find(VolumeRe(), (n, u) => (u == "ml" ? n : n * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " ml"),
            Find(MassRe(), (n, u) => (u == "g" ? n : n * 1000).ToString("0.##", CultureInfo.InvariantCulture) + " g"),
            Find(InchRe(), (n, _) => n.ToString("0.###", CultureInfo.InvariantCulture) + " inch"),
            Find(MmRe(), (n, _) => n.ToString("0.##", CultureInfo.InvariantCulture) + " mm"));
    }

    private static string Format(decimal n, string pattern) => n.ToString(pattern, CultureInfo.InvariantCulture);

    private static readonly HashSet<decimal> StandardMm = [15, 16, 20, 25, 32, 40, 50, 63, 65, 75, 90, 100, 110, 125, 140, 160, 180, 200, 250, 315];
    private static readonly HashSet<decimal> StandardInch = [0.25m, 0.375m, 0.5m, 0.75m, 1, 1.25m, 1.5m, 2, 2.5m, 3, 4, 5, 6];
    private static readonly HashSet<string> NotSizeBefore = ["sch", "schd", "schedule", "class", "cl", "pn", "grade", "no", "pack", "set", "of"];
    private static readonly HashSet<string> NotSizeAfter = ["kg", "g", "l", "ml", "m", "feet", "ft", "foot", "kgf", "bar", "pc", "pcs", "nos", "box", "bag", "set", "pair", "%", "x", "mtr"];

    /// <summary>
    /// Suppliers write sizes bare ("PRI AGRI 75 TEE", "UPVC 1 PIPE"). The first stand-alone number that is a standard pipe
    /// size is taken as one: 15–315 as millimetres, ¼–6 (incl. fractions) as inches. Schedule, pressure and pack numbers
    /// (SCHD-40, 4KG, 6MTR) are skipped.
    /// </summary>
    private static (decimal Value, bool Mm)? ImpliedSize(string text)
    {
        var w = Words(text);
        for (var i = 0; i < w.Length; i++)
        {
            if (!char.IsDigit(w[i][0])) continue;
            if (i > 0 && NotSizeBefore.Contains(w[i - 1])) continue;
            var token = w[i];
            var next = i + 1 < w.Length ? w[i + 1] : "";
            if (next.Contains('/') && char.IsDigit(next[0]) && !token.Contains('/')) { token += " " + next; next = i + 2 < w.Length ? w[i + 2] : ""; }
            if (NotSizeAfter.Contains(next)) continue;
            if (Fraction(token) is not { } n) continue;
            if (StandardMm.Contains(n)) return (n, true);
            if (StandardInch.Contains(n)) return (n, false);
        }
        return null;
    }

    private static decimal? Fraction(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        decimal total = 0;
        foreach (var p in parts)
        {
            var f = p.Split('/');
            if (f.Length == 2 && decimal.TryParse(f[0], NumberStyles.Number, CultureInfo.InvariantCulture, out var a) && decimal.TryParse(f[1], NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && b != 0) total += a / b;
            else if (decimal.TryParse(p, NumberStyles.Number, CultureInfo.InvariantCulture, out var v)) total += v;
            else return null;
        }
        return total;
    }

    private static double Similarity(string a, string b)
    {
        var row = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) row[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return 1 - (double)row[b.Length] / Math.Max(a.Length, b.Length);
    }

    [GeneratedRegex(@"(\d)\s*['’](?!['’])")] private static partial Regex InchMark();
    [GeneratedRegex(@"(\d+)\s*[""'’]\s*(\d/\d{1,2})\s*[""'’]?")] private static partial Regex CompoundInch();
    [GeneratedRegex(@"\b([uc])[\s\-.]*pvc\b")] private static partial Regex SplitPvc();
    [GeneratedRegex(@"(\d)([a-z])")] private static partial Regex DigitLetter();
    [GeneratedRegex(@"([a-z])(\d)")] private static partial Regex LetterDigit();
    [GeneratedRegex(@"[^\p{L}\p{N}/.]+")] private static partial Regex NonWord();
    private const string Num = @"((?:\d+ )?\d+(?:\.\d+)?(?:/\d+)?)";
    [GeneratedRegex(" " + Num + " (ml|l) ")] private static partial Regex VolumeRe();
    [GeneratedRegex(" " + Num + " (kg|g) ")] private static partial Regex MassRe();
    [GeneratedRegex(" " + Num + " (inch) ")] private static partial Regex InchRe();
    [GeneratedRegex(" " + Num + " (mm) ")] private static partial Regex MmRe();
}
