using System.Collections.Concurrent;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Telemetry;
using Dokument.Server.Auth;
using Microsoft.AspNetCore.SignalR;

namespace Dokument.Server.Live;

/// <summary>
/// Live updates (docs/design.md §15.4): the server tells each person's open clients what changed, and they fetch it
/// again through the API. It only talks to clients; they have nothing to call.
/// </summary>
public sealed class LiveHub(LiveConnections connections) : Hub
{
    public const string Path = "/api/v1/live";

    /// <summary>Sent with mutations so the client that made a change can skip the notice about it.</summary>
    public const string ClientHeader = "X-Dokument-Client";

    /// <summary>"changed", with a <see cref="LiveChangedMessage"/>.</summary>
    public const string ChangedMethod = "changed";

    /// <summary>"signedOut", with a <see cref="LiveSignedOutMessage"/>.</summary>
    public const string SignedOutMethod = "signedOut";

    public static string UserGroup(Guid userId) => $"user:{userId}";

    public static string SessionGroup(Guid sessionId) => $"session:{sessionId}";

    /// <summary>A client id is the client's own random string. Anything else is ignored.</summary>
    public static bool IsClientId(string? value) =>
        value is { Length: > 0 and <= 64 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    public override async Task OnConnectedAsync()
    {
        var principal = Context.User!;
        var sessionId = principal.SessionId();
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroup(principal.UserId()));
        await Groups.AddToGroupAsync(Context.ConnectionId, SessionGroup(sessionId));
        connections.Add(Context, sessionId);
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>What changed. <c>Origin</c> is the client id of the tab that made the change, if it sent one.</summary>
public sealed record LiveChangedMessage(string[] Topics, string? Origin);

public sealed record LiveSignedOutMessage(SessionEndReason Reason);

/// <summary>
/// The open connections and their sessions. A connection is authenticated once, when it opens, so when its session
/// ends the server closes it rather than leave it listening.
/// </summary>
public sealed class LiveConnections
{
    /// <summary>Long enough for the "signedOut" message to reach the client before the connection closes.</summary>
    private static readonly TimeSpan CloseDelay = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, (Guid SessionId, HubCallerContext Context)> open =
        new();

    public int Count => open.Count;

    public void Add(HubCallerContext context, Guid sessionId)
    {
        if (open.TryAdd(context.ConnectionId, (sessionId, context)))
        {
            DokumentTelemetry.LiveConnections.Add(1);
        }
    }

    public void Remove(string connectionId)
    {
        if (open.TryRemove(connectionId, out _))
        {
            DokumentTelemetry.LiveConnections.Add(-1);
        }
    }

    /// <summary>Closes the session's connections shortly, once they have had time to hear why.</summary>
    public void Close(Guid sessionId)
    {
        var closing = open
            .Values.Where(c => c.SessionId == sessionId)
            .Select(c => c.Context)
            .ToList();
        if (closing.Count == 0)
        {
            return;
        }

        _ = Task.Delay(CloseDelay)
            .ContinueWith(_ => closing.ForEach(context => context.Abort()), TaskScheduler.Default);
    }
}
