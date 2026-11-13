using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Identity;
using Dokument.Domain.Sharing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Sharing;

/// <summary>
/// Public links (docs/design.md §9): the owner turns one on for a file or folder, and anyone with it can look without
/// signing in, through <c>/s/{token}</c>. A link is viewer-only, with an optional password, expiry and download switch.
/// What's behind it is the node and, for a folder, everything in it that isn't in the trash.
/// </summary>
public sealed class LinkService(
    DokumentDbContext db,
    DriveService drive,
    AuditLog audit,
    LiveChanges live,
    IDataProtectionProvider protection,
    TimeProvider time
)
{
    /// <summary>How long typing a link's password lets a browser in.</summary>
    public static readonly TimeSpan UnlockLifetime = TimeSpan.FromHours(12);

    private readonly ITimeLimitedDataProtector unlocks = protection
        .CreateProtector("Dokument.Links.Unlock")
        .ToTimeLimitedDataProtector();

    /// <summary>The node's working link, for its owner; null when it has none.</summary>
    public async Task<LinkView?> GetAsync(Actor actor, Guid nodeId, CancellationToken ct = default)
    {
        var node = await drive.GetAsync(actor, nodeId, ct, Access.Own);
        var link = await WorkingAsync(node.Id, ct);
        return link is null ? null : await ViewAsync(link, ct);
    }

    /// <summary>Turns the node's link on, or changes the one it has.</summary>
    public async Task<LinkView> SaveAsync(
        Actor actor,
        Guid nodeId,
        LinkSettings settings,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetAsync(actor, nodeId, ct, Access.Own);
        var now = time.GetUtcNow();
        if (settings.ExpiresAt is { } end && end <= now)
        {
            throw new DriveException(DriveError.InvalidRequest, "Pick a day after today.");
        }

        var link = await db.Links.SingleOrDefaultAsync(
            l => l.NodeId == node.Id && l.RevokedAt == null,
            ct
        );
        var created = link is null;
        if (link is null)
        {
            link = new Link
            {
                NodeId = node.Id,
                Token = LinkToken.Create(),
                AllowDownload = settings.AllowDownload,
                CreatedBy = actor.UserId,
                CreatedAt = now,
            };
            db.Links.Add(link);
        }

        var details = new JsonObject();
        if (created || link.AllowDownload != settings.AllowDownload)
        {
            link.AllowDownload = settings.AllowDownload;
            details["allowDownload"] = settings.AllowDownload;
        }

        if (created || link.ExpiresAt != settings.ExpiresAt)
        {
            link.ExpiresAt = settings.ExpiresAt;
            details["expiresAt"] = settings.ExpiresAt?.ToString("O");
        }

        // The password itself is never logged, only that it was set or removed.
        switch (settings.Password)
        {
            case PasswordChange.Set(var password):
                link.PasswordHash = LinkPasswordHasher.Hash(password);
                details["password"] = "set";
                break;
            case PasswordChange.Remove when link.PasswordHash is not null:
                link.PasswordHash = null;
                details["password"] = "removed";
                break;
        }

        if (created || details.Count > 0)
        {
            audit.Add(
                actor,
                created ? AuditActions.LinkCreate : AuditActions.LinkChange,
                node.Id,
                details: details
            );
            await db.SaveChangesAsync(ct);
            await live.NotifyAsync([node.OwnerId], LiveTopics.Shares | LiveTopics.Files, ct);
        }

        return await ViewAsync(link, ct);
    }

    /// <summary>Turns the node's link off. It stops working at once and can't be turned back on; a new one can be made.</summary>
    public async Task RevokeAsync(Actor actor, Guid nodeId, CancellationToken ct = default)
    {
        var node = await drive.GetAsync(actor, nodeId, ct, Access.Own);
        var link =
            await db.Links.SingleOrDefaultAsync(l => l.NodeId == node.Id && l.RevokedAt == null, ct)
            ?? throw DriveException.NotFound("link");
        link.RevokedAt = time.GetUtcNow();
        audit.Add(actor, AuditActions.LinkRevoke, node.Id);
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync([node.OwnerId], LiveTopics.Shares | LiveTopics.Files, ct);
    }

    /// <summary>
    /// The link behind a token, if it still works: not turned off or expired, its node not in the trash, its owner not
    /// disabled. One with a password also needs <paramref name="unlock"/>, the cookie <see cref="Unlock"/> gave.
    /// Anything else is <see cref="DriveError.LinkGone"/>, saying nothing about why.
    /// </summary>
    public async Task<OpenLink> OpenAsync(
        string? tokenText,
        string? unlock,
        CancellationToken ct = default
    )
    {
        var link = await FindAsync(tokenText, ct);
        if (link.Link.PasswordHash is { } hash && !Unlocked(link.Link, hash, unlock))
        {
            throw new DriveException(DriveError.PasswordRequired, "This link needs a password.");
        }

        return link;
    }

    /// <summary>Checks a link's password and returns the cookie that lets this browser in for <see cref="UnlockLifetime"/>.</summary>
    public async Task<string> UnlockAsync(
        string? tokenText,
        string password,
        CancellationToken ct = default
    )
    {
        var link = (await FindAsync(tokenText, ct)).Link;
        if (link.PasswordHash is not { } hash)
        {
            return "";
        }

        if (!LinkPasswordHasher.Verify(password, hash))
        {
            throw new DriveException(DriveError.WrongPassword, "That's not the password.");
        }

        return unlocks.Protect($"{link.Id:N}:{hash.Stamp()}", time.GetUtcNow() + UnlockLifetime);
    }

    /// <summary>Records that someone opened the link (§18). Opening it again is another access.</summary>
    public async Task RecordAccessAsync(
        OpenLink link,
        string? ip,
        string? userAgent,
        CancellationToken ct = default
    )
    {
        audit.AddLink(link.Link.Id, AuditActions.LinkAccess, link.Root.Id, ip, userAgent);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The link's own node, or one inside its folder, not in the trash.</summary>
    public async Task<Node> NodeAsync(OpenLink link, Guid? id, CancellationToken ct = default)
    {
        if (id is null || id == link.Root.Id)
        {
            return link.Root;
        }

        var node = await db
            .Nodes.AsNoTracking()
            .Include(n => n.CurrentVersion)
            .SingleOrDefaultAsync(n => n.Id == id && n.TrashedAt == null, ct);
        return node is not null && node.Id != link.Root.Id && link.Root.Contains(node)
            ? node
            : throw DriveException.NotFound();
    }

    public async Task<List<Node>> ChildrenAsync(
        OpenLink link,
        Node folder,
        CancellationToken ct = default
    ) =>
        folder.IsFolder
            ? await db
                .Nodes.AsNoTracking()
                .Include(n => n.CurrentVersion)
                .Where(n => n.ParentId == folder.Id && n.TrashedAt == null)
                .ToListAsync(ct)
            : throw new DriveException(DriveError.InvalidRequest, "That is a file, not a folder.");

    /// <summary>The folders from the link's own folder down to <paramref name="node"/>'s, never any above the link.</summary>
    public async Task<List<Node>> PathAsync(
        OpenLink link,
        Node node,
        CancellationToken ct = default
    )
    {
        var labels = node.Path.Split('.');
        var start = Array.IndexOf(labels, Node.Label(link.Root.Id));
        var ids = labels[start..^1].Select(Guid.Parse).ToList();
        var folders = await db
            .Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id))
            .ToDictionaryAsync(n => n.Id, ct);
        return ids.Select(i => folders[i]).ToList();
    }

    /// <summary>A file's bytes for showing it. Not audited, like previews for people signed in (§18).</summary>
    public async Task<FileContent> ContentAsync(
        OpenLink link,
        Guid id,
        CancellationToken ct = default
    )
    {
        var node = await NodeAsync(link, id, ct);
        return node.IsFolder
            ? throw new DriveException(DriveError.InvalidRequest, "That is a folder, not a file.")
            : await drive.ContentOfAsync(node, versionId: null, ct);
    }

    /// <summary>The current version's bytes for saving, when the link allows it. The first request of each download is audited.</summary>
    public async Task<FileContent> DownloadAsync(
        OpenLink link,
        Guid id,
        bool audited,
        string? ip,
        string? userAgent,
        CancellationToken ct = default
    )
    {
        if (!link.Link.AllowDownload)
        {
            throw new DriveException(DriveError.Forbidden, "This link doesn't allow downloads.");
        }

        var content = await ContentAsync(link, id, ct);
        if (audited)
        {
            audit.AddLink(
                link.Link.Id,
                AuditActions.FileDownload,
                content.Node.Id,
                ip,
                userAgent,
                content.Version.Id
            );
            await db.SaveChangesAsync(ct);
        }

        return content;
    }

    private async Task<OpenLink> FindAsync(string? tokenText, CancellationToken ct)
    {
        var gone = new DriveException(DriveError.LinkGone, "This link no longer works.");
        if (!LinkToken.TryParse(tokenText, out var token))
        {
            throw gone;
        }

        var link =
            await db.Links.AsNoTracking().SingleOrDefaultAsync(l => l.Token == token, ct)
            ?? throw gone;
        if (!link.WorksAt(time.GetUtcNow()))
        {
            throw gone;
        }

        var root =
            await db
                .Nodes.AsNoTracking()
                .Include(n => n.CurrentVersion)
                .SingleOrDefaultAsync(n => n.Id == link.NodeId && n.TrashedAt == null, ct)
            ?? throw gone;
        var owner = await db.Users.AsNoTracking().SingleAsync(u => u.Id == root.OwnerId, ct);
        return owner.IsDisabled ? throw gone : new OpenLink(link, root, owner);
    }

    private bool Unlocked(Link link, PasswordHash hash, string? cookie)
    {
        if (string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        try
        {
            return unlocks.Unprotect(cookie) == $"{link.Id:N}:{hash.Stamp()}";
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private Task<Link?> WorkingAsync(Guid nodeId, CancellationToken ct) =>
        db
            .Links.AsNoTracking()
            .SingleOrDefaultAsync(l => l.NodeId == nodeId && l.RevokedAt == null, ct);

    private async Task<LinkView> ViewAsync(Link link, CancellationToken ct)
    {
        // Opens of earlier links to the node all come before this one was made.
        var opens = db.AuditEvents.Where(e =>
            e.Action == AuditActions.LinkAccess
            && e.TargetNodeId == link.NodeId
            && e.At >= link.CreatedAt
        );
        var count = await opens.CountAsync(ct);
        var last = count == 0 ? null : await opens.MaxAsync(e => (DateTimeOffset?)e.At, ct);
        return new LinkView(link, count, last, link.WorksAt(time.GetUtcNow()));
    }
}

/// <summary>What the owner asks a link to be.</summary>
public sealed record LinkSettings(
    bool AllowDownload,
    DateTimeOffset? ExpiresAt,
    PasswordChange Password
);

/// <summary>Whether saving a link sets its password, takes it off, or leaves it as it is.</summary>
public abstract record PasswordChange
{
    private PasswordChange() { }

    public sealed record Keep : PasswordChange;

    public sealed record Remove : PasswordChange;

    public sealed record Set(LinkPassword Password) : PasswordChange;
}

/// <summary>A link as its owner sees it.</summary>
public sealed record LinkView(Link Link, int Opens, DateTimeOffset? LastOpenedAt, bool Works);

/// <summary>A working link someone has opened: the link, its own node and the node's owner.</summary>
public sealed record OpenLink(Link Link, Node Root, User Owner);
