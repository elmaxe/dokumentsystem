using Dokument.Infrastructure.Files;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Server.Files;

/// <summary>
/// Runs the purge (docs/design.md §12) on start, every hour after, and soon after "Delete forever" or "Empty trash".
/// Hourly keeps "Deleted forever in 2 days" right to the hour. It deletes originals, so it runs in the server role,
/// the only one that can write to that volume (§10.4).
/// </summary>
public sealed partial class PurgeSchedule(
    IServiceScopeFactory scopes,
    PurgeSignal signal,
    ILogger<PurgeSchedule> logger
) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<PurgeService>()
                        .RunAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(e);
                }

                await signal.WaitAsync(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The purge failed; it runs again within the hour"
    )]
    private partial void LogFailed(Exception e);
}
