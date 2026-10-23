namespace Dokument.Domain.Settings;

/// <summary>
/// The base URL of a companion container, such as the ML service (docs/design.md §11): absolute http or https, with
/// no credentials, query or fragment. A trailing slash is dropped so paths can be appended.
/// </summary>
public sealed record ServiceUrl
{
    public const int MaxLength = 2048;

    private ServiceUrl(string value) => Value = value;

    public string Value { get; }

    public static ServiceUrl? TryParse(string? value, out string? error)
    {
        var trimmed = value?.Trim() ?? "";
        error = null;
        if (
            trimmed.Length is 0 or > MaxLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
        )
        {
            error = "Enter an http:// or https:// address, such as http://ml:3003.";
            return null;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "Leave out user names, passwords, ?query and #fragment parts.";
            return null;
        }

        return new ServiceUrl(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    public static ServiceUrl Parse(string value) =>
        TryParse(value, out var error) ?? throw new FormatException(error);

    public Uri Resolve(string path) => new($"{Value}/{path.TrimStart('/')}");

    public override string ToString() => Value;
}
