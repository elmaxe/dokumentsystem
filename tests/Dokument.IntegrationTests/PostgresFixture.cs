using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(Dokument.IntegrationTests.PostgresFixture))]

namespace Dokument.IntegrationTests;

/// <summary>
/// One container from our Postgres image (docs/design.md §13.1) for the whole run; each test app gets its own database.
/// Build the image first: <c>docker build -t dokumentsystem-postgres:ci docker/postgres</c>.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder(
        Environment.GetEnvironmentVariable("DOKUMENT_TEST_POSTGRES_IMAGE")
            ?? "dokumentsystem-postgres:ci"
    )
        .WithImagePullPolicy(_ => false)
        .Build();

    public async ValueTask InitializeAsync() => await container.StartAsync();

    public async ValueTask DisposeAsync() => await container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync()
    {
        var name = "test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(container.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE {name}", connection);
        await command.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(container.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }
}
