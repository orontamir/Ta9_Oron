using Ta9.ProcessorService.Domain;
using Testcontainers.MongoDb;

namespace Ta9.IntegrationTests.Infrastructure;

/// <summary>One MongoDB container (Docker) shared by all end-to-end tests; each system gets its own databases.</summary>
public sealed class MongoContainerFixture : IAsyncLifetime
{
    private readonly MongoDbContainer _container = new MongoDbBuilder().WithImage("mongo:7.0").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition(EndToEndCollection.Name)]
public sealed class EndToEndCollection : ICollectionFixture<MongoContainerFixture>
{
    public const string Name = "end-to-end";
}

/// <summary>Real analyzer, plus the maximum number of documents analyzed at the same time across the whole processor.</summary>
public sealed class ConcurrencyTrackingAnalyzer : IDocumentAnalyzer
{
    private readonly DocumentAnalyzer _inner = new();
    private int _current;
    private int _max;
    private int _calls;

    public int MaxConcurrency => Volatile.Read(ref _max);
    public int Calls => Volatile.Read(ref _calls);

    public DocumentAnalysis Analyze(string content)
    {
        var now = Interlocked.Increment(ref _current);
        int snapshot;
        do
        {
            snapshot = Volatile.Read(ref _max);
            if (now <= snapshot)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _max, now, snapshot) != snapshot);

        try
        {
            return _inner.Analyze(content);
        }
        finally
        {
            Interlocked.Decrement(ref _current);
            Interlocked.Increment(ref _calls);
        }
    }
}

/// <summary>Blocks every analysis until the test opens the gate (or a safety timeout elapses).</summary>
public sealed class GatedAnalyzer(ManualResetEventSlim gate) : IDocumentAnalyzer
{
    private readonly DocumentAnalyzer _inner = new();

    public DocumentAnalysis Analyze(string content)
    {
        gate.Wait(TimeSpan.FromSeconds(60));
        return _inner.Analyze(content);
    }
}
