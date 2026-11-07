using Dokument.Domain.Jobs;
using Dokument.Worker.Filing;
using Dokument.Worker.Jobs;
using Dokument.Worker.Media;
using Dokument.Worker.Office;
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

        services.AddScoped<TagRuleHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(JobType.TagRule, typeof(TagRuleHandler), 1)
        );

        // One at a time, so two files filed into a new folder don't both make it.
        services.AddScoped<FileHandler>();
        services.AddSingleton(new JobHandlerRegistration(JobType.File, typeof(FileHandler), 1));
        services.AddScoped<FilingRunHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(JobType.FilingRun, typeof(FilingRunHandler), 1)
        );

        services.AddSingleton<FFmpeg>();
        services.AddHttpClient<Gotenberg>(http =>
            http.Timeout = Gotenberg.Timeout + TimeSpan.FromSeconds(10)
        );
        services.AddScoped<TranscodeHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.Transcode,
                typeof(TranscodeHandler),
                worker.TranscodeConcurrency,
                worker.TranscodeJobTimeout
            )
        );

        services.AddScoped<SeekPreviewsHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.SeekPreviews,
                typeof(SeekPreviewsHandler),
                worker.SeekPreviewsConcurrency,
                worker.SeekPreviewsJobTimeout
            )
        );

        services.AddScoped<TranscribeHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(
                JobType.Transcribe,
                typeof(TranscribeHandler),
                worker.TranscribeConcurrency,
                worker.TranscribeJobTimeout
            )
        );

        services.AddScoped<PageSheetsHandler>();
        services.AddSingleton(
            new JobHandlerRegistration(JobType.PageSheets, typeof(PageSheetsHandler), 1)
        );

        // One at a time: PDFium works under one lock anyway (docs/design.md §8.4).
        services.AddScoped<PdfHandler>();
        services.AddSingleton(new JobHandlerRegistration(JobType.Pdf, typeof(PdfHandler), 1));

        services.AddHostedService<JobRunner>();
        services.AddHostedService<ThumbnailBackfill>();
        services.AddHostedService<ContentBackfill>();
        return services;
    }

    /// <summary>The queue, upload and storage gauges (docs/design.md §22.1). Only worth their queries when metrics are exported.</summary>
    public static IServiceCollection AddMetricsSampler(this IServiceCollection services) =>
        services.AddHostedService<MetricsSampler>();
}
