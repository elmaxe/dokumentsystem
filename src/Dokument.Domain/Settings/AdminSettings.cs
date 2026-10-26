using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Jobs;

namespace Dokument.Domain.Settings;

/// <summary>The default quota for people without their own (docs/design.md §17). No limit until an admin sets one.</summary>
public sealed record StorageSettings(StorageLimit DefaultQuota)
{
    public static readonly StorageSettings Default = new(StorageLimit.None);
}

/// <summary>Version retention (§8.2, decision 2) as an admin edits it.</summary>
public sealed record VersionSettings(int KeepDays, int MaxVersions)
{
    public const int MaxKeepDays = 3650;
    public const int MaxMaxVersions = 10_000;

    public static readonly VersionSettings Default = new(30, 100);

    public VersionRetention Retention => new(TimeSpan.FromDays(KeepDays), MaxVersions);

    public bool IsValid =>
        KeepDays is >= 1 and <= MaxKeepDays && MaxVersions is >= 1 and <= MaxMaxVersions;
}

/// <summary>How long trashed items are kept before the purge job deletes them forever (§12).</summary>
public sealed record TrashSettings(int KeepDays)
{
    public const int MaxKeepDays = 3650;

    public static readonly TrashSettings Default = new(30);

    public TimeSpan KeepFor => TimeSpan.FromDays(KeepDays);

    public bool IsValid => KeepDays is >= 1 and <= MaxKeepDays;
}

/// <summary>ML services in priority order (§11). The worker uses the first healthy one.</summary>
public sealed record MlSettings(IReadOnlyList<MlEndpoint> Endpoints)
{
    public const int MaxEndpoints = 10;

    public static readonly MlSettings Default = new([]);
}

/// <param name="ProtectedApiKey">The API key, encrypted with ASP.NET Data Protection (§21). Never sent to clients.</param>
public sealed record MlEndpoint(ServiceUrl Url, string? ProtectedApiKey);

/// <summary>The optional ClamAV container (§21). Null means scanning is off.</summary>
public sealed record ScannerSettings(ClamdAddress? Address)
{
    public static readonly ScannerSettings Default = new((ClamdAddress?)null);
}

/// <summary>
/// Whether the worker reads text in scans and photos (§10.2, §13). Text in PDFs and Office files is always read; this
/// only governs recognition, which is the slow part. On by default.
/// </summary>
public sealed record TextRecognitionSettings(bool Enabled)
{
    public static readonly TextRecognitionSettings Default = new(true);
}

/// <summary>
/// The job queues an admin has paused on the Jobs page (§10.1). Workers stop claiming those jobs; ones already running
/// finish.
/// </summary>
public sealed record JobSettings(IReadOnlySet<JobType> Paused)
{
    public static readonly JobSettings Default = new(new HashSet<JobType>());

    public bool IsPaused(JobType type) => Paused.Contains(type);

    public JobSettings With(JobType type, bool paused)
    {
        var set = new HashSet<JobType>(Paused);
        if (paused)
        {
            set.Add(type);
        }
        else
        {
            set.Remove(type);
        }

        return new JobSettings(set);
    }
}

/// <summary>When the worker makes playable copies of videos and audio browsers can't play (§10.3, §14).</summary>
public enum VideoCopies
{
    /// <summary>In the background after upload, so the copy is ready when someone opens the file.</summary>
    Background,

    /// <summary>Only when someone opens the file. Saves work for files nobody plays; the first viewer waits.</summary>
    OnOpen,

    /// <summary>Never: those files can only be downloaded.</summary>
    Never,
}

/// <summary>Playable copies (§14). <see cref="MaxHeight"/> is 720 or 1080, or 0 to keep the original's size.</summary>
public sealed record VideoSettings(VideoCopies Copies, int MaxHeight)
{
    public static readonly VideoSettings Default = new(VideoCopies.Background, 1080);

    public bool IsValid => Enum.IsDefined(Copies) && MaxHeight is 0 or 720 or 1080;

    /// <summary>
    /// The longest edge a copy may have: 1920 for 1080p, so a portrait phone video keeps its 1080 × 1920. Null keeps
    /// the original's size.
    /// </summary>
    public int? MaxEdge =>
        MaxHeight switch
        {
            720 => 1280,
            1080 => 1920,
            _ => null,
        };
}

/// <summary>
/// Speech to text (§11.1), done by the ML service: on by default, so adding an ML URL is all it takes. With no ML
/// service set, nothing is queued.
/// </summary>
/// <param name="Model">One of <see cref="Models"/>; <c>auto</c> lets the service pick by its hardware.</param>
/// <param name="Videos">Whether the sound in videos is transcribed too, not only audio files.</param>
public sealed record TranscriptionSettings(bool Enabled, string Model, bool Videos)
{
    public const string AutoModel = "auto";

    /// <summary>The models offered: Whisper sizes, and KBLab's Swedish fine-tunes (KB-Whisper).</summary>
    public static readonly IReadOnlyList<string> Models =
    [
        AutoModel,
        "small",
        "medium",
        "large-v3-turbo",
        "kb-whisper-small",
        "kb-whisper-medium",
        "kb-whisper-large",
    ];

    public static readonly TranscriptionSettings Default = new(true, AutoModel, true);

    public bool IsValid => Models.Contains(Model);
}
