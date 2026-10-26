using MongoDB.Bson;
using MongoDB.Driver;
using Ta9.ServiceDefaults.Logging;
using Testcontainers.MongoDb;
using static Ta9.ServiceDefaults.Tests.Logging.LogTestData;

namespace Ta9.ServiceDefaults.Tests.Logging;

public sealed class LogMongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

/// <summary>The real MongoDB writer against a MongoDB container (Docker).</summary>
[Trait("Category", "Integration")]
public class MongoLogBatchWriterTests(LogMongoFixture mongo) : IClassFixture<LogMongoFixture>
{
    private MongoLogOptions NewOptions(int retentionDays = 7) => new()
    {
        ConnectionString = mongo.ConnectionString,
        Database = $"logs_{Guid.NewGuid():N}",
        RetentionDays = retentionDays
    };

    private IMongoCollection<BsonDocument> Collection(MongoLogOptions options) =>
        new MongoClient(options.ConnectionString).GetDatabase(options.Database).GetCollection<BsonDocument>(options.Collection);

    [Fact]
    public async Task Stores_records_that_can_be_queried_by_job_id()
    {
        var options = NewOptions();
        var jobId = Guid.NewGuid();
        var records = new[]
        {
            LogRecord.From(Event("Job started", properties: [("JobId", jobId)]), "processor-service", "h"),
            LogRecord.From(Event("Other job", properties: [("JobId", Guid.NewGuid())]), "processor-service", "h"),
            LogRecord.From(Event("Job completed", properties: [("JobId", jobId)]), "job-service", "h")
        };

        await new MongoLogBatchWriter(options).WriteAsync(records, CancellationToken.None);

        var stored = await Collection(options).Find(Builders<BsonDocument>.Filter.Eq("jobId", jobId.ToString()))
            .Sort(Builders<BsonDocument>.Sort.Ascending("timestamp")).ToListAsync();
        stored.Select(d => d["message"].AsString).Should().Equal("Job started", "Job completed");
        stored.Select(d => d["service"].AsString).Should().Equal("processor-service", "job-service");
    }

    [Fact]
    public async Task Creates_the_retention_ttl_index_and_the_lookup_indexes()
    {
        var options = NewOptions(retentionDays: 7);

        await new MongoLogBatchWriter(options).WriteAsync([LogRecord.From(Event("x"), "svc", "h")], CancellationToken.None);

        var indexes = await (await Collection(options).Indexes.ListAsync()).ToListAsync();
        var ttl = indexes.Single(i => i["name"] == MongoLogBatchWriter.TtlIndexName);
        ttl["expireAfterSeconds"].ToInt64().Should().Be((long)TimeSpan.FromDays(7).TotalSeconds);
        indexes.Select(i => i["key"].AsBsonDocument.Names.First()).Should().Contain(["jobId", "correlationId", "level", "service"]);
    }

    [Fact]
    public async Task Changing_the_retention_updates_the_existing_ttl_index()
    {
        var options = NewOptions(retentionDays: 7);
        await new MongoLogBatchWriter(options).WriteAsync([LogRecord.From(Event("x"), "svc", "h")], CancellationToken.None);

        options.RetentionDays = 30;
        await new MongoLogBatchWriter(options).WriteAsync([LogRecord.From(Event("y"), "svc", "h")], CancellationToken.None);

        var indexes = await (await Collection(options).Indexes.ListAsync()).ToListAsync();
        indexes.Single(i => i["name"] == MongoLogBatchWriter.TtlIndexName)["expireAfterSeconds"].ToInt64()
            .Should().Be((long)TimeSpan.FromDays(30).TotalSeconds);
    }

    [Fact]
    public async Task Unreachable_database_fails_fast()
    {
        var options = NewOptions();
        options.ConnectionString = "mongodb://127.0.0.1:1";
        var started = DateTime.UtcNow;

        var write = () => new MongoLogBatchWriter(options).WriteAsync([LogRecord.From(Event("x"), "svc", "h")], CancellationToken.None);

        await write.Should().ThrowAsync<TimeoutException>();
        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(15), "server selection is limited to 3 s");
    }
}
