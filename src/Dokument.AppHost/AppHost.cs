// Local development: `dotnet run --project src/Dokument.AppHost` starts Postgres (our image), the server,
// a worker and the Vite dev server, and opens the Aspire dashboard. See docs/design.md §3.2.

var builder = DistributedApplication.CreateBuilder(args);

var dataRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../.data"));

var postgres = builder.AddPostgres("postgres")
    .WithDockerfile("../../docker/postgres")
    .WithDataVolume("dokumentsystem-postgres");
var database = postgres.AddDatabase("Dokument", databaseName: "dokument");

IResourceBuilder<ProjectResource> AddRole(string role) => builder.AddProject<Projects.Dokument_Host>(role)
    .WithEnvironment("ROLE", role)
    .WithEnvironment("Storage__OriginalsPath", Path.Combine(dataRoot, "originals"))
    .WithEnvironment("Storage__DerivedPath", Path.Combine(dataRoot, "derived"))
    .WithEnvironment("Storage__BackupsPath", Path.Combine(dataRoot, "backups"))
    .WithReference(database)
    .WaitFor(database);

var server = AddRole("server").WithHttpHealthCheck("/health/ready");
AddRole("worker").WithEndpointsInEnvironment(_ => false).WaitFor(server);

builder.AddViteApp("web", "../../apps/web")
    .WithPnpm()
    .WithReference(server)
    .WaitFor(server);

builder.Build().Run();
