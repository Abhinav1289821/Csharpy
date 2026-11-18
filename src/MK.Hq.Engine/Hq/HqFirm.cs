using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MK.Hq.Domain;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Hq;

/// <summary>One HQ firm-year database (e.g. MK Building Materials 2026-27 in ISTMBM26).</summary>
public sealed partial class HqFirm
{
    private readonly HqOptions _options;
    private readonly ProcedureCatalog _catalog;

    public HqFirm(FirmOptions firm, HqOptions options, ProcedureCatalog catalog)
    {
        Guard.Require(IdShape().IsMatch(firm.Id), "INVALID_CONFIGURATION", "Firm ids are short lowercase words");
        Guard.Require(DbShape().IsMatch(firm.Database), "INVALID_CONFIGURATION", "Database name is not valid");
        Options = firm;
        _options = options;
        _catalog = catalog;
        var (start, end) = Period(firm);
        PeriodStart = start;
        PeriodEnd = end;
        ConnectionString = new SqlConnectionStringBuilder
        {
            DataSource = options.Server,
            InitialCatalog = firm.Database,
            IntegratedSecurity = string.IsNullOrEmpty(options.UserId),
            UserID = options.UserId ?? "",
            Password = options.Password ?? "",
            TrustServerCertificate = true,
            Encrypt = SqlConnectionEncryptOption.Optional,
            ApplicationName = "MK HQ Billing",
            MinPoolSize = 1,
            MaxPoolSize = 32,
            ConnectTimeout = 8,
        }.ConnectionString;
    }

    public FirmOptions Options { get; }
    public string Id => Options.Id;
    public string Name => Options.Name;
    public string Database => Options.Database;
    public DateOnly PeriodStart { get; }
    public DateOnly PeriodEnd { get; }
    public string ConnectionString { get; }

    public Task<HqSession> OpenAsync(CancellationToken ct = default) =>
        HqSession.OpenAsync(ConnectionString, _catalog, _options.LockTimeoutSeconds, ct);

    public void RequireInPeriod(DateOnly date) =>
        Guard.Require(date >= PeriodStart && date <= PeriodEnd, "DATE_OUTSIDE_PERIOD",
            $"{date:dd-MM-yyyy} is outside {Name}'s financial year {PeriodStart:dd-MM-yyyy} to {PeriodEnd:dd-MM-yyyy}");

    /// <summary>The firm's financial year: as configured, else from the database name (ISTMBM26 → 1 Apr 2026 to 31 Mar 2027).
    /// Only one end configured gives the other a year away; before, a lone PeriodStart was silently replaced by the name's
    /// year (or refused for a name without one), and a start after the end let no date pass RequireInPeriod.</summary>
    public static (DateOnly Start, DateOnly End) Period(FirmOptions firm)
    {
        (DateOnly, DateOnly) period;
        if (firm.PeriodStart is { } s && firm.PeriodEnd is { } e) period = (s, e);
        else if (firm.PeriodStart is { } start) period = (start, start.AddYears(1).AddDays(-1));
        else if (firm.PeriodEnd is { } end) period = (end.AddYears(-1).AddDays(1), end);
        else
        {
            var m = YearSuffix().Match(firm.Database);
            Guard.Require(m.Success, "INVALID_CONFIGURATION", $"Set PeriodStart/PeriodEnd for {firm.Database}");
            var year = 2000 + int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            period = (new DateOnly(year, 4, 1), new DateOnly(year + 1, 3, 31));
        }
        Guard.Require(period.Item1 <= period.Item2, "INVALID_CONFIGURATION",
            $"{firm.Database}: the financial year starts {period.Item1:dd-MM-yyyy}, after its end {period.Item2:dd-MM-yyyy}");
        return period;
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{0,15}$")] private static partial Regex IdShape();
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")] private static partial Regex DbShape();
    [GeneratedRegex("(\\d{2})$")] private static partial Regex YearSuffix();
}

public sealed class FirmRegistry
{
    private readonly Dictionary<string, HqFirm> _firms;

    public FirmRegistry(HqOptions options, ProcedureCatalog catalog)
    {
        Guard.Require(options.Firms.Count > 0, "INVALID_CONFIGURATION", "No HQ firms are configured");
        _firms = options.Firms.Select(f => new HqFirm(f, options, catalog)).ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<HqFirm> All => _firms.Values;

    public HqFirm Get(string id) =>
        _firms.TryGetValue(id, out var firm) ? firm : throw HqException.NotFound("FIRM_NOT_FOUND", $"Unknown firm '{id}'");
}
