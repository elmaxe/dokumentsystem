using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Dokument.Server.Auth;

/// <summary>
/// Makes the app see itself at <c>PUBLIC_URL</c> (docs/design.md §21). TLS ends at the reverse proxy, so without
/// this the OIDC redirect URI and the cookie's Secure flag would follow the proxy's internal http hop.
/// </summary>
public static class PublicUrl
{
    public static Uri? FromConfiguration(IConfiguration configuration)
    {
        var value = configuration["PUBLIC_URL"];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (
            !Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
        )
        {
            throw new InvalidOperationException(
                $"PUBLIC_URL must be an absolute http(s) URL, e.g. https://docs.example.com. Got '{value}'."
            );
        }

        return uri;
    }

    public static IApplicationBuilder UsePublicUrl(this IApplicationBuilder app, Uri? publicUrl)
    {
        if (publicUrl is null)
        {
            return app;
        }

        var host = HostString.FromUriComponent(publicUrl);
        app.Use(
            (context, next) =>
            {
                context.Request.Scheme = publicUrl.Scheme;
                context.Request.Host = host;
                return next(context);
            }
        );

        var pathBase = publicUrl.AbsolutePath.TrimEnd('/');
        return pathBase.Length > 0 ? app.UsePathBase(pathBase) : app;
    }
}
