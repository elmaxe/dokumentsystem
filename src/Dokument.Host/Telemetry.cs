using System.Reflection;
using Dokument.Infrastructure.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Sinks.OpenTelemetry;

namespace Dokument.Host;

/// <summary>
/// OpenTelemetry traces, metrics and logs (docs/design.md §22.1). Off until <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (or a
/// per-signal endpoint) is set; Aspire sets it in development. Everything else is the standard <c>OTEL_*</c> variables,
/// which the SDK reads itself.
/// </summary>
internal sealed class Telemetry(IConfiguration configuration, HostRoles roles)
{
    private const string Grpc = "grpc";

    private string? BaseEndpoint => Setting("OTEL_EXPORTER_OTLP_ENDPOINT");

    internal bool TracesEnabled =>
        BaseEndpoint is not null || Setting("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT") is not null;

    internal bool MetricsEnabled =>
        BaseEndpoint is not null || Setting("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT") is not null;

    /// <summary>The OTLP endpoint for logs, as the full URL the Serilog sink wants, or null when logs are not exported.</summary>
    internal string? LogsEndpoint
    {
        get
        {
            if (Setting("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT") is { } endpoint)
            {
                return endpoint;
            }

            // Over HTTP the base endpoint gets the signal's path; over gRPC it is used as is.
            return BaseEndpoint is { } baseEndpoint && LogsProtocol != OtlpProtocol.Grpc
                ? $"{baseEndpoint.TrimEnd('/')}/v1/logs"
                : BaseEndpoint;
        }
    }

    internal OtlpProtocol LogsProtocol =>
        (
            Setting("OTEL_EXPORTER_OTLP_LOGS_PROTOCOL")
            ?? Setting("OTEL_EXPORTER_OTLP_PROTOCOL")
            ?? Grpc
        ) == Grpc
            ? OtlpProtocol.Grpc
            : OtlpProtocol.HttpProtobuf;

    public void AddOpenTelemetry(IServiceCollection services)
    {
        if (!TracesEnabled && !MetricsEnabled)
        {
            return;
        }

        var otel = services.AddOpenTelemetry().ConfigureResource(ConfigureResource);
        if (TracesEnabled)
        {
            otel.WithTracing(tracing =>
                tracing
                    .AddSource(DokumentTelemetry.Name)
                    .AddSource("Npgsql")
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments("/health");

                        // Span names already use the route template. The raw path and query can hold names and
                        // tokens, so they are dropped (docs/design.md §22.1).
                        options.EnrichWithHttpRequest = (activity, _) =>
                        {
                            activity.SetTag("url.path", null);
                            activity.SetTag("url.query", null);
                        };
                    })
                    .AddHttpClientInstrumentation()
                    .AddOtlpExporter()
            );
        }

        if (MetricsEnabled)
        {
            otel.WithMetrics(metrics =>
                metrics
                    .AddMeter(DokumentTelemetry.Name)
                    .AddMeter("Npgsql")
                    // ASP.NET Core, Kestrel, HttpClient and the runtime publish these meters themselves since .NET 9.
                    .AddMeter(
                        "Microsoft.AspNetCore.Hosting",
                        "Microsoft.AspNetCore.Server.Kestrel",
                        "Microsoft.AspNetCore.Http.Connections",
                        "Microsoft.AspNetCore.Routing",
                        "Microsoft.AspNetCore.Diagnostics",
                        "Microsoft.AspNetCore.RateLimiting",
                        "System.Net.Http",
                        "System.Net.NameResolution",
                        "System.Runtime"
                    )
                    .AddOtlpExporter()
            );
        }
    }

    /// <summary>Sends Serilog's events over OTLP too, with their trace and span ids, when logs are exported.</summary>
    public void WriteToOpenTelemetry(LoggerConfiguration logger)
    {
        if (LogsEndpoint is not { } endpoint)
        {
            return;
        }

        logger.WriteTo.OpenTelemetry(
            options =>
            {
                options.LogsEndpoint = endpoint;
                options.TracesEndpoint = null;
                options.Protocol = LogsProtocol;
                options.Headers = ParseHeaders(
                    Setting("OTEL_EXPORTER_OTLP_LOGS_HEADERS")
                        ?? Setting("OTEL_EXPORTER_OTLP_HEADERS")
                );
                var resource = ResourceBuilder.CreateDefault();
                ConfigureResource(resource);
                options.ResourceAttributes = resource
                    .Build()
                    .Attributes.ToDictionary(
                        attribute => attribute.Key,
                        attribute => attribute.Value
                    );
            },
            // We resolved the variables above, the same way the SDK does, so the sink must not apply its own reading.
            ignoreEnvironment: true
        );
    }

    /// <summary>Our defaults for the resource; <c>OTEL_SERVICE_NAME</c> and <c>OTEL_RESOURCE_ATTRIBUTES</c> override them.</summary>
    private void ConfigureResource(ResourceBuilder resource) =>
        resource
            .AddService(
                serviceName: roles switch
                {
                    { Server: true, Worker: true } => "dokumentsystem",
                    { Server: true } => "dokumentsystem-server",
                    _ => "dokumentsystem-worker",
                },
                serviceVersion: Assembly
                    .GetEntryAssembly()
                    ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    ?.InformationalVersion,
                serviceInstanceId: Environment.MachineName
            )
            .AddEnvironmentVariableDetector();

    private string? Setting(string name) =>
        configuration[name] is { Length: > 0 } value ? value.Trim() : null;

    /// <summary>Parses <c>key=value,key2=value2</c>, with URL-encoded values, as the OTLP exporter spec says.</summary>
    internal static Dictionary<string, string> ParseHeaders(string? headers) =>
        (headers ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim().Length > 0)
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0].Trim()),
                pair => Uri.UnescapeDataString(pair[1].Trim())
            );
}
