namespace Dokument.Domain.Identity;

/// <summary>What the IdP's quota claim said at one login (docs/design.md §17.1).</summary>
public sealed record ClaimedQuota
{
    /// <summary>No quota claim is configured, or its value couldn't be read. The person's quota is left alone.</summary>
    public static readonly ClaimedQuota NotRead = new(ClaimedQuotaKind.NotRead, null);

    /// <summary>A quota claim is configured but the IdP didn't send it for this person.</summary>
    public static readonly ClaimedQuota Absent = new(ClaimedQuotaKind.Absent, null);

    private ClaimedQuota(ClaimedQuotaKind kind, UserQuota? quota)
    {
        Kind = kind;
        Quota = quota;
    }

    public ClaimedQuotaKind Kind { get; }

    /// <summary>Only set for <see cref="ClaimedQuotaKind.Present"/>: a limit or unlimited, never the default.</summary>
    public UserQuota? Quota { get; }

    public static ClaimedQuota Of(UserQuota quota) =>
        quota.Kind == UserQuotaKind.Default
            ? throw new ArgumentException("A claim sets a limit or no limit.", nameof(quota))
            : new ClaimedQuota(ClaimedQuotaKind.Present, quota);

    /// <summary>
    /// Applies the claim to a person: a value sets their quota, a missing claim hands a claimed quota back to the
    /// default (and to admins), and an unread claim changes nothing. Returns false when nothing changed.
    /// </summary>
    public bool ApplyTo(User user) =>
        Kind switch
        {
            ClaimedQuotaKind.Present => user.SetQuota(Quota!, QuotaSource.Claim),
            ClaimedQuotaKind.Absent when user.QuotaSource == QuotaSource.Claim => user.SetQuota(
                UserQuota.Default,
                QuotaSource.Admin
            ),
            _ => false,
        };
}

public enum ClaimedQuotaKind
{
    NotRead,
    Absent,
    Present,
}
