using Dokument.Domain.Backups;
using Dokument.Infrastructure;
using Dokument.Infrastructure.Backups;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dokument.Host;

/// <summary>
/// <c>dokument backup</c>: backs up the database now, with the same code as the worker's scheduled backups
/// (docs/design.md §19), e.g. before an upgrade: <c>docker compose exec worker dotnet /app/dokument.dll backup</c>.
/// Prints where the files went and exits 0, or prints why it failed and exits 1. The admin Storage page lists it too.
/// </summary>
internal static class BackupCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddInfrastructure(builder.Configuration);
        using var host = builder.Build();
        await using var scope = host.Services.CreateAsyncScope();
        var backups = scope.ServiceProvider.GetRequiredService<BackupService>();
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        var backup = await backups.RunNowAsync(cancel.Token);
        if (backup.Status == BackupStatus.Done)
        {
            Console.WriteLine($"Backed up to {Path.Combine(backups.Folder, backup.DatabaseFile!)}");
            Console.WriteLine(
                $"Name manifest: {Path.Combine(backups.Folder, backup.ManifestFile!)}"
            );
            return 0;
        }

        await Console.Error.WriteLineAsync(
            $"The backup failed ({backup.Error}): {backup.ErrorDetail}"
        );
        return 1;
    }
}
