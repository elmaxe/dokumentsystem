using Dokument.Domain.Identity;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Identity;

/// <summary>Issues, validates, lists and revokes app sessions (docs/design.md §5).</summary>
public sealed class SessionStore(DokumentDbContext db, LiveChanges live, TimeProvider time)
{
    /// <summary>Creates a session and returns it with its token. The token is not stored and can't be recovered later.</summary>
    public async Task<(Session Session, SessionToken Token)> CreateAsync(
        Guid userId,
        SessionKind kind,
        DeviceName? deviceName,
        CancellationToken ct = default
    )
    {
        var now = time.GetUtcNow();
        var token = SessionToken.Create();
        var session = new Session
        {
            UserId = userId,
            Kind = kind,
            TokenHash = token.Hash(),
            DeviceName = deviceName,
            CreatedAt = now,
            LastSeenAt = now,
            ExpiresAt = now + Session.IdleLifetime,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct);
        return (session, token);
    }

    /// <summary>
    /// Returns the active session for a token, with its user, and slides its expiry. The session is null if the token is
    /// unknown, expired or revoked, or the user is disabled. <c>Refreshed</c> says whether the expiry moved.
    /// </summary>
    public async Task<(Session? Session, bool Refreshed)> ValidateAsync(
        SessionToken token,
        CancellationToken ct = default
    )
    {
        var hash = token.Hash();
        var session = await db
            .Sessions.Include(s => s.User)
            .SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        var now = time.GetUtcNow();
        if (session is null || !session.IsActive(now) || session.User is not { IsDisabled: false })
        {
            return (null, false);
        }

        var refreshed = session.Touch(now);
        if (refreshed)
        {
            await db.SaveChangesAsync(ct);
        }

        return (session, refreshed);
    }

    /// <summary>An active session of an enabled user by id, without touching it.</summary>
    public async Task<Session?> FindActiveAsync(Guid sessionId, CancellationToken ct = default)
    {
        var session = await db
            .Sessions.AsNoTracking()
            .Include(s => s.User)
            .SingleOrDefaultAsync(s => s.Id == sessionId, ct);
        return
            session is not null
            && session.IsActive(time.GetUtcNow())
            && session.User is { IsDisabled: false }
            ? session
            : null;
    }

    public Task<List<Session>> ListActiveAsync(Guid userId, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        return db
            .Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Revokes a session and sends its open clients to the sign-in page. With <paramref name="userId"/>, only if it
    /// belongs to that user. Returns false if there was nothing to revoke.
    /// </summary>
    public Task<bool> RevokeAsync(
        Guid sessionId,
        Guid? userId = null,
        CancellationToken ct = default
    ) => RevokeAsync(sessionId, userId, SessionEndReason.Revoked, ct);

    public async Task<bool> RevokeAsync(
        Guid sessionId,
        Guid? userId,
        SessionEndReason reason,
        CancellationToken ct = default
    )
    {
        var now = time.GetUtcNow();
        var revoked = await db
            .Sessions.Where(s =>
                s.Id == sessionId && s.RevokedAt == null && (userId == null || s.UserId == userId)
            )
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.RevokedAt, now), ct);
        if (revoked > 0)
        {
            await live.SessionEndedAsync(sessionId, reason, ct);
        }

        return revoked > 0;
    }
}
