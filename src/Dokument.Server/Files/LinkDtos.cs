using Dokument.Domain.Files;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Sharing;
using Microsoft.AspNetCore.Http;

namespace Dokument.Server.Files;

/// <summary>A node's public link, for its owner (§9).</summary>
/// <param name="Url">The link to send, or null if the server can no longer read its token (turn it off and on for a new one).</param>
/// <param name="ExpiresAt">When it stops working; null for never.</param>
/// <param name="Opens">How many times it was opened, and when last.</param>
public sealed record LinkDto(
    Guid Id,
    string? Url,
    bool AllowDownload,
    bool HasPassword,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset CreatedAt,
    int Opens,
    DateTimeOffset? LastOpenedAt
)
{
    public static LinkDto From(LinkView view, HttpContext context) =>
        new(
            view.Link.Id,
            view.Token is { } token
                ? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/s/{token}"
                : null,
            view.Link.AllowDownload,
            view.Link.PasswordHash is not null,
            view.Link.ExpiresAt,
            view.Link.CreatedAt,
            view.Opens,
            view.LastOpenedAt
        );
}

/// <param name="ExpiresAt">Null for no expiry. Must be later than now.</param>
/// <param name="Password">A new password; null leaves it as it is.</param>
/// <param name="RemovePassword">Takes the password off.</param>
public sealed record SaveLinkRequest(
    bool AllowDownload,
    DateTimeOffset? ExpiresAt,
    string? Password,
    bool RemovePassword = false
);

public sealed record UnlockLinkRequest(string Password);

/// <summary>
/// A file or folder as someone with a public link sees it: no tags, details, versions, stars or who else has access.
/// </summary>
public sealed record LinkNodeDto(
    Guid Id,
    Guid? ParentId,
    NodeKind Kind,
    string Name,
    long? Size,
    string? MimeType,
    DateTimeOffset ModifiedAt,
    ThumbnailState Thumbnail,
    FolderCounts? Contents,
    MediaDto? Media,
    bool ShownAsPdf
)
{
    public static LinkNodeDto From(NodeDto node) =>
        new(
            node.Id,
            node.ParentId,
            node.Kind,
            node.Name,
            node.Size,
            node.MimeType,
            node.ModifiedAt,
            node.Thumbnail,
            node.Contents,
            node.Media,
            node.ShownAsPdf
        );
}

/// <summary>What a public link opens on.</summary>
/// <param name="Node">The link's own file or folder.</param>
/// <param name="SharedBy">The owner's name.</param>
/// <param name="ExpiresAt">When the link stops working; null for never.</param>
public sealed record LinkPage(
    LinkNodeDto Node,
    string SharedBy,
    DateTimeOffset? ExpiresAt,
    bool AllowDownload
);

/// <param name="Path">From the link's own folder down to this one, which is last.</param>
public sealed record LinkFolder(LinkNodeDto Folder, List<PathEntry> Path, List<LinkNodeDto> Items);

/// <param name="Path">The folders from the link's own one down to the node's, never any above the link.</param>
public sealed record LinkNodeDetails(LinkNodeDto Node, List<PathEntry> Path);
