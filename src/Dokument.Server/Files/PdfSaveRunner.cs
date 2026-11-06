using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Server.Files;

/// <summary>
/// Runs the <see cref="JobType.PdfSave"/> jobs (docs/design.md §8.4): adds the PDFs the worker made for a merge or
/// split to the drive. This is the server's own job, because the worker can't write originals (§10.4); it only hashes
/// and moves bytes, never parses them. Also forgets finished operations once a week has passed.
/// </summary>
public sealed partial class PdfSaveRunner(
    IServiceScopeFactory scopes,
    ILogger<PdfSaveRunner> logger
) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    private readonly string runnerId = $"{Environment.MachineName}/{Environment.ProcessId}/server";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPrune = DateTimeOffset.MinValue;
        try
        {
            while (true)
            {
                var ran = false;
                try
                {
                    ran = await RunNextAsync(stoppingToken);
                    if (DateTimeOffset.UtcNow >= nextPrune)
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        await scope
                            .ServiceProvider.GetRequiredService<PdfService>()
                            .PruneAsync(stoppingToken);
                        nextPrune = DateTimeOffset.UtcNow.AddHours(1);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogQueueError(e);
                }

                if (!ran)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<bool> RunNextAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var job = await queue.ClaimAsync(JobType.PdfSave, runnerId, Lease, stoppingToken);
        if (job is null)
        {
            return false;
        }

        var pdf = scope.ServiceProvider.GetRequiredService<PdfService>();
        if (!Guid.TryParse(job.Subject, out var id))
        {
            await queue.FailAsync(
                job,
                "Not an operation id.",
                RetryPolicy.Default,
                CancellationToken.None
            );
            return true;
        }

        try
        {
            await pdf.SaveAsync(id, stoppingToken);
            await queue.SucceedAsync(job, CancellationToken.None);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            await queue.ReleaseAsync(job, CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            LogFailed(e, id);
            await queue.FailAsync(
                job,
                $"{e.GetType().Name}: {e.Message}",
                RetryPolicy.Default,
                CancellationToken.None
            );
            if (job.Attempts >= RetryPolicy.Default.MaxAttempts)
            {
                await using var failing = scopes.CreateAsyncScope();
                await failing
                    .ServiceProvider.GetRequiredService<PdfService>()
                    .FailAsync(id, "The new files couldn't be saved.", CancellationToken.None);
            }
        }

        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Saving PDF operation {Id} failed")]
    private partial void LogFailed(Exception exception, Guid id);

    [LoggerMessage(Level = LogLevel.Error, Message = "PDF save queue error; retrying shortly")]
    private partial void LogQueueError(Exception exception);
}
