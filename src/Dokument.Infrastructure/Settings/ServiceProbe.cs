using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Dokument.Domain.Settings;

namespace Dokument.Infrastructure.Settings;

/// <param name="Version">What the service reported about itself, such as "1.0.0" or "ClamAV 1.4.3/27436".</param>
public sealed record ProbeResult(bool Reachable, string? Version, string? Error)
{
    public static ProbeResult Failed(string error) => new(false, null, error);
}

/// <summary>Checks that the companion containers answer: the ML service (§11) and ClamAV (§21).</summary>
public sealed class ServiceProbe(HttpClient http)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>Calls the ML service's <c>GET /health</c>.</summary>
    public async Task<ProbeResult> CheckMlAsync(
        ServiceUrl url,
        string? apiKey,
        CancellationToken ct
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url.Resolve("health"));
        if (!string.IsNullOrEmpty(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ProbeResult.Failed($"HTTP {(int)response.StatusCode}");
            }

            return new ProbeResult(true, await ReadVersionAsync(response, timeout.Token), null);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return ProbeResult.Failed(
                e is OperationCanceledException ? "No answer within 5 seconds" : e.Message
            );
        }
    }

    /// <summary>Sends clamd's <c>zVERSION</c> command and returns its reply.</summary>
    public async Task<ProbeResult> CheckClamdAsync(ClamdAddress address, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            using var client = new TcpClient();
            await client.ConnectAsync(address.Host, address.Port, timeout.Token);
            var stream = client.GetStream();
            await stream.WriteAsync("zVERSION\0"u8.ToArray(), timeout.Token);

            var reply = new MemoryStream();
            var buffer = new byte[256];
            while (reply.Length < 1024)
            {
                var read = await stream.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                reply.Write(buffer, 0, read);
                if (buffer[read - 1] == 0)
                {
                    break;
                }
            }

            var version = Encoding.ASCII.GetString(reply.ToArray()).TrimEnd('\0', '\n').Trim();
            return version.StartsWith("ClamAV", StringComparison.Ordinal)
                ? new ProbeResult(true, version, null)
                : ProbeResult.Failed("Something answered, but it isn't ClamAV");
        }
        catch (Exception e) when (e is SocketException or IOException or OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return ProbeResult.Failed(
                e is OperationCanceledException ? "No answer within 5 seconds" : e.Message
            );
        }
    }

    private static async Task<string?> ReadVersionAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        if (response.Content.Headers.ContentType?.MediaType != "application/json")
        {
            return null;
        }

        try
        {
            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct),
                cancellationToken: ct
            );
            return
                json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
