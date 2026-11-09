using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MK.Hq.Engine.Plans;

/// <summary>A value produced by an earlier step (an identity HQ assigns, a bill number).</summary>
public sealed record Ref(string Name)
{
    public override string ToString() => "$" + Name;
}

/// <summary>Text with {variable} placeholders filled from plan variables (e.g. a narration that contains the bill number).</summary>
public sealed record Interp(string Template)
{
    public override string ToString() => "~" + Template;
}

public enum ClockField
{
    /// <summary>HQ's Bill_Time/Ent_Time text: 24-hour "HH:mm:ss" India time.</summary>
    Time,
    /// <summary>India calendar day at midnight (Ent_Date, VisitDate).</summary>
    Day,
    /// <summary>India wall-clock timestamp with milliseconds (Modify_Date).</summary>
    Stamp,
    /// <summary>yyyyMMdd as an integer (HQ's legacy compact dates).</summary>
    CompactDay,
}

/// <summary>Capture-time metadata, resolved when the plan executes, never part of the reviewed content.</summary>
public sealed record Clock(ClockField Field)
{
    public override string ToString() => "@clock." + Field;
}

/// <summary>
/// A write to HQ expressed as data. The planner turns a reviewed request plus a snapshot of HQ into a plan;
/// the executor runs it inside one transaction. Because a plan is data:
/// <list type="bullet">
/// <item>its hash is the review fingerprint — posting re-plans from fresh HQ data and refuses unless the
/// hash is identical, so "what you reviewed" is provably "what gets written";</item>
/// <item>it can be shown to the user ("these HQ procedures with these values"), logged, and unit-tested
/// without a database.</item>
/// </list>
/// </summary>
public sealed record WritePlan(string Purpose, IReadOnlyList<PlanStep> Steps)
{
    /// <summary>SQL Server application-lock resources taken before the first step (sorted, deadlock-free).</summary>
    public IReadOnlyList<string> Locks { get; init; } = [];

    /// <summary>HQ documents to hold HQ's own .HQS edit lock on for the duration of the write.</summary>
    public IReadOnlyList<long> HqDocumentLocks { get; init; } = [];

    public string Hash => PlanHash.Of(this);
}

public abstract record PlanStep(string Label);

/// <summary>Run an allow-listed HQ procedure. <paramref name="Capture"/> maps output parameters to plan variables.</summary>
public sealed record CallStep(string Label, string Procedure, IReadOnlyDictionary<string, object?> Args, IReadOnlyDictionary<string, string>? Capture = null)
    : PlanStep(Label)
{
    /// <summary>
    /// Run this one procedure at READ COMMITTED inside the otherwise SERIALIZABLE transaction. Used for HQ's derived-stock
    /// refresh, whose joins could otherwise take key-range locks across IQDoc_Header and hold back an HQ PC's bill insert.
    /// </summary>
    public bool ReadCommitted { get; init; }
}

/// <summary>A plain statement HQ's own client issues (e.g. ADJUST rows, Cost_Calculated). Optional scalar capture.
/// Guards inside the SQL raise "MK:CODE|message" to abort the whole plan.</summary>
public sealed record SqlStep(string Label, string Sql, IReadOnlyDictionary<string, object?> Args, string? CaptureScalar = null)
    : PlanStep(Label);

/// <summary>HQ's GetMaxDocHeaderTableBillNo (MAX(TNo)+1 per type and series), moved past any number already used
/// series-wide, under an application lock shared by all MK terminals.</summary>
public sealed record AllocateNumberStep(string Label, int TtId, string Series, string Extension, string Capture)
    : PlanStep(Label);

/// <summary>HQ's IsDuplicateDocHeaderTable. Runs where placed and again immediately before COMMIT.</summary>
public sealed record AssertNumberFreeStep(string Label, int TtId, string Series, object Number, string Extension, object DocumentId, bool SeriesWide)
    : PlanStep(Label);

/// <summary>Reads rows (SourceSql) and calls an audit procedure once per row with m{Column} = value, then overrides.
/// This is how HQ's SaveData_For_UserChanges copies a saved document into its User_IQ* audit tables.</summary>
public sealed record CopyRowsStep(string Label, string Procedure, string SourceSql, IReadOnlyDictionary<string, object?> SourceArgs,
    IReadOnlyList<string> Columns, IReadOnlyDictionary<string, object?> Overrides, IReadOnlyDictionary<string, string>? RenameColumns = null)
    : PlanStep(Label);

/// <summary>A post-write assertion: the scalar must equal Expected, otherwise the transaction is rolled back.</summary>
public sealed record VerifyStep(string Label, string Sql, IReadOnlyDictionary<string, object?> Args, object Expected, string Code, string Message)
    : PlanStep(Label);

public static class PlanHash
{
    public static string Of(WritePlan plan)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("purpose", plan.Purpose);
            w.WriteStartArray("locks");
            foreach (var l in plan.Locks) w.WriteStringValue(l);
            w.WriteEndArray();
            w.WriteStartArray("docLocks");
            foreach (var d in plan.HqDocumentLocks) w.WriteNumberValue(d);
            w.WriteEndArray();
            w.WriteStartArray("steps");
            foreach (var step in plan.Steps) WriteStep(w, step);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
    }

    private static void WriteStep(Utf8JsonWriter w, PlanStep step)
    {
        w.WriteStartObject();
        w.WriteString("type", step.GetType().Name);
        w.WriteString("label", step.Label);
        switch (step)
        {
            case CallStep c:
                w.WriteString("proc", c.Procedure);
                WriteArgs(w, "args", c.Args);
                if (c.Capture is not null) WriteArgs(w, "capture", c.Capture.ToDictionary(k => k.Key, k => (object?)k.Value));
                break;
            case SqlStep s:
                w.WriteString("sql", s.Sql);
                WriteArgs(w, "args", s.Args);
                w.WriteString("capture", s.CaptureScalar);
                break;
            case AllocateNumberStep n:
                w.WriteNumber("tt", n.TtId); w.WriteString("series", n.Series); w.WriteString("ext", n.Extension); w.WriteString("capture", n.Capture);
                break;
            case AssertNumberFreeStep a:
                w.WriteNumber("tt", a.TtId); w.WriteString("series", a.Series); w.WriteString("number", Text(a.Number)); w.WriteString("ext", a.Extension);
                w.WriteString("doc", Text(a.DocumentId)); w.WriteBoolean("wide", a.SeriesWide);
                break;
            case CopyRowsStep r:
                w.WriteString("proc", r.Procedure); w.WriteString("sql", r.SourceSql); WriteArgs(w, "sourceArgs", r.SourceArgs);
                w.WriteString("columns", string.Join(',', r.Columns)); WriteArgs(w, "overrides", r.Overrides);
                if (r.RenameColumns is not null) WriteArgs(w, "rename", r.RenameColumns.ToDictionary(k => k.Key, k => (object?)k.Value));
                break;
            case VerifyStep v:
                w.WriteString("sql", v.Sql); WriteArgs(w, "args", v.Args); w.WriteString("expected", Text(v.Expected)); w.WriteString("code", v.Code);
                break;
        }
        w.WriteEndObject();
    }

    private static void WriteArgs(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, object?> args)
    {
        w.WriteStartObject(name);
        foreach (var (key, value) in args.OrderBy(a => a.Key, StringComparer.OrdinalIgnoreCase)) w.WriteString(key, Text(value));
        w.WriteEndObject();
    }

    public static string Text(object? value) => value switch
    {
        null => "null",
        string s => "s:" + s,
        decimal d => "n:" + d.ToString("0.############", CultureInfo.InvariantCulture),
        double d => "n:" + ((decimal)d).ToString("0.############", CultureInfo.InvariantCulture),
        int or long or short or byte => "n:" + Convert.ToString(value, CultureInfo.InvariantCulture),
        bool b => b ? "n:1" : "n:0",
        DateOnly d => "d:" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime d => "d:" + d.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
        Ref r => r.ToString(),
        Clock c => c.ToString(),
        Interp i => i.ToString(),
        _ => "?:" + value,
    };
}

