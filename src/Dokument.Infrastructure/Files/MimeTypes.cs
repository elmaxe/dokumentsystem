using Microsoft.AspNetCore.StaticFiles;

namespace Dokument.Infrastructure.Files;

/// <summary>Picks a file's MIME type from its name, falling back to what the client said.</summary>
public static class MimeTypes
{
    public const string Default = "application/octet-stream";

    private static readonly FileExtensionContentTypeProvider Provider = CreateProvider();

    private static FileExtensionContentTypeProvider CreateProvider()
    {
        var provider = new FileExtensionContentTypeProvider();

        // Phone photos and notes, which the built-in table doesn't know.
        provider.Mappings[".heic"] = "image/heic";
        provider.Mappings[".heif"] = "image/heif";
        provider.Mappings[".md"] = "text/markdown";
        return provider;
    }

    /// <summary>Types a browser would run as a page are previewed as plain text: the viewer shows their source, never the page.</summary>
    private static readonly HashSet<string> Markup = ["text/html", "application/xhtml+xml", "image/svg+xml", "text/xml", "application/xml"];

    /// <summary>The Content-Type to preview a file with: its own, except that markup becomes <c>text/plain</c>.</summary>
    public static string ForPreview(string mimeType) => Markup.Contains(mimeType) ? "text/plain; charset=utf-8" : mimeType;

    public static string For(string name, string? clientType = null) =>
        Provider.TryGetContentType(name, out var type) ? type
        : !string.IsNullOrWhiteSpace(clientType) && clientType.Length <= 255 && clientType.Contains('/') ? clientType.Trim().ToLowerInvariant()
        : Default;
}
