using Ta9.JobService.Domain;

namespace Ta9.JobService.Application;

public enum JobUpdateOutcome
{
    Applied,
    NotFound,

    /// <summary>The mutation declined (e.g. illegal transition); nothing was written.</summary>
    NotApplied
}

public sealed record JobUpdateResult(JobUpdateOutcome Outcome, Job? Job);

public sealed class JobConcurrencyException(Guid jobId, int attempts)
    : Exception($"Job {jobId} could not be updated after {attempts} attempts due to concurrent modifications.");

public static class JobRepositoryExtensions
{
    /// <summary>
    /// Load → mutate → save with optimistic concurrency, retrying on conflicts.
    /// <paramref name="mutate"/> returns false to abort without writing (e.g. the transition is not allowed).
    /// It may run several times, so it must only touch the job it receives.
    /// </summary>
    public static async Task<JobUpdateResult> UpdateAsync(
        this IJobRepository repository,
        Guid jobId,
        Func<Job, bool> mutate,
        CancellationToken cancellationToken,
        int maxAttempts = 5)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var job = await repository.GetAsync(jobId, cancellationToken);
            if (job is null)
            {
                return new JobUpdateResult(JobUpdateOutcome.NotFound, null);
            }

            if (!mutate(job))
            {
                return new JobUpdateResult(JobUpdateOutcome.NotApplied, job);
            }

            if (await repository.TryUpdateAsync(job, cancellationToken))
            {
                return new JobUpdateResult(JobUpdateOutcome.Applied, job);
            }
        }

        throw new JobConcurrencyException(jobId, maxAttempts);
    }
}
