// Local development: `dotnet run --project src/Dokument.AppHost` starts Postgres (our image), Keycloak as a test IdP,
// Gotenberg for Office previews, the server, a worker and the Vite dev server, and opens the Aspire dashboard. See docs/design.md §3.2.
//
// Sign in at http://localhost:5173 as alice/alice (admin), bob/bob (user) or carol/carol (no groups). The mobile app
// signs in at this machine's network address, which the dashboard lists on the web resource.
// pgAdmin runs at http://localhost:5050, already connected to Postgres. The API reference (Scalar) is linked on the
// web resource in the dashboard; its Authorize button signs in at Keycloak too (docs/design.md §5, token endpoint).
//
// Telemetry is on in development: Aspire gives every project OTEL_EXPORTER_OTLP_ENDPOINT and friends, so the server's
// and worker's traces, metrics and logs show up in the dashboard. Outside Aspire it stays off until that variable is
// set (docs/design.md §22.1).

using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// The app on a phone reaches Keycloak and the web app at this machine's address on the network, so both listen on all
// addresses and everything signs in at that address. Keycloak names its issuer after the address it's asked at, so the
// server must use the same one. Pass `-- --LanHost=192.168.1.20` when the guess picks the wrong network.
var lanHost = builder.Configuration["LanHost"] ?? Lan.Address() ?? "localhost";
Console.WriteLine(
    $"Sign-in and the mobile app use http://{lanHost}:5173 (Keycloak at http://{lanHost}:8180)."
);

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
#pragma warning disable ASPIRECERTIFICATES001 // WithoutHttpsCertificate is still marked experimental.
var keycloak = builder
    .AddKeycloak("keycloak", port: 8180)
    .WithEndpoint("http", endpoint => endpoint.TargetHost = "0.0.0.0")
    // Plain http: Aspire otherwise serves Keycloak with the ASP.NET dev certificate where one is trusted, and that only
    // covers localhost, so a phone would refuse it.
    .WithoutHttpsCertificate()
    // The dashboard links the address everything signs in at.
    .WithUrlForEndpoint("http", url => url.Url = $"http://{lanHost}:8180")
    .WithRealmImport("keycloak")
    // The realm allows the app's sign-in page at this address too.
    .WithEnvironment("DOKUMENT_LAN_WEB_URL", $"http://{lanHost}:5173");
#pragma warning restore ASPIRECERTIFICATES001

// Office previews (docs/design.md §14). Each role gets the address it reaches Gotenberg at as GOTENBERG_URL, which wins
// over the admin setting. Long documents take a while; the worker waits up to 3 minutes.
var gotenberg = builder
    .AddContainer("gotenberg", "gotenberg/gotenberg", "8")
    .WithArgs("gotenberg", "--api-timeout=180s")
    .WithHttpEndpoint(targetPort: 3000)
    .WithHttpHealthCheck("/health");
var gotenbergUrl = gotenberg.GetEndpoint("http");

// The ML service for speech to text (§11.1), built from ml/. Its models download into the volume the first time they're
// used. The server on the host and the worker in a container reach it at different addresses, so it comes to both
// through the environment (Ml__Url) rather than Admin › Settings. Start with `-- --Ml=false` to leave it out.
var ml = builder.Configuration.GetValue("Ml", true)
    ? builder
        .AddDockerfile("ml", "../../ml")
        .WithVolume("dokumentsystem-ml-cache", "/cache")
        .WithHttpEndpoint(targetPort: 3003)
        .WithHttpHealthCheck("/health")
    : null;

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
    .WithEnvironment("OIDC_ISSUER", $"http://{lanHost}:8180/realms/dokument")
    .WithEnvironment("OIDC_CLIENT_ID", "dokument")
    .WithEnvironment("OIDC_CLIENT_SECRET", "dokument-dev-secret")
    .WithEnvironment("OIDC_PROVIDER_NAME", "Keycloak (dev)")
    // bob gets 50GB from this claim; alice and carol fall back to the admin page (§17.1).
    .WithEnvironment("OIDC_QUOTA_CLAIM", "dokument_quota")
    .WithEnvironment("GOTENBERG_URL", gotenbergUrl)
    .WaitFor(keycloak);
if (ml is not null)
{
    server.WithEnvironment("Ml__Url", ml.GetEndpoint("http"));
}

// The worker runs in the app's image by default, so exiftool, Tesseract and libvips are there on any machine, the same
// as in production. Start with `-- --WorkerOnHost=true` to run it as a project instead, e.g. to debug it; it then needs
// those tools installed (README, Develop).
if (builder.Configuration.GetValue<bool>("WorkerOnHost"))
{
    var worker = AddRole("worker", launchProfile: null)
        .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
        // One line per job and per file read, in the dashboard's structured logs.
        .WithEnvironment("Serilog__MinimumLevel__Override__Dokument.Worker", "Debug")
        .WithHttpEndpoint(env: "HTTP_PORTS")
        .WithHttpHealthCheck("/health/ready")
        .WithEnvironment("GOTENBERG_URL", gotenbergUrl)
        .WaitFor(gotenberg)
        .WaitFor(server);
    if (ml is not null)
    {
        worker.WithEnvironment("Ml__Url", ml.GetEndpoint("http"));
    }
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
        .WithEnvironment("GOTENBERG_URL", gotenbergUrl)
        .WaitFor(gotenberg)
        .WaitFor(server);

    if (ml is not null)
    {
        worker.WithEnvironment("Ml__Url", ml.GetEndpoint("http"));
    }

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
    .WithEndpoint(
        "http",
        endpoint =>
        {
            endpoint.Port = 5173;
            endpoint.TargetHost = "0.0.0.0";
        }
    )
    .WithReference(server)
    .WaitFor(server);

// No PUBLIC_URL: Vite's proxy passes on the address each device used, so redirects go back to localhost:5173 in the
// browser on this machine and to the network address on a phone. A browser needs localhost, because plain http
// anywhere else isn't a secure context.

// Through Vite as well, because Keycloak sends Scalar's sign-in back to the public URL.
web.WithUrl(
    ReferenceExpression.Create($"{web.GetEndpoint("http")}/scalar/"),
    "API reference (Scalar)"
);
web.WithUrl($"http://{lanHost}:5173", "Server address for the app");

builder.Build().Run();

static class Lan
{
    /// <summary>
    /// The IPv4 address this machine reaches other networks from: a UDP socket "connected" to a public address picks
    /// it from the routing table without sending anything. That skips Docker's, WSL's and VPN-less bridges on every OS.
    /// Null when there's no network, and then everything stays on localhost.
    /// </summary>
    internal static string? Address()
    {
        try
        {
            using var socket = new Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp
            );
            socket.Connect("8.8.8.8", 53);
            return
                socket.LocalEndPoint is IPEndPoint { Address: var address }
                && !IPAddress.IsLoopback(address)
                ? address.ToString()
                : null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

static class Unix
{
    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getuid();

    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern uint getgid();
}
