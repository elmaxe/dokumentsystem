using Dokument.Application.Identity;
using Dokument.Domain.Identity;

namespace Dokument.UnitTests;

public class SessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 22, 8, 0, 0, TimeSpan.Zero);

    private static Session NewSession() => new()
    {
        UserId = Guid.NewGuid(),
        Kind = SessionKind.Cookie,
        TokenHash = [],
        CreatedAt = Start,
        LastSeenAt = Start,
        ExpiresAt = Start + Session.IdleLifetime,
    };

    [Fact]
    public void Touch_waits_for_the_interval_before_writing()
    {
        var session = NewSession();

        Assert.False(session.Touch(Start + TimeSpan.FromMinutes(1)));
        Assert.Equal(Start + Session.IdleLifetime, session.ExpiresAt);
    }

    [Fact]
    public void Touch_slides_the_expiry()
    {
        var session = NewSession();
        var later = Start + TimeSpan.FromDays(10);

        Assert.True(session.Touch(later));
        Assert.Equal(later, session.LastSeenAt);
        Assert.Equal(later + Session.IdleLifetime, session.ExpiresAt);
    }

    [Fact]
    public void Expired_and_revoked_sessions_are_inactive()
    {
        var session = NewSession();
        Assert.True(session.IsActive(Start));
        Assert.False(session.IsActive(Start + Session.IdleLifetime));

        session.RevokedAt = Start;
        Assert.False(session.IsActive(Start));
    }

    [Fact]
    public void Tokens_are_random_and_hashed()
    {
        var a = SessionToken.Create();
        var b = SessionToken.Create();

        Assert.NotEqual(a, b);
        Assert.Equal(43, a.Length);
        Assert.Equal(32, SessionToken.Hash(a).Length);
        Assert.Equal(SessionToken.Hash(a), SessionToken.Hash(a));
    }

    [Fact]
    public void Csrf_token_is_tied_to_the_session_token()
    {
        var token = SessionToken.Create();
        var csrf = SessionToken.CsrfToken(token);

        Assert.True(SessionToken.CsrfTokenMatches(token, csrf));
        Assert.False(SessionToken.CsrfTokenMatches(SessionToken.Create(), csrf));
        Assert.False(SessionToken.CsrfTokenMatches(token, null));
        Assert.NotEqual(token, csrf);
    }
}
