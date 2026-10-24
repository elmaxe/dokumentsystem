using Dokument.Domain.Identity;
using Dokument.Domain.Settings;

namespace Dokument.UnitTests;

public class SettingsTests
{
    [Theory]
    [InlineData("http://ml:3003", "http://ml:3003")]
    [InlineData(" https://gpu-box.lan:3003/ ", "https://gpu-box.lan:3003")]
    [InlineData("http://nas.lan/ml/", "http://nas.lan/ml")]
    public void Service_urls_are_normalised(string input, string expected) =>
        Assert.Equal(expected, ServiceUrl.Parse(input).Value);

    [Theory]
    [InlineData("")]
    [InlineData("ml:3003")]
    [InlineData("ftp://ml")]
    [InlineData("http://user:pass@ml:3003")]
    [InlineData("http://ml:3003/?x=1")]
    public void Service_urls_reject_what_cant_be_called(string input) =>
        Assert.Null(ServiceUrl.TryParse(input, out _));

    [Theory]
    [InlineData("tcp://clamav:3310", "tcp://clamav:3310")]
    [InlineData("clamav", "tcp://clamav:3310")]
    [InlineData("10.0.0.5:3311", "tcp://10.0.0.5:3311")]
    public void Clamd_addresses_default_to_tcp_and_3310(string input, string expected) =>
        Assert.Equal(expected, ClamdAddress.Parse(input).ToString());

    [Theory]
    [InlineData("http://clamav:3310")]
    [InlineData("tcp://clamav:3310/scan")]
    public void Clamd_addresses_must_be_plain_tcp(string input) =>
        Assert.Null(ClamdAddress.TryParse(input, out _));

    [Fact]
    public void A_quota_resolves_against_the_default()
    {
        var fifty = StorageLimit.Of(50);
        Assert.Equal(50, UserQuota.Default.Resolve(fifty));
        Assert.Null(UserQuota.Default.Resolve(StorageLimit.None));
        Assert.Equal(10, UserQuota.Limit(10).Resolve(fifty));
        Assert.Null(UserQuota.Unlimited.Resolve(fifty));
        Assert.Throws<ArgumentOutOfRangeException>(() => UserQuota.Limit(0));
    }

    [Fact]
    public void A_user_keeps_the_quota_it_was_given()
    {
        var user = new User
        {
            OidcIssuer = "i",
            OidcSubject = "s",
            DisplayName = "d",
        };
        Assert.Equal(UserQuota.Default, user.Quota);
        user.Quota = UserQuota.Unlimited;
        Assert.Equal(UserQuota.Unlimited, user.Quota);
        user.Quota = UserQuota.Limit(7);
        Assert.Equal(UserQuota.Limit(7), user.Quota);
        Assert.False(user.QuotaUnlimited);
    }

    [Fact]
    public void Version_settings_turn_into_retention()
    {
        Assert.Equal(TimeSpan.FromDays(30), VersionSettings.Default.Retention.MaxAge);
        Assert.False(new VersionSettings(0, 100).IsValid);
        Assert.False(new VersionSettings(30, 0).IsValid);
    }

    [Fact]
    public void Only_supported_languages_parse()
    {
        Assert.Equal(UiLanguage.Swedish, UiLanguage.TryParse("SV"));
        Assert.Null(UiLanguage.TryParse("de"));
    }
}
