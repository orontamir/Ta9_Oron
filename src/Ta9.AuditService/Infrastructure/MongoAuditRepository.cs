using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Ta9.AuditService.Application;
using Ta9.AuditService.Domain;

namespace Ta9.AuditService.Infrastructure;

public enum StorageProvider
{
    Mongo,
    InMemory
}

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public StorageProvider Provider { get; set; } = StorageProvider.Mongo;

    [Required]
    public string ConnectionString { get; set; } = "mongodb://localhost:27017";

    [Required]
    public string Database { get; set; } = "ta9_audit";
}

public sealed class MongoAuditRepository : IAuditRepository
{
    public const string CollectionName = "audit_entries";

    private readonly IMongoCollection<AuditEntry> _entries;

    public MongoAuditRepository(IMongoDatabase database)
    {
        AuditMongoMappings.Register();
        _entries = database.GetCollection<AuditEntry>(CollectionName);
    }

    public async Task<bool> TryInsertAsync(AuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _entries.InsertOneAsync(entry, options: null, cancellationToken);
            return true;
        }
        catch (MongoWriteException ex) when (ex.WriteError.Category == ServerErrorCategory.DuplicateKey)
        {
            return false; // the job id is the _id: first write wins, also under concurrent posts
        }
    }

    public async Task<AuditEntry?> GetAsync(Guid jobId, CancellationToken cancellationToken) =>
        await _entries.Find(entry => entry.JobId == jobId).FirstOrDefaultAsync(cancellationToken);
}

/// <summary>Same BSON conventions as the Job Service (each service owns its persistence code).</summary>
public static class AuditMongoMappings
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        TryRegisterSerializer(new GuidSerializer(BsonType.String));
        TryRegisterSerializer(new UtcDateTimeOffsetSerializer());
        ConventionRegistry.Register(
            "ta9-audit-service",
            new ConventionPack { new CamelCaseElementNameConvention(), new IgnoreExtraElementsConvention(true) },
            type => type.FullName?.StartsWith("Ta9.AuditService.", StringComparison.Ordinal) == true);

        if (!BsonClassMap.IsClassMapRegistered(typeof(AuditEntry)))
        {
            BsonClassMap.RegisterClassMap<AuditEntry>(map =>
            {
                map.AutoMap();
                map.MapIdMember(entry => entry.JobId).SetSerializer(new GuidSerializer(BsonType.String));
            });
        }
    }

    private static void TryRegisterSerializer<T>(IBsonSerializer<T> serializer)
    {
        try
        {
            BsonSerializer.RegisterSerializer(serializer);
        }
        catch (BsonSerializationException)
        {
            // Already registered in this process (e.g. by the Job Service in integration tests).
        }
    }

    private sealed class UtcDateTimeOffsetSerializer : StructSerializerBase<DateTimeOffset>
    {
        public override DateTimeOffset Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
            DateTimeOffset.FromUnixTimeMilliseconds(context.Reader.ReadDateTime());

        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTimeOffset value) =>
            context.Writer.WriteDateTime(value.ToUnixTimeMilliseconds());
    }
}

public sealed class AuditStorageHealthCheck(IServiceProvider services, IOptions<StorageOptions> options) : IHealthCheck
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
