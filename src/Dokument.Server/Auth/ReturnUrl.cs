namespace Dokument.Server.Auth;

public static class ReturnUrl
{
    /// <summary>Only same-site paths are allowed after login, so the login link can't be used as an open redirect.</summary>
    public static string Sanitize(string? returnTo) =>
        returnTo is { Length: > 0 } && returnTo[0] == '/' && !returnTo.StartsWith("//", StringComparison.Ordinal) &&
        !returnTo.StartsWith("/\\", StringComparison.Ordinal) && !returnTo.Any(char.IsControl)
            ? returnTo
            : "/";
}
