using System.Globalization;
using MK.Hq.Domain;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Plans;

/// <summary>India wall clock (HQ stores local strings, not UTC).</summary>
public interface IHqClock
{
    DateTime Now { get; }
}

public sealed class IndiaClock : IHqClock
{
    private static readonly TimeZoneInfo Ist = FindIst();
    public DateTime Now => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Ist);

    private static TimeZoneInfo FindIst()
    {
        foreach (var id in new[] { "India Standard Time", "Asia/Kolkata" })
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { }
        return TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromMinutes(330), "IST", "IST");
    }
}

/// <summary>HQ's duplicate-number rule, resolved to values, re-run in the same server batch as COMMIT.</summary>
public sealed record NumberCheck(int TtId, string Series, int Number, string Extension, long DocumentId, bool SeriesWide);

public sealed class PlanOutcome(IReadOnlyDictionary<string, object?> variables, DateTime executedAt, IReadOnlyList<NumberCheck> finalChecks)
{
    public IReadOnlyDictionary<string, object?> Variables { get; } = variables;
    public IReadOnlyList<NumberCheck> FinalChecks { get; } = finalChecks;
    public DateTime ExecutedAt { get; } = executedAt;
    public long Long(string name) => Convert.ToInt64(Variables[name], CultureInfo.InvariantCulture);
}

/// <summary>Executes a <see cref="WritePlan"/> on a session that already has an open transaction.</summary>
public sealed class PlanExecutor(IHqClock clock)
{
    public async Task<PlanOutcome> ExecuteAsync(HqSession session, WritePlan plan, int lockTimeoutMs, CancellationToken ct = default)
    {
        Guard.Invariant(session.Transaction is not null, "Plans run inside a transaction");
        var now = clock.Now;
        var vars = new Dictionary<string, object?>(StringComparer.Ordinal);
        var numberChecks = new List<AssertNumberFreeStep>();

        foreach (var resource in plan.Locks.Distinct().Order(StringComparer.Ordinal))
            await session.AppLockAsync(resource, lockTimeoutMs);

        foreach (var step in plan.Steps)
        {
            ct.ThrowIfCancellationRequested();
            switch (step)
            {
                case CallStep c:
                {
                    if (c.ReadCommitted) await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL READ COMMITTED");
                    var outputs = await session.CallAsync(c.Procedure, Resolve(c.Args, vars, now), ct);
                    if (c.ReadCommitted) await session.ExecuteAsync("SET TRANSACTION ISOLATION LEVEL SERIALIZABLE");
                    if (c.Capture is not null)
                        foreach (var (parameter, variable) in c.Capture)
                        {
                            var value = outputs.GetValueOrDefault(parameter);
                            Guard.Require(value is not null && Convert.ToDecimal(value, CultureInfo.InvariantCulture) > 0, "HQ_SAVE_NOT_VISIBLE",
                                $"{c.Procedure} did not return {parameter}; nothing was saved", FailureKind.Unavailable);
                            vars[variable] = value;
                        }
                    break;
                }
                case SqlStep s:
                {
                    var args = Resolve(s.Args, vars, now);
                    if (s.CaptureScalar is null) await session.ExecuteAsync(s.Sql, Dapper(args));
                    else vars[s.CaptureScalar] = await session.ScalarAsync<object?>(s.Sql, Dapper(args));
                    break;
                }
                case AllocateNumberStep n:
                    vars[n.Capture] = await AllocateAsync(session, n, lockTimeoutMs);
                    break;
                case AssertNumberFreeStep a:
                    await AssertFreeAsync(session, a, vars, now);
                    numberChecks.Add(a);
                    break;
                case CopyRowsStep r:
                {
                    var rows = await session.RowsAsync(r.SourceSql, Dapper(Resolve(r.SourceArgs, vars, now)));
                    Guard.Require(rows.Count > 0, "HQ_SAVE_NOT_VISIBLE", $"{r.Label}: the saved rows could not be read back", FailureKind.Unavailable);
                    var overrides = Resolve(r.Overrides, vars, now);
                    foreach (var row in rows)
                    {
                        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                        foreach (var column in r.Columns)
                        {
                            Guard.Invariant(row.ContainsKey(column), $"{r.Label}: source has no column {column}");
                            var parameter = r.RenameColumns?.GetValueOrDefault(column) ?? "m" + column;
                            args[parameter] = row[column];
                        }
                        foreach (var (key, value) in overrides) args[key] = value;
                        await session.CallAsync(r.Procedure, args, ct);
                    }
                    break;
                }
                case VerifyStep v:
                {
                    var actual = await session.ScalarAsync<object?>(v.Sql, Dapper(Resolve(v.Args, vars, now)));
                    Guard.Require(Same(actual, v.Expected), v.Code, v.Message, FailureKind.Conflict);
                    break;
                }
                default:
                    throw HqException.Internal($"Unknown plan step {step.GetType().Name}");
            }
        }

        await session.AssertIntactAsync();
        // HQ's duplicate rule runs once more inside the COMMIT batch itself (HqSession.CommitAsync).
        var finals = numberChecks.Select(a => new NumberCheck(a.TtId, a.Series, Convert.ToInt32(Resolve(a.Number, vars, now), CultureInfo.InvariantCulture),
            a.Extension, Convert.ToInt64(Resolve(a.DocumentId, vars, now), CultureInfo.InvariantCulture), a.SeriesWide)).ToList();
        return new PlanOutcome(vars, now, finals);
    }

    private static async Task<int> AllocateAsync(HqSession session, AllocateNumberStep n, int lockTimeoutMs)
    {
        // Serialises every MK terminal on this series. HQ PCs do not take the lock; they are handled by the
        // number re-checks and by never holding a range lock on IQDoc_Header (READCOMMITTEDLOCK).
        await session.AppLockAsync($"MK:number:{session.Database}:{n.TtId}:{n.Series}", lockTimeoutMs);
        var next = await session.ScalarAsync<int>(
            "SELECT ISNULL(MAX(TNo),0)+1 FROM dbo.IQDoc_Header WITH (READCOMMITTEDLOCK) WHERE Tt_Id=@tt AND TSeries=@series",
            new { tt = n.TtId, series = n.Series });
        for (var guard = 0; guard < 200; guard++, next++)
        {
            var taken = await session.ScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.IQDoc_Header WITH (READCOMMITTEDLOCK) WHERE TSeries=@series AND TNo=@no AND ISNULL(TExtn,'')=@ext",
                new { series = n.Series, no = next, ext = n.Extension });
            if (taken == 0) return next;
        }
        throw HqException.Conflict("HQ_NUMBER_TAKEN", $"No free number found in series {n.Series}");
    }

    private static async Task AssertFreeAsync(HqSession session, AssertNumberFreeStep a, Dictionary<string, object?> vars, DateTime now)
    {
        var number = Resolve(a.Number, vars, now);
        var doc = Resolve(a.DocumentId, vars, now);
        var taken = await session.ScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.IQDoc_Header WITH (READCOMMITTEDLOCK) WHERE TSeries=@series AND TNo=@no AND ISNULL(TExtn,'')=@ext AND Doc_Id<>@doc AND (@wide=1 OR Tt_Id=@tt)",
            new { series = a.Series, no = number, ext = a.Extension, doc, wide = a.SeriesWide ? 1 : 0, tt = a.TtId });
        Guard.Require(taken == 0, "HQ_NUMBER_TAKEN", $"{a.Series}/{number} was just used by another PC", FailureKind.Conflict, new { number });
    }

    private static bool Same(object? actual, object expected)
    {
        if (actual is null) return false;
        if (expected is string es) return string.Equals(Convert.ToString(actual, CultureInfo.InvariantCulture)?.Trim(), es.Trim(), StringComparison.Ordinal);
        return Convert.ToDecimal(actual, CultureInfo.InvariantCulture) == Convert.ToDecimal(expected, CultureInfo.InvariantCulture);
    }

    private static Dapper.DynamicParameters Dapper(IReadOnlyDictionary<string, object?> args)
    {
        var p = new Dapper.DynamicParameters();
        foreach (var (k, v) in args) p.Add("@" + k.TrimStart('@'), v is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : v);
        return p;
    }

    private static Dictionary<string, object?> Resolve(IReadOnlyDictionary<string, object?> args, Dictionary<string, object?> vars, DateTime now) =>
        args.ToDictionary(kv => kv.Key, kv => Resolve(kv.Value, vars, now), StringComparer.OrdinalIgnoreCase);

    private static object? Resolve(object? value, Dictionary<string, object?> vars, DateTime now) => value switch
    {
        Ref r => vars.TryGetValue(r.Name, out var v) ? v : throw HqException.Internal($"Plan variable {r.Name} is not set"),
        Interp i => System.Text.RegularExpressions.Regex.Replace(i.Template, @"\{(\w+)\}", m =>
            vars.TryGetValue(m.Groups[1].Value, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : throw HqException.Internal($"Plan variable {m.Groups[1].Value} is not set")),
        Clock { Field: ClockField.Time } => now.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        Clock { Field: ClockField.Day } => now.Date,
        Clock { Field: ClockField.Stamp } => new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Millisecond),
        Clock { Field: ClockField.CompactDay } => int.Parse(now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture),
        _ => value,
    };
}

