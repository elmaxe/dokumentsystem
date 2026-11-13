using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;

namespace Dokument.Links;

/// <summary>
/// The reverse proxy in front of the gateway, if any, from <c>TRUSTED_PROXIES</c>: addresses or networks,
/// comma-separated, as on the server (Dokument.Server.Auth.TrustedProxies, kept apart so the gateway references
/// nothing of the app). Without it every visitor has the proxy's address, and the server's rate limits count them as one.
/// </summary>
internal static class TrustedProxies
{
    public static ForwardedHeadersOptions Options(string? value)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        };
        if (string.IsNullOrWhiteSpace(value))
        {
            return options;
        }

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        options.ForwardLimit = null;
        foreach (
            var part in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (part.Contains('/') && IPNetwork.TryParse(part, out var network))
            {
                options.KnownIPNetworks.Add(network);
            }
            else if (IPAddress.TryParse(part, out var address))
            {
                options.KnownProxies.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"TRUSTED_PROXIES takes addresses or networks, comma-separated, e.g. 172.16.0.0/12,10.0.0.5. Got '{part}'."
                );
            }
        }

        return options;
    }
}
