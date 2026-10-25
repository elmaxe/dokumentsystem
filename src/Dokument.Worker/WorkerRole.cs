using Dokument.Domain.Jobs;
using Dokument.Worker.Jobs;
using Dokument.Worker.Previews;
using Dokument.Worker.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.Worker;

/// <summary>The <c>worker</c> role: background jobs such as metadata, thumbnails and text extraction (docs/design.md §10).</summary>
public static class WorkerRole
{
    public static IServiceCollection AddWorkerRole(
        this IServiceCollection services,
        IConfiguration configuration
    )
    {
        var options = configuration.GetSection(WorkerOptions.SectionName);
        services.Configure<WorkerOptions>(options);
        var worker = options.Get<WorkerOptions>() ?? new WorkerOptions();

        services.AddSingleton<Thumbnailer>();
        services.AddScoped<ThumbnailHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.Thumbnail,
                typeof(ThumbnailHandler),
                worker.ThumbnailConcurrency
            )
        );

        services.AddSingleton<ExifTool>();
        services.AddScoped<MetadataHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.Metadata,
                typeof(MetadataHandler),
                worker.MetadataConcurrency
            )
        );

        services.AddSingleton<ITextRecognizer, TesseractOcr>();
        services.AddSingleton<TextExtractor>();
        services.AddScoped<TextHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.Text,
                typeof(TextHandler),
                worker.TextConcurrency,
                worker.TextJobTimeout
            )
        );

        services.AddHostedService<JobRunner>();
        services.AddHostedService<ThumbnailBackfill>();
        services.AddHostedService<ContentBackfill>();
        return services;
    }

    /// <summary>The queue, upload and storage gauges (docs/design.md §22.1). Only worth their queries when metrics are exported.</summary>
    public static IServiceCollection AddMetricsSampler(this IServiceCollection services) =>
        services.AddHostedService<MetricsSampler>();
}
