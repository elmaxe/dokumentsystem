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
        Assert.Equal(43, a.Value.Length);
        Assert.Equal(32, a.Hash().Length);
        Assert.Equal(a.Hash(), a.Hash());
    }

    [Fact]
    public void Tokens_round_trip_and_reject_garbage()
    {
        var token = SessionToken.Create();

        Assert.True(SessionToken.TryParse(token.Value, out var parsed));
        Assert.Equal(token, parsed);
        Assert.False(SessionToken.TryParse(null, out _));
        Assert.False(SessionToken.TryParse("", out _));
        Assert.False(SessionToken.TryParse(token.Value[..42], out _));
        Assert.False(SessionToken.TryParse(token.Value[..42] + "!", out _));
        Assert.False(SessionToken.TryParse(token.Value + "A", out _));
    }

    [Fact]
    public void Tokens_never_print_their_secret()
    {
        var token = SessionToken.Create();

        Assert.DoesNotContain(token.Value, token.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token.Value, $"{new { token }}", StringComparison.Ordinal);
    }

    [Fact]
    public void Csrf_token_is_tied_to_the_session_token()
    {
        var token = SessionToken.Create();
        var csrf = token.CsrfToken();

        Assert.True(token.CsrfTokenMatches(csrf));
        Assert.False(SessionToken.Create().CsrfTokenMatches(csrf));
        Assert.False(token.CsrfTokenMatches(null));
        Assert.NotEqual(token.Value, csrf);
    }
}
