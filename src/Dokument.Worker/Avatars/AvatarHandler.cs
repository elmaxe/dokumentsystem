using System.Security.Cryptography;
using Dokument.Domain.Identity;
using Dokument.Domain.Jobs;
using Dokument.Domain.Previews;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Worker.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Dokument.Worker.Avatars;

/// <summary>
/// Keeps one person's avatar in step with their picture claim (docs/design.md §5.1), queued at every sign-in. A new
/// picture is fetched, converted and written to <c>avatars/{id}.webp</c> on the derived volume; a claim that went away
/// removes ours. A picture that can't be fetched or read keeps the one we have and doesn't retry: the next sign-in
/// tries again.
/// </summary>
internal sealed partial class AvatarHandler(
    DokumentDbContext db,
    DerivedStore derived,
    AvatarFetcher fetcher,
    LiveChanges live,
    ILogger<AvatarHandler> logger
) : IJobHandler
{
    public async Task RunAsync(Job job, CancellationToken ct)
    {
        if (!Guid.TryParse(job.Subject, out var id))
        {
            throw new InvalidOperationException($"\"{job.Subject}\" isn't a person's id.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
        {
            return;
        }

        if (user.PictureUrl is not { } url)
        {
            if (user.Avatar is not null)
            {
                derived.Delete(DerivedKey.ForAvatar(user.Id));
                user.Avatar = null;
                await db.SaveChangesAsync(ct);
                await live.NotifyAsync(user.Id, LiveTopics.Account, ct);
                LogRemoved(logger, user.Id);
            }

            return;
        }

        var current = user.Avatar;
        var etag = current?.SourceUrl == url ? current.ETag : null;
        switch (await fetcher.FetchAsync(url, etag, ct))
        {
            case FetchedPicture.Unchanged:
                return;
            case FetchedPicture.Failed failed:
                LogFetchFailed(logger, user.Id, Shorten(url), failed.Reason);
                return;
            case FetchedPicture.Bytes fetched:
                if (AvatarImage.Make(fetched.Content) is not { } webp)
                {
                    LogUnreadable(logger, user.Id, Shorten(url));
                    return;
                }

                var version = Convert.ToHexStringLower(SHA256.HashData(webp))[..12];
                var changed = current?.Version != version;
                if (changed)
                {
                    await derived.WriteAsync(DerivedKey.ForAvatar(user.Id), webp, ct);
                }

                user.Avatar = new Avatar(version, url, fetched.ETag);
                await db.SaveChangesAsync(ct);
                if (changed)
                {
                    await live.NotifyAsync(user.Id, LiveTopics.Account, ct);
                    LogStored(logger, user.Id, webp.Length);
                }

                return;
        }
    }

    /// <summary>A <c>data:</c> URL can be 64 KB; logs get its start.</summary>
    private static string Shorten(string url) => url.Length > 120 ? url[..120] + "…" : url;

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Stored the picture of {UserId} ({Bytes} bytes)"
    )]
    private static partial void LogStored(ILogger logger, Guid userId, int bytes);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Removed the picture of {UserId}: the sign-in provider sends none"
    )]
    private static partial void LogRemoved(ILogger logger, Guid userId);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Couldn't fetch the picture of {UserId} from {Url}: {Reason}"
    )]
    private static partial void LogFetchFailed(
        ILogger logger,
        Guid userId,
        string url,
        string reason
    );

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The picture of {UserId} from {Url} isn't an image we read"
    )]
    private static partial void LogUnreadable(ILogger logger, Guid userId, string url);
}
