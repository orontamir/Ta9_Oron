using Microsoft.Extensions.Options;
using MongoDB.Driver;
using Ta9.JobService.Application;
using Ta9.JobService.Infrastructure.Storage;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Infrastructure.Storage.Mongo;
using Ta9.ServiceDefaults;

namespace Ta9.JobService;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the job storage. The provider (Mongo | InMemory) is read from options when the repositories are
    /// first resolved, so swapping storage is a configuration change; Mongo objects are never created in InMemory mode.
    /// </summary>
    public static IServiceCollection AddJobStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Mongo.ConnectionString) && !string.IsNullOrWhiteSpace(o.Mongo.Database),
                "Storage:Mongo:ConnectionString and Storage:Mongo:Database are required.")
            .ValidateOnStart();

        // Mongo (lazy singletons)
        services.AddSingleton<IMongoClient>(sp =>
        {
            var settings = MongoClientSettings.FromConnectionString(sp.GetRequiredService<IOptions<StorageOptions>>().Value.Mongo.ConnectionString);
            // Fail fast (→ 503) instead of the driver's 30s default when the database is down.
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(5);
            settings.ConnectTimeout = TimeSpan.FromSeconds(5);
            return new MongoClient(settings);
        });
        services.AddSingleton(sp => sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<StorageOptions>>().Value.Mongo.Database));
        services.AddSingleton<MongoJobRepository>();
        services.AddSingleton<MongoJobDocumentRepository>();

        // In-memory
        services.AddSingleton<InMemoryJobRepository>();
        services.AddSingleton<InMemoryJobDocumentRepository>();

        services.AddSingleton<IJobRepository>(sp => UseInMemory(sp)
            ? sp.GetRequiredService<InMemoryJobRepository>()
            : sp.GetRequiredService<MongoJobRepository>());
        services.AddSingleton<IJobDocumentRepository>(sp => UseInMemory(sp)
            ? sp.GetRequiredService<InMemoryJobDocumentRepository>()
            : sp.GetRequiredService<MongoJobDocumentRepository>());

        services.AddHostedService<MongoIndexInitializer>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>("storage", tags: [ServiceDefaultsExtensions.ReadyTag]);

        return services;
    }

    private static bool UseInMemory(IServiceProvider sp) =>
        sp.GetRequiredService<IOptions<StorageOptions>>().Value.Provider == StorageProvider.InMemory;
}
