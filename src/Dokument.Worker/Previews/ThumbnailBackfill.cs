using Dokument.Infrastructure.Previews;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Previews;

/// <summary>
/// On worker start, queues thumbnails for every blob without them from the current pipeline (docs/design.md §10.1),
/// at background priority so new uploads go first. Covers files from before this stage and pipeline upgrades.
/// </summary>
internal sealed partial class ThumbnailBackfill(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<ThumbnailBackfill> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var queued = await scope
                .ServiceProvider.GetRequiredService<PreviewService>()
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
        Message = "Queued thumbnails for {Count} existing files"
    )]
    private static partial void LogQueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't queue thumbnails for existing files; they get them on the next start"
    )]
    private static partial void LogFailed(ILogger logger, Exception e);
}
