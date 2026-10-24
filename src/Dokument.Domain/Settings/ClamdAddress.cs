namespace Dokument.Domain.Settings;

/// <summary>Where a ClamAV daemon listens (docs/design.md §21), written <c>tcp://host:port</c>. The port defaults to 3310.</summary>
public sealed record ClamdAddress
{
    public const int DefaultPort = 3310;

    private ClamdAddress(string host, int port)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }

    public static ClamdAddress? TryParse(string? value, out string? error)
    {
        var trimmed = value?.Trim() ?? "";
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "tcp://" + trimmed;
        }

        error = null;
        if (
            trimmed.Length > ServiceUrl.MaxLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != "tcp"
            || string.IsNullOrEmpty(uri.Host)
            || uri.AbsolutePath is not ("" or "/")
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
        )
        {
            error = "Enter the scanner as tcp://host:port, such as tcp://clamav:3310.";
            return null;
        }

        return new ClamdAddress(
            uri.Host,
            uri.IsDefaultPort || uri.Port < 0 ? DefaultPort : uri.Port
        );
    }

    public static ClamdAddress Parse(string value) =>
        TryParse(value, out var error) ?? throw new FormatException(error);

    public override string ToString() => $"tcp://{Host}:{Port}";
}
