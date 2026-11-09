using Microsoft.Data.SqlClient;

namespace MK.Hq.Tests;

/// <summary>Connection to the restored vendor databases on this workstation (read-only use unless noted).</summary>
public static class TestDatabase
{
    public static string Server => Environment.GetEnvironmentVariable("MKHQ_TEST_SERVER") ?? @"localhost\SQLEXPRESS";

    public static string ConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = Server,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        ApplicationName = "MK.Hq.Tests",
        Pooling = true,
    }.ConnectionString;

    public static readonly string[] VendorDatabases = ["ISTMBM26", "ISTMP26"];
}
