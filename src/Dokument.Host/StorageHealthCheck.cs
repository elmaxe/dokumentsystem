using Dokument.Infrastructure.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Dokument.Host;

/// <summary>Fails readiness when a data volume is not mounted, so a missing NAS share is caught early.</summary>
internal sealed class StorageHealthCheck(IOptions<StorageOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default
    )
    {
        var missing = options.Value.All.Where(path => !Directory.Exists(path)).ToList();
        return Task.FromResult(
            missing.Count == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    "Missing data directories: " + string.Join(", ", missing)
                )
        );
    }
}
