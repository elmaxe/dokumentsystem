using Dokument.Domain.Identity;

namespace Dokument.UnitTests;

public class QuotaClaimTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Theory]
    [InlineData("53687091200", 53_687_091_200L)]
    [InlineData("50GB", 50 * GiB)]
    [InlineData("50 GiB", 50 * GiB)]
    [InlineData("50gb", 50 * GiB)]
    [InlineData("50g", 50 * GiB)]
    [InlineData(" 1.5TB ", 1536 * GiB)]
    [InlineData("500 MB", 500L * 1024 * 1024)]
    [InlineData("1KiB", 1024L)]
    [InlineData("2048 b", 2048L)]
    [InlineData("0.5k", 512L)]
    public void Sizes_and_byte_counts_become_limits(string value, long bytes) =>
        Assert.Equal(UserQuota.Limit(bytes), QuotaClaim.TryParse(value));

    [Theory]
    [InlineData("unlimited")]
    [InlineData("Unlimited")]
    public void Unlimited_means_no_limit(string value) =>
        Assert.Equal(UserQuota.Unlimited, QuotaClaim.TryParse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("0GB")]
    [InlineData("-5GB")]
    [InlineData("1.5")]
    [InlineData("50 PB")]
    [InlineData("1,5GB")]
    [InlineData("fifty")]
    [InlineData("50 GB extra")]
    [InlineData("99999999999999999999999")]
    [InlineData("9999999999TB")]
    [InlineData("٥٠GB")]
    public void Anything_else_is_ignored(string? value) => Assert.Null(QuotaClaim.TryParse(value));

    [Fact]
    public void A_claim_sets_the_quota_and_a_missing_one_hands_it_back_to_the_default()
    {
        var user = new User
        {
            OidcIssuer = "i",
            OidcSubject = "s",
            DisplayName = "d",
        };
        user.SetQuota(UserQuota.Limit(10), QuotaSource.Admin);

        Assert.False(ClaimedQuota.NotRead.ApplyTo(user));
        Assert.False(ClaimedQuota.Absent.ApplyTo(user));
        Assert.Equal(UserQuota.Limit(10), user.Quota);

        Assert.True(ClaimedQuota.Of(UserQuota.Limit(20)).ApplyTo(user));
        Assert.Equal((UserQuota.Limit(20), QuotaSource.Claim), (user.Quota, user.QuotaSource));
        Assert.False(ClaimedQuota.Of(UserQuota.Limit(20)).ApplyTo(user));

        // An unreadable value leaves the last good one in place.
        Assert.False(ClaimedQuota.NotRead.ApplyTo(user));
        Assert.Equal(UserQuota.Limit(20), user.Quota);

        Assert.True(ClaimedQuota.Absent.ApplyTo(user));
        Assert.Equal((UserQuota.Default, QuotaSource.Admin), (user.Quota, user.QuotaSource));
    }

    [Fact]
    public void A_claim_never_asks_for_the_default() =>
        Assert.Throws<ArgumentException>(() => ClaimedQuota.Of(UserQuota.Default));
}
