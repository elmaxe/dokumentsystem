using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker;

/// <summary>The <c>worker</c> role: background jobs such as metadata, thumbnails and text extraction (docs/design.md §10).</summary>
public static class WorkerRole
{
    public static IServiceCollection AddWorkerRole(this IServiceCollection services)
    {
        services.AddHostedService<JobRunner>();
        return services;
    }
}

/// <summary>Placeholder for the Postgres-backed job queue (roadmap M2).</summary>
internal sealed partial class JobRunner(ILogger<JobRunner> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(logger);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker started; no job handlers are registered yet")]
    private static partial void LogStarted(ILogger logger);
}
