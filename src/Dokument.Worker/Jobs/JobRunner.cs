using System.Diagnostics;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Worker.Jobs;

/// <summary>
/// Runs queued jobs (docs/design.md §10.1): for each registered type, as many loops as its concurrency allows, each
/// claiming one job at a time from Postgres. Several worker containers can share the queue.
/// </summary>
internal sealed partial class JobRunner(
    IServiceScopeFactory scopes,
    IEnumerable<JobHandlerRegistration> registrations,
    IOptions<WorkerOptions> options,
    ILogger<JobRunner> logger
) : BackgroundService
{
    private static readonly TimeSpan PruneAfter = TimeSpan.FromDays(7);

    private readonly string workerId = $"{Environment.MachineName}/{Environment.ProcessId}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var loops = registrations
            .SelectMany(r =>
                Enumerable
                    .Range(0, Math.Max(1, r.Concurrency))
                    .Select(_ => RunLoopAsync(r, stoppingToken))
            )
            .Append(PruneLoopAsync(stoppingToken))
            .ToList();
        LogStarted(
            logger,
            workerId,
            string.Join(", ", registrations.Select(r => $"{r.Type} ×{r.Concurrency}"))
        );
        await Task.WhenAll(loops);
    }

    private async Task RunLoopAsync(
        JobHandlerRegistration registration,
        CancellationToken stoppingToken
    )
    {
        try
        {
            while (true)
            {
                var idle = TimeSpan.Zero;
                try
                {
                    if (!await RunNextAsync(registration, stoppingToken))
                    {
                        idle = options.Value.PollInterval;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // The queue itself failed, e.g. the database is restarting. Back off and try again.
                    LogQueueError(logger, e, registration.Type.Name);
                    idle = options.Value.PollInterval * 5;
                }

                if (idle > TimeSpan.Zero)
                {
                    await Task.Delay(idle, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Claims and runs one job. False when nothing was due.</summary>
    private async Task<bool> RunNextAsync(
        JobHandlerRegistration registration,
        CancellationToken stoppingToken
    )
    {
        await using var scope = scopes.CreateAsyncScope();
        var queue = scope.ServiceProvider.GetRequiredService<JobQueue>();
        var timeout = options.Value.JobTimeout;
        var job = await queue.ClaimAsync(
            registration.Type,
            workerId,
            lease: timeout * 2,
            stoppingToken
        );
        if (job is null)
        {
            return false;
        }

        var handler = (IJobHandler)
            scope.ServiceProvider.GetRequiredService(registration.HandlerType);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        deadline.CancelAfter(timeout);

        // One span per run (docs/design.md §22.1), linked to the trace that queued the job. The subject is left out: it
        // can identify the file's content.
        using var activity = DokumentTelemetry.ActivitySource.StartActivity(
            $"job {job.Type.Name}",
            ActivityKind.Consumer,
            parentContext: default,
            links: ActivityContext.TryParse(job.TraceParent, null, out var queuedBy)
                ? [new ActivityLink(queuedBy)]
                : null
        );
        activity?.SetTag("job.type", job.Type.Name);
        activity?.SetTag("job.id", job.Id);
        activity?.SetTag("job.attempt", job.Attempts);
        var started = Stopwatch.GetTimestamp();
        var outcome = "succeeded";
        try
        {
            await handler.RunAsync(job, deadline.Token);
            await queue.SucceedAsync(job, CancellationToken.None);
            LogSucceeded(logger, job.Type.Name, job.Subject, job.Attempts);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            outcome = "released";
            await queue.ReleaseAsync(job, CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            outcome = deadline.IsCancellationRequested ? "timed_out" : "failed";
            var error = deadline.IsCancellationRequested
                ? $"Timed out after {timeout}."
                : $"{e.GetType().Name}: {e.Message}";
            activity?.SetStatus(ActivityStatusCode.Error, e.GetType().Name);
            activity?.SetTag("error.type", e.GetType().FullName);
            DokumentTelemetry.JobsFailed.Add(
                1,
                new KeyValuePair<string, object?>("job.type", job.Type.Name)
            );
            LogFailed(logger, e, job.Type.Name, job.Subject, job.Attempts);
            await queue.FailAsync(job, error, RetryPolicy.Default, CancellationToken.None);
        }
        finally
        {
            activity?.SetTag("job.outcome", outcome);
            DokumentTelemetry.JobDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("job.type", job.Type.Name),
                new KeyValuePair<string, object?>("job.outcome", outcome)
            );
        }

        return true;
    }

    private async Task PruneLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope
                        .ServiceProvider.GetRequiredService<JobQueue>()
                        .PruneAsync(PruneAfter, stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogQueueError(logger, e, "prune");
                }

                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Worker {WorkerId} started: {Handlers}")]
    private static partial void LogStarted(ILogger logger, string workerId, string handlers);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Job {Type} {Subject} done (attempt {Attempt})"
    )]
    private static partial void LogSucceeded(
        ILogger logger,
        string type,
        string subject,
        int attempt
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Job {Type} {Subject} failed (attempt {Attempt})"
    )]
    private static partial void LogFailed(
        ILogger logger,
        Exception e,
        string type,
        string subject,
        int attempt
    );

    [LoggerMessage(Level = LogLevel.Error, Message = "Job queue error ({Type}); retrying shortly")]
    private static partial void LogQueueError(ILogger logger, Exception e, string type);
}
