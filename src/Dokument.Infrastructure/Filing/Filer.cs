using System.Text.Json.Nodes;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Filing;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Infrastructure.Filing;

/// <summary>
/// Runs a person's filing rules on one of their files (docs/design.md §13.6). The first enabled rule whose tag the file
/// got while the rule was on moves it to the rule's folder, made if missing, with the file's year and month. A file is
/// filed once: after that it stays wherever its owner puts it. The move is audited as the system's, naming the rule.
/// </summary>
public sealed partial class Filer(
    DokumentDbContext db,
    DriveService drive,
    AuditLog audit,
    LiveChanges live,
    TimeProvider time,
    ILogger<Filer> logger
)
{
    /// <summary>
    /// Files the file if one of its owner's rules matches. <paramref name="anyTimeRuleId"/> is a rule that also matches
    /// when the file had its tag before the rule was turned on, for "Also file the files that have this tag now".
    /// Returns whether it was filed.
    /// </summary>
    public async Task<bool> FileAsync(
        Guid nodeId,
        Guid? anyTimeRuleId = null,
        CancellationToken ct = default
    )
    {
        var node = await db
            .Nodes.Include(n => n.CurrentVersion)
            .SingleOrDefaultAsync(
                n => n.Id == nodeId && n.Kind == NodeKind.File && n.TrashedAt == null,
                ct
            );
        if (
            node?.CurrentVersion is not { } version
            || await db.NodeFilings.AnyAsync(f => f.NodeId == nodeId, ct)
        )
        {
            return false;
        }

        var rules = await db
            .FilingRules.Where(r => r.OwnerId == node.OwnerId && r.Enabled)
            .OrderBy(r => r.Position)
            .ToListAsync(ct);
        if (rules.Count == 0)
        {
            return false;
        }

        var tags = await db
            .EffectiveTags.Where(t => t.NodeId == nodeId)
            .ToDictionaryAsync(t => t.TagKey, t => t.Since, ct);
        var rule = rules.FirstOrDefault(r =>
            tags.TryGetValue(r.TagKey, out var since)
            && (since >= r.ActiveSince || r.Id == anyTimeRuleId)
        );
        if (rule is null)
        {
            return false;
        }

        var names = rule.Folder.Resolve(await DateOfAsync(version, ct));
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var folder = await EnsureFoldersAsync(node.OwnerId, names, rule, ct);
        if (folder is null)
        {
            LogBlocked(logger, node.Id, rule.Folder.Value);
            return false;
        }

        var fromId = node.ParentId;
        if (fromId != folder.Id)
        {
            var details = new JsonObject
            {
                ["from"] = fromId?.ToString(),
                ["to"] = folder.Id.ToString(),
                ["rule"] = rule.Id.ToString(),
                ["tag"] = rule.Tag.Value,
            };

            // A name already taken there gets a number, as an upload would: "kvitto (1).pdf".
            if (await drive.FindByNameAsync(node.OwnerId, folder.Id, node.Name, ct) is not null)
            {
                var name = await drive.FreeNameAsync(node.OwnerId, folder.Id, node.Name, ct);
                details["name"] = name.Value;
                node.Name = name;
            }

            node.PlaceUnder(folder);
            audit.AddSystem(AuditActions.NodeMove, node.Id, details: details);
        }

        db.NodeFilings.Add(
            new NodeFiling
            {
                NodeId = node.Id,
                RuleId = rule.Id,
                Tag = rule.Tag.Value,
                FromFolderId = fromId,
                ToFolderId = folder.Id,
                FiledAt = time.GetUtcNow(),
            }
        );
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files | LiveTopics.Filings, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// The file's date for <c>{year}</c> and <c>{month}</c>: the date taken people gave it, else when the photo was taken
    /// (on the server's clock), else when the file last changed on the device it came from, else when it was uploaded.
    /// </summary>
    internal async Task<DateTimeOffset> DateOfAsync(FileVersion version, CancellationToken ct)
    {
        // A date people gave the file is a wall-clock date already (§13.5), so "1974" stays in 1974.
        var edited = await db
            .VersionDetails.Where(d => d.VersionId == version.Id)
            .Select(d => d.TakenAt)
            .FirstOrDefaultAsync(ct);
        if (edited is { } wall)
        {
            return new DateTimeOffset(wall.DateTime, TimeSpan.Zero);
        }

        var taken = await db
            .BlobMetadata.Where(m => m.BlobHash == version.BlobHash)
            .Select(m => m.TakenAt)
            .FirstOrDefaultAsync(ct);
        var at = taken ?? version.ClientModifiedAt ?? version.UploadedAt;
        return TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
    }

    /// <summary>The folder at these names from the top of My files, making the ones missing. Null when a file has one of the names.</summary>
    private async Task<Node?> EnsureFoldersAsync(
        Guid ownerId,
        IReadOnlyList<NodeName> names,
        FilingRule rule,
        CancellationToken ct
    )
    {
        Node? parent = null;
        var made = false;
        var now = time.GetUtcNow();
        foreach (var name in names)
        {
            var existing = made ? null : await drive.FindByNameAsync(ownerId, parent?.Id, name, ct);
            if (existing is { IsFolder: false })
            {
                return null;
            }

            if (existing is null)
            {
                var folder = new Node
                {
                    OwnerId = ownerId,
                    Kind = NodeKind.Folder,
                    Name = name,
                    CreatedAt = now,
                    ModifiedAt = now,
                };
                folder.PlaceUnder(parent);
                db.Nodes.Add(folder);
                audit.AddSystem(
                    AuditActions.FolderCreate,
                    folder.Id,
                    details: new JsonObject { ["rule"] = rule.Id.ToString() }
                );
                existing = folder;
                made = true;
            }

            parent = existing;
        }

        return parent;
    }

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Not filing {NodeId}: a file has the name of a folder in \"{Folder}\""
    )]
    private static partial void LogBlocked(ILogger logger, Guid nodeId, string folder);
}
