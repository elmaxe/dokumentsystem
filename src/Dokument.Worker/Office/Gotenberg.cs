using System.Net;
using System.Net.Http.Headers;
using Dokument.Domain.Settings;

namespace Dokument.Worker.Office;

/// <summary>Why Gotenberg made no PDF of a file it was able to read: damaged, password-protected, or not a document.</summary>
public sealed class DocumentUnreadableException(string message) : Exception(message);

/// <summary>
/// Gotenberg's LibreOffice route (docs/design.md §10.3). Spreadsheets get one page per sheet, so a wide sheet isn't
/// cut into strips. A file Gotenberg refuses throws <see cref="DocumentUnreadableException"/>; Gotenberg being down
/// or busy throws <see cref="HttpRequestException"/>, so the job retries.
/// </summary>
public sealed class Gotenberg(HttpClient http)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>Converts <paramref name="path"/> and writes the PDF to <paramref name="target"/>.</summary>
    public async Task ConvertAsync(
        ServiceUrl url,
        string path,
        string extension,
        string target,
        CancellationToken ct
    )
    {
        await using var source = File.OpenRead(path);
        using var file = new StreamContent(source);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var form = new MultipartFormDataContent
        {
            { file, "files", $"document.{extension}" },
            { new StringContent("true"), "singlePageSheets" },
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var response = await http.PostAsync(
            url.Resolve("forms/libreoffice/convert"),
            form,
            timeout.Token
        );
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity)
        {
            var reason = await response.Content.ReadAsStringAsync(timeout.Token);
            throw new DocumentUnreadableException(
                string.IsNullOrWhiteSpace(reason)
                    ? "Gotenberg couldn't convert the file."
                    : reason.Trim()
            );
        }

        response.EnsureSuccessStatusCode();
        await using var output = File.Create(target);
        await response.Content.CopyToAsync(output, timeout.Token);
    }
}
