namespace Dokument.Links;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args is ["healthcheck", ..])
        {
            return await HealthCheck.RunAsync();
        }

        var app = LinksGateway.Build(LinksGateway.CreateBuilder(args));
        await app.RunAsync();
        return 0;
    }
}
