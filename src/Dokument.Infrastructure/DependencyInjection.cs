using System.Diagnostics;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Ml;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Storage;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddDbContext<DokumentDbContext>(options =>
            options.UseNpgsql(
                DatabaseOptions.GetConnectionString(configuration),
                npgsql =>
                    npgsql.ConfigureDataSource(dataSource =>
                        // Only trace SQL inside a trace. Otherwise every poll of the job queue would be its own trace.
                        dataSource.ConfigureTracing(tracing =>
                            tracing
                                .ConfigureCommandFilter(_ => Activity.Current is not null)
                                .EnablePhysicalOpenTracing(false)
                        )
                    )
            )
        );
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<UserProvisioner>();
        services.AddScoped<SessionStore>();
        services.AddSingleton<BlobStore>();
        services.AddSingleton<UploadLocks>();
        services.AddScoped<AuditLog>();
        services.AddScoped<LiveChanges>();
        services.AddScoped<DriveService>();
        services.AddScoped<UploadService>();
        services.AddScoped<PurgeService>();
        services.AddSingleton<PurgeSignal>();
        services.AddScoped<TrashService>();
        services.AddScoped<StarService>();
        services.AddScoped<RecentFiles>();
        services.AddScoped<JobQueue>();
        services.AddScoped<JobMonitor>();
        services.AddSingleton<DerivedStore>();
        services.AddScoped<PreviewService>();
        services.AddScoped<MediaService>();
        services.AddScoped<ContentService>();
        services.AddScoped<SearchService>();
        services.AddScoped<FileDetailsService>();
        services.AddScoped<Tags.TagService>();
        services.AddScoped<Tags.TagRules>();
        services.AddScoped<Filing.FilingQueue>();
        services.AddScoped<Filing.Filer>();
        services.AddScoped<Filing.FilingService>();
        services.AddScoped<SettingsStore>();
        services.AddHttpClient<ServiceProbe>();
        services.Configure<MlOptions>(configuration.GetSection(MlOptions.SectionName));
        services.AddSingleton<MlHealth>();
        services.AddScoped<MlServices>();
        services.AddScoped<TranscriptService>();

        // Keys live on the originals volume, so they're backed up with it and survive restarts (§21). The worker
        // shares them to read the ML services' API keys.
        var storage =
            configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>()
            ?? new StorageOptions();
        services
            .AddDataProtection()
            .SetApplicationName("Dokumentsystem")
            .PersistKeysToFileSystem(
                new DirectoryInfo(Path.Combine(storage.OriginalsPath, "keys"))
            );
        services.AddSingleton<SecretProtector>();

        // Transcription streams for as long as a recording takes; the client sets its own limits per call.
        services
            .AddHttpClient<MlClient>()
            .ConfigureHttpClient(http => http.Timeout = Timeout.InfiniteTimeSpan);
        return services;
    }
}
