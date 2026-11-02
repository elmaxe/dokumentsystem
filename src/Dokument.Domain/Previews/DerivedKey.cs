using Dokument.Domain.Files;

namespace Dokument.Domain.Previews;

/// <summary>
/// Where a rendition lives, relative to the derived volume (docs/design.md §7): images at
/// <c>thumbs/ab/abcd…/thumb_m.webp</c>, playable copies of video and audio at <c>previews/ab/abcd…/playable.mp4</c>, PDFs of Office files at
/// <c>previews/ab/abcd…/document.pdf</c>.
/// Renditions belong to a blob, not a version, so a copy or a restored version reuses them.
/// </summary>
public sealed record DerivedKey
{
    private DerivedKey(string value) => Value = value;

    public string Value { get; }

    public static DerivedKey For(ContentHash hash, RenditionKind kind)
    {
        var hex = hash.Hex;
        return kind switch
        {
            RenditionKind.Playable => new DerivedKey($"previews/{hex[..2]}/{hex}/{kind.Key()}.mp4"),
            RenditionKind.Document => new DerivedKey($"previews/{hex[..2]}/{hex}/{kind.Key()}.pdf"),
            RenditionKind.Waveform => new DerivedKey($"previews/{hex[..2]}/{hex}/{kind.Key()}.bin"),
            _ => new DerivedKey($"thumbs/{hex[..2]}/{hex}/{kind.Key()}.webp"),
        };
    }

    /// <summary>For values read back from the database.</summary>
    public static DerivedKey From(string value) =>
        (
            value.StartsWith("thumbs/", StringComparison.Ordinal)
            || value.StartsWith("previews/", StringComparison.Ordinal)
        ) && !value.Contains("..", StringComparison.Ordinal)
            ? new DerivedKey(value)
            : throw new ArgumentException("Not a derived storage key.", nameof(value));

    public override string ToString() => Value;
}
