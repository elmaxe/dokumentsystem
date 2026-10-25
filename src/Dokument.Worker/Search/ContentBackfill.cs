using Dokument.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Search;

/// <summary>
/// On worker start, queues metadata and text for every blob the current pipelines haven't read (docs/design.md §10.1),
/// at background priority so new uploads go first. Covers files from before these stages and pipeline upgrades.
/// </summary>
internal sealed partial class ContentBackfill(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ContentBackfill> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var queued = await scope
                .ServiceProvider.GetRequiredService<ContentService>()
                .EnqueueMissingAsync(time.GetUtcNow(), stoppingToken);
            if (queued > 0)
            {
                LogQueued(logger, queued);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Queued {Count} metadata and text jobs for existing files"
    )]
    private static partial void LogQueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't queue metadata and text for existing files; they get them on the next start"
    )]
    private static partial void LogFailed(ILogger logger, Exception e);
}
