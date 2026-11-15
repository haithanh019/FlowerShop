using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace FlowerShop.IntegrationTests.Helpers;

internal sealed class TestApiFactory(string connection)
    : WebApplicationFactory<FlowerShop.API.Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        var sql = new SqlConnectionStringBuilder(connection);

        if (!string.Equals(
                sql.InitialCatalog,
                "FlowerShop_Test",
                StringComparison.OrdinalIgnoreCase)
            || sql.MultipleActiveResultSets)
        {
            throw new InvalidOperationException(
                "Use FlowerShop_Test with MARS disabled.");
        }

        builder.UseEnvironment("Testing");

        builder.ConfigureHostConfiguration(config =>
            config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:DefaultConnection"] = connection,
                    ["Jwt:Issuer"] = "FlowerShop.Tests",
                    ["Jwt:Audience"] = "FlowerShop.Tests.Client",
                    ["Jwt:Key"] = Convert.ToBase64String(
                        RandomNumberGenerator.GetBytes(48))
                }));

        return base.CreateHost(builder);
    }
}
