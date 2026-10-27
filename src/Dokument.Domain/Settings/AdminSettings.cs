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
