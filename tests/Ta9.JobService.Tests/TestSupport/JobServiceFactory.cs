using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Tests.TestSupport;

/// <summary>Job Service host for API tests: in-memory storage, no external dependencies.</summary>
public sealed class JobServiceFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Storage:Provider", "InMemory");
    }

    public IJobRepository Jobs => Services.GetRequiredService<IJobRepository>();
    public IJobDocumentRepository Documents => Services.GetRequiredService<IJobDocumentRepository>();

    /// <summary>Simulates the processor finishing a job (what the WS event handler will do in step 7).</summary>
    public async Task CompleteAsync(Guid jobId, int documentCount)
    {
        var now = DateTimeOffset.UtcNow;
        await Jobs.UpdateAsync(jobId, job => job.TryMarkQueued(now) && job.TryComplete(TestJobs.Summary(), now), CancellationToken.None);
        var results = Enumerable.Range(0, documentCount).Select(i => TestJobs.Result(i, $"doc-{i}.txt")).ToList();
        await Documents.SaveResultsAsync(jobId, results, CancellationToken.None);
    }

    public async Task SetStatusAsync(Guid jobId, Func<Job, bool> mutate) =>
        await Jobs.UpdateAsync(jobId, mutate, CancellationToken.None);
}
