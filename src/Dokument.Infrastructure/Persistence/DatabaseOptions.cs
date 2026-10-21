using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Dokument.Infrastructure.Persistence;

public static class DatabaseOptions
{
    /// <summary>
    /// Uses <c>ConnectionStrings:Dokument</c> when set (Aspire sets it), otherwise builds one from the
    /// NAS-friendly <c>DB_HOST</c>, <c>DB_PORT</c>, <c>DB_USERNAME</c>, <c>DB_PASSWORD</c> and <c>DB_DATABASE</c> variables.
    /// </summary>
    public static string GetConnectionString(IConfiguration configuration)
    {
        var explicitConnectionString = configuration.GetConnectionString("Dokument");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            return explicitConnectionString;
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = configuration["DB_HOST"] ?? "localhost",
            Port = int.TryParse(configuration["DB_PORT"], out var port) ? port : 5432,
            Username = configuration["DB_USERNAME"] ?? "dokument",
            Password = configuration["DB_PASSWORD"],
            Database = configuration["DB_DATABASE"] ?? "dokument",
        };
        return builder.ConnectionString;
    }
}
