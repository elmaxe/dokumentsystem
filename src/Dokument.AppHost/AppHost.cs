// Local development: `dotnet run --project src/Dokument.AppHost` starts Postgres (our image), Keycloak as a test IdP,
// the server, a worker and the Vite dev server, and opens the Aspire dashboard. See docs/design.md §3.2.
//
// Sign in at http://localhost:5173 as alice/alice (admin), bob/bob (user) or carol/carol (no groups).
// pgAdmin runs at http://localhost:5050, already connected to Postgres. The API reference (Scalar) is linked on the
// server resource in the dashboard.
//
// Telemetry is on in development: Aspire gives every project OTEL_EXPORTER_OTLP_ENDPOINT and friends, so the server's
// and worker's traces, metrics and logs show up in the dashboard. Outside Aspire it stays off until that variable is
// set (docs/design.md §22.1).

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
    .WaitFor(keycloak);
server.WithUrl(
    ReferenceExpression.Create($"{server.GetEndpoint("http")}/scalar"),
    "API reference (Scalar)"
);
AddRole("worker", launchProfile: null)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithHttpEndpoint(env: "HTTP_PORTS")
    .WithHttpHealthCheck("/health/ready")
    .WaitFor(server);

// A fixed port, because the dev realm's redirect URI names it.
var web = builder
    .AddViteApp("web", "../../apps/web")
    .WithPnpm()
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithReference(server)
    .WaitFor(server);

// The browser reaches the server through Vite's proxy, so that is the public URL for redirects and cookies.
server.WithEnvironment("PUBLIC_URL", web.GetEndpoint("http"));

builder.Build().Run();
