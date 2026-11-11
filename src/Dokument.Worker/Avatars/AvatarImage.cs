using NetVips;

namespace Dokument.Worker.Avatars;

/// <summary>
/// Turns whatever the sign-in provider sent as a picture into our own avatar (docs/design.md §5.1): a square
/// <see cref="Size"/> px WebP, cropped from the middle, without metadata. Only libvips's image loaders are trusted
/// with the bytes, and the result is always re-encoded, so nothing the provider sent is served as it is.
/// </summary>
public static class AvatarImage
{
    public const int Size = 256;

    private const int WebpQuality = 82;

    /// <summary>The loaders we accept: photos and drawings, never PDFs, CSV or matrix files.</summary>
    private static readonly HashSet<string> Loaders =
    [
        "jpeg",
        "png",
        "spng",
        "webp",
        "gif",
        "nsgif",
        "heif",
        "tiff",
        "svg",
    ];

    /// <summary>The avatar's WebP bytes, or null when the bytes aren't an image we read.</summary>
    public static byte[]? Make(byte[] bytes)
    {
        // "VipsForeignLoadJpegBuffer" → "jpeg".
        var loader = Image
            .FindLoadBuffer(bytes)
            ?.Replace("VipsForeignLoad", "", StringComparison.Ordinal)
            .Replace("Buffer", "", StringComparison.Ordinal)
            .Replace("Source", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        if (loader is null || !Loaders.Contains(loader))
        {
            return null;
        }

        try
        {
            // The first frame of an animation, turned upright by its EXIF orientation, filled to a square.
            using var image = Image.ThumbnailBuffer(
                bytes,
                Size,
                height: Size,
                crop: Enums.Interesting.Centre,
                size: Enums.Size.Both
            );
            using var srgb =
                image.Interpretation == Enums.Interpretation.Srgb
                    ? image.Copy()
                    : image.Colourspace(Enums.Interpretation.Srgb);
            return srgb.WebpsaveBuffer(q: WebpQuality, keep: Enums.ForeignKeep.None);
        }
        catch (VipsException)
        {
            return null;
        }
    }
}
