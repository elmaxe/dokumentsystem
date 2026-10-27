using Dokument.Domain.Settings;
using Dokument.Infrastructure.Settings;

namespace Dokument.Infrastructure.Ml;

/// <summary>
/// An ML service set by the environment (<c>Ml__Url</c>, <c>Ml__ApiKey</c>), tried after the admin's list (docs/design.md
/// §11). Local development uses it, since the server on the host and the worker in a container reach the service at
/// different addresses; the admin page shows it but can't change it.
/// </summary>
public sealed class MlOptions
{
    public const string SectionName = "Ml";

    public string? Url { get; set; }

    public string? ApiKey { get; set; }

    /// <summary>The service, or null when none is set or the URL isn't one.</summary>
    public ServiceUrl? ServiceUrl => ServiceUrl.TryParse(Url, out _);
}

/// <summary>Every ML service to try, in order: the admin's list, then the environment's (§11).</summary>
public sealed class MlServices(
    SettingsStore settings,
    SecretProtector secrets,
    Microsoft.Extensions.Options.IOptions<MlOptions> options
)
{
    public async Task<List<MlConnection>> ListAsync(CancellationToken ct)
    {
        var list = (await settings.GetAsync(SettingKeys.Ml, ct))
            .Endpoints.Select(e => new MlConnection(
                e.Url,
                e.ProtectedApiKey is { } key ? secrets.Unprotect(key) : null
            ))
            .ToList();
        if (options.Value.ServiceUrl is { } url && list.All(c => c.Url.Value != url.Value))
        {
            list.Add(
                new MlConnection(
                    url,
                    string.IsNullOrWhiteSpace(options.Value.ApiKey) ? null : options.Value.ApiKey,
                    FromEnvironment: true
                )
            );
        }

        return list;
    }

    /// <summary>Whether any ML service is set at all; without one, nothing that needs it is queued.</summary>
    public async Task<bool> ConfiguredAsync(CancellationToken ct) =>
        options.Value.ServiceUrl is not null
        || (await settings.GetAsync(SettingKeys.Ml, ct)).Endpoints.Count > 0;
}
