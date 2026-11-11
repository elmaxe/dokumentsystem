using System.Security.Claims;
using System.Text;
using Dokument.Domain.Identity;
using Dokument.Server.Auth;
using Dokument.Worker.Avatars;
using Microsoft.Extensions.Configuration;
using NetVips;

namespace Dokument.UnitTests;

/// <summary>Profile pictures from the sign-in provider (docs/design.md §5.1).</summary>
public class AvatarTests
{
    [Fact]
    public void The_picture_claim_is_picture_unless_configured()
    {
        Assert.Equal("picture", Settings([]).PictureClaim);
        Assert.Equal(
            "avatar_url",
            Settings(new() { ["OIDC_PICTURE_CLAIM"] = "avatar_url" }).PictureClaim
        );
        Assert.Null(Settings(new() { ["OIDC_PICTURE_CLAIM"] = "" }).PictureClaim);
        Assert.Null(Settings(new() { ["OIDC_PICTURE_CLAIM"] = "  " }).PictureClaim);
    }

    [Theory]
    [InlineData("https://idp.example/avatar/1.png", "https://idp.example/avatar/1.png")]
    [InlineData("http://192.168.1.10:9000/u/1.jpg", "http://192.168.1.10:9000/u/1.jpg")]
    [InlineData("data:image/svg+xml;base64,PHN2Zy8+", "data:image/svg+xml;base64,PHN2Zy8+")]
    [InlineData("  https://idp.example/a.png  ", "https://idp.example/a.png")]
    [InlineData("file:///etc/passwd", null)]
    [InlineData("ftp://idp.example/a.png", null)]
    [InlineData("/relative.png", null)]
    [InlineData("data:text/html,<b>hi</b>", null)]
    [InlineData("", null)]
    public void Only_fetchable_pictures_are_kept(string claim, string? expected)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("picture", claim)]));
        Assert.Equal(expected, AuthSetup.PictureFrom(principal, Settings([])));
    }

    [Fact]
    public void A_huge_picture_claim_is_ignored()
    {
        var huge = "data:image/png;base64," + new string('A', Avatar.MaxSourceLength);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("picture", huge)]));
        Assert.Null(AuthSetup.PictureFrom(principal, Settings([])));
    }

    [Fact]
    public void Pictures_are_off_when_the_claim_is_empty()
    {
        var principal = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim("picture", "https://idp.example/a.png")])
        );
        Assert.Null(
            AuthSetup.PictureFrom(principal, Settings(new() { ["OIDC_PICTURE_CLAIM"] = "" }))
        );
    }

    [Fact]
    public void A_photo_becomes_a_square_webp()
    {
        using var photo = Image.Black(640, 480, bands: 3) + new double[] { 200, 120, 40 };
        var webp = AvatarImage.Make(photo.JpegsaveBuffer());

        Assert.NotNull(webp);
        using var avatar = Image.NewFromBuffer(webp);
        Assert.Equal("webpload_buffer", avatar.Get("vips-loader"));
        Assert.Equal(AvatarImage.Size, avatar.Width);
        Assert.Equal(AvatarImage.Size, avatar.Height);
    }

    [Fact]
    public void A_small_picture_is_scaled_up()
    {
        using var photo = Image.Black(48, 64, bands: 3);
        using var avatar = Image.NewFromBuffer(AvatarImage.Make(photo.PngsaveBuffer())!);
        Assert.Equal((AvatarImage.Size, AvatarImage.Size), (avatar.Width, avatar.Height));
    }

    [Fact]
    public void An_svg_initials_picture_is_drawn()
    {
        var svg =
            """<svg xmlns="http://www.w3.org/2000/svg" width="64" height="64"><rect width="64" height="64" fill="#36c"/></svg>""";
        Assert.NotNull(AvatarImage.Make(Encoding.UTF8.GetBytes(svg)));
    }

    [Fact]
    public void The_dev_keycloak_picture_is_drawn()
    {
        // alice's picture attribute in src/Dokument.AppHost/keycloak/dokument-realm.json, as Keycloak sends it.
        var claim =
            "data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 8 8'%3E%3Crect width='8' height='8' fill='%23e8a87c'/%3E%3Ccircle cx='4' cy='3' r='1.6' fill='%233a2a33'/%3E%3Cpath d='M1 8c0-2 1.4-3 3-3s3 1 3 3z' fill='%233a2a33'/%3E%3C/svg%3E";
        var bytes = Assert.IsType<FetchedPicture.Bytes>(AvatarFetcher.DecodeDataUrl(claim));
        using var avatar = Image.NewFromBuffer(AvatarImage.Make(bytes.Content)!);
        Assert.Equal((AvatarImage.Size, AvatarImage.Size), (avatar.Width, avatar.Height));
    }

    [Fact]
    public void Bytes_that_are_not_an_image_give_nothing()
    {
        Assert.Null(AvatarImage.Make(Encoding.UTF8.GetBytes("<html>Sign in</html>")));
        Assert.Null(AvatarImage.Make(Encoding.UTF8.GetBytes("a,b,c\n1,2,3\n")));
        Assert.Null(AvatarImage.Make([]));
    }

    [Fact]
    public void Data_urls_decode_base64_and_text()
    {
        var png = Assert.IsType<FetchedPicture.Bytes>(
            AvatarFetcher.DecodeDataUrl("data:image/png;base64,AQID")
        );
        Assert.Equal(new byte[] { 1, 2, 3 }, png.Content);

        var svg = Assert.IsType<FetchedPicture.Bytes>(
            AvatarFetcher.DecodeDataUrl("data:image/svg+xml,%3Csvg%2F%3E")
        );
        Assert.Equal("<svg/>", Encoding.UTF8.GetString(svg.Content));

        Assert.IsType<FetchedPicture.Failed>(
            AvatarFetcher.DecodeDataUrl("data:image/png;base64,***")
        );
        Assert.IsType<FetchedPicture.Failed>(AvatarFetcher.DecodeDataUrl("data:image/png"));
    }

    [Fact]
    public void Avatars_live_in_their_own_folder()
    {
        var id = Guid.Parse("0192f0c4-0000-7000-8000-000000000001");
        Assert.Equal(
            "avatars/0192f0c4000070008000000000000001.webp",
            Domain.Previews.DerivedKey.ForAvatar(id).Value
        );
    }

    private static OidcSettings Settings(Dictionary<string, string?> values) =>
        OidcSettings.FromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()
        );
}
