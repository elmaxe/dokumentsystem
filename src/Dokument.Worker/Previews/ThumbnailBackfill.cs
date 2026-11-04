using Dokument.Domain.Settings;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Previews;

/// <summary>
/// On worker start, queues thumbnails for every blob without them from the current pipeline (docs/design.md §10.1),
/// at background priority so new uploads go first. Covers files from before this stage and pipeline upgrades. When
/// playable copies are made in the background (§14), also queues the copies still missing.
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

            // An address in the configuration is never saved, so this is where documents from before it get previews.
            if (scope.ServiceProvider.GetRequiredService<OfficeAddress>().Configured is not null)
            {
                await scope
                    .ServiceProvider.GetRequiredService<PreviewService>()
                    .EnqueueOfficeAsync(time.GetUtcNow(), stoppingToken);
            }

            var seekPreviews = await scope
                .ServiceProvider.GetRequiredService<MediaService>()
                .EnqueueMissingSeekPreviewsAsync(time.GetUtcNow(), stoppingToken);
            if (seekPreviews > 0)
            {
                LogSeekPreviewsQueued(logger, seekPreviews);
            }

            var videos = await scope
                .ServiceProvider.GetRequiredService<SettingsStore>()
                .GetAsync(SettingKeys.Videos, stoppingToken);
            if (videos.Copies == VideoCopies.Background)
            {
                var copies = await scope
                    .ServiceProvider.GetRequiredService<MediaService>()
                    .EnqueueMissingCopiesAsync(time.GetUtcNow(), stoppingToken);
                if (copies > 0)
                {
                    LogCopiesQueued(logger, copies);
                }
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
        Level = LogLevel.Information,
        Message = "Queued playable copies for {Count} existing videos"
    )]
    private static partial void LogCopiesQueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Queued seek previews for {Count} existing videos"
    )]
    private static partial void LogSeekPreviewsQueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't queue thumbnails for existing files; they get them on the next start"
    )]
    private static partial void LogFailed(ILogger logger, Exception e);
}
