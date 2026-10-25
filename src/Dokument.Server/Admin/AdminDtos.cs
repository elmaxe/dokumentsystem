using Dokument.Domain.Identity;
using Dokument.Infrastructure.Settings;

namespace Dokument.Server.Admin;

/// <summary>Everything on the admin settings page (docs/design.md §4.3). Secrets are never included.</summary>
public sealed record AdminSettingsDto(
    StorageSettingsDto Storage,
    VersionSettingsDto Versions,
    TrashSettingsDto Trash,
    MlSettingsDto Ml,
    ScannerSettingsDto Scanner,
    SignInInfo SignIn
);

/// <param name="DefaultQuotaBytes">Null means no limit.</param>
public sealed record StorageSettingsDto(long? DefaultQuotaBytes);

public sealed record VersionSettingsDto(int KeepDays, int MaxVersions);

/// <param name="KeepDays">Items in the trash are deleted forever this many days after they were trashed (§12).</param>
public sealed record TrashSettingsDto(int KeepDays);

public sealed record MlSettingsDto(List<MlEndpointDto> Endpoints);

public sealed record MlEndpointDto(string Url, bool HasApiKey);

public sealed record UpdateMlSettingsRequest(List<MlEndpointUpdate> Endpoints);

/// <param name="ApiKey">A new key, or null for none.</param>
/// <param name="KeepApiKey">Keep the key already saved for this URL instead; <c>ApiKey</c> is then ignored.</param>
public sealed record MlEndpointUpdate(string Url, string? ApiKey, bool KeepApiKey);

/// <param name="Address">Null or empty turns scanning off.</param>
public sealed record ScannerSettingsDto(string? Address);

/// <summary>Sign-in comes from <c>OIDC_*</c> environment variables (§5), so the page only shows it.</summary>
/// <param name="QuotaClaim">The claim that sets each person's quota (§17.1), or null when quotas are set here.</param>
public sealed record SignInInfo(
    bool Enabled,
    string? ProviderName,
    string? Issuer,
    string? UserGroup,
    string AdminGroup,
    string? QuotaClaim
);

/// <param name="ApiKey">Null uses the key saved for this URL, if any.</param>
public sealed record TestMlRequest(string Url, string? ApiKey);

public sealed record TestScannerRequest(string Address);

public sealed record MlEndpointStatus(string Url, ProbeResult Status);

/// <summary>Disk use for the Storage section (§17).</summary>
/// <param name="FilesBytes">All stored originals, each blob once.</param>
public sealed record StorageReport(long FilesBytes, List<VolumeUsage> Volumes);

/// <param name="Holds">What lives on the volume: <c>originals</c>, <c>derived</c>, or both.</param>
public sealed record VolumeUsage(List<string> Holds, long TotalBytes, long FreeBytes);

/// <param name="QuotaSource"><c>claim</c> when the sign-in provider sets the quota; it can't be changed here then (§17.1).</param>
public sealed record AdminUser(
    Guid Id,
    string DisplayName,
    string? Email,
    UserRole Role,
    long UsedBytes,
    QuotaDto Quota,
    QuotaSource QuotaSource,
    long? EffectiveQuotaBytes,
    DateTimeOffset? LastLoginAt,
    bool Disabled
);

/// <param name="Bytes">Only for <c>limit</c>.</param>
public sealed record QuotaDto(UserQuotaKind Kind, long? Bytes)
{
    public static QuotaDto From(UserQuota quota) => new(quota.Kind, quota.Bytes);
}
