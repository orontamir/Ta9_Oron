using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ta9.AuditService.Application;
using Ta9.AuditService.Infrastructure;
using Ta9.ServiceDefaults;

namespace Ta9.AuditService;

public static class AuditServiceCollectionExtensions
{
    /// <summary>Audit use-cases and storage (Mongo | InMemory, chosen lazily from Storage:Provider).</summary>
    public static IServiceCollection AddAudit(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(sp.GetRequiredService<IOptions<StorageOptions>>().Value.ConnectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<StorageOptions>>().Value.Database));
        services.AddSingleton<MongoAuditRepository>();
        services.AddSingleton<InMemoryAuditRepository>();
        services.AddSingleton<IAuditRepository>(sp =>
            sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider == StorageProvider.InMemory
                ? sp.GetRequiredService<InMemoryAuditRepository>()
                : sp.GetRequiredService<MongoAuditRepository>());

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<AuditLog>();
        services.AddHealthChecks().AddCheck<AuditStorageHealthCheck>("storage", tags: [ServiceDefaultsExtensions.ReadyTag]);

        return services;
    }
}
