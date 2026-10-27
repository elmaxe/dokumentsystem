// Local development: `dotnet run --project src/Dokument.AppHost` starts Postgres (our image), Keycloak as a test IdP,
// the server, a worker and the Vite dev server, and opens the Aspire dashboard. See docs/design.md §3.2.
//
// Sign in at http://localhost:5173 as alice/alice (admin), bob/bob (user) or carol/carol (no groups).
// pgAdmin runs at http://localhost:5050, already connected to Postgres. The API reference (Scalar) is linked on the
// web resource in the dashboard; its Authorize button signs in at Keycloak too (docs/design.md §5, token endpoint).
//
// Telemetry is on in development: Aspire gives every project OTEL_EXPORTER_OTLP_ENDPOINT and friends, so the server's
// and worker's traces, metrics and logs show up in the dashboard. Outside Aspire it stays off until that variable is
// set (docs/design.md §22.1).

using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../.data"));

var postgres = builder
    .AddPostgres("postgres")
    .WithDockerfile("../../docker/postgres")
    .WithDataVolume("dokumentsystem-postgres")
    // pgAdmin listens on IPv6 by default, which fails where Docker has IPv6 turned off.
    .WithPgAdmin(pgAdmin =>
        pgAdmin.WithHostPort(5050).WithEnvironment("PGADMIN_LISTEN_ADDRESS", "0.0.0.0")
    );
var database = postgres.AddDatabase("Dokument", databaseName: "dokument");

// The realm, client and test users are imported on every start, so the dev IdP needs no setup or volume.
var keycloak = builder.AddKeycloak("keycloak", port: 8180).WithRealmImport("keycloak");

// Only the server uses the Host's launch profile. Both roles taking its fixed port 5080 made Aspire bind that port
// for whichever came first, so the server could stay "not ready" and nothing waiting on it would start.
IResourceBuilder<ProjectResource> AddRole(string role, string? launchProfile) =>
    builder
        .AddProject<Projects.Dokument_Host>(role, launchProfile)
        .WithEnvironment("ROLE", role)
        .WithEnvironment("Storage__OriginalsPath", Path.Combine(dataRoot, "originals"))
        .WithEnvironment("Storage__DerivedPath", Path.Combine(dataRoot, "derived"))
        .WithEnvironment("Storage__BackupsPath", Path.Combine(dataRoot, "backups"))
        .WithReference(database)
        .WaitFor(database);

var server = AddRole("server", launchProfile: "http")
    .WithHttpHealthCheck("/health/ready")
    .WithEnvironment(
        "OIDC_ISSUER",
        ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/dokument")
    )
    .WithEnvironment("OIDC_CLIENT_ID", "dokument")
    .WithEnvironment("OIDC_CLIENT_SECRET", "dokument-dev-secret")
    .WithEnvironment("OIDC_PROVIDER_NAME", "Keycloak (dev)")
    // bob gets 50GB from this claim; alice and carol fall back to the admin page (§17.1).
    .WithEnvironment("OIDC_QUOTA_CLAIM", "dokument_quota")
    .WaitFor(keycloak);

// The worker runs in the app's image by default, so exiftool, Tesseract and libvips are there on any machine, the same
// as in production. Start with `-- --WorkerOnHost=true` to run it as a project instead, e.g. to debug it; it then needs
// those tools installed (README, Develop).
if (builder.Configuration.GetValue<bool>("WorkerOnHost"))
{
    AddRole("worker", launchProfile: null)
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        // One line per job and per file read, in the dashboard's structured logs.
        .WithEnvironment("Serilog__MinimumLevel__Override__Dokument.Worker", "Debug")
        .WithHttpEndpoint(env: "HTTP_PORTS")
        .WithHttpHealthCheck("/health/ready")
        .WaitFor(server);
}
else
{
    var worker = builder
        .AddDockerfile("worker", "../..", stage: "runtime")
        .WithEnvironment("ROLE", "worker")
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        // One line per job and per file read, in the dashboard's structured logs.
        .WithEnvironment("Serilog__MinimumLevel__Override__Dokument.Worker", "Debug")
        .WithBindMount(dataRoot, "/data")
        .WithEnvironment("Storage__OriginalsPath", "/data/originals")
        .WithEnvironment("Storage__DerivedPath", "/data/derived")
        .WithEnvironment("Storage__BackupsPath", "/data/backups")
        .WithReference(database)
        .WithOtlpExporter()
        .WithHttpEndpoint(targetPort: 8080)
        .WithHttpHealthCheck("/health/ready")
        .WaitFor(server);

    // On Linux and macOS the container writes thumbnails as you, so the server on the host can clean them up.
    if (!OperatingSystem.IsWindows())
    {
        worker.WithContainerRuntimeArgs("--user", $"{Unix.getuid()}:{Unix.getgid()}");
    }
}

// A fixed port, because the dev realm's redirect URI names it.
var web = builder
    .AddViteApp("web", "../../apps/web")
    .WithPnpm()
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithReference(server)
    .WaitFor(server);

// The browser reaches the server through Vite's proxy, so that is the public URL for redirects and cookies.
server.WithEnvironment("PUBLIC_URL", web.GetEndpoint("http"));

// Through Vite as well, because Keycloak sends Scalar's sign-in back to the public URL.
web.WithUrl(
    ReferenceExpression.Create($"{web.GetEndpoint("http")}/scalar/"),
    "API reference (Scalar)"
);

builder.Build().Run();

static class Unix
{
    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getuid();

    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getgid();
}
