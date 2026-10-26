using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Ta9.JobService.Infrastructure.Storage.Mongo;

namespace Ta9.JobService.Infrastructure.Storage;

/// <summary>Creates Mongo indexes at startup. A failure is logged, not fatal: readiness reports the database state.</summary>
public sealed class MongoIndexInitializer(
    IServiceProvider services,
    IOptions<StorageOptions> options,
    ILogger<MongoIndexInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Provider != StorageProvider.Mongo)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await services.GetRequiredService<MongoJobRepository>().EnsureIndexesAsync(timeout.Token);
            await services.GetRequiredService<MongoJobDocumentRepository>().EnsureIndexesAsync(timeout.Token);
            logger.LogInformation("MongoDB indexes ensured on database {Database}", options.Value.Mongo.Database);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not ensure MongoDB indexes; continuing without them");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class StorageHealthCheck(IServiceProvider services, IOptions<StorageOptions> options) : IHealthCheck
{
    private static readonly BsonDocumentCommand<BsonDocument> Ping = new(new BsonDocument("ping", 1));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (options.Value.Provider == StorageProvider.InMemory)
        {
            return HealthCheckResult.Healthy("In-memory storage");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await services.GetRequiredService<IMongoDatabase>().RunCommandAsync(Ping, cancellationToken: timeout.Token);
            return HealthCheckResult.Healthy("MongoDB reachable");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB unreachable", ex);
        }
    }
}
