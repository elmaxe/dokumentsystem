using Dokument.Domain.Jobs;
using Dokument.Worker.Jobs;
using Dokument.Worker.Previews;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.Worker;

/// <summary>The <c>worker</c> role: background jobs such as metadata, thumbnails and text extraction (docs/design.md §10).</summary>
public static class WorkerRole
{
    public static IServiceCollection AddWorkerRole(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(WorkerOptions.SectionName);
        services.Configure<WorkerOptions>(options);
        var worker = options.Get<WorkerOptions>() ?? new WorkerOptions();

        services.AddSingleton<Thumbnailer>();
        services.AddScoped<ThumbnailHandler>();
        services.AddSingleton(new JobHandlerRegistration(JobType.Thumbnail, typeof(ThumbnailHandler), worker.ThumbnailConcurrency));

        services.AddHostedService<JobRunner>();
        services.AddHostedService<ThumbnailBackfill>();
        return services;
    }
}
