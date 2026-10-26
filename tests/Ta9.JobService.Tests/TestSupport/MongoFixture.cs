using MongoDB.Driver;
using Ta9.JobService.Infrastructure.Storage.Mongo;
using Testcontainers.MongoDb;

namespace Ta9.JobService.Tests.TestSupport;

/// <summary>
/// One real MongoDB container (Docker) shared by all Mongo tests; each test gets its own database.
/// Tests using it are tagged <c>Category=Integration</c> so they can be excluded where Docker is unavailable:
/// <c>dotnet test --filter Category!=Integration</c>.
/// </summary>
public sealed class MongoFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public string ConnectionString => _container.GetConnectionString();

    public IMongoDatabase CreateDatabase()
    {
        MongoMappings.Register();
        return new MongoClient(ConnectionString).GetDatabase($"test_{Guid.NewGuid():N}");
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(MongoCollection.Name)]
public sealed class MongoCollection : ICollectionFixture<MongoFixture>
{
    public const string Name = "mongo";
}
