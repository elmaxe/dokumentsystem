// Local development: `dotnet run --project src/Dokument.AppHost` starts Postgres (our image), Keycloak as a test IdP,
// the server, a worker and the Vite dev server, and opens the Aspire dashboard. See docs/design.md §3.2.
//
// Sign in at http://localhost:5173 as alice/alice (admin), bob/bob (user) or carol/carol (no groups).

var builder = DistributedApplication.CreateBuilder(args);

var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../.data"));

var postgres = builder.AddPostgres("postgres")
    .WithDockerfile("../../docker/postgres")
    .WithDataVolume("dokumentsystem-postgres");
var database = postgres.AddDatabase("Dokument", databaseName: "dokument");

// The realm, client and test users are imported on every start, so the dev IdP needs no setup or volume.
var keycloak = builder.AddKeycloak("keycloak", port: 8180)
    .WithRealmImport("keycloak");

IResourceBuilder<ProjectResource> AddRole(string role) => builder.AddProject<Projects.Dokument_Host>(role)
    .WithEnvironment("ROLE", role)
    .WithEnvironment("Storage__OriginalsPath", Path.Combine(dataRoot, "originals"))
    .WithEnvironment("Storage__DerivedPath", Path.Combine(dataRoot, "derived"))
    .WithEnvironment("Storage__BackupsPath", Path.Combine(dataRoot, "backups"))
    .WithReference(database)
    .WaitFor(database);

var server = AddRole("server")
    .WithHttpHealthCheck("/health/ready")
    .WithEnvironment("OIDC_ISSUER", ReferenceExpression.Create($"{keycloak.GetEndpoint("http")}/realms/dokument"))
    .WithEnvironment("OIDC_CLIENT_ID", "dokument")
    .WithEnvironment("OIDC_CLIENT_SECRET", "dokument-dev-secret")
    .WithEnvironment("OIDC_PROVIDER_NAME", "Keycloak (dev)")
    .WaitFor(keycloak);
AddRole("worker").WithEndpointsInEnvironment(_ => false).WaitFor(server);

// A fixed port, because the dev realm's redirect URI names it.
var web = builder.AddViteApp("web", "../../apps/web")
    .WithPnpm()
    .WithEndpoint("http", endpoint => endpoint.Port = 5173)
    .WithReference(server)
    .WaitFor(server);

// The browser reaches the server through Vite's proxy, so that is the public URL for redirects and cookies.
server.WithEnvironment("PUBLIC_URL", web.GetEndpoint("http"));

builder.Build().Run();
