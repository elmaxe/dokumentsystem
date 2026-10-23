using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Dokument.Infrastructure.Telemetry;

/// <summary>
/// Our own spans and metrics (docs/design.md §22.1). Both cost nothing until OpenTelemetry listens, which it does only
/// when an OTLP endpoint is set. Attributes follow the privacy rules there: opaque ids only, never names or paths, and
/// metric labels stay low-cardinality.
/// </summary>
public static class DokumentTelemetry
{
    public const string Name = "Dokument";

    public static readonly ActivitySource ActivitySource = new(Name);

    public static readonly Meter Meter = new(Name);

    /// <summary>How long each job run took, by <c>job.type</c> and <c>job.outcome</c>.</summary>
    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>(
        "dokument.jobs.duration",
        unit: "s",
        description: "Duration of job runs."
    );

    /// <summary>Job runs that failed, by <c>job.type</c>. A failed run may still be retried.</summary>
    public static readonly Counter<long> JobsFailed = Meter.CreateCounter<long>(
        "dokument.jobs.failed",
        unit: "{run}",
        description: "Job runs that failed."
    );
}
