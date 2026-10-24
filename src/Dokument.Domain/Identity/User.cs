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

    public UserQuota Quota
    {
        get =>
            QuotaUnlimited ? UserQuota.Unlimited
            : QuotaBytes is { } bytes ? UserQuota.Limit(bytes)
            : UserQuota.Default;
        set
        {
            QuotaUnlimited = value.Kind == UserQuotaKind.Unlimited;
            QuotaBytes = value.Bytes;
        }
    }

    public long UsedBytes { get; set; }
    public long ReservedBytes { get; set; }

    /// <summary>The UI language the person picked; null follows the browser (§23).</summary>
    public UiLanguage? Locale { get; set; }

    /// <summary>Show times to the second ("09:14:07") instead of to the minute (§16.5).</summary>
    public bool ShowSeconds { get; set; }
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
