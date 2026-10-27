using Dokument.Domain.Settings;
using Microsoft.Extensions.Configuration;

namespace Dokument.Infrastructure.Settings;

/// <summary>
/// Where Gotenberg runs (§10.3, §14). <c>GOTENBERG_URL</c> in the configuration wins over the admin setting, so a
/// deployment that starts Gotenberg itself, like the Aspire AppHost, needs no setup. Each process reads its own, since
/// the server on the host and the worker in a container reach Gotenberg at different addresses.
/// </summary>
public sealed class OfficeAddress
{
    public const string ConfigKey = "GOTENBERG_URL";

    public OfficeAddress(IConfiguration configuration)
    {
        var value = configuration[ConfigKey];
        if (!string.IsNullOrWhiteSpace(value))
        {
            Configured =
                ServiceUrl.TryParse(value, out var error)
                ?? throw new InvalidOperationException($"{ConfigKey}: {error}");
        }
    }

    /// <summary>The address from the configuration, or null when the admin setting decides.</summary>
    public ServiceUrl? Configured { get; }

    public async Task<OfficeSettings> GetAsync(SettingsStore settings, CancellationToken ct) =>
        Configured is { } url
            ? new OfficeSettings(url)
            : await settings.GetAsync(SettingKeys.Office, ct);
}
