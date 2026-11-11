using System.Text.Json;
using System.Text.Json.Serialization;
using Dokument.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Dokument.Infrastructure.Live;

/// <summary>
/// What changed, as broad topics: each tells a client which of its data to fetch again (docs/design.md §15.4). They
/// carry no ids or names, so a notice says nothing a client couldn't learn by asking.
/// </summary>
[Flags]
public enum LiveTopics
{
    None = 0,

    /// <summary>Files and folders: listings, a file's details and versions, Starred and Recent.</summary>
    Files = 1,

    /// <summary>The person's Recent page only, such as a file opened or downloaded.</summary>
    Recent = 2,

    Trash = 4,

    /// <summary>The storage meter: used, trashed and quota.</summary>
    Storage = 8,

    /// <summary>The person's own settings, such as language or showing seconds, and their profile picture.</summary>
    Account = 16,

    /// <summary>A filing rule moved some of the person's files, so clients can offer to undo it (§13.6).</summary>
    Filings = 32,

    /// <summary>Something was shared with the person or stopped being, or they changed who has access (§9).</summary>
    Shares = 64,

    All = Files | Recent | Trash | Storage | Account | Filings | Shares,
}

/// <summary>Why a session's clients should go to the sign-in page.</summary>
public enum SessionEndReason
{
    /// <summary>Signed out from that browser, perhaps in another tab.</summary>
    SignedOut,

    /// <summary>Ended from another device or by an admin.</summary>
    Revoked,
}

/// <summary>
/// Tells people's open clients that something changed (docs/design.md §15.4). The notice is a Postgres
/// <c>NOTIFY</c> on the request's own connection: inside a transaction it goes out when the transaction commits, and
/// not at all if it rolls back. The server role listens and passes it on over SignalR, so the worker can send them
/// too. Call it after the change is saved.
/// </summary>
public sealed class LiveChanges(DokumentDbContext db)
{
    public const string Channel = "dokument_live";

    /// <summary>The client that made the request, so it can skip a notice about its own change.</summary>
    public string? Origin { get; set; }

    public Task NotifyAsync(Guid userId, LiveTopics topics, CancellationToken ct = default) =>
        NotifyAsync([userId], topics, ct);

    /// <summary>
    /// Notifies the people named. A <see cref="LiveTopics.Files"/> change to someone's files also reaches everyone they
    /// share anything with (§15.4): notices carry no ids, so a client refetches what it shows and sees only what it may.
    /// </summary>
    public async Task NotifyAsync(
        IEnumerable<Guid> userIds,
        LiveTopics topics,
        CancellationToken ct = default
    )
    {
        var users = userIds.Distinct().ToArray();
        if (users.Length == 0 || topics == LiveTopics.None)
        {
            return;
        }

        await SendAsync(
            new LiveNotice
            {
                Users = users,
                Topics = topics,
                Origin = Origin,
            },
            ct
        );
        if (topics.HasFlag(LiveTopics.Files))
        {
            var sharedWith = await db
                .Database.SqlQuery<Guid>(
                    $"""
                    SELECT DISTINCT s.grantee_user_id AS "Value" FROM shares s
                    JOIN nodes n ON n.id = s.node_id
                    WHERE n.owner_id = ANY({users})
                    """
                )
                .ToListAsync(ct);
            var others = sharedWith.Except(users).ToArray();
            if (others.Length > 0)
            {
                await SendAsync(
                    new LiveNotice
                    {
                        Users = others,
                        Topics = LiveTopics.Files,
                        Origin = Origin,
                    },
                    ct
                );
            }
        }
    }

    /// <summary>For changes that touch everyone, such as the default quota.</summary>
    public Task NotifyEveryoneAsync(LiveTopics topics, CancellationToken ct = default) =>
        SendAsync(new LiveNotice { Topics = topics, Origin = Origin }, ct);

    /// <summary>Sends the session's open clients to the sign-in page.</summary>
    public Task SessionEndedAsync(
        Guid sessionId,
        SessionEndReason reason,
        CancellationToken ct = default
    ) => SendAsync(new LiveNotice { Session = sessionId, Reason = reason }, ct);

    private async Task SendAsync(LiveNotice notice, CancellationToken ct) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT pg_notify({Channel}, {JsonSerializer.Serialize(notice, LiveNotice.Json)})",
            ct
        );
}

/// <summary>
/// The payload of one <c>NOTIFY</c>. Either <see cref="Topics"/> for <see cref="Users"/> (everyone when null), or a
/// <see cref="Session"/> that ended. Small by design: Postgres caps a payload at 8000 bytes.
/// </summary>
public sealed record LiveNotice
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public Guid[]? Users { get; init; }
    public LiveTopics Topics { get; init; }
    public string? Origin { get; init; }
    public Guid? Session { get; init; }
    public SessionEndReason? Reason { get; init; }

    /// <summary>The topics as the names clients see: <c>files</c>, <c>recent</c> and so on.</summary>
    public string[] TopicNames() =>
        Enum.GetValues<LiveTopics>()
            .Where(t => t is not LiveTopics.None and not LiveTopics.All && Topics.HasFlag(t))
            .Select(t => JsonNamingPolicy.CamelCase.ConvertName(t.ToString()))
            .ToArray();
}
