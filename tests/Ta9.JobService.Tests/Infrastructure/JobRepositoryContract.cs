using Ta9.JobService.Application;
using Ta9.JobService.Domain;
using Ta9.JobService.Infrastructure.Storage.InMemory;
using Ta9.JobService.Infrastructure.Storage.Mongo;
using Ta9.JobService.Tests.TestSupport;

namespace Ta9.JobService.Tests.Infrastructure;

/// <summary>
/// Behavior every <see cref="IJobRepository"/> must have. Runs against the in-memory and the Mongo
/// implementation: the proof that storage is swappable.
/// </summary>
public abstract class JobRepositoryContract
{
    protected abstract IJobRepository Jobs { get; }

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Insert_then_get_round_trips_every_field()
    {
        var job = TestJobs.InStatus(JobStatus.Completed);
        job.FailureReason = "none";
        job.DispatchAttempts = 2;
        job.RedispatchCount = 1;
        job.LeaseUntil = TestJobs.T0.AddSeconds(5);

        await Jobs.InsertAsync(job, None);
        var loaded = await Jobs.GetAsync(job.Id, None);

        loaded.Should().BeEquivalentTo(job);
    }

    [Fact]
    public async Task Get_unknown_job_returns_null() =>
        (await Jobs.GetAsync(Guid.NewGuid(), None)).Should().BeNull();

    [Fact]
    public async Task Update_persists_changes_and_increments_version()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);

        job.TryMarkQueued(TestJobs.T0.AddSeconds(1));
        (await Jobs.TryUpdateAsync(job, None)).Should().BeTrue();

        job.Version.Should().Be(1);
        var loaded = await Jobs.GetAsync(job.Id, None);
        loaded!.Status.Should().Be(JobStatus.Queued);
        loaded.Version.Should().Be(1);
    }

    [Fact]
    public async Task Update_with_a_stale_version_is_rejected()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);
        var first = (await Jobs.GetAsync(job.Id, None))!;
        var second = (await Jobs.GetAsync(job.Id, None))!;

        first.TryMarkQueued(TestJobs.T0);
        (await Jobs.TryUpdateAsync(first, None)).Should().BeTrue();

        second.TryCancel(TestJobs.T0);
        (await Jobs.TryUpdateAsync(second, None)).Should().BeFalse("someone else updated the job in between");
        second.Version.Should().Be(0, "a rejected update leaves the token unchanged");

        (await Jobs.GetAsync(job.Id, None))!.Status.Should().Be(JobStatus.Queued);
    }

    [Fact]
    public async Task Changes_to_a_loaded_job_are_not_visible_until_saved()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);

        var loaded = (await Jobs.GetAsync(job.Id, None))!;
        loaded.TryCancel(TestJobs.T0);

        (await Jobs.GetAsync(job.Id, None))!.Status.Should().Be(JobStatus.Pending);
    }

    [Fact]
    public async Task Concurrent_updates_never_lose_a_write()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() =>
            Jobs.UpdateAsync(job.Id, j => { j.DispatchAttempts++; return true; }, None, maxAttempts: 100))));

        var loaded = (await Jobs.GetAsync(job.Id, None))!;
        loaded.DispatchAttempts.Should().Be(20);
        loaded.Version.Should().Be(20);
    }

    [Fact]
    public async Task List_filters_orders_newest_first_and_pages()
    {
        var created = new List<Job>();
        for (var i = 0; i < 5; i++)
        {
            var status = i % 2 == 0 ? JobStatus.Pending : JobStatus.Completed;
            var job = TestJobs.InStatus(status, TestJobs.T0.AddMinutes(i));
            await Jobs.InsertAsync(job, None);
            created.Add(job);
        }

        var firstPage = await Jobs.ListAsync(status: null, page: 1, pageSize: 2, None);
        firstPage.TotalCount.Should().BeGreaterThanOrEqualTo(5);
        firstPage.Items.Select(j => j.Id).Should().Equal(created[4].Id, created[3].Id);

        var pending = await Jobs.ListAsync(JobStatus.Pending, page: 1, pageSize: 10, None);
        pending.Items.Should().OnlyContain(j => j.Status == JobStatus.Pending);
        pending.Items.Select(j => j.Id).Should().ContainInOrder(created[4].Id, created[2].Id, created[0].Id);
    }

    [Fact]
    public async Task Delete_removes_the_job_once()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);

        (await Jobs.DeleteAsync(job.Id, None)).Should().BeTrue();
        (await Jobs.DeleteAsync(job.Id, None)).Should().BeFalse();
        (await Jobs.GetAsync(job.Id, None)).Should().BeNull();
    }
}

public sealed class InMemoryJobRepositoryTests : JobRepositoryContract
{
    protected override IJobRepository Jobs { get; } = new InMemoryJobRepository();
}

[Collection(MongoCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MongoJobRepositoryTests(MongoFixture mongo) : JobRepositoryContract
{
    protected override IJobRepository Jobs { get; } = new MongoJobRepository(mongo.CreateDatabase());
}
