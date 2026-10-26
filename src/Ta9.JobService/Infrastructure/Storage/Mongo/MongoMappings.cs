using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Infrastructure.Storage.Mongo;

/// <summary>
/// BSON mapping configured in code (no attributes on domain types). Registration is process-wide in the
/// driver, so it is guarded to run once even when several hosts start in the same test process.
/// </summary>
public static class MongoMappings
{
    private static int _registered;

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        TryRegisterSerializer(new UtcDateTimeOffsetSerializer());

        var conventions = new ConventionPack
        {
            new CamelCaseElementNameConvention(),
            new IgnoreExtraElementsConvention(true),
            new EnumRepresentationConvention(BsonType.String)
        };
        ConventionRegistry.Register("ta9-job-service", conventions, type => type.FullName?.StartsWith("Ta9.", StringComparison.Ordinal) == true);

        if (!BsonClassMap.IsClassMapRegistered(typeof(Job)))
        {
            BsonClassMap.RegisterClassMap<Job>(map =>
            {
                map.AutoMap();
                map.MapIdMember(job => job.Id);
            });
        }

        if (!BsonClassMap.IsClassMapRegistered(typeof(JobDocument)))
        {
            BsonClassMap.RegisterClassMap<JobDocument>(map =>
            {
                map.AutoMap();
                map.MapIdMember(document => document.Id);
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
            // Already registered by another component in this process.
        }
    }
}

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as a native BSON date (UTC, millisecond precision) so it can be
/// indexed, sorted and compared in queries. The default serializer stores an array that cannot.
/// </summary>
public sealed class UtcDateTimeOffsetSerializer : StructSerializerBase<DateTimeOffset>
{
    public override DateTimeOffset Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args) =>
        DateTimeOffset.FromUnixTimeMilliseconds(context.Reader.ReadDateTime());

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTimeOffset value) =>
        context.Writer.WriteDateTime(value.ToUnixTimeMilliseconds());
}
