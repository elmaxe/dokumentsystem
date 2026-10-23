namespace Dokument.Worker.Jobs;

/// <summary>The <c>Worker</c> configuration section. Moves to the admin UI with the queue page (docs/design.md §10.1).</summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>Thumbnail jobs run at once. Each holds one decoded image in memory.</summary>
    public int ThumbnailConcurrency { get; set; } = 2;

    /// <summary>How often an idle worker looks for new jobs.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A job running longer than this is cancelled and counts as a failed attempt (§10.4).</summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
