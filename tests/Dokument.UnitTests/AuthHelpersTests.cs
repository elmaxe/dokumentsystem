using Dokument.Domain.Identity;
using Dokument.Server.Auth;
using Microsoft.Extensions.Configuration;

namespace Dokument.UnitTests;

public class AuthHelpersTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/files/123?view=grid", "/files/123?view=grid")]
    [InlineData("//evil.example", "/")]
    [InlineData("/\\evil.example", "/")]
    [InlineData("https://evil.example", "/")]
    [InlineData("files", "/")]
    [InlineData("/a\nb", "/")]
    public void ReturnUrl_only_allows_local_paths(string? returnTo, string expected)
    {
        Assert.Equal(expected, ReturnUrl.Sanitize(returnTo));
    }

    [Theory]
    [InlineData(
        "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0",
        "Firefox on Linux"
    )]
    [InlineData(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0",
        "Edge on Windows"
    )]
    [InlineData(
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        "Safari on iOS"
    )]
    [InlineData(
        "Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36",
        "Chrome on Android"
    )]
    [InlineData(
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
        "Safari on macOS"
    )]
    [InlineData("curl/8.5.0", "curl/8.5.0")]
    [InlineData(null, null)]
    public void DeviceNames_are_readable(string? userAgent, string? expected)
    {
        Assert.Equal(expected, DeviceName.FromUserAgent(userAgent)?.Value);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData(" Pixel 9 ", "Pixel 9")]
    public void DeviceName_is_trimmed_and_never_blank(string? value, string? expected)
    {
        Assert.Equal(expected, DeviceName.From(value)?.Value);
    }

    [Fact]
    public void DeviceName_is_shortened()
    {
        Assert.Equal(DeviceName.MaxLength, DeviceName.From(new string('x', 500))!.Value.Length);
    }

    [Fact]
    public void OidcSettings_are_off_until_issuer_and_client_are_set()
    {
        Assert.False(Settings(new()).Enabled);
        Assert.True(
            Settings(
                new() { ["OIDC_ISSUER"] = "https://idp.example", ["OIDC_CLIENT_ID"] = "dokument" }
            ).Enabled
        );
    }

    [Fact]
    public void OidcSettings_have_defaults()
    {
        var settings = Settings(new() { ["OIDC_USER_GROUP"] = " family " });

        Assert.Equal("openid profile email", settings.Scope);
        Assert.Equal("groups", settings.GroupsClaim);
        Assert.Equal("family", settings.Policy.UserGroup);
        Assert.Equal("dokument-admins", settings.Policy.AdminGroup);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("https://docs.example.com/", "https://docs.example.com/")]
    [InlineData("http://nas.local:8080", "http://nas.local:8080/")]
    public void PublicUrl_is_parsed(string? value, string? expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["PUBLIC_URL"] = value })
            .Build();

        Assert.Equal(expected, PublicUrl.FromConfiguration(config)?.ToString());
    }

    [Fact]
    public void PublicUrl_rejects_relative_urls()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["PUBLIC_URL"] = "docs.example.com" }
            )
            .Build();

        Assert.Throws<InvalidOperationException>(() => PublicUrl.FromConfiguration(config));
    }

    private static OidcSettings Settings(Dictionary<string, string?> values) =>
        OidcSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()
        );
}
