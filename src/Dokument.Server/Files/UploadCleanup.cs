using Dokument.Infrastructure.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Server.Files;

/// <summary>
/// Hourly: removes uploads abandoned for 24 h and releases their quota (docs/design.md §8.1). The server owns the temp
/// files, so this runs in the server role rather than as a worker job.
/// </summary>
public sealed partial class UploadCleanup(IServiceScopeFactory scopes, ILogger<UploadCleanup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var removed = await scope.ServiceProvider.GetRequiredService<UploadService>().ExpireAsync(stoppingToken);
                if (removed > 0)
                {
                    LogRemoved(removed);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogFailed(e);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} expired uploads")]
    private partial void LogRemoved(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleaning up expired uploads failed")]
    private partial void LogFailed(Exception exception);
}
