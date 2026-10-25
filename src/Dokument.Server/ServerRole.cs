using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Storage;
using Dokument.Server.Admin;
using Dokument.Server.Auth;
using Dokument.Server.Files;
using Dokument.Server.Live;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

namespace Dokument.Server;

/// <summary>The <c>server</c> role: REST API, uploads, SignalR and the web SPA (docs/design.md §3.1).</summary>
public static class ServerRole
{
    public static WebApplicationBuilder AddServerRole(this WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi(options =>
            options.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Dokumentsystem API";
                    return Task.CompletedTask;
                }
            )
        );
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<DriveExceptionHandler>();
        builder.Services.AddHostedService<UploadCleanup>();
        builder.Services.AddHostedService<PurgeSchedule>();
        builder
            .Services.AddSignalR()
            .AddJsonProtocol(options =>
                options.PayloadSerializerOptions.Converters.Add(
                    new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
                )
            );
        builder.Services.AddSingleton<LiveConnections>();
        builder.Services.AddHostedService<LiveListener>();
        builder.Services.AddScoped<NodeDtoMapper>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
            );

            // Numbers are numbers: without this the OpenAPI schema types every integer as "number | string".
            options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        builder.AddDokumentAuth();

        // Keys live on the originals volume, so they're backed up with it and survive restarts (§21).
        var storage =
            builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
            ?? new StorageOptions();
        builder
            .Services.AddDataProtection()
            .SetApplicationName("Dokumentsystem")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(storage.OriginalsPath, "keys"))
            );
        builder.Services.AddSingleton<SecretProtector>();
        return builder;
    }

    public static WebApplication MapServerRole(this WebApplication app)
    {
        app.UsePublicUrl(PublicUrl.FromConfiguration(app.Configuration));
        app.UseExceptionHandler();

        // The built web app is copied into wwwroot by the Docker image. Unknown non-API paths go to the SPA router.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UseCsrfProtection();
        app.Use(
            (context, next) =>
            {
                // Which tab made the change, so its own notice can be skipped (§15.4).
                var client = context.Request.Headers[LiveHub.ClientHeader].ToString();
                if (LiveHub.IsClientId(client))
                {
                    context.RequestServices.GetRequiredService<LiveChanges>().Origin = client;
                }

                return next(context);
            }
        );

        app.MapOpenApi();
        if (app.Environment.IsDevelopment())
        {
            // Interactive API reference at /scalar, reading /openapi/v1.json. Development only.
            app.MapScalarApiReference();
        }

        var api = app.MapGroup("/api/v1");
        api.MapGet("/server/info", GetServerInfo)
            .WithName("GetServerInfo")
            .WithTags("Server")
            .AllowAnonymous();
        api.MapAuthEndpoints();
        api.MapFileEndpoints();
        api.MapFileDetailsEndpoints();
        api.MapTrashEndpoints();
        api.MapStarredAndRecentEndpoints();
        api.MapSearchEndpoints();
        api.MapTusEndpoints();
        api.MapAdminEndpoints();
        app.MapHub<LiveHub>(LiveHub.Path).RequireAuthorization();
        app.MapFallback("/api/{**path}", () => TypedResults.NotFound());
        app.MapFallbackToFile("index.html");
        return app;
    }

    private static Ok<ServerInfo> GetServerInfo() =>
        TypedResults.Ok(
            new ServerInfo(
                Name: "Dokumentsystem",
                Version: typeof(ServerRole)
                    .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion
                    ?? "dev"
            )
        );

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
