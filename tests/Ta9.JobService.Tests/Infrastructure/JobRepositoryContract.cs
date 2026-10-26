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
    public async Task Claim_leases_the_due_pending_job_until_the_lease_expires()
    {
        var job = TestJobs.New();
        await Jobs.InsertAsync(job, None);
        var leaseUntil = TestJobs.T0.AddMinutes(1);

        var claimed = await Jobs.TryClaimNextForDispatchAsync(TestJobs.T0, leaseUntil, None);

        claimed!.Id.Should().Be(job.Id);
        claimed.LeaseUntil.Should().Be(leaseUntil);
        claimed.Version.Should().Be(1, "claiming is a write; stale copies must not overwrite it");
        (await Jobs.TryClaimNextForDispatchAsync(TestJobs.T0.AddSeconds(30), leaseUntil, None)).Should().BeNull("the lease is still live");
        (await Jobs.TryClaimNextForDispatchAsync(leaseUntil, leaseUntil.AddMinutes(1), None))!.Id.Should().Be(job.Id, "an expired lease can be reclaimed");
    }

    [Fact]
    public async Task Claim_skips_jobs_that_are_not_due_or_not_pending()
    {
        await Jobs.InsertAsync(TestJobs.New(createdAt: TestJobs.T0.AddMinutes(5)), None); // due in 5 minutes
        await Jobs.InsertAsync(TestJobs.InStatus(JobStatus.Queued), None);
        await Jobs.InsertAsync(TestJobs.InStatus(JobStatus.Cancelled), None);

        (await Jobs.TryClaimNextForDispatchAsync(TestJobs.T0, TestJobs.T0.AddMinutes(1), None)).Should().BeNull();
    }

    [Fact]
    public async Task Claim_takes_the_longest_waiting_job_first()
    {
        var newer = TestJobs.New(createdAt: TestJobs.T0.AddSeconds(10));
        var older = TestJobs.New(createdAt: TestJobs.T0);
        await Jobs.InsertAsync(newer, None);
        await Jobs.InsertAsync(older, None);

        var now = TestJobs.T0.AddMinutes(1);
        (await Jobs.TryClaimNextForDispatchAsync(now, now.AddMinutes(1), None))!.Id.Should().Be(older.Id);
        (await Jobs.TryClaimNextForDispatchAsync(now, now.AddMinutes(1), None))!.Id.Should().Be(newer.Id);
    }

    [Fact]
    public async Task Concurrent_claims_never_hand_out_the_same_job_twice()
    {
        for (var i = 0; i < 5; i++)
        {
            await Jobs.InsertAsync(TestJobs.New(), None);
        }

        var now = TestJobs.T0.AddMinutes(1);
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => Jobs.TryClaimNextForDispatchAsync(now, now.AddMinutes(1), None))));

        var claimed = claims.Where(j => j is not null).Select(j => j!.Id).ToList();
        claimed.Should().HaveCount(5).And.OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Stale_query_returns_only_silent_active_jobs_oldest_first()
    {
        var oldProcessing = TestJobs.InStatus(JobStatus.Processing, TestJobs.T0);
        var olderQueued = TestJobs.InStatus(JobStatus.Queued, TestJobs.T0.AddSeconds(-30));
        var recentProcessing = TestJobs.InStatus(JobStatus.Processing, TestJobs.T0.AddMinutes(5));
        var oldPending = TestJobs.InStatus(JobStatus.Pending, TestJobs.T0);
        var oldCompleted = TestJobs.InStatus(JobStatus.Completed, TestJobs.T0);
        foreach (var job in new[] { oldProcessing, olderQueued, recentProcessing, oldPending, oldCompleted })
        {
            await Jobs.InsertAsync(job, None);
        }

        var stale = await Jobs.FindStaleActiveAsync(TestJobs.T0.AddMinutes(1), limit: 10, None);

        stale.Select(j => j.Id).Should().Equal(olderQueued.Id, oldProcessing.Id);
        (await Jobs.FindStaleActiveAsync(TestJobs.T0.AddMinutes(1), limit: 1, None)).Should().ContainSingle();
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
