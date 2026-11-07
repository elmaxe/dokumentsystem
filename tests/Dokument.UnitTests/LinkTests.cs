using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Sharing;

namespace Dokument.UnitTests;

public sealed class LinkTests
{
    [Fact]
    public void A_token_is_22_url_safe_characters_and_never_printed()
    {
        var token = LinkToken.Create();
        Assert.Equal(22, token.Value.Length);
        Assert.True(LinkToken.TryParse(token.Value, out var parsed));
        Assert.Equal(token, parsed);
        Assert.Equal(token.Hash(), parsed.Hash());
        Assert.DoesNotContain(token.Value, token.ToString());
        Assert.False(LinkToken.TryParse(token.Value[..21], out _));
        Assert.False(LinkToken.TryParse(token.Value + "A", out _));
        Assert.False(LinkToken.TryParse("not/a+token-at-all===", out _));
        Assert.False(LinkToken.TryParse(null, out _));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("abcd", true)]
    public void A_password_has_at_least_four_characters(string? value, bool valid)
    {
        Assert.Equal(valid, LinkPassword.TryCreate(value, out var password, out var error));
        Assert.Equal(valid, error is null);
        if (password is not null)
        {
            Assert.DoesNotContain(value!, password.ToString());
        }
    }

    [Fact]
    public void A_password_hash_matches_only_its_password_and_changes_with_each_set()
    {
        Assert.True(LinkPassword.TryCreate("semester", out var password, out _));
        var hash = LinkPasswordHasher.Hash(password);
        Assert.StartsWith("$argon2id$v=19$m=19456,t=2,p=1$", hash);
        Assert.True(LinkPasswordHasher.Verify("semester", hash));
        Assert.False(LinkPasswordHasher.Verify("semestr", hash));
        Assert.False(LinkPasswordHasher.Verify("semester", "$bcrypt$nope"));

        var again = LinkPasswordHasher.Hash(password);
        Assert.NotEqual(hash, again);
        Assert.NotEqual(LinkPasswordHasher.Stamp(hash), LinkPasswordHasher.Stamp(again));
    }

    [Fact]
    public void A_link_works_until_it_expires_or_is_turned_off()
    {
        var now = DateTimeOffset.UtcNow;
        var link = new Link
        {
            NodeId = Guid.NewGuid(),
            TokenHash = [],
            ProtectedToken = "",
            AllowDownload = true,
            CreatedBy = Guid.NewGuid(),
        };
        Assert.True(link.WorksAt(now));
        link.ExpiresAt = now.AddDays(1);
        Assert.True(link.WorksAt(now));
        Assert.False(link.WorksAt(now.AddDays(1)));
        link.ExpiresAt = null;
        link.RevokedAt = now;
        Assert.False(link.WorksAt(now));
    }
}
