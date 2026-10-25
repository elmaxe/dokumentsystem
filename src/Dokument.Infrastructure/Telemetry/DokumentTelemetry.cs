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

    /// <summary>Bytes received by uploads, counted as chunks arrive.</summary>
    public static readonly Counter<long> UploadBytes = Meter.CreateCounter<long>(
        "dokument.uploads.bytes",
        unit: "By",
        description: "Bytes received by uploads."
    );

    /// <summary>Original downloads, counted once per download like the audit log, not per range request.</summary>
    public static readonly Counter<long> Downloads = Meter.CreateCounter<long>(
        "dokument.downloads",
        unit: "{download}",
        description: "Original file downloads."
    );

    /// <summary>Thumbnails and previews written, by <c>dokument.rendition.kind</c>.</summary>
    public static readonly Counter<long> RenditionsGenerated = Meter.CreateCounter<long>(
        "dokument.renditions.generated",
        unit: "{rendition}",
        description: "Thumbnails and previews generated."
    );

    /// <summary>Pages and images whose text was recognized (§10.2).</summary>
    public static readonly Counter<long> OcrPages = Meter.CreateCounter<long>(
        "dokument.ocr.pages",
        unit: "{page}",
        description: "Pages and images whose text was recognized."
    );

    /// <summary>How long a search took, in the server (§13).</summary>
    public static readonly Histogram<double> SearchDuration = Meter.CreateHistogram<double>(
        "dokument.search.duration",
        unit: "s",
        description: "How long a search took."
    );

    /// <summary>Clients connected for live updates (§15.4).</summary>
    public static readonly UpDownCounter<long> LiveConnections = Meter.CreateUpDownCounter<long>(
        "dokument.live.connections",
        unit: "{connection}",
        description: "Clients connected for live updates."
    );

    /// <summary>Change notices passed on to clients, whether or not anyone was connected to get them.</summary>
    public static readonly Counter<long> LiveNotices = Meter.CreateCounter<long>(
        "dokument.live.notices",
        unit: "{notice}",
        description: "Change notices passed on to clients."
    );
}
