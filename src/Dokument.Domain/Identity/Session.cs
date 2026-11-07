using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Dokument.Domain.Identity;

/// <summary>
/// An app-issued login session (docs/design.md §5). Only the SHA-256 of the token is stored, so a database
/// leak doesn't hand out live sessions.
/// </summary>
public sealed class Session
{
    /// <summary>Sessions stay valid this long after they were last used.</summary>
    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);

    /// <summary>How stale <see cref="LastSeenAt"/> may get before a request refreshes it, to avoid a write per request.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);

    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required Guid UserId { get; init; }
    public User? User { get; init; }
    public required SessionKind Kind { get; init; }
    public required byte[] TokenHash { get; init; }

    /// <summary>A readable name for the "active devices" list, e.g. "Firefox on Linux".</summary>
    public DeviceName? DeviceName { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>
    /// The ID token the IdP issued at this sign-in, kept only as the <c>id_token_hint</c> for ending the person's
    /// session at the IdP when they sign out (§5). It is never accepted as a credential here. Null when the IdP sent none.
    /// </summary>
    public string? IdToken { get; init; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    /// <summary>
    /// Proves to the app that this server issued its session, without the token crossing the network (§15.3): an
    /// HMAC-SHA256 of the nonce keyed by the stored token hash, which the app can compute from its token. The token
    /// hash never leaves the server, and a proof can't be turned into a session.
    /// </summary>
    public string Prove(ProofNonce nonce) =>
        Base64Url.EncodeToString(
            HMACSHA256.HashData(TokenHash, Encoding.UTF8.GetBytes(nonce.Value))
        );

    /// <summary>Slides the expiry forward. Returns false when nothing needed saving.</summary>
    public bool Touch(DateTimeOffset now)
    {
        if (now - LastSeenAt < TouchInterval)
        {
            return false;
        }

        LastSeenAt = now;
        ExpiresAt = now + IdleLifetime;
        return true;
    }
}

public enum SessionKind
{
    /// <summary>Web: an httpOnly cookie, with CSRF protection on mutations.</summary>
    Cookie,

    /// <summary>Mobile: an opaque bearer token kept in the device keystore.</summary>
    Bearer,
}
