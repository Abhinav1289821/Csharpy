using Microsoft.Extensions.DependencyInjection;
using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Documents;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Journal;
using MK.Hq.Engine.Plans;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Engine;

public static class EngineServices
{
    public static IServiceCollection AddHqEngine(this IServiceCollection services, HqOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IHqClock, IndiaClock>();
        services.AddSingleton<ProcedureCatalog>();
        services.AddSingleton<FirmRegistry>();
        services.AddSingleton<FirmProfiles>();
        services.AddSingleton<Catalogue>();
        services.AddSingleton(_ => new EngineStore(options.DataDirectory));
        services.AddSingleton<PlanExecutor>();
        services.AddSingleton<WriteCoordinator>();
        services.AddSingleton<DocumentResolver>();
        services.AddSingleton<DocumentService>();
        services.AddSingleton<DocumentMutations>();
        services.AddSingleton<Vouchers.VoucherService>();
        services.AddSingleton<Masters.ItemMasterService>();
        services.AddSingleton<Masters.AccountMasterService>();
        services.AddSingleton<Masters.ItemCleanupService>();
        services.AddSingleton<Integrity.IntegrityService>();
        services.AddSingleton<Reports.ReportService>();
        services.AddSingleton<HqContract>();
        services.AddSingleton<Capture.AiSettings>();
        services.AddSingleton(_ => new Capture.CaptureStore(options.DataDirectory));
        services.AddSingleton(_ => new Capture.GemmaExtractor(_.GetRequiredService<Capture.AiSettings>(), new HttpClient { Timeout = TimeSpan.FromMinutes(3) }));
        // The API host replaces this with Antigravity unless "Agent": { "ReadPhotos": false }.
        services.AddSingleton<Capture.IBillReader>(sp => sp.GetRequiredService<Capture.GemmaExtractor>());
        services.AddSingleton<Capture.CaptureService>();
        foreach (var registration in ExtraRegistrations) registration(services);
        return services;
    }

    /// <summary>Feature modules add themselves here (keeps this file stable as modules grow).</summary>
    internal static readonly List<Action<IServiceCollection>> ExtraRegistrations = [];
}
