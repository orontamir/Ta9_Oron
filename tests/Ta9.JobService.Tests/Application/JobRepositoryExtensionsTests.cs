using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Application;

public class JobRepositoryExtensionsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>Simulates another writer winning the race for the first N saves.</summary>
    private sealed class ConflictingRepository(IJobRepository inner, int conflicts) : IJobRepository
    {
        private int _remainingConflicts = conflicts;

        public Task InsertAsync(Job job, CancellationToken ct) => inner.InsertAsync(job, ct);
        public Task<Job?> GetAsync(Guid id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<PagedResult<Job>> ListAsync(JobStatus? status, int page, int pageSize, CancellationToken ct) => inner.ListAsync(status, page, pageSize, ct);
        public Task<bool> DeleteAsync(Guid id, CancellationToken ct) => inner.DeleteAsync(id, ct);
        public Task<Job?> TryClaimNextForDispatchAsync(DateTimeOffset now, DateTimeOffset leaseUntil, CancellationToken ct) =>
            inner.TryClaimNextForDispatchAsync(now, leaseUntil, ct);
        public Task<IReadOnlyList<Job>> FindStaleActiveAsync(DateTimeOffset updatedBefore, int limit, CancellationToken ct) =>
            inner.FindStaleActiveAsync(updatedBefore, limit, ct);
        public Task<IReadOnlyList<Job>> FindUnpublishedTerminalAsync(int limit, CancellationToken ct) =>
            inner.FindUnpublishedTerminalAsync(limit, ct);

        public Task<bool> TryUpdateAsync(Job job, CancellationToken ct) =>
            Interlocked.Decrement(ref _remainingConflicts) >= 0 ? Task.FromResult(false) : inner.TryUpdateAsync(job, ct);
    }

    private static async Task<(IJobRepository Repository, Job Job)> WithJobAsync(int conflicts = 0)
    {
        var inner = new InMemoryJobRepository();
        var job = TestJobs.New();
        await inner.InsertAsync(job, None);
        return (new ConflictingRepository(inner, conflicts), job);
    }

    [Fact]
    public async Task Applies_the_mutation_and_saves()
    {
        var (repository, job) = await WithJobAsync();

        var result = await repository.UpdateAsync(job.Id, j => j.TryMarkQueued(TestJobs.T0), None);

        result.Outcome.Should().Be(JobUpdateOutcome.Applied);
        result.Job!.Status.Should().Be(JobStatus.Queued);
        (await repository.GetAsync(job.Id, None))!.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Reports_not_found() =>
        (await new InMemoryJobRepository().UpdateAsync(Guid.NewGuid(), _ => true, None))
            .Outcome.Should().Be(JobUpdateOutcome.NotFound);

    [Fact]
    public async Task Does_not_write_when_the_mutation_declines()
    {
        var (repository, job) = await WithJobAsync();

        var result = await repository.UpdateAsync(job.Id, j => j.TryStart(TestJobs.T0), None); // Pending → Processing is illegal

        result.Outcome.Should().Be(JobUpdateOutcome.NotApplied);
        (await repository.GetAsync(job.Id, None))!.Version.Should().Be(0);
    }

    [Fact]
    public async Task Reloads_and_retries_on_conflict()
    {
        var (repository, job) = await WithJobAsync(conflicts: 2);
        var calls = 0;

        var result = await repository.UpdateAsync(job.Id, j => { calls++; return j.TryMarkQueued(TestJobs.T0); }, None);

        result.Outcome.Should().Be(JobUpdateOutcome.Applied);
        calls.Should().Be(3);
    }

    [Fact]
    public async Task Gives_up_after_max_attempts()
    {
        var (repository, job) = await WithJobAsync(conflicts: 10);

        var act = () => repository.UpdateAsync(job.Id, _ => true, None, maxAttempts: 3);

        await act.Should().ThrowAsync<JobConcurrencyException>();
    }
}
