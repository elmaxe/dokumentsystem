using Dokument.Application.Files;
using Dokument.Domain.Files;
using Dokument.Domain.Jobs;
using Dokument.Domain.Search;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Server.Files;

/// <summary>Where a file's transcript is (§11.1).</summary>
public enum TranscriptState
{
    /// <summary>No transcript and none coming: not a recording, transcription is off, or no ML service is set.</summary>
    None,
    Waiting,
    Running,
    Ready,

    /// <summary>Under a few seconds of speech: music or silence.</summary>
    NoSpeech,
    Failed,
}

public sealed record TranscriptLineDto(int StartMs, int EndMs, string Text);

/// <param name="Language">The spoken language, <c>sv</c> or <c>en</c>.</param>
/// <param name="DoneMs">While it's running: how far it has got, for "At 12:34 of 38:12".</param>
/// <param name="Lines">What's been heard so far, in order. A running transcript fills in as it goes.</param>
public sealed record TranscriptDto(
    TranscriptState State,
    string? Language,
    string? Model,
    int? DurationMs,
    int? DoneMs,
    List<TranscriptLineDto> Lines
);

/// <summary>Speech to text for video and audio files (docs/design.md §11.1). Like previews, reading it isn't audited.</summary>
public static class TranscriptEndpoints
{
    public static RouteGroupBuilder MapTranscriptEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("").WithTags("Files").RequireAuthorization();
        group
            .MapGet("/nodes/{id:guid}/transcript", GetTranscript)
            .WithName("GetTranscript")
            .WithDescription("What's said in a video or audio file, line by line with times.");
        group
            .MapPost("/nodes/{id:guid}/transcript/redo", RedoTranscript)
            .WithName("RedoTranscript")
            .WithDescription(
                "Transcribes the file again from the start, ahead of the background work, e.g. after the admin picked another model. The old transcript stays until the new run starts. 409 when the file isn't transcribed (no sound, speech to text off, or no ML service)."
            );
        group
            .MapGet("/nodes/{id:guid}/captions.vtt", GetCaptions)
            .WithName("GetCaptions")
            .WithDescription(
                "The transcript as WebVTT captions, for the player's CC button. 404 until there is one."
            );
        return api;
    }

    private static async Task<Ok<TranscriptDto>> GetTranscript(
        Guid id,
        HttpContext context,
        DriveService drive,
        TranscriptService transcripts,
        CancellationToken ct
    )
    {
        var file = await drive.GetFileAsync(context.Actor(), id, ct);
        if (file.CurrentVersion is not { } version)
        {
            return TypedResults.Ok(
                new TranscriptDto(TranscriptState.None, null, null, null, null, [])
            );
        }

        return TypedResults.Ok(await DtoAsync(transcripts, version.BlobHash, ct));
    }

    private static async Task<Results<Ok<TranscriptDto>, ProblemHttpResult>> RedoTranscript(
        Guid id,
        HttpContext context,
        DriveService drive,
        TranscriptService transcripts,
        DokumentDbContext db,
        CancellationToken ct
    )
    {
        var file = await drive.GetFileAsync(context.Actor(), id, ct, Access.Edit);
        if (
            file.CurrentVersion is not { } version
            || await db.BlobMedia.SingleOrDefaultAsync(m => m.BlobHash == version.BlobHash, ct)
                is not { } media
            || !await transcripts.RedoAsync(media, JobPriority.Interactive, ct)
        )
        {
            return TypedResults.Problem(
                "This file isn't transcribed.",
                statusCode: StatusCodes.Status409Conflict
            );
        }

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(await DtoAsync(transcripts, version.BlobHash, ct));
    }

    private static async Task<TranscriptDto> DtoAsync(
        TranscriptService transcripts,
        ContentHash hash,
        CancellationToken ct
    )
    {
        var transcript = await transcripts.ReadAsync(hash, ct);
        var job = await transcripts.JobStatusAsync(hash, ct);
        var state = transcript?.Info.Status switch
        {
            TranscriptStatus.Ready => TranscriptState.Ready,
            TranscriptStatus.NoSpeech => TranscriptState.NoSpeech,
            TranscriptStatus.Failed => TranscriptState.Failed,
            _ when job == JobStatus.Running => TranscriptState.Running,
            _ when job == JobStatus.Queued => TranscriptState.Waiting,
            _ => TranscriptState.None,
        };
        return new TranscriptDto(
            state,
            transcript?.Info.Language,
            transcript?.Info.Model,
            transcript?.Info.DurationMs,
            state == TranscriptState.Running ? transcript?.Info.DoneMs : null,
            transcript
                ?.Lines.Select(l => new TranscriptLineDto(l.StartMs, l.EndMs, l.Text))
                .ToList()
                ?? []
        );
    }

    private static async Task<Results<ContentHttpResult, NotFound>> GetCaptions(
        Guid id,
        HttpContext context,
        DriveService drive,
        TranscriptService transcripts,
        CancellationToken ct
    )
    {
        var file = await drive.GetFileAsync(context.Actor(), id, ct);
        if (
            file.CurrentVersion is not { } version
            || await transcripts.ReadAsync(version.BlobHash, ct)
                is not { Lines.Count: > 0 } transcript
        )
        {
            return TypedResults.NotFound();
        }

        context.Response.Headers.XContentTypeOptions = "nosniff";
        return TypedResults.Text(WebVtt.From(transcript.Lines), "text/vtt; charset=utf-8");
    }
}
