namespace Dokument.Domain.Identity;

/// <summary>A readable name for a session in the "active devices" list, e.g. "Firefox on Linux". Never blank, at most 100 characters.</summary>
public sealed record DeviceName
{
    public const int MaxLength = 100;

    private DeviceName(string value) => Value = value;

    public string Value { get; }

    /// <summary>Trims and shortens the name. Returns null for a blank one.</summary>
    public static DeviceName? From(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : new DeviceName(trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength]);
    }

    /// <summary>Names the browser and OS from a User-Agent, falling back to the raw string.</summary>
    public static DeviceName? FromUserAgent(string? userAgent)
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

        return From(
            (browser, os) switch
            {
                (not null, not null) => $"{browser} on {os}",
                (not null, null) => browser,
                (null, not null) => os,
                _ => userAgent,
            }
        );
    }

    public override string ToString() => Value;
}
