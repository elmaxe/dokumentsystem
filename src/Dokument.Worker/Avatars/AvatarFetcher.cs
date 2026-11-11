using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Dokument.Worker.Avatars;

/// <summary>What fetching a picture claim gave.</summary>
public abstract record FetchedPicture
{
    /// <summary>The provider answered 304 to our ETag: the stored avatar is still right.</summary>
    public sealed record Unchanged : FetchedPicture;

    public sealed record Bytes(byte[] Content, string? ETag) : FetchedPicture;

    /// <summary>Nothing usable came back. The stored avatar, if any, is kept.</summary>
    public sealed record Failed(string Reason) : FetchedPicture;
}

/// <summary>
/// Downloads a picture claim (docs/design.md §5.1): an http or https URL with at most <see cref="MaxRedirects"/>
/// redirects, <see cref="MaxBytes"/> and <see cref="Timeout"/>, or a <c>data:</c> URL decoded in place. Local addresses
/// are allowed, since the sign-in provider often runs on the same network; only a decoded image is ever kept.
/// </summary>
public sealed class AvatarFetcher(HttpClient http)
{
    public const string ClientName = "avatars";
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRedirects = 3;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public async Task<FetchedPicture> FetchAsync(string url, string? etag, CancellationToken ct)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeDataUrl(url);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("image/*");
        if (etag is not null && EntityTagHeaderValue.TryParse(etag, out var tag))
        {
            request.Headers.IfNoneMatch.Add(tag);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token
            );
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                return new FetchedPicture.Unchanged();
            }

            if (!response.IsSuccessStatusCode)
            {
                return new FetchedPicture.Failed($"HTTP {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                return new FetchedPicture.Failed("bigger than 5 MB");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    return new FetchedPicture.Failed("bigger than 5 MB");
                }

                buffer.Write(chunk, 0, read);
            }

            return new FetchedPicture.Bytes(buffer.ToArray(), response.Headers.ETag?.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new FetchedPicture.Failed("timed out");
        }
        catch (HttpRequestException e)
        {
            return new FetchedPicture.Failed(e.Message);
        }
    }

    /// <summary><c>data:image/png;base64,…</c>, or percent-encoded text such as an SVG.</summary>
    internal static FetchedPicture DecodeDataUrl(string url)
    {
        var comma = url.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return new FetchedPicture.Failed("not a data URL");
        }

        var header = url[5..comma];
        var data = url[(comma + 1)..];
        try
        {
            var bytes = header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(Uri.UnescapeDataString(data))
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
            return bytes.Length > MaxBytes
                ? new FetchedPicture.Failed("bigger than 5 MB")
                : new FetchedPicture.Bytes(bytes, null);
        }
        catch (FormatException)
        {
            return new FetchedPicture.Failed("not valid base64");
        }
    }
}
