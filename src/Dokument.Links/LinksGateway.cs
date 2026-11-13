using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace Dokument.Links;

/// <summary>
/// The links gateway (docs/design.md §9.1): the only part of Dokumentsystem on the internet when the rest is kept
/// private. It serves the link page and forwards the link API to the server's links port, and nothing else. It holds no
/// secrets and reaches no database or storage, so someone who takes it over gets what a link visitor gets.
/// </summary>
public static partial class LinksGateway
{
    public const string UpstreamSetting = "LINKS_UPSTREAM";
    public const string UpstreamClient = "upstream";

    /// <summary>Request headers the server is sent; everything else, cookies and credentials included, stays here.</summary>
    private static readonly string[] PassedOn =
    [
        HeaderNames.Accept,
        HeaderNames.AcceptLanguage,
        HeaderNames.ContentType,
        HeaderNames.IfMatch,
        HeaderNames.IfModifiedSince,
        HeaderNames.IfNoneMatch,
        HeaderNames.IfRange,
        HeaderNames.Range,
        HeaderNames.UserAgent,
    ];

    /// <summary>Response headers that belong to one hop, not to the response.</summary>
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase)
    {
        HeaderNames.Connection,
        HeaderNames.KeepAlive,
        HeaderNames.ProxyAuthenticate,
        HeaderNames.ProxyConnection,
        HeaderNames.TE,
        HeaderNames.Trailer,
        HeaderNames.TransferEncoding,
        HeaderNames.Upgrade,
        HeaderNames.Server,
    };

    /// <summary>The unlock cookie, the one cookie that goes to the server (Dokument.Server.Files.LinkEndpoints).</summary>
    private const string UnlockCookie = "dk_link";

    /// <summary>A link token: 128 bits as base64url (Dokument.Domain.Sharing.LinkToken).</summary>
    [GeneratedRegex("^[A-Za-z0-9_-]{22}$")]
    private static partial Regex TokenFormat();

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        var upstream = Upstream(builder.Configuration[UpstreamSetting]);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            // The only body a visitor sends is a password.
            kestrel.Limits.MaxRequestBodySize = 4096;
        });
        builder
            .Services.AddHttpClient(
                UpstreamClient,
                http =>
                {
                    http.BaseAddress = upstream;
                    // Downloads of large files stream for as long as they take.
                    http.Timeout = Timeout.InfiniteTimeSpan;
                }
            )
            .ConfigurePrimaryHttpMessageHandler(() =>
                new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    AutomaticDecompression = System.Net.DecompressionMethods.None,
                    ConnectTimeout = TimeSpan.FromSeconds(10),
                }
            );
        return builder;
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        var app = builder.Build();
        app.UseForwardedHeaders(TrustedProxies.Options(app.Configuration["TRUSTED_PROXIES"]));
        app.Use(
            (context, next) =>
            {
                var headers = context.Response.Headers;
                headers.XContentTypeOptions = "nosniff";
                // The token is in the address, so it never goes along to another site.
                headers["Referrer-Policy"] = "no-referrer";
                headers.ContentSecurityPolicy =
                    "frame-ancestors 'none'; object-src 'none'; base-uri 'none'";
                return next(context);
            }
        );

        var files = app.Environment.WebRootFileProvider;
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

        app.MapGet("/health/live", () => TypedResults.Text("ok"));
        app.MapGet("/s/{token}", (string token, HttpContext context) => Page(files, context));
        app.MapMethods(
            "/api/v1/links/{token}/{**rest}",
            [HttpMethods.Get, HttpMethods.Head, HttpMethods.Post],
            ForwardAsync
        );
        app.MapFallback(() => TypedResults.NotFound());
        return app;
    }

    private static Uri Upstream(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri
            : throw new InvalidOperationException(
                $"{UpstreamSetting} must be the server's links port, e.g. http://server:8081. Got '{value}'."
            );

    /// <summary>The link page; it reads the token from the address and asks the API for the rest.</summary>
    private static IResult Page(IFileProvider files, HttpContext context)
    {
        var page = files.GetFileInfo("link.html");
        if (!page.Exists)
        {
            return TypedResults.NotFound();
        }

        context.Response.Headers.CacheControl = "no-cache";
        return TypedResults.Stream(page.CreateReadStream(), "text/html; charset=utf-8");
    }

    private static async Task ForwardAsync(
        string token,
        string? rest,
        HttpContext context,
        IHttpClientFactory clients
    )
    {
        var request = context.Request;
        var post = HttpMethods.IsPost(request.Method);
        if (!TokenFormat().IsMatch(token) || (post && rest != "unlock"))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var path = rest is null ? $"api/v1/links/{token}" : $"api/v1/links/{token}/{rest}";
        using var message = new HttpRequestMessage(
            new HttpMethod(request.Method),
            path + request.QueryString
        );
        if (post)
        {
            message.Content = new StreamContent(request.Body);
        }

        foreach (var name in PassedOn)
        {
            if (request.Headers.TryGetValue(name, out var values))
            {
                var value = values.ToString();
                if (!message.Headers.TryAddWithoutValidation(name, value))
                {
                    message.Content?.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        if (request.Cookies[UnlockCookie] is { } unlock)
        {
            message.Headers.TryAddWithoutValidation(HeaderNames.Cookie, $"{UnlockCookie}={unlock}");
        }

        if (context.Connection.RemoteIpAddress is { } visitor)
        {
            message.Headers.TryAddWithoutValidation("X-Forwarded-For", visitor.ToString());
        }

        HttpResponseMessage response;
        try
        {
            response = await clients
                .CreateClient(UpstreamClient)
                .SendAsync(
                    message,
                    HttpCompletionOption.ResponseHeadersRead,
                    context.RequestAborted
                );
        }
        catch (HttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using var _ = response;
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
        {
            if (!HopByHop.Contains(name))
            {
                context.Response.Headers[name] = values.ToArray();
            }
        }

        // Streams as it arrives, so playing video and seeking start without waiting for the whole file.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
