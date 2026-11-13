namespace Dokument.Domain.Identity;

/// <summary>
/// A person's stored profile picture (docs/design.md §5.1): a 256 px WebP the worker made from their sign-in
/// provider's picture claim.
/// </summary>
/// <param name="Version">
/// A short hash of the stored bytes. Clients put it in the picture's URL, so a new picture is a new URL and the old
/// one can be cached for good.
/// </param>
/// <param name="SourceUrl">The picture claim it was made from.</param>
/// <param name="ETag">The provider's ETag for it, sent back to skip downloading an unchanged picture.</param>
public sealed record Avatar(string Version, string SourceUrl, string? ETag)
{
    /// <summary>The longest picture claim that is kept. Longer ones (huge <c>data:</c> URLs) are ignored.</summary>
    public const int MaxSourceLength = 64 * 1024;
}
