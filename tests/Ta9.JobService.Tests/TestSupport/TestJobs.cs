using Ta9.Contracts.Processing;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Tests.TestSupport;

public static class TestJobs
{
    /// <summary>Millisecond precision, UTC: survives a BSON date round trip unchanged.</summary>
    public static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero).AddMilliseconds(123);

    public static Job New(int totalDocuments = 3, DateTimeOffset? createdAt = null, string? name = "test-job") =>
        Job.Create(Guid.NewGuid(), name, totalDocuments, correlationId: "corr-1", createdAt ?? T0);

    public static Job InStatus(JobStatus status, DateTimeOffset? createdAt = null)
    {
        var job = New(createdAt: createdAt);
        var now = createdAt ?? T0;
        switch (status)
        {
            case JobStatus.Pending:
                break;
            case JobStatus.Queued:
                job.TryMarkQueued(now);
                break;
            case JobStatus.Processing:
                job.TryMarkQueued(now);
                job.TryStart(now);
                break;
            case JobStatus.Completed:
                job.TryMarkQueued(now);
                job.TryComplete(Summary(), now);
                break;
            case JobStatus.Failed:
                job.TryFail("boom", now);
                break;
            case JobStatus.Cancelled:
                job.TryCancel(now);
                break;
        }

        job.Status.Should().Be(status);
        return job;
    }

    public static ProcessingSummaryDto Summary() => new(3, 2, 1, 42, 15);

    public static DocumentResultDto Result(int index, string name = "doc.txt") => new(
        index, name, DocumentStatus.Succeeded, 3, 2,
        [new WordCountDto("hello", 2), new WordCountDto("world", 1)],
        "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9",
        17, 1, 5.0, "hello", null);
}
