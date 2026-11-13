namespace Dokument.Links;

/// <summary>The image's HEALTHCHECK: it has no shell or curl, so the gateway checks itself.</summary>
internal static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port =
            Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0]
            ?? "8080";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(
                new Uri($"http://127.0.0.1:{port}/health/live")
            );
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}
