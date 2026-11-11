namespace Dokument.Domain.Identity;

/// <summary>A person, created on first OIDC login and identified by issuer + subject (docs/design.md §5, §6).</summary>
public sealed class User
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string OidcIssuer { get; init; }
    public required string OidcSubject { get; init; }
    public string? Email { get; set; }
    public required string DisplayName { get; set; }
    public UserRole Role { get; set; }

    /// <summary>The person's own limit. Null with <see cref="QuotaUnlimited"/> false means the default applies.</summary>
    public long? QuotaBytes { get; private set; }

    public bool QuotaUnlimited { get; private set; }

    /// <summary>Who set <see cref="Quota"/>: an admin, or the IdP's quota claim at the last login (§17.1).</summary>
    public QuotaSource QuotaSource { get; private set; }

    public UserQuota Quota =>
        QuotaUnlimited ? UserQuota.Unlimited
        : QuotaBytes is { } bytes ? UserQuota.Limit(bytes)
        : UserQuota.Default;

    /// <summary>Sets the quota and where it came from. Returns false when neither changed.</summary>
    public bool SetQuota(UserQuota quota, QuotaSource source)
    {
        if (quota == Quota && source == QuotaSource)
        {
            return false;
        }

        QuotaUnlimited = quota.Kind == UserQuotaKind.Unlimited;
        QuotaBytes = quota.Bytes;
        QuotaSource = source;
        return true;
    }

    public long UsedBytes { get; set; }
    public long ReservedBytes { get; set; }

    /// <summary>The UI language the person picked; null follows the browser (§23).</summary>
    public UiLanguage? Locale { get; set; }

    /// <summary>Show times to the second ("09:14:07") instead of to the minute (§16.5).</summary>
    public bool ShowSeconds { get; set; }

    /// <summary>The sign-in provider's picture claim at the last sign-in (§5.1): a URL, or null when it sent none.</summary>
    public string? PictureUrl { get; set; }

    public string? AvatarVersion { get; private set; }
    public string? AvatarSource { get; private set; }
    public string? AvatarETag { get; private set; }

    /// <summary>The stored profile picture, or null when initials show instead (§5.1).</summary>
    public Avatar? Avatar
    {
        get =>
            AvatarVersion is { } version
                ? new Avatar(version, AvatarSource ?? "", AvatarETag)
                : null;
        set
        {
            AvatarVersion = value?.Version;
            AvatarSource = value?.SourceUrl;
            AvatarETag = value?.ETag;
        }
    }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }

    public bool IsDisabled => DisabledAt is not null;
}

public enum UserRole
{
    User,
    Admin,
}
