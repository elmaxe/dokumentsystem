using Dokument.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Dokument.UnitTests;

public class DatabaseOptionsTests
{
    [Fact]
    public void Builds_connection_string_from_nas_variables()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DB_HOST"] = "postgres",
            ["DB_PASSWORD"] = "secret",
        }).Build();

        var connection = new NpgsqlConnectionStringBuilder(DatabaseOptions.GetConnectionString(configuration));

        Assert.Equal("postgres", connection.Host);
        Assert.Equal(5432, connection.Port);
        Assert.Equal("dokument", connection.Username);
        Assert.Equal("secret", connection.Password);
        Assert.Equal("dokument", connection.Database);
    }

    [Fact]
    public void Prefers_explicit_connection_string()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Dokument"] = "Host=aspire;Database=x",
            ["DB_HOST"] = "postgres",
        }).Build();

        Assert.Equal("Host=aspire;Database=x", DatabaseOptions.GetConnectionString(configuration));
    }
}
