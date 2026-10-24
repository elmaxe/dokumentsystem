namespace Dokument.Domain.Identity;

/// <summary>
/// How much one person may store (docs/design.md §17): the admin-set default, a limit of their own, or no limit.
/// </summary>
public sealed record UserQuota
{
    public static readonly UserQuota Default = new(UserQuotaKind.Default, null);
    public static readonly UserQuota Unlimited = new(UserQuotaKind.Unlimited, null);

    private UserQuota(UserQuotaKind kind, long? bytes)
    {
        Kind = kind;
        Bytes = bytes;
    }

    public UserQuotaKind Kind { get; }

    /// <summary>Only set for <see cref="UserQuotaKind.Limit"/>.</summary>
    public long? Bytes { get; }

    public static UserQuota Limit(long bytes) =>
        bytes > 0
            ? new UserQuota(UserQuotaKind.Limit, bytes)
            : throw new ArgumentOutOfRangeException(nameof(bytes), "A limit must be positive.");

    /// <summary>The limit that applies, given the default; null means unlimited.</summary>
    public long? Resolve(StorageLimit defaultLimit) =>
        Kind switch
        {
            UserQuotaKind.Limit => Bytes,
            UserQuotaKind.Unlimited => null,
            _ => defaultLimit.Bytes,
        };
}

public enum UserQuotaKind
{
    Default,
    Limit,
    Unlimited,
}
