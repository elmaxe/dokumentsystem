using Dokument.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Trace;
using Serilog.Sinks.OpenTelemetry;

namespace Dokument.UnitTests;

public class TelemetryTests
{
    private static readonly HostRoles Both = new(Server: true, Worker: true);

    private static Telemetry Create(params (string Key, string Value)[] settings) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => (string?)s.Value))
                .Build(),
            Both
        );

    [Fact]
    public void Is_off_without_an_endpoint()
    {
        var telemetry = Create();

        Assert.False(telemetry.TracesEnabled);
        Assert.False(telemetry.MetricsEnabled);
        Assert.Null(telemetry.LogsEndpoint);

        var services = new ServiceCollection();
        telemetry.AddOpenTelemetry(services);
        Assert.Null(services.BuildServiceProvider().GetService<TracerProvider>());
    }

    [Fact]
    public void Base_endpoint_turns_on_every_signal()
    {
        var telemetry = Create(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"));

        Assert.True(telemetry.TracesEnabled);
        Assert.True(telemetry.MetricsEnabled);
        Assert.Equal("http://collector:4317", telemetry.LogsEndpoint);
        Assert.Equal(OtlpProtocol.Grpc, telemetry.LogsProtocol);
    }

    [Fact]
    public void Http_base_endpoint_gets_the_logs_path()
    {
        var telemetry = Create(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318/"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
        );

        Assert.Equal("http://collector:4318/v1/logs", telemetry.LogsEndpoint);
        Assert.Equal(OtlpProtocol.HttpProtobuf, telemetry.LogsProtocol);
    }

    [Fact]
    public void Per_signal_endpoints_turn_on_only_their_signal()
    {
        var telemetry = Create(("OTEL_EXPORTER_OTLP_LOGS_ENDPOINT", "http://logs:4318/custom"));

        Assert.False(telemetry.TracesEnabled);
        Assert.False(telemetry.MetricsEnabled);
        Assert.Equal("http://logs:4318/custom", telemetry.LogsEndpoint);
    }

    [Fact]
    public void Metrics_port_turns_on_metrics_alone()
    {
        var telemetry = Create(("METRICS_PORT", "9464"));

        Assert.Equal(9464, telemetry.PrometheusPort);
        Assert.True(telemetry.MetricsEnabled);
        Assert.False(telemetry.OtlpMetricsEnabled);
        Assert.False(telemetry.TracesEnabled);
        Assert.Null(telemetry.LogsEndpoint);
    }

    [Theory]
    [InlineData("metrics")]
    [InlineData("0")]
    [InlineData("70000")]
    public void Rejects_a_bad_metrics_port(string value)
    {
        Assert.Throws<InvalidOperationException>(() =>
            Create(("METRICS_PORT", value)).PrometheusPort
        );
    }

    [Fact]
    public void Parses_headers_as_the_spec_says()
    {
        var headers = Telemetry.ParseHeaders(
            "x-otlp-api-key=abc%3D, Authorization = Basic eA==,bad"
        );

        Assert.Equal(2, headers.Count);
        Assert.Equal("abc=", headers["x-otlp-api-key"]);
        Assert.Equal("Basic eA==", headers["Authorization"]);
    }
}
