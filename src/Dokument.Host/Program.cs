using Dokument.Host;
using Dokument.Infrastructure;
using Dokument.Infrastructure.Persistence;
using Dokument.Server;
using Dokument.Worker;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

if (args is ["healthcheck", ..])
{
    return await HealthCheckCommand.RunAsync();
}

var roles = HostRoles.Parse(Environment.GetEnvironmentVariable("ROLE"));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSerilog((services, logger) => logger
    .ReadFrom.Configuration(builder.Configuration)
    .ReadFrom.Services(services)
    .Enrich.WithProperty("Role", roles.ToString())
    .WriteTo.Console());

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<DokumentDbContext>(tags: ["ready"])
    .AddCheck<StorageHealthCheck>("storage", tags: ["ready"]);

if (roles.Server)
{
    builder.AddServerRole();
}

if (roles.Worker)
{
    builder.Services.AddWorkerRole();
}

var app = builder.Build();

// The build-time OpenAPI generator starts the app without a database.
if (System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name != "GetDocument.Insider")
{
    await DatabaseStartup.EnsureDatabaseAsync(app.Services, applyMigrations: roles.Server);
}

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });

if (roles.Server)
{
    app.MapServerRole();
}

await app.RunAsync();
return 0;

public partial class Program;
