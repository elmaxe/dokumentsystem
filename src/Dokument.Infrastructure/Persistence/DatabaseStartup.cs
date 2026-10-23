using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dokument.Infrastructure.Persistence;

public static class DatabaseStartup
{
    /// <summary>Extension versions this build is tested against. They must match docker/postgres/Dockerfile.</summary>
    public static readonly IReadOnlyDictionary<string, string> RequiredExtensions = new Dictionary<
        string,
        string
    >
    {
        ["pg_search"] = "0.20.6",
        ["vchord"] = "0.5.3",
    };

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Waits for Postgres, refuses to start if the installed extensions don't match (docs/design.md §13.1),
    /// then either applies migrations (server role) or waits until another process has applied them (worker role).
    /// </summary>
    public static async Task EnsureDatabaseAsync(
        IServiceProvider services,
        bool applyMigrations,
        CancellationToken ct = default
    )
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var logger = scope
            .ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DatabaseStartup));

        await WaitForConnectionAsync(db, logger, ct);
        await CheckExtensionsAsync(db, ct);

        if (applyMigrations)
        {
            logger.LogInformation("Applying database migrations");
            await db.Database.MigrateAsync(ct);
            return;
        }

        while ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
        {
            logger.LogInformation("Waiting for the server to apply database migrations");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task WaitForConnectionAsync(
        DokumentDbContext db,
        ILogger logger,
        CancellationToken ct
    )
    {
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        while (true)
        {
            try
            {
                if (await db.Database.CanConnectAsync(ct))
                {
                    return;
                }
            }
            catch (NpgsqlException) when (DateTimeOffset.UtcNow < deadline) { }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    "Could not connect to Postgres. Check DB_HOST, DB_USERNAME and DB_PASSWORD."
                );
            }

            logger.LogInformation("Waiting for Postgres");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    private static async Task CheckExtensionsAsync(DokumentDbContext db, CancellationToken ct)
    {
        var available = await db
            .Database.SqlQueryRaw<AvailableExtension>(
                "SELECT name AS \"Name\", default_version AS \"Version\" FROM pg_available_extensions"
            )
            .ToDictionaryAsync(e => e.Name, e => e.Version, ct);

        var problems = RequiredExtensions
            .Where(r => !available.TryGetValue(r.Key, out var version) || version != r.Value)
            .Select(r =>
                $"{r.Key} {r.Value} (found {available.GetValueOrDefault(r.Key) ?? "none"})"
            )
            .ToList();

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The database is missing required extensions: "
                    + string.Join(", ", problems)
                    + ". Use the dokumentsystem-postgres image that matches this release."
            );
        }
    }

    private sealed record AvailableExtension(string Name, string? Version);
}
