using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MK.Hq.Engine;
using MK.Hq.Engine.Configuration;

namespace MK.Hq.Tests.Support;

/// <summary>
/// Fresh restores of the production backups, used for every test that writes. The reference databases
/// (ISTMBM26/ISTMP26) are never written by tests.
/// </summary>
public static class ScratchDatabase
{
    public const string Backups = @"C:\MK_WORKSPACE\Context\Databases";
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task RestoreAsync(string name, string backup)
    {
        await Gate.WaitAsync();
        try
        {
            await using var cn = new SqlConnection(TestDatabase.ConnectionString("master"));
            await cn.OpenAsync();
            var dataDir = await cn.ExecuteScalarAsync<string>("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400))") ?? @"C:\sql-lab-data\";
            var files = (await cn.QueryAsync("RESTORE FILELISTONLY FROM DISK=@bak", new { bak = Path.Combine(Backups, backup) }))
                .Select(r => (Logical: (string)r.LogicalName, Type: (string)r.Type)).ToList();
            var moves = string.Join(", ", files.Select(f =>
                $"MOVE '{f.Logical}' TO '{Path.Combine(dataDir, name + (f.Type == "L" ? "_log.ldf" : ".mdf"))}'"));
            await cn.ExecuteAsync($"""
                IF DB_ID('{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END
                RESTORE DATABASE [{name}] FROM DISK=@bak WITH {moves}, REPLACE, RECOVERY;
                """, new { bak = Path.Combine(Backups, backup) }, commandTimeout: 600);
            SqlConnection.ClearAllPools();
        }
        finally { Gate.Release(); }
    }

    public static ServiceProvider Engine(string firmId, string database, string? dataDirectory = null, Action<HqOptions>? configure = null)
    {
        var options = new HqOptions
        {
            Server = TestDatabase.Server,
            DataDirectory = dataDirectory ?? Path.Combine(Path.GetTempPath(), "mkhq-tests", Guid.NewGuid().ToString("N")),
            RequireHqDocumentLocks = false,
            MachineName = "MK",
            Firms = [new FirmOptions { Id = firmId, Name = firmId.ToUpperInvariant(), Database = database, PeriodStart = new DateOnly(2026, 4, 1), PeriodEnd = new DateOnly(2027, 3, 31) }],
        };
        configure?.Invoke(options);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddHqEngine(options);
        return services.BuildServiceProvider();
    }
}
