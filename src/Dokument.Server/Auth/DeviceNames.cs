namespace Dokument.Server.Auth;

/// <summary>Turns a User-Agent into a readable name for the "active devices" list, e.g. "Firefox on Linux".</summary>
public static class DeviceNames
{
    public static string? FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return null;
        }

        bool Has(string s) => userAgent.Contains(s, StringComparison.OrdinalIgnoreCase);

        var browser =
            Has("Firefox/") ? "Firefox"
            : Has("Edg/") ? "Edge"
            : Has("OPR/") ? "Opera"
            : Has("Chrome/") || Has("CriOS/") ? "Chrome"
            : Has("Safari/") ? "Safari"
            : null;
        var os =
            Has("iPhone") || Has("iPad") ? "iOS"
            : Has("Android") ? "Android"
            : Has("Windows") ? "Windows"
            : Has("Mac OS X") || Has("Macintosh") ? "macOS"
            : Has("CrOS") ? "ChromeOS"
            : Has("Linux") ? "Linux"
            : null;

        var name = (browser, os) switch
        {
            (not null, not null) => $"{browser} on {os}",
            (not null, null) => browser,
            (null, not null) => os,
            _ => userAgent,
        };
        return name.Length <= 100 ? name : name[..100];
    }
}
