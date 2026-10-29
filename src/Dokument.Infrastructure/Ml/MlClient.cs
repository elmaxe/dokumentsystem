using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Dokument.Domain.Search;
using Dokument.Domain.Settings;
using Dokument.Infrastructure.Settings;

namespace Dokument.Infrastructure.Ml;

/// <summary>An ML service: one in the admin's list, or the one the environment sets (docs/design.md §11).</summary>
public sealed record MlConnection(ServiceUrl Url, string? ApiKey, bool FromEnvironment = false);

/// <summary>What <c>POST /transcribe</c> streams back, one per line (§11.1).</summary>
public abstract record TranscribeEvent
{
    /// <summary>First: the language heard (or the one asked for), the recording's length and how much of it is speech.</summary>
    public sealed record Detected(
        string Language,
        double LanguageProbability,
        int DurationMs,
        int SpeechMs,
        string Model,
        string Engine
    ) : TranscribeEvent;

    public sealed record Segment(TranscriptLine Line) : TranscribeEvent;

    public sealed record Done(int Segments) : TranscribeEvent;

    /// <summary>The service failed after it had started answering, e.g. out of memory.</summary>
    public sealed record Failed(string Message) : TranscribeEvent;
}

/// <param name="Language">Set when resuming, so the rest is read in the same language.</param>
public sealed record TranscribeRequest(string Model, string? Language, int StartMs);

/// <summary>The ML service's answer was cut off or isn't the contract; the job retries.</summary>
public sealed class MlProtocolException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>What the last health checks said, per service URL; one per app, since the client is made per job.</summary>
public sealed class MlHealth
{
    internal ConcurrentDictionary<string, (bool Healthy, long At)> Known { get; } = new();
}

/// <summary>
/// Talks to the ML services (§11). The worker uses the first one in the admin's list that answers its health check;
/// checks are remembered for a short while, so a queue of jobs doesn't ask for each one.
/// </summary>
public sealed class MlClient(
    HttpClient http,
    MlServices services,
    ServiceProbe probe,
    MlHealth health,
    TimeProvider time
)
{
    private static readonly TimeSpan RememberHealth = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether any ML service is set at all; without one, nothing that needs it is queued.</summary>
    public Task<bool> ConfiguredAsync(CancellationToken ct) => services.ConfiguredAsync(ct);

    /// <summary>The first ML service that answers, in the admin's order; null when none does.</summary>
    public async Task<MlConnection?> PickAsync(CancellationToken ct)
    {
        foreach (var endpoint in await services.ListAsync(ct))
        {
            var now = time.GetTimestamp();
            if (
                !health.Known.TryGetValue(endpoint.Url.Value, out var known)
                || time.GetElapsedTime(known.At, now) > RememberHealth
            )
            {
                known = (
                    (await probe.CheckMlAsync(endpoint.Url, endpoint.ApiKey, ct)).Reachable,
                    now
                );
                health.Known[endpoint.Url.Value] = known;
            }

            if (known.Healthy)
            {
                return endpoint;
            }
        }

        return null;
    }

    /// <summary>Forgets that a service was healthy, e.g. after a call to it failed, so the next pick checks again.</summary>
    public void Forget(MlConnection connection) =>
        health.Known.TryRemove(connection.Url.Value, out _);

    /// <summary>Sends 16 kHz audio to <c>POST /transcribe</c> and yields what comes back as it arrives.</summary>
    public async IAsyncEnumerable<TranscribeEvent> TranscribeAsync(
        MlConnection connection,
        string audioPath,
        TranscribeRequest request,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await using var audio = File.OpenRead(audioPath);
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(audio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/ogg");
        form.Add(file, "audio", "audio.ogg");
        form.Add(new StringContent(request.Model), "model");
        form.Add(
            new StringContent(
                request.StartMs.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ),
            "start_ms"
        );
        if (request.Language is { } language)
        {
            form.Add(new StringContent(language), "language");
        }

        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            connection.Url.Resolve("transcribe")
        )
        {
            Content = form,
        };
        if (connection.ApiKey is { } key)
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await http.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException(
                $"The ML service answered {(int)response.StatusCode}: {(body.Length > 500 ? body[..500] : body)}",
                null,
                response.StatusCode
            );
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var finished = false;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parsed = Parse(line);
            finished = parsed is TranscribeEvent.Done or TranscribeEvent.Failed;
            yield return parsed;
            if (finished)
            {
                yield break;
            }
        }

        throw new MlProtocolException("The ML service stopped answering before it was done.");
    }

    public static TranscribeEvent Parse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            return root.GetProperty("type").GetString() switch
            {
                "detected" => new TranscribeEvent.Detected(
                    root.GetProperty("language").GetString() ?? "",
                    root.GetProperty("language_probability").GetDouble(),
                    root.GetProperty("duration_ms").GetInt32(),
                    root.GetProperty("speech_ms").GetInt32(),
                    root.GetProperty("model").GetString() ?? "",
                    root.GetProperty("engine").GetString() ?? ""
                ),
                "segment" => new TranscribeEvent.Segment(
                    new TranscriptLine(
                        root.GetProperty("start_ms").GetInt32(),
                        root.GetProperty("end_ms").GetInt32(),
                        root.GetProperty("text").GetString() ?? "",
                        root.GetProperty("words").Deserialize<List<TranscriptWord>>(Json) ?? []
                    )
                ),
                "done" => new TranscribeEvent.Done(root.GetProperty("segments").GetInt32()),
                "error" => new TranscribeEvent.Failed(
                    root.GetProperty("message").GetString() ?? "Unknown error"
                ),
                var other => throw new MlProtocolException($"Unknown line type \"{other}\"."),
            };
        }
        catch (Exception e)
            when (e
                    is JsonException
                        or KeyNotFoundException
                        or InvalidOperationException
                        or FormatException
            )
        {
            throw new MlProtocolException("The ML service sent a line that isn't JSON we know.", e);
        }
    }
}
