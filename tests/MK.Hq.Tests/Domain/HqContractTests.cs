using MK.Hq.Engine.Configuration;
using MK.Hq.Engine.Hq;
using MK.Hq.Engine.Sql;

namespace MK.Hq.Tests.Domain;

public sealed class HqContractTests
{
    [Fact]
    public async Task WritesRequireAContractCheckWhenNoVerifiedStateExists()
    {
        var options = new HqOptions
        {
            Firms = [new FirmOptions { Id = "mbm", Name = "Building Materials", Database = "MKHQ_Test26" }]
        };
        var catalog = new ProcedureCatalog();
        var firms = new FirmRegistry(options, catalog);
        var contract = new HqContract(options, catalog, firms);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => contract.RequireWritableAsync(firms.Get("mbm"), new CancellationToken(canceled: true)));
    }
}
