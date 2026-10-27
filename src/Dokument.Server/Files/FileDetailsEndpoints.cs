using Dokument.Domain.Search;
using Dokument.Infrastructure.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Dokument.Server.Files;

/// <summary>The Details and Text sections of a file's info, and people's corrections (docs/design.md §13.5).</summary>
public static class FileDetailsEndpoints
{
    public static RouteGroupBuilder MapFileDetailsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Files").RequireAuthorization();
        group
            .MapGet("/nodes/{id:guid}/details", GetDetails)
            .WithName("GetFileDetails")
            .WithDescription(
                "What the file's current version says about itself (title, date taken, camera, place…), with people's edits on top."
            );
        group
            .MapPut("/nodes/{id:guid}/details", SetDetails)
            .WithName("SetFileDetails")
            .WithDescription(
                "Edits the current version's title, description and date taken, all three at once; null keeps what the file says. Later versions start with these edits. The file itself is never changed."
            );
        group
            .MapGet("/nodes/{id:guid}/text", GetText)
            .WithName("GetFileText")
            .WithDescription(
                "The searchable text of the current version, page by page from `from`, with corrections in place."
            );
        group
            .MapPut("/nodes/{id:guid}/text/{pageNo:int}", SetPage)
            .WithName("SetFileTextPage")
            .WithDescription(
                "Corrects one page of the current version's text, or adds text to a page where none was read. Search uses it from then on."
            );
        group
            .MapDelete("/nodes/{id:guid}/text/{pageNo:int}", RevertPage)
            .WithName("RevertFileTextPage")
            .WithDescription(
                "Drops a page's correction, so the text read from the file counts again."
            );
        return api;
    }

    private static async Task<Ok<FileDetailsDto>> GetDetails(
        Guid id,
        HttpContext context,
        FileDetailsService details,
        CancellationToken ct
    ) =>
        TypedResults.Ok(
            FileDetailsDto.From(await details.GetDetailsAsync(context.Actor(), id, ct))
        );

    private static async Task<NoContent> SetDetails(
        Guid id,
        FileDetailsRequest request,
        HttpContext context,
        FileDetailsService details,
        CancellationToken ct
    )
    {
        await details.SetDetailsAsync(
            context.Actor(),
            id,
            new DetailsEdit(request.Title, request.Description, request.Taken),
            ct
        );
        return TypedResults.NoContent();
    }

    private static async Task<Ok<FileTextDto>> GetText(
        Guid id,
        HttpContext context,
        FileDetailsService details,
        CancellationToken ct,
        int from = 1,
        int limit = 20
    ) =>
        TypedResults.Ok(
            FileTextDto.From(await details.GetTextAsync(context.Actor(), id, from, limit, ct))
        );

    private static async Task<NoContent> SetPage(
        Guid id,
        int pageNo,
        FileTextPageRequest request,
        HttpContext context,
        FileDetailsService details,
        CancellationToken ct
    )
    {
        await details.SetPageAsync(context.Actor(), id, pageNo, request.Text, ct);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> RevertPage(
        Guid id,
        int pageNo,
        HttpContext context,
        FileDetailsService details,
        CancellationToken ct
    )
    {
        await details.RevertPageAsync(context.Actor(), id, pageNo, ct);
        return TypedResults.NoContent();
    }
}

/// <summary>A date as people know it: <c>value</c> is "1974", "1974-07", "1974-07-14" or "1974-07-14 15:30".</summary>
/// <param name="OffsetMinutes">The camera's UTC offset, when it recorded one; the time is its clock either way.</param>
public sealed record TakenDto(string Value, DatePrecision Precision, int? OffsetMinutes)
{
    public static TakenDto? From(PartialDate? date, int? offsetMinutes = null) =>
        date is null ? null : new(date.ToString(), date.Precision, offsetMinutes);
}

/// <param name="Value">What to show: the edit, or what the file says.</param>
/// <param name="Read">What the file says.</param>
public sealed record EditableTextDto(string? Value, string? Read, bool Edited);

public sealed record EditableTakenDto(TakenDto? Value, TakenDto? Read, bool Edited);

/// <summary>Where a photo or video was taken, in decimal degrees.</summary>
public sealed record PlaceDto(double Latitude, double Longitude);

/// <summary>A file's details (§13.5): what its current version says about itself, with people's edits on top.</summary>
/// <param name="Reading">The worker hasn't read the details yet.</param>
/// <param name="Fields">Other read-only details, by key: subject, author, artist, composer, album, genre, company, keywords, comment.</param>
/// <param name="Width">Pixels, for images and videos.</param>
/// <param name="EditedBy">Who last edited the title, description or date, when anyone has.</param>
public sealed record FileDetailsDto(
    bool Reading,
    EditableTextDto Title,
    EditableTextDto Description,
    EditableTakenDto Taken,
    string? Camera,
    string? Lens,
    int? Width,
    int? Height,
    double? DurationSeconds,
    int? PageCount,
    PlaceDto? Place,
    List<DetailField> Fields,
    string? EditedBy,
    DateTimeOffset? EditedAt
)
{
    public static FileDetailsDto From(FileDetails details)
    {
        var read = details.Read;
        var edits = details.Edits;
        var metadata = details.Metadata;
        var readTaken = TakenDto.From(read.Taken, read.TakenOffsetMinutes);
        return new(
            details.Reading,
            Text(edits?.Title, read.Title),
            Text(edits?.Description, read.Description),
            new EditableTakenDto(
                edits?.Taken is { } taken ? TakenDto.From(taken) : readTaken,
                readTaken,
                edits?.Taken is not null
            ),
            metadata?.Camera,
            read.Lens,
            details.Width,
            details.Height,
            details.DurationSeconds,
            details.PageCount,
            metadata is { Latitude: { } lat, Longitude: { } lon } ? new PlaceDto(lat, lon) : null,
            [.. read.Fields],
            details.EditedByName,
            edits?.EditedAt
        );
    }

    private static EditableTextDto Text(string? edit, string? read) =>
        new(
            edit switch
            {
                null => read,
                "" => null,
                _ => edit,
            },
            read,
            edit is not null
        );
}

/// <summary>An edit of the title, description and date taken. Null keeps what the file says; an empty title or description hides it.</summary>
/// <param name="Taken">"1974", "1974-07", "1974-07-14" or "1974-07-14 15:30".</param>
public sealed record FileDetailsRequest(string? Title, string? Description, string? Taken);

/// <param name="Source">Where the read page came from: text, ocr, or added when nothing was read there.</param>
/// <param name="Edited">Someone corrected it; <c>DELETE</c> brings back what was read.</param>
public sealed record TextPageDto(
    int PageNo,
    PageSource Source,
    string Text,
    bool Edited,
    string? EditedBy,
    DateTimeOffset? EditedAt
);

/// <param name="State">reading, ready, empty, unsupported or unreadable.</param>
/// <param name="OcrSkipped">Scans or photos in it weren't read because text recognition is off.</param>
/// <param name="TotalPages">Pages with text, read or added.</param>
/// <param name="MaxPage">Text can be added to pages 1 to this.</param>
/// <param name="Words">Words in all pages, corrections included.</param>
/// <param name="NextFrom">Pass as <c>from</c> for the next pages; null at the end.</param>
public sealed record FileTextDto(
    TextState State,
    bool OcrSkipped,
    int? PageCount,
    int TotalPages,
    int MaxPage,
    int Words,
    int EditedPages,
    List<TextPageDto> Pages,
    int? NextFrom
)
{
    public static FileTextDto From(FileText text) =>
        new(
            text.State,
            text.OcrSkipped,
            text.PageCount,
            text.TotalPages,
            text.MaxPage,
            text.Words,
            text.EditedPages,
            text.Pages.Select(p => new TextPageDto(
                    p.PageNo,
                    p.Source,
                    p.Text,
                    p.Edited,
                    p.EditedByName,
                    p.EditedAt
                ))
                .ToList(),
            text.NextFrom
        );
}

public sealed record FileTextPageRequest(string Text);
