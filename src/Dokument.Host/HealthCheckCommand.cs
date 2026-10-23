namespace Dokument.Host;

/// <summary>
/// <c>dokument healthcheck</c>: used by the Docker HEALTHCHECK, since the runtime image has no curl.
/// Exits 0 when <c>/health/ready</c> answers 200.
/// </summary>
internal static class HealthCheckCommand
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
                new Uri($"http://127.0.0.1:{port}/health/ready")
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
