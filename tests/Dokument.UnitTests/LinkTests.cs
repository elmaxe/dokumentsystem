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
        Assert.Equal(token, LinkToken.Parse(token.Value));
        Assert.Throws<FormatException>(() => LinkToken.Parse("nope"));
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
        Assert.Equal(PasswordHashVersion.Argon2idV1, hash.Version);
        var stored = hash.ToBytes();
        Assert.Equal(1, stored[0]);
        Assert.Equal(1 + 16 + 32, stored.Length);
        Assert.Equal(hash, PasswordHash.FromBytes(stored));
        Assert.True(LinkPasswordHasher.Verify("semester", hash));
        Assert.False(LinkPasswordHasher.Verify("semestr", hash));
        Assert.True(LinkPasswordHasher.Verify("semester", PasswordHash.FromBytes(stored)));
        Assert.DoesNotContain(Convert.ToHexString(hash.Hash), hash.ToString());

        var again = LinkPasswordHasher.Hash(password);
        Assert.NotEqual(hash, again);
        Assert.NotEqual(hash.Stamp(), again.Stamp());
    }

    [Fact]
    public void Only_known_versions_with_their_length_are_password_hashes()
    {
        Assert.False(PasswordHash.TryFromBytes(null, out _));
        Assert.False(PasswordHash.TryFromBytes([], out _));
        Assert.False(PasswordHash.TryFromBytes([1, 2, 3], out _));
        Assert.False(PasswordHash.TryFromBytes([2, .. new byte[48]], out _));
        Assert.False(PasswordHash.TryFromBytes([0, .. new byte[48]], out _));
        Assert.True(PasswordHash.TryFromBytes([1, .. new byte[48]], out var hash));
        Assert.Equal(16, hash.Salt.Length);
        Assert.Equal(32, hash.Hash.Length);
    }

    [Fact]
    public void A_link_works_until_it_expires_or_is_turned_off()
    {
        var now = DateTimeOffset.UtcNow;
        var link = new Link
        {
            NodeId = Guid.NewGuid(),
            Token = LinkToken.Create(),
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
