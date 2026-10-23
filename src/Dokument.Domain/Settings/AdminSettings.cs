using Dokument.Domain.Files;
using Dokument.Domain.Identity;

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
