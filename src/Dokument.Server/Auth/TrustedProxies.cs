using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace Dokument.Server.Auth;

/// <summary>
/// The reverse proxies whose <c>X-Forwarded-For</c> is believed (docs/design.md §21), from <c>TRUSTED_PROXIES</c>:
/// addresses or networks, comma-separated. Without it, every visitor behind the proxy shares the proxy's address,
/// so rate limits count them all together and the audit log records the proxy.
/// </summary>
public static class TrustedProxies
{
    public const string Setting = "TRUSTED_PROXIES";

    public static TrustedProxyList? FromConfiguration(IConfiguration configuration)
    {
        var value = configuration[Setting];
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var addresses = new List<IPAddress>();
        var networks = new List<IPNetwork>();
        foreach (
            var part in value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (part.Contains('/') && IPNetwork.TryParse(part, out var network))
            {
                networks.Add(network);
            }
            else if (IPAddress.TryParse(part, out var address))
            {
                addresses.Add(address);
            }
            else
            {
                throw new InvalidOperationException(
                    $"{Setting} takes addresses or networks, comma-separated, e.g. 172.16.0.0/12,10.0.0.5. Got '{part}'."
                );
            }
        }

        return new TrustedProxyList(addresses, networks);
    }

    /// <summary>
    /// Gives each request the address of whoever the trusted proxies say sent it. Unset, only a proxy on the same
    /// machine (loopback) is trusted, which is ASP.NET's default.
    /// </summary>
    public static IApplicationBuilder UseTrustedProxies(
        this IApplicationBuilder app,
        TrustedProxyList? proxies
    )
    {
        var options = new ForwardedHeadersOptions
        {
            // The scheme and host come from PUBLIC_URL, so only the client's address is taken from the proxy.
            ForwardedHeaders = ForwardedHeaders.XForwardedFor,
        };
        if (proxies is not null)
        {
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (var address in proxies.Addresses)
            {
                options.KnownProxies.Add(address);
            }

            foreach (var network in proxies.Networks)
            {
                options.KnownIPNetworks.Add(network);
            }

            // A chain of trusted proxies (e.g. Cloudflare in front of Caddy) is walked back to the first untrusted hop.
            options.ForwardLimit = null;
        }

        return app.UseForwardedHeaders(options);
    }
}

/// <summary>The addresses and networks in <c>TRUSTED_PROXIES</c>.</summary>
public sealed record TrustedProxyList(
    IReadOnlyList<IPAddress> Addresses,
    IReadOnlyList<IPNetwork> Networks
);
