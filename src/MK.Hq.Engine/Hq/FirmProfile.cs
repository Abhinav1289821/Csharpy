using System.Collections.Concurrent;
using MK.Hq.Domain;
using MK.Hq.Domain.Gst;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine.Hq;

public sealed record TransactionType(
    int TtId, string Name, string VouchType, string Series, int SeriesType, int Firstor, int IncTax,
    int RoundedOffType, decimal RoundedOffTo, int ChargeSet, int StockPointId, int DefaultDays, int BranchDefault,
    int Trmode, int SubType, int Status, string Message, int RestartDaily)
{
    public TaxMode TaxMode => Firstor == 0 ? TaxMode.Interstate : TaxMode.Local;
    public PricingPolicy Policy(TaxBasis? overrideBasis = null) =>
        new(overrideBasis ?? (IncTax == 1 ? TaxBasis.Inclusive : TaxBasis.Exclusive), (RoundingDirection)RoundedOffType, RoundedOffTo);
}

public sealed record ChargeRow(int ChargeId, string Name, long AccountId, decimal Rate, bool IsAdd, int GridIndex);

public sealed record HqConfig(
    int SkipAudit, int SeparateAuditDatabase, int BillWise, int UpdateRecentRates, int UpdatePurchaseRates,
    int CostCodeGeneration, int GlobalSaleNumbers, int CreditLimitCheck, long DefaultUpiBank);

public sealed record PaymentMode(int Id, string Name, string Caption, int Order, int Status);

public sealed record Salesperson(int Id, string Name);

/// <summary>
/// A snapshot of the HQ configuration that decides how documents are written. Loaded per firm and cached
/// briefly. The engine refuses (rather than guesses) whenever HQ is configured in a way it has not verified.
/// </summary>
public sealed class FirmProfile
{
    public required HqFirm Firm { get; init; }
    public required HqConfig Config { get; init; }
    public required IReadOnlyDictionary<int, TransactionType> Types { get; init; }
    public required IReadOnlyDictionary<int, IReadOnlyList<ChargeRow>> ChargeSets { get; init; }
    public required IReadOnlyList<PaymentMode> PaymentModes { get; init; }
    public required IReadOnlyList<Salesperson> Salespeople { get; init; }
    public required string StateCaption { get; init; }
    public required DateTime LoadedAt { get; init; }

    public bool AuditEnabled => Config.SkipAudit == 0;

    public TransactionType Type(int ttId) =>
        Types.TryGetValue(ttId, out var t) ? t : throw HqException.Unsupported("HQ_CONFIGURATION_MISSING", $"HQ transaction type {ttId} is not configured in {Firm.Name}");

    public IReadOnlyList<ChargeRow> Charges(int set) => ChargeSets.TryGetValue(set, out var rows) ? rows : [];

    /// <summary>Settings the engine has not verified; writing under them could differ from HQ.</summary>
    public void RequireWritableProfile()
    {
        Guard.Supported(Config.SeparateAuditDatabase == 0, "HQ_CONFIGURATION_UNSUPPORTED", "HQ is set to keep its change audit in a separate database");
        Guard.Supported(Config.CostCodeGeneration == 0, "HQ_CONFIGURATION_UNSUPPORTED", "HQ cost-code generation is switched on");
        Guard.Supported(Config.UpdateRecentRates == 0 && Config.UpdatePurchaseRates == 0, "HQ_CONFIGURATION_UNSUPPORTED", "HQ is set to update item rates from bills");
        Guard.Supported(Config.GlobalSaleNumbers == 0, "HQ_CONFIGURATION_UNSUPPORTED", "HQ is set to one bill-number sequence across all sale types");
    }
}

public sealed class FirmProfiles(TimeProvider time)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (FirmProfile Profile, DateTimeOffset At)> _cache = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<FirmProfile> GetAsync(HqFirm firm, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(firm.Id, out var hit) && time.GetUtcNow() - hit.At < Ttl) return hit.Profile;
        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(firm.Id, out hit) && time.GetUtcNow() - hit.At < Ttl) return hit.Profile;
            await using var s = await firm.OpenAsync(ct);
            var profile = await LoadAsync(firm, s);
            _cache[firm.Id] = (profile, time.GetUtcNow());
            return profile;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate(string firmId) => _cache.TryRemove(firmId, out _);

    public static async Task<FirmProfile> LoadAsync(HqFirm firm, HqSession s)
    {
        var config = (await s.QueryAsync<HqConfig>("""
            SELECT CAST(ISNULL(DoNotSaveUserchanges,0) AS int) SkipAudit, CAST(ISNULL(SaveUserchangesAuditDatabase,0) AS int) SeparateAuditDatabase,
              CAST(ISNULL(Bill2Bill_Comp,0) AS int) BillWise, CAST(ISNULL(Update_RecRates,0) AS int) UpdateRecentRates,
              CAST(ISNULL(UpdateRecent_Pur_Rate_Disc_To_ItemMast,0) AS int) UpdatePurchaseRates, CAST(ISNULL(Enable_CostCode_Gen_Inward,0) AS int) CostCodeGeneration,
              CAST(ISNULL(SetSaleWiseUnique_BillNo,0) AS int) GlobalSaleNumbers, CAST(ISNULL(CheckCreditLimit,0) AS int) CreditLimitCheck,
              CAST(ISNULL(DefBankActID_ForUPI,0) AS bigint) DefaultUpiBank
            FROM dbo.Configs
            """)).SingleOrDefault() ?? throw HqException.Unsupported("HQ_CONFIGURATION_MISSING", "HQ Configs row is missing");

        var types = await s.QueryAsync<TransactionType>("""
            SELECT CAST(TT_ID AS int) TtId, RTRIM(ISNULL(Ttype,'')) Name, RTRIM(ISNULL(Vouchtype,'')) VouchType, RTRIM(ISNULL(Series,'')) Series,
              CAST(ISNULL(SeriesType,0) AS int) SeriesType, CAST(ISNULL(Firstor,0) AS int) Firstor, CAST(ISNULL(Inc_Tax,0) AS int) IncTax,
              CAST(ISNULL(RoundedOffType,0) AS int) RoundedOffType, CAST(ISNULL(RoundedOffTo,0) AS decimal(9,2)) RoundedOffTo,
              CAST(ISNULL(vousetno,0) AS int) ChargeSet, CAST(ISNULL(Sp_id,0) AS int) StockPointId, CAST(ISNULL(Def_Days,0) AS int) DefaultDays,
              CAST(ISNULL(BranchId_Def,0) AS int) BranchDefault, CAST(ISNULL(Trmode,0) AS int) Trmode, CAST(ISNULL(SubType,0) AS int) SubType,
              CAST(ISNULL(Tt_Status,0) AS int) Status, LTRIM(RTRIM(ISNULL(Message,''))) Message, CAST(ISNULL(CustomSeries_RestartNoEveryDay,0) AS int) RestartDaily
            FROM dbo.TTypes
            """);

        var charges = await s.QueryAsync<(int SetNo, int ChargeId, string Name, long AccountId, decimal Rate, int Op, int GridIndex)>("""
            SELECT CAST(cs.Set_No AS int), CAST(c.Chrg_Id AS int), RTRIM(ISNULL(c.Chrg_Name,'')), CAST(ISNULL(c.Act_ID,0) AS bigint),
              CAST(ISNULL(c.Chrg_Rate,0) AS decimal(19,4)), CAST(ISNULL(ct.ChTp_Op,0) AS int), CAST(ISNULL(cs.Grd_Indx,0) AS int)
            FROM dbo.IQCHRGSETS cs JOIN dbo.IQCHARGES c ON cs.Chrg_Id=c.Chrg_Id JOIN dbo.IQCHRG_TYPES ct ON ct.ChTp_ID=c.Chrg_Type
            ORDER BY cs.Set_No, cs.Grd_Indx, c.Chrg_Id
            """);

        var modes = await s.QueryAsync<PaymentMode>(
            "SELECT CAST(ModeID AS int) Id, RTRIM(ISNULL(Mode_OriginalName,'')) Name, RTRIM(ISNULL(Mode_CaptionName,'')) Caption, CAST(ISNULL(Mode_Ord,0) AS int) [Order], CAST(ISNULL(Mode_Active,0) AS int) Status FROM dbo.Payment_Modes ORDER BY Mode_Ord, ModeID");
        var people = await s.QueryAsync<Salesperson>("SELECT CAST(SM_ID AS int) Id, RTRIM(ISNULL(SM_Name,'')) Name FROM dbo.Salesmen WHERE ISNULL(SM_Status,0)=0 ORDER BY SM_Name");

        return new FirmProfile
        {
            Firm = firm,
            Config = config,
            Types = types.ToDictionary(t => t.TtId),
            ChargeSets = charges.GroupBy(c => c.SetNo).ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ChargeRow>)g.Select(c => new ChargeRow(c.ChargeId, c.Name, c.AccountId, c.Rate, c.Op == 1, c.GridIndex)).ToList()),
            PaymentModes = modes,
            Salespeople = people,
            // Both firms are in Andhra Pradesh; HQ's fresh account form uses this caption when a master is blank.
            StateCaption = "37-Andhra Pradesh",
            LoadedAt = DateTime.UtcNow,
        };
    }
}
