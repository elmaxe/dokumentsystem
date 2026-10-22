using Microsoft.AspNetCore.StaticFiles;

namespace Dokument.Infrastructure.Files;

/// <summary>Picks a file's MIME type from its name, falling back to what the client said.</summary>
public static class MimeTypes
{
    public const string Default = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider Provider = new();

    public static string For(string name, string? clientType = null) =>
        Provider.TryGetContentType(name, out var type) ? type
        : !string.IsNullOrWhiteSpace(clientType) && clientType.Length <= 255 && clientType.Contains('/') ? clientType.Trim().ToLowerInvariant()
        : Default;
}
