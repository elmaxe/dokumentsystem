using System.Diagnostics.Metrics;
using Dokument.Domain.Jobs;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Storage;
using Dokument.Infrastructure.Telemetry;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dokument.Worker;

/// <summary>
/// The <c>dokument.*</c> gauges that describe the whole install rather than one process (docs/design.md §22.1): the
/// job queue, unfinished uploads and storage use. The worker reads them from Postgres and the derived volume now and
/// then, and the gauges report the last reading. Registered only when metrics are exported.
/// </summary>
internal sealed partial class MetricsSampler(
    IServiceScopeFactory scopes,
    IEnumerable<JobHandlerRegistration> registrations,
    IOptions<StorageOptions> storage,
    TimeProvider time,
    ILogger<MetricsSampler> logger
) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>Walking the derived volume is slower than a query, so it is read less often.</summary>
    private static readonly TimeSpan DerivedInterval = TimeSpan.FromMinutes(10);

    private volatile Reading reading = Reading.Empty;
    private long derivedBytes = -1;
    private DateTimeOffset derivedReadAt = DateTimeOffset.MinValue;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        var meter = DokumentTelemetry.Meter;
        meter.CreateObservableGauge(
            "dokument.jobs.queued",
            () => ByType(reading.Queued),
            unit: "{job}",
            description: "Jobs waiting to run, by type."
        );
        meter.CreateObservableGauge(
            "dokument.jobs.running",
            () => ByType(reading.Running),
            unit: "{job}",
            description: "Jobs running now, by type."
        );
        meter.CreateObservableGauge(
            "dokument.jobs.lag",
            () => ByType(reading.LagSeconds),
            unit: "s",
            description: "Age of the oldest job that is due but not running, by type."
        );
        meter.CreateObservableGauge(
            "dokument.uploads.active",
            () => reading.ActiveUploads,
            unit: "{upload}",
            description: "Uploads started and not yet finished or expired."
        );
        meter.CreateObservableGauge(
            "dokument.storage.bytes",
            StorageBytes,
            unit: "By",
            description: "Bytes stored, by dokument.storage.kind (originals or derived)."
        );
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                try
                {
                    reading = await ReadAsync(stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogFailed(logger, e);
                }

                await Task.Delay(Interval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task<Reading> ReadAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DokumentDbContext>();
        var now = time.GetUtcNow();

        var active = await db
            .Jobs.Where(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running)
            .GroupBy(j => new { j.Type, j.Status })
            .Select(g => new
            {
                g.Key.Type,
                g.Key.Status,
                Count = g.Count(),
            })
            .ToListAsync(ct);
        var oldestDue = await db
            .Jobs.Where(j => j.Status == JobStatus.Queued && j.RunAfter <= now)
            .GroupBy(j => j.Type)
            .Select(g => new { Type = g.Key, RunAfter = g.Min(j => j.RunAfter) })
            .ToListAsync(ct);
        var activeUploads = await db.Uploads.CountAsync(
            u => u.CompletedAt == null && u.ExpiresAt > now,
            ct
        );
        var originals = await db.Blobs.SumAsync(b => (long?)b.Size, ct) ?? 0;

        if (now - derivedReadAt >= DerivedInterval)
        {
            derivedBytes = await Task.Run(() => DirectoryBytes(storage.Value.DerivedPath), ct);
            derivedReadAt = now;
        }

        // Every registered type reports, so a queue that empties drops to zero rather than going quiet.
        var types = registrations
            .Select(r => r.Type.Name)
            .Union(active.Select(a => a.Type.Name))
            .ToList();
        Dictionary<string, double> Count(JobStatus status) =>
            types.ToDictionary(
                t => t,
                t =>
                    (double)
                        active.Where(a => a.Type.Name == t && a.Status == status).Sum(a => a.Count)
            );

        return new Reading(
            Queued: Count(JobStatus.Queued),
            Running: Count(JobStatus.Running),
            LagSeconds: types.ToDictionary(
                t => t,
                t =>
                    oldestDue.FirstOrDefault(o => o.Type.Name == t) is { } due
                        ? Math.Max(0, (now - due.RunAfter).TotalSeconds)
                        : 0
            ),
            ActiveUploads: activeUploads,
            OriginalBytes: originals
        );
    }

    private IEnumerable<Measurement<long>> StorageBytes()
    {
        yield return new Measurement<long>(
            reading.OriginalBytes,
            new KeyValuePair<string, object?>("dokument.storage.kind", "originals")
        );
        if (derivedBytes >= 0)
        {
            yield return new Measurement<long>(
                derivedBytes,
                new KeyValuePair<string, object?>("dokument.storage.kind", "derived")
            );
        }
    }

    private static IEnumerable<Measurement<double>> ByType(Dictionary<string, double> values) =>
        values.Select(v => new Measurement<double>(
            v.Value,
            new KeyValuePair<string, object?>("job.type", v.Key)
        ));

    private static long DirectoryBytes(string path) =>
        Directory.Exists(path)
            ? new DirectoryInfo(path)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length)
            : 0;

    private sealed record Reading(
        Dictionary<string, double> Queued,
        Dictionary<string, double> Running,
        Dictionary<string, double> LagSeconds,
        int ActiveUploads,
        long OriginalBytes
    )
    {
        public static readonly Reading Empty = new([], [], [], 0, 0);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't sample the metrics; retrying shortly"
    )]
    private static partial void LogFailed(ILogger logger, Exception e);
}
