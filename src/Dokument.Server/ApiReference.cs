using Dokument.Server.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Scalar.AspNetCore;

namespace Dokument.Server;

/// <summary>
/// The OpenAPI document, and in development the interactive API reference (Scalar) at <c>${PUBLIC_URL}/scalar</c>. When
/// <c>OIDC_SCALAR_CLIENT_ID</c> is set, Scalar signs in at the IdP with OAuth (code + PKCE) and calls the API with the
/// access token it gets (<see cref="AccessTokenAuthentication"/>).
/// </summary>
public static class ApiReference
{
    private const string SecurityScheme = "OAuth2";

    public static WebApplicationBuilder AddApiReference(this WebApplicationBuilder builder)
    {
        var signIn = AccessTokenAuthentication.IsEnabled(
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
                if (!AccessTokenAuthentication.IsEnabled(oidc, app.Environment))
                {
                    return;
                }

                // The IdP sends the browser back to this page at the public URL, which UsePublicUrl gave the request.
                var request = context.Request;
                options
                    .AddPreferredSecuritySchemes(SecurityScheme)
                    .AddAuthorizationCodeFlow(
                        SecurityScheme,
                        flow =>
                            flow.WithClientId(oidc.ScalarClientId!)
                                .WithPkce(Pkce.Sha256)
                                .WithRedirectUri(
                                    $"{request.Scheme}://{request.Host}{request.PathBase}/scalar/"
                                )
                                .WithSelectedScopes(Scopes(oidc))
                    )
                    .EnablePersistentAuthentication();
            }
        );
        return app;
    }

    private static string[] Scopes(OidcSettings oidc) =>
        oidc.Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Describes the IdP's code flow, with the endpoints from its discovery document.</summary>
    private static async Task AddSecuritySchemeAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken ct
    )
    {
        var oidc = context.ApplicationServices.GetRequiredService<OidcSettings>();
        var jwt = context
            .ApplicationServices.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(AccessTokenAuthentication.Scheme);
        var idp = await jwt.ConfigurationManager!.GetConfigurationAsync(ct);

        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes[SecurityScheme] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.OAuth2,
            Description = $"Sign in with {oidc.ProviderName ?? "the IdP"}. Development only.",
            Flows = new OpenApiOAuthFlows
            {
                AuthorizationCode = new OpenApiOAuthFlow
                {
                    AuthorizationUrl = new Uri(idp.AuthorizationEndpoint),
                    TokenUrl = new Uri(idp.TokenEndpoint),
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
