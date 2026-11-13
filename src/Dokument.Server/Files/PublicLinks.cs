using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.Server.Files;

/// <summary>
/// Where public links live when the links gateway serves them (docs/design.md §9.1): <c>LINKS_PUBLIC_URL</c> is the
/// address owners copy, and <c>LINKS_PORT</c> is a second port that only the gateway reaches, on which nothing but the
/// link API exists.
/// </summary>
public sealed record PublicLinks(Uri? PublicUrl, int? Port)
{
    public const string PublicUrlSetting = "LINKS_PUBLIC_URL";
    public const string PortSetting = "LINKS_PORT";

    /// <summary>The paths the links port answers; anything else there is 404.</summary>
    private static readonly PathString[] Open = ["/api/v1/links", "/health/live"];

    public static PublicLinks FromConfiguration(IConfiguration configuration) =>
        new(ParseUrl(configuration[PublicUrlSetting]), ParsePort(configuration[PortSetting]));

    /// <summary>The address to hand out for a link's token.</summary>
    public string Address(HttpContext context, string token) =>
        PublicUrl is { } url
            ? $"{url.GetLeftPart(UriPartial.Authority)}/s/{token}"
            : $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/s/{token}";

    public bool IsLinksPort(HttpContext context) =>
        Port is { } port && context.Connection.LocalPort == port;

    public static bool IsOpen(PathString path) =>
        Open.Any(open => path.StartsWithSegments(open, StringComparison.Ordinal));

    private static Uri? ParseUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (
            !Uri.TryCreate(value.Trim().TrimEnd('/'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
        )
        {
            throw new InvalidOperationException(
                $"{PublicUrlSetting} must be the links gateway's own address, with no path, e.g. https://links.example.com. Got '{value}'."
            );
        }

        return uri;
    }

    private static int? ParsePort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return int.TryParse(value, out var port) && port is > 0 and < 65536
            ? port
            : throw new InvalidOperationException(
                $"{PortSetting} must be a port number, e.g. 8081. Got '{value}'."
            );
    }
}

public static class PublicLinksSetup
{
    /// <summary>Reads the settings and, with <c>LINKS_PORT</c>, has the server listen on that port too.</summary>
    public static WebApplicationBuilder AddPublicLinks(this WebApplicationBuilder builder)
    {
        var links = PublicLinks.FromConfiguration(builder.Configuration);
        builder.Services.AddSingleton(links);
        if (links.Port is { } port)
        {
            // Added to the ports or addresses already configured, so the main port keeps working as it is.
            var ports = builder.WebHost.GetSetting(WebHostDefaults.HttpPortsKey);
            var urls = builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey);
            if (!string.IsNullOrEmpty(ports))
            {
                builder.WebHost.UseSetting(WebHostDefaults.HttpPortsKey, $"{ports};{port}");
            }
            else
            {
                builder.WebHost.UseSetting(
                    WebHostDefaults.ServerUrlsKey,
                    $"{(string.IsNullOrEmpty(urls) ? "http://localhost:5000" : urls)};http://+:{port}"
                );
            }
        }

        return builder;
    }

    /// <summary>
    /// On the links port, everything but the link API is 404, and the visitor's address is the one the gateway sends
    /// in <c>X-Forwarded-For</c>: the port is on a network only the gateway reaches, so whatever arrives there came
    /// through it. Runs first, before the main port's trusted proxies.
    /// </summary>
    public static IApplicationBuilder UseLinksPort(this IApplicationBuilder app, PublicLinks links)
    {
        if (links.Port is null)
        {
            return app;
        }

        return app.Use(
            (context, next) =>
            {
                if (!links.IsLinksPort(context))
                {
                    return next(context);
                }

                if (!PublicLinks.IsOpen(context.Request.Path))
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return Task.CompletedTask;
                }

                var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
                if (IPAddress.TryParse(forwarded, out var visitor))
                {
                    context.Connection.RemoteIpAddress = visitor;
                }

                context.Request.Headers.Remove("X-Forwarded-For");

                // No sign-in reaches the server this way, even if the gateway were made to forward one.
                context.Request.Headers.Remove("Authorization");
                var unlock = context.Request.Cookies[LinkEndpoints.UnlockCookie];
                context.Request.Headers.Remove("Cookie");
                if (unlock is not null)
                {
                    context.Request.Headers.Cookie = $"{LinkEndpoints.UnlockCookie}={unlock}";
                }

                return next(context);
            }
        );
    }
}
