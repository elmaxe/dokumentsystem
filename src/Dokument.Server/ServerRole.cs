using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dokument.Infrastructure.Storage;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Dokument.Server;

/// <summary>The <c>server</c> role: REST API, uploads, SignalR and the web SPA (docs/design.md §3.1).</summary>
public static class ServerRole
{
    public static WebApplicationBuilder AddServerRole(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Dokumentsystem API";
            return Task.CompletedTask;
        }));
        builder.Services.AddProblemDetails();
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
        builder.AddDokumentAuth();
        return builder;
    }

    public static WebApplication MapServerRole(this WebApplication app)
    {
        app.UsePublicUrl(PublicUrl.FromConfiguration(app.Configuration));

        // The built web app is copied into wwwroot by the Docker image. Unknown non-API paths go to the SPA router.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseCsrfProtection();

        app.MapOpenApi();

        var api = app.MapGroup("/api/v1");
        api.MapGet("/server/info", GetServerInfo).WithName("GetServerInfo").WithTags("Server").AllowAnonymous();
        api.MapAuthEndpoints();
        app.MapFallback("/api/{**path}", () => TypedResults.NotFound());
        app.MapFallbackToFile("index.html");
        return app;
    }

    private static Ok<ServerInfo> GetServerInfo() => TypedResults.Ok(new ServerInfo(
        Name: "Dokumentsystem",
        Version: typeof(ServerRole).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev"));

    /// <summary>Creates the data layout (docs/design.md §7) on the mounted volumes. Called on startup, not at build time.</summary>
    public static void EnsureStorageDirectories(IServiceProvider services)
    {
        var storage = services.GetRequiredService<IOptions<StorageOptions>>().Value;
        Directory.CreateDirectory(Path.Combine(storage.OriginalsPath, "blobs"));
        Directory.CreateDirectory(Path.Combine(storage.OriginalsPath, "tmp"));
        Directory.CreateDirectory(storage.DerivedPath);
        Directory.CreateDirectory(storage.BackupsPath);
    }
}

public sealed record ServerInfo(string Name, string Version);
