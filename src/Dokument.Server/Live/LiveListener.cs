using System.Text.Json;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Telemetry;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Dokument.Server.Live;

/// <summary>
/// Listens for <see cref="LiveChanges"/> notices on a Postgres connection of its own and passes each one on to the
/// clients it is for (docs/design.md §15.4). Requests and the worker both send notices, and they arrive here only once
/// their transaction commits. If the connection drops, notices sent meanwhile are lost, so once it is back every client
/// is told to fetch everything again.
/// </summary>
internal sealed partial class LiveListener(
    IConfiguration configuration,
    IHubContext<LiveHub> hub,
    LiveConnections connections,
    ILogger<LiveListener> logger
) : BackgroundService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        var missed = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString());
                var pending = new Queue<string>();
                connection.Notification += (_, e) => pending.Enqueue(e.Payload);
                await connection.OpenAsync(stoppingToken);
                await using (
                    var listen = new NpgsqlCommand($"LISTEN {LiveChanges.Channel}", connection)
                )
                {
                    await listen.ExecuteNonQueryAsync(stoppingToken);
                }

                if (missed)
                {
                    LogListening(logger);
                    await SendAsync(new LiveNotice { Topics = LiveTopics.All }, stoppingToken);
                }

                missed = false;
                retryDelay = TimeSpan.FromSeconds(1);
                while (true)
                {
                    await connection.WaitAsync(stoppingToken);
                    while (pending.TryDequeue(out var payload))
                    {
                        await DispatchAsync(payload, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                if (!missed)
                {
                    LogListenFailed(logger, e);
                }

                missed = true;
                await Task.Delay(retryDelay, stoppingToken)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
                retryDelay = TimeSpan.FromTicks(
                    Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks)
                );
            }
        }
    }

    private string ConnectionString() =>
        // TCP keepalives notice a connection that died quietly, such as after the database host restarts.
        new NpgsqlConnectionStringBuilder(DatabaseOptions.GetConnectionString(configuration))
        {
            KeepAlive = 30,
            Pooling = false,
        }.ConnectionString;

    private async Task DispatchAsync(string payload, CancellationToken ct)
    {
        LiveNotice? notice;
        try
        {
            notice = JsonSerializer.Deserialize<LiveNotice>(payload, LiveNotice.Json);
        }
        catch (JsonException e)
        {
            LogBadNotice(logger, e);
            return;
        }

        if (notice is not null)
        {
            await SendAsync(notice, ct);
        }
    }

    private async Task SendAsync(LiveNotice notice, CancellationToken ct)
    {
        DokumentTelemetry.LiveNotices.Add(1);
        if (notice.Session is { } sessionId)
        {
            await hub
                .Clients.Group(LiveHub.SessionGroup(sessionId))
                .SendAsync(
                    LiveHub.SignedOutMethod,
                    new LiveSignedOutMessage(notice.Reason ?? SessionEndReason.Revoked),
                    ct
                );
            connections.Close(sessionId);
            return;
        }

        var message = new LiveChangedMessage(notice.TopicNames(), notice.Origin);
        var clients = notice.Users is { } users
            ? hub.Clients.Groups(users.Select(LiveHub.UserGroup).ToList())
            : hub.Clients.All;
        await clients.SendAsync(LiveHub.ChangedMethod, message, ct);
    }

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Lost the database connection for live updates; retrying"
    )]
    private static partial void LogListenFailed(ILogger logger, Exception e);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Listening for live updates again; telling clients to refresh"
    )]
    private static partial void LogListening(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Skipped a live update notice that wasn't valid"
    )]
    private static partial void LogBadNotice(ILogger logger, Exception e);
}
