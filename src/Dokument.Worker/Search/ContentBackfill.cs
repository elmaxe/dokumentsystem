using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Search;

/// <summary>
/// On worker start, queues metadata and text for every blob the current pipelines haven't read (docs/design.md §10.1),
/// at background priority so new uploads go first. Covers files from before these stages and pipeline upgrades, and
/// files read while exiftool or Tesseract was missing, once they are installed.
/// </summary>
internal sealed partial class ContentBackfill(
    IServiceScopeFactory scopes,
    ITextRecognizer recognizer,
    TimeProvider time,
    ILogger<ContentBackfill> logger
) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var content = scope.ServiceProvider.GetRequiredService<ContentService>();
            await content.ForgetToolMissingAsync(stoppingToken);
            var queued = await content.EnqueueMissingAsync(time.GetUtcNow(), stoppingToken);
            if (await OcrReadyAsync(scope.ServiceProvider, stoppingToken))
            {
                queued += await content.EnqueueSkippedAsync(time.GetUtcNow(), stoppingToken);
            }

            queued += await scope
                .ServiceProvider.GetRequiredService<TranscriptService>()
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

    /// <summary>Whether text recognition is on and Tesseract runs, so files skipped for want of it can be read now.</summary>
    private async Task<bool> OcrReadyAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<SettingsStore>();
        if (!(await settings.GetAsync(SettingKeys.TextRecognition, ct)).Enabled)
        {
            return false;
        }

        try
        {
            await recognizer.EngineAsync(ct);
            return true;
        }
        catch (ToolMissingException)
        {
            return false;
        }
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Queued {Count} metadata, text and transcribe jobs for existing files"
    )]
    private static partial void LogQueued(ILogger logger, int count);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't queue metadata and text for existing files; they get them on the next start"
    )]
    private static partial void LogFailed(ILogger logger, Exception e);
}
