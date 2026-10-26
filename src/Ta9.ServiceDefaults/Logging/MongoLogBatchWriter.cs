using MongoDB.Bson;
using MongoDB.Driver;

namespace Ta9.ServiceDefaults.Logging;

/// <summary>
/// Writes log records to <c>{Database}.{Collection}</c> as plain BSON documents (no class maps, so it cannot clash
/// with a service's own Mongo conventions). Indexes, including the retention TTL, are ensured on the first write.
/// </summary>
public sealed class MongoLogBatchWriter : ILogBatchWriter
{
    public const string TtlIndexName = "ttl_timestamp";

    private readonly IMongoDatabase _database;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly MongoLogOptions _options;
    private readonly SemaphoreSlim _indexGate = new(1, 1);
    private volatile bool _indexesEnsured;

    public MongoLogBatchWriter(MongoLogOptions options)
    {
        _options = options;
        var settings = MongoClientSettings.FromConnectionString(options.ConnectionString);
        // Logging must not wait 30s (driver default) to find out the database is down.
        settings.ServerSelectionTimeout = TimeSpan.FromSeconds(3);
        settings.ConnectTimeout = TimeSpan.FromSeconds(3);
        _database = new MongoClient(settings).GetDatabase(options.Database);
        _collection = _database.GetCollection<BsonDocument>(options.Collection);
    }

    public async Task WriteAsync(IReadOnlyList<LogRecord> batch, CancellationToken cancellationToken)
    {
        if (!_indexesEnsured)
        {
            await EnsureIndexesAsync(cancellationToken).ConfigureAwait(false);
        }

        await _collection.InsertManyAsync(batch.Select(ToBson), new InsertManyOptions { IsOrdered = false }, cancellationToken)
            .ConfigureAwait(false);
    }

    public static BsonDocument ToBson(LogRecord record)
    {
        var document = new BsonDocument
        {
            ["timestamp"] = new BsonDateTime(record.Timestamp.UtcDateTime),
            ["level"] = record.Level,
            ["service"] = record.Service,
            ["host"] = record.Host,
            ["message"] = record.Message,
            ["messageTemplate"] = record.MessageTemplate
        };
        AddIfPresent(document, "sourceContext", record.SourceContext);
        AddIfPresent(document, "correlationId", record.CorrelationId);
        AddIfPresent(document, "jobId", record.JobId);
        AddIfPresent(document, "exception", record.Exception);
        if (record.Properties.Count > 0)
        {
            var properties = new BsonDocument();
            foreach (var (name, value) in record.Properties)
            {
                // '$' and '.' are not allowed in field names.
                properties[name.Replace('.', '_').Replace('$', '_')] = value;
            }

            document["properties"] = properties;
        }

        return document;
    }

    private async Task EnsureIndexesAsync(CancellationToken cancellationToken)
    {
        await _indexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_indexesEnsured)
            {
                return;
            }

            var keys = Builders<BsonDocument>.IndexKeys;
            await EnsureTtlIndexAsync(cancellationToken).ConfigureAwait(false);
            await _collection.Indexes.CreateManyAsync(
            [
                // Follow one job / one request across the three services.
                new CreateIndexModel<BsonDocument>(keys.Ascending("jobId").Ascending("timestamp")),
                new CreateIndexModel<BsonDocument>(keys.Ascending("correlationId").Ascending("timestamp")),
                // "Latest errors", "latest logs of the processor".
                new CreateIndexModel<BsonDocument>(keys.Ascending("level").Descending("timestamp")),
                new CreateIndexModel<BsonDocument>(keys.Ascending("service").Descending("timestamp"))
            ], cancellationToken).ConfigureAwait(false);
            _indexesEnsured = true;
        }
        finally
        {
            _indexGate.Release();
        }
    }

    private async Task EnsureTtlIndexAsync(CancellationToken cancellationToken)
    {
        var expireAfter = TimeSpan.FromDays(Math.Max(1, _options.RetentionDays));
        try
        {
            await _collection.Indexes.CreateOneAsync(
                new CreateIndexModel<BsonDocument>(
                    Builders<BsonDocument>.IndexKeys.Ascending("timestamp"),
                    new CreateIndexOptions { Name = TtlIndexName, ExpireAfter = expireAfter }),
                options: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (MongoCommandException ex) when (ex.CodeName is "IndexOptionsConflict" or "IndexKeySpecsConflict")
        {
            // The retention was changed since the index was created: update it in place.
            var command = new BsonDocument
            {
                ["collMod"] = _options.Collection,
                ["index"] = new BsonDocument { ["name"] = TtlIndexName, ["expireAfterSeconds"] = (long)expireAfter.TotalSeconds }
            };
            await _database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddIfPresent(BsonDocument document, string name, string? value)
    {
        if (value is not null)
        {
            document[name] = value;
        }
    }
}
