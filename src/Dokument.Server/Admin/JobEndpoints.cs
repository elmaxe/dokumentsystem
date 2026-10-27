using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Jobs;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Admin;

/// <summary>
/// The admin Jobs page (docs/design.md §10.1): the queues, what the worker is doing by file, pause, and retry. Admins
/// see every job and the names of the files it is for, whoever owns them (decision 25).
/// </summary>
public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder admin)
    {
        admin.MapGet("/jobs/queues", GetQueues).WithName("AdminGetJobQueues");
        admin
            .MapPut("/jobs/queues/{type}/paused", PauseQueue)
            .WithName("AdminPauseJobQueue")
            .WithDescription("Paused queues start no new jobs; running ones finish.");
        admin
            .MapGet("/jobs", ListJobs)
            .WithName("AdminListJobs")
            .WithDescription(
                "Running jobs longest-running first, queued ones in the order they will run, failed ones latest first."
            );
        admin.MapPost("/jobs/{id:guid}/retry", Retry).WithName("AdminRetryJob");
        admin
            .MapPost("/jobs/retry-failed", RetryFailed)
            .WithName("AdminRetryFailedJobs")
            .WithDescription("Every failed job, or those of one type, with fresh attempts.");
        admin
            .MapDelete("/jobs/{id:guid}", Clear)
            .WithName("AdminClearJob")
            .WithDescription("Forgets a failed job without running it again.");
        return admin;
    }

    private static async Task<Ok<List<JobQueueDto>>> GetQueues(
        JobMonitor jobs,
        CancellationToken ct
    ) =>
        TypedResults.Ok(
            (await jobs.QueuesAsync(ct))
                .Select(q => new JobQueueDto(
                    JobKinds.Of(q.Type),
                    q.Running,
                    q.Waiting,
                    q.Failed,
                    q.DoneLastWeek,
                    q.Paused
                ))
                .ToList()
        );

    /// <param name="type">thumbnail, metadata or text.</param>
    private static async Task<Results<Ok<List<JobQueueDto>>, NotFound>> PauseQueue(
        string type,
        PauseQueueRequest request,
        HttpContext context,
        JobMonitor jobs,
        CancellationToken ct
    )
    {
        if (!JobKinds.TryParse(type, out var kind))
        {
            return TypedResults.NotFound();
        }

        await jobs.SetPausedAsync(JobKinds.TypeOf(kind), request.Paused, context.Actor(), ct);
        return await GetQueues(jobs, ct);
    }

    /// <param name="status">running, queued or failed.</param>
    /// <param name="type">thumbnail, metadata or text; all when left out.</param>
    private static async Task<Results<Ok<JobList>, ValidationProblem>> ListJobs(
        JobMonitor jobs,
        CancellationToken ct,
        string status = "running",
        string? type = null,
        int offset = 0,
        int limit = 50
    )
    {
        if (
            !Enum.TryParse<JobStatus>(status, ignoreCase: true, out var state)
            || !Enum.IsDefined(state)
            || state == JobStatus.Done
        )
        {
            return Invalid("status", "Use running, queued or failed.");
        }

        JobKind? kind = null;
        if (type is not null)
        {
            if (!JobKinds.TryParse(type, out var parsed))
            {
                return Invalid("type", "Use thumbnail, metadata or text.");
            }

            kind = parsed;
        }

        limit = Math.Clamp(limit, 1, JobMonitor.MaxPage - 1);
        var page = await jobs.ListAsync(
            state,
            kind is { } k ? JobKinds.TypeOf(k) : null,
            offset,
            limit + 1,
            ct
        );
        return TypedResults.Ok(
            new JobList(page.Take(limit).Select(JobDto.From).ToList(), page.Count > limit)
        );
    }

    private static async Task<Results<NoContent, NotFound>> Retry(
        Guid id,
        JobMonitor jobs,
        CancellationToken ct
    ) => await jobs.RetryAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    /// <param name="type">thumbnail, metadata or text; all when left out.</param>
    private static async Task<Results<Ok<List<JobQueueDto>>, ValidationProblem>> RetryFailed(
        JobMonitor jobs,
        CancellationToken ct,
        string? type = null
    )
    {
        JobType? only = null;
        if (type is not null)
        {
            if (!JobKinds.TryParse(type, out var kind))
            {
                return Invalid("type", "Use thumbnail, metadata or text.");
            }

            only = JobKinds.TypeOf(kind);
        }

        await jobs.RetryFailedAsync(only, ct);
        return await GetQueues(jobs, ct);
    }

    private static async Task<Results<NoContent, NotFound>> Clear(
        Guid id,
        JobMonitor jobs,
        CancellationToken ct
    ) => await jobs.ClearAsync(id, ct) ? TypedResults.NoContent() : TypedResults.NotFound();

    private static ValidationProblem Invalid(string field, string message) =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}
