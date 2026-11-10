using Dokument.Infrastructure.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Backups;

/// <summary>
/// Runs database backups in the worker (docs/design.md §19): every few seconds it queues the scheduled backup when one
/// is due, then runs whatever is queued, including an admin's "Back up now". The worker has the backups folder and
/// only reads originals, so it is where the dump belongs.
/// </summary>
internal sealed partial class BackupRunner(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<BackupRunner> logger
) : BackgroundService
{
    /// <summary>How long "Back up now" waits at most before the worker picks it up.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = time.GetUtcNow();
        try
        {
            while (true)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
                    await backups.QueueDueAsync(started, stoppingToken);
                    if (await backups.ClaimAsync(stoppingToken) is { } backup)
                    {
                        await backups.RunAsync(backup, stoppingToken);
                        continue;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(e);
                }

                await Task.Delay(PollInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Checking for backups to run failed")]
    private partial void LogFailed(Exception e);
}
