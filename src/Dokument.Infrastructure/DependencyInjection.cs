using Dokument.Infrastructure.Identity;
using Dokument.Infrastructure.Persistence;
using Dokument.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Dokument.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));
        services.AddDbContext<DokumentDbContext>(options =>
            options.UseNpgsql(DatabaseOptions.GetConnectionString(configuration)));
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<UserProvisioner>();
        services.AddScoped<SessionStore>();
        return services;
    }
}
