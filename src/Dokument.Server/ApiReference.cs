using Dokument.Server.Auth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Dokument.Server;

/// <summary>
/// The OpenAPI document, and in development the interactive API reference (Scalar) at <c>${PUBLIC_URL}/scalar/</c>.
/// Scalar signs in at the IdP with code + PKCE and redeems the code at our token endpoint, so it calls the API with
/// one of our own bearer sessions (<see cref="TokenEndpoint"/>, docs/design.md §5).
/// </summary>
public static class ApiReference
{
    private const string SecurityScheme = "OAuth2";

    public static WebApplicationBuilder AddApiReference(this WebApplicationBuilder builder)
    {
        var signIn = SignsIn(
            OidcSettings.FromConfiguration(builder.Configuration),
            builder.Environment
        );
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer(
                (document, _, _) =>
                {
                    document.Info.Title = "Dokumentsystem API";
                    return Task.CompletedTask;
                }
            );
            if (signIn)
            {
                options.AddDocumentTransformer(AddSecuritySchemeAsync);
            }
        });
        return builder;
    }

    public static WebApplication MapApiReference(this WebApplication app)
    {
        app.MapOpenApi();
        if (!app.Environment.IsDevelopment())
        {
            return app;
        }

        var oidc = app.Services.GetRequiredService<OidcSettings>();
        app.MapScalarApiReference(
            (options, context) =>
            {
                if (!SignsIn(oidc, app.Environment))
                {
                    return;
                }

                options
                    .AddPreferredSecuritySchemes(SecurityScheme)
                    .AddAuthorizationCodeFlow(
                        SecurityScheme,
                        flow =>
                            flow.WithClientId(oidc.ClientId!)
                                .WithPkce(Pkce.Sha256)
                                .WithRedirectUri(RedirectUri(context.Request))
                                .WithCredentialsLocation(CredentialsLocation.Body)
                                .WithSelectedScopes(Scopes(oidc))
                    )
                    .EnablePersistentAuthentication();
            }
        );
        return app;
    }

    /// <summary>
    /// Where the IdP sends Scalar's sign-in back to: this page at the public URL, which UsePublicUrl gave the request.
    /// The token endpoint accepts it in development only.
    /// </summary>
    internal static string RedirectUri(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}/scalar/";

    private static bool SignsIn(OidcSettings oidc, IHostEnvironment environment) =>
        environment.IsDevelopment() && oidc.Enabled;

    private static string[] Scopes(OidcSettings oidc) =>
        oidc.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Describes the code flow: sign in at the IdP, then redeem the code at our token endpoint.</summary>
    private static async Task AddSecuritySchemeAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken ct
    )
    {
        var services = context.ApplicationServices;
        var oidc = services.GetRequiredService<OidcSettings>();
        var idp = await services
            .GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(AuthSetup.OidcScheme)
            .ConfigurationManager!.GetConfigurationAsync(ct);
        var request = services.GetRequiredService<IHttpContextAccessor>().HttpContext!.Request;

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecurityScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description =
                $"Sign in with {oidc.ProviderName ?? "the IdP"}. The code is redeemed for one of our bearer sessions.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri(idp.AuthorizationEndpoint),
                    TokenUrl = new Uri(
                        $"{request.Scheme}://{request.Host}{request.PathBase}{TokenEndpoint.Path}"
                    ),
                    Scopes = Scopes(oidc).ToDictionary(scope => scope, _ => ""),
                },
            },
        };
        document.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(SecurityScheme, document)] = [],
            },
        ];
    }
}
