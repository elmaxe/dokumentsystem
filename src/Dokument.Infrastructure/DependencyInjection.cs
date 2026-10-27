using System.Diagnostics;
using Dokument.Infrastructure.Files;
using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Jobs;
using Dokument.Infrastructure.Live;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Previews;
using Dokument.Infrastructure.Search;
using Dokument.Infrastructure.Settings;
using Dokument.Infrastructure.Storage;
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
        services.AddScoped<ContentService>();
        services.AddScoped<SearchService>();
        services.AddScoped<FileDetailsService>();
        services.AddScoped<SettingsStore>();
        services.AddHttpClient<ServiceProbe>();
        return services;
    }
}
