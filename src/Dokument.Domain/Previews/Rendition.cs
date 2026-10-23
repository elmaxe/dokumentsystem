using Dokument.Domain.Files;

namespace Dokument.Domain.Previews;

/// <summary>One image the worker made from a blob (docs/design.md §6). Regenerable, so it never counts toward quota.</summary>
public sealed class Rendition
{
    public required ContentHash BlobHash { get; init; }
    public required RenditionKind Kind { get; init; }
    public required DerivedKey StorageKey { get; init; }
    public string MimeType { get; init; } = "image/webp";
    public required int Width { get; init; }
    public required int Height { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
