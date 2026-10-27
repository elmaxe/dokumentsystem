using System.Text.Json.Nodes;
using Dokument.Application.Files;
using Dokument.Domain.Audit;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Search;

/// <summary>Where the text of a file's current version stands, for the details panel.</summary>
public enum TextState
{
    /// <summary>The worker hasn't read it yet.</summary>
    Reading,

    /// <summary>Text was found, or people added some.</summary>
    Ready,

    /// <summary>A format with text, but none was found, such as a photo of a beach.</summary>
    Empty,

    /// <summary>Not a format with text, such as a zip.</summary>
    Unsupported,

    /// <summary>The file looked readable but wasn't, such as a password-protected PDF.</summary>
    Unreadable,
}

/// <summary>What was read from a file version's bytes, with the edits on top (§13.5).</summary>
/// <param name="Reading">Whether the metadata step is still to run.</param>
/// <param name="Width">Pixels of an image or video.</param>
public sealed record FileDetails(
    Node Node,
    FileVersion Version,
    bool Reading,
    BlobMetadata? Metadata,
    ReadDetails Read,
    int? PageCount,
    int? Width,
    int? Height,
    VersionDetails? Edits,
    string? EditedByName
);

/// <param name="Text">The page's text: the correction where there is one, otherwise what was read.</param>
/// <param name="Source">Where the read page came from; <see cref="PageSource.Added"/> when nothing was read there.</param>
public sealed record TextPage(
    int PageNo,
    PageSource Source,
    string Text,
    bool Edited,
    string? EditedByName,
    DateTimeOffset? EditedAt
);

/// <param name="OcrSkipped">Some pages or images weren't read because text recognition is off.</param>
/// <param name="PageCount">Pages in the file, when it has pages.</param>
/// <param name="TotalPages">Pages with text, read or added.</param>
/// <param name="MaxPage">The highest page number text can be added to.</param>
/// <param name="NextFrom">The page to ask for next, when there are more.</param>
public sealed record FileText(
    TextState State,
    bool OcrSkipped,
    int? PageCount,
    int TotalPages,
    int MaxPage,
    int Words,
    int EditedPages,
    IReadOnlyList<TextPage> Pages,
    int? NextFrom
);

/// <summary>An edit of a version's details. Null means "as the file says".</summary>
public sealed record DetailsEdit(string? Title, string? Description, string? Taken);

/// <summary>
/// The Details and Text sections of the details panel (docs/design.md §13.5): what the worker read from a file's bytes,
/// and people's corrections, which belong to the file version because what was read is shared by everyone with the
/// same bytes. Search uses the corrections. The original is never changed.
/// </summary>
public sealed class FileDetailsService(
    DokumentDbContext db,
    DriveService drive,
    AuditLog audit,
    LiveChanges live,
    TimeProvider time
)
{
    /// <summary>The longest page of text someone can save, the same cap as the text stage's (§10.3).</summary>
    public const int MaxPageLength = 200_000;

    public async Task<FileDetails> GetDetailsAsync(
        Actor actor,
        Guid id,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        var hash = version.BlobHash;
        var metadata = await db
            .BlobMetadata.AsNoTracking()
            .SingleOrDefaultAsync(m => m.BlobHash == hash, ct);
        var reading =
            metadata is null
            && await db.Jobs.AnyAsync(
                j =>
                    j.Type == JobType.Metadata
                    && j.Subject == hash.Hex
                    && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running),
                ct
            );
        var preview = await db
            .BlobPreviews.AsNoTracking()
            .SingleOrDefaultAsync(p => p.BlobHash == hash && p.Status == PreviewStatus.Ready, ct);
        var pageCount = await db
            .BlobContents.Where(c => c.BlobHash == hash)
            .Select(c => c.PageCount)
            .SingleOrDefaultAsync(ct);
        var edits = await db
            .VersionDetails.AsNoTracking()
            .SingleOrDefaultAsync(d => d.VersionId == version.Id, ct);
        var editor = edits is null
            ? null
            : await db
                .Users.Where(u => u.Id == edits.EditedBy)
                .Select(u => u.DisplayName)
                .SingleOrDefaultAsync(ct);

        var read = ReadDetails.From(metadata);

        // A PDF's thumbnail size is its first page's; only images have one worth showing.
        var image = preview?.PageCount is null ? preview : null;
        return new FileDetails(
            node,
            version,
            reading,
            metadata,
            read,
            pageCount ?? preview?.PageCount,
            read.Width ?? image?.Width,
            read.Height ?? image?.Height,
            edits,
            editor
        );
    }

    /// <summary>
    /// Saves the edits of the current version's title, description and date taken, all three at once. A value the same
    /// as what the file says counts as no edit; an empty title or description hides what the file says.
    /// </summary>
    public async Task SetDetailsAsync(
        Actor actor,
        Guid id,
        DetailsEdit edit,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        var title = Normalize(edit.Title, VersionDetails.MaxTitleLength, "title");
        var description = Normalize(
            edit.Description,
            VersionDetails.MaxDescriptionLength,
            "description"
        );
        PartialDate? taken = null;
        if (!string.IsNullOrWhiteSpace(edit.Taken) && !PartialDate.TryParse(edit.Taken, out taken))
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                "The date taken must be a year, a month (1974-07), a day (1974-07-14) or a date and time (1974-07-14 15:30)."
            );
        }

        var read = ReadDetails.From(
            await db
                .BlobMetadata.AsNoTracking()
                .SingleOrDefaultAsync(m => m.BlobHash == version.BlobHash, ct)
        );
        title = title == (read.Title ?? "") ? null : title;
        description = description == (read.Description ?? "") ? null : description;
        taken = taken == read.Taken ? null : taken;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var details = await db.VersionDetails.SingleOrDefaultAsync(
            d => d.VersionId == version.Id,
            ct
        );
        var changed = new JsonArray();
        if (details?.Title != title)
        {
            changed.Add("title");
        }

        if (details?.Description != description)
        {
            changed.Add("description");
        }

        if (details?.Taken != taken)
        {
            changed.Add("taken");
        }

        if (changed.Count == 0)
        {
            return;
        }

        if (details is null)
        {
            details = new VersionDetails { VersionId = version.Id, EditedBy = actor.UserId };
            db.VersionDetails.Add(details);
        }

        details.Title = title;
        details.Description = description;
        details.Taken = taken;
        details.EditedBy = actor.UserId;
        details.EditedAt = time.GetUtcNow();
        if (details.IsEmpty)
        {
            db.VersionDetails.Remove(details);
        }

        await SetSearchPageAsync(version, details.SearchText, ct);
        audit.Add(
            actor,
            AuditActions.FileDetailsEdit,
            node.Id,
            version.Id,
            new JsonObject { ["fields"] = changed }
        );
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>The current version's text, page by page from <paramref name="from"/>, with corrections in place.</summary>
    public async Task<FileText> GetTextAsync(
        Actor actor,
        Guid id,
        int from,
        int limit,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        var hash = version.BlobHash;
        var content = await db
            .BlobContents.AsNoTracking()
            .SingleOrDefaultAsync(c => c.BlobHash == hash, ct);
        var pages = Pages(version);
        var numbers = await pages.Select(p => p.PageNo).OrderBy(n => n).ToListAsync(ct);
        var slice = numbers.Where(n => n >= from).Take(Math.Clamp(limit, 1, 100)).ToList();
        var rows = await pages
            .Where(p => slice.Contains(p.PageNo))
            .OrderBy(p => p.PageNo)
            .Select(p => new
            {
                p.PageNo,
                p.Source,
                p.Text,
                p.VersionId,
                p.EditedAt,
                Editor = db
                    .Users.Where(u => u.Id == p.EditedBy)
                    .Select(u => u.DisplayName)
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var words = await db
            .Database.SqlQuery<int>(
                $"""
                SELECT COALESCE(sum(array_length(regexp_split_to_array(btrim(p.text), '\s+'), 1))
                                FILTER (WHERE btrim(p.text) <> ''), 0)::int AS "Value"
                FROM content_pages p
                WHERE p.blob_hash = {hash.ToArray()} AND p.page_no > 0
                  AND (p.version_id = {version.Id}
                       OR (p.version_id IS NULL AND NOT EXISTS (
                           SELECT 1 FROM content_pages e WHERE e.version_id = {version.Id} AND e.page_no = p.page_no)))
                """
            )
            .SingleAsync(ct);
        var edited = await db.ContentPages.CountAsync(
            p => p.VersionId == version.Id && p.PageNo > 0,
            ct
        );

        var failed =
            content is null
            && await db.Jobs.AnyAsync(
                j =>
                    j.Type == JobType.Text && j.Subject == hash.Hex && j.Status == JobStatus.Failed,
                ct
            );
        var state = (content?.Status, numbers.Count > 0) switch
        {
            (_, true) => TextState.Ready,
            (null, _) => failed ? TextState.Unreadable : TextState.Reading,
            (ContentStatus.Unsupported, _) => TextState.Unsupported,
            (ContentStatus.Failed, _) => TextState.Unreadable,
            _ => TextState.Empty,
        };
        var last = slice.Count > 0 ? slice[^1] : 0;
        var next = numbers.FirstOrDefault(n => n > last);
        return new FileText(
            state,
            content?.OcrSkipped ?? false,
            content?.PageCount,
            numbers.Count,
            MaxPage(content, numbers),
            words,
            edited,
            rows.Select(r => new TextPage(
                    r.PageNo,
                    r.Source,
                    r.Text,
                    r.VersionId is not null,
                    r.Editor,
                    r.EditedAt
                ))
                .ToList(),
            next > 0 ? next : null
        );
    }

    /// <summary>
    /// Saves a correction of one page of the current version, or text for a page where nothing was read. Text the same
    /// as what was read removes the correction.
    /// </summary>
    public async Task SetPageAsync(
        Actor actor,
        Guid id,
        int pageNo,
        string text,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        text = text.Replace("\r\n", "\n").Replace('\0', ' ');
        if (text.Length > MaxPageLength)
        {
            throw new DriveException(
                DriveError.InvalidRequest,
                $"A page can hold at most {MaxPageLength:N0} characters."
            );
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var read = await db.ContentPages.SingleOrDefaultAsync(
            p => p.BlobHash == version.BlobHash && p.VersionId == null && p.PageNo == pageNo,
            ct
        );
        if (read is null)
        {
            var content = await db
                .BlobContents.AsNoTracking()
                .SingleOrDefaultAsync(c => c.BlobHash == version.BlobHash, ct);
            var numbers = await Pages(version).Select(p => p.PageNo).ToListAsync(ct);
            if (pageNo < 1 || pageNo > MaxPage(content, numbers))
            {
                throw DriveException.NotFound("page");
            }
        }

        var correction = await db.ContentPages.SingleOrDefaultAsync(
            p => p.VersionId == version.Id && p.PageNo == pageNo,
            ct
        );
        var same = read is not null ? read.Text == text : text.Trim().Length == 0;
        if (same)
        {
            if (correction is null)
            {
                return;
            }

            db.ContentPages.Remove(correction);
        }
        else if (correction is null)
        {
            db.ContentPages.Add(
                new ContentPage
                {
                    BlobHash = version.BlobHash,
                    VersionId = version.Id,
                    PageNo = pageNo,
                    Source = read?.Source ?? PageSource.Added,
                    Text = text,
                    EditedBy = actor.UserId,
                    EditedAt = time.GetUtcNow(),
                }
            );
        }
        else
        {
            correction.Text = text;
            correction.EditedBy = actor.UserId;
            correction.EditedAt = time.GetUtcNow();
        }

        audit.Add(
            actor,
            AuditActions.FileTextEdit,
            node.Id,
            version.Id,
            new JsonObject { ["page"] = pageNo, ["reverted"] = same }
        );
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Drops a page's correction, so what was read counts again.</summary>
    public async Task RevertPageAsync(
        Actor actor,
        Guid id,
        int pageNo,
        CancellationToken ct = default
    )
    {
        var node = await drive.GetFileAsync(actor, id, ct);
        var version = node.CurrentVersion!;
        var correction = await db.ContentPages.SingleOrDefaultAsync(
            p => p.VersionId == version.Id && p.PageNo == pageNo && p.PageNo > 0,
            ct
        );
        if (correction is null)
        {
            return;
        }

        db.ContentPages.Remove(correction);
        audit.Add(
            actor,
            AuditActions.FileTextEdit,
            node.Id,
            version.Id,
            new JsonObject { ["page"] = pageNo, ["reverted"] = true }
        );
        await db.SaveChangesAsync(ct);
        await live.NotifyAsync(node.OwnerId, LiveTopics.Files, ct);
    }

    /// <summary>
    /// Gives a new version the edits of an earlier one (§13.5): the details always, and the text corrections only when
    /// <paramref name="withText"/>, because they describe those bytes. Restoring a version passes true, as its bytes are
    /// the same. Both versions must be saved; the caller saves.
    /// </summary>
    internal static async Task CarryEditsAsync(
        DokumentDbContext db,
        Guid fromVersionId,
        FileVersion to,
        bool withText,
        CancellationToken ct
    )
    {
        var details = await db
            .VersionDetails.AsNoTracking()
            .SingleOrDefaultAsync(d => d.VersionId == fromVersionId, ct);
        if (details is not null)
        {
            db.VersionDetails.Add(
                new VersionDetails
                {
                    VersionId = to.Id,
                    Title = details.Title,
                    Description = details.Description,
                    Taken = details.Taken,
                    EditedBy = details.EditedBy,
                    EditedAt = details.EditedAt,
                }
            );
        }

        var pages = await db
            .ContentPages.AsNoTracking()
            .Where(p =>
                p.VersionId == fromVersionId && (withText || p.PageNo == ContentPage.MetadataPage)
            )
            .ToListAsync(ct);
        db.ContentPages.AddRange(
            pages.Select(p => new ContentPage
            {
                BlobHash = to.BlobHash,
                VersionId = to.Id,
                PageNo = p.PageNo,
                Source = p.Source,
                Text = p.Text,
                EditedBy = p.EditedBy,
                EditedAt = p.EditedAt,
            })
        );
    }

    /// <summary>A camera's clock: the stored UTC time at the offset it recorded, or as it was if it recorded none.</summary>
    public static DateTime Local(DateTimeOffset takenAt, int? offsetMinutes) =>
        offsetMinutes is { } minutes
            ? takenAt.ToOffset(TimeSpan.FromMinutes(minutes)).DateTime
            : takenAt.UtcDateTime;

    /// <summary>The pages of a version as search sees them: corrections, and read pages without one. Page 0 is left out.</summary>
    private IQueryable<ContentPage> Pages(FileVersion version) =>
        db.ContentPages.Where(p =>
            p.BlobHash == version.BlobHash
            && p.PageNo > 0
            && (
                p.VersionId == version.Id
                || (
                    p.VersionId == null
                    && !db.ContentPages.Any(e => e.VersionId == version.Id && e.PageNo == p.PageNo)
                )
            )
        );

    private static int MaxPage(BlobContent? content, IReadOnlyCollection<int> numbers) =>
        Math.Max(Math.Max(content?.PageCount ?? 1, 1), numbers.Count > 0 ? numbers.Max() : 0);

    /// <summary>Page 0 of the version: the edited title and description, which search adds to what was read.</summary>
    private async Task SetSearchPageAsync(FileVersion version, string text, CancellationToken ct)
    {
        var page = await db.ContentPages.SingleOrDefaultAsync(
            p => p.VersionId == version.Id && p.PageNo == ContentPage.MetadataPage,
            ct
        );
        if (text.Length == 0)
        {
            if (page is not null)
            {
                db.ContentPages.Remove(page);
            }
        }
        else if (page is null)
        {
            db.ContentPages.Add(
                new ContentPage
                {
                    BlobHash = version.BlobHash,
                    VersionId = version.Id,
                    PageNo = ContentPage.MetadataPage,
                    Source = PageSource.Metadata,
                    Text = text,
                }
            );
        }
        else
        {
            page.Text = text;
        }
    }

    private static string? Normalize(string? value, int max, string what)
    {
        if (value is null)
        {
            return null;
        }

        value = value.Replace("\r\n", "\n").Replace('\0', ' ').Trim();
        return value.Length <= max
            ? value
            : throw new DriveException(
                DriveError.InvalidRequest,
                $"The {what} can be at most {max:N0} characters."
            );
    }
}
