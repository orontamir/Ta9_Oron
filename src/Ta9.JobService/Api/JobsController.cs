using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Ta9.JobService.Application;
using Ta9.JobService.Domain;

namespace Ta9.JobService.Api;

[ApiController]
[Route("api/jobs")]
public sealed class JobsController(JobManager jobs, JobEventStream eventStream) : ControllerBase
{
    public const long MaxRequestBytes = 10 * 1024 * 1024;

    /// <summary>Submits a job. It is processed asynchronously; poll GET /api/jobs/{id} or subscribe to its events.</summary>
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [ProducesResponseType<JobResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Create([FromBody] CreateJobRequest request, CancellationToken cancellationToken)
    {
        var errors = CreateJobRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            foreach (var (field, messages) in errors)
            {
                foreach (var message in messages)
                {
                    ModelState.AddModelError(field, message);
                }
            }
            return ValidationProblem(ModelState);
        }

        var job = await jobs.CreateAsync(request.ToCommand(), cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = job.Id }, JobResponse.From(job));
    }

    /// <summary>Lists jobs, newest first.</summary>
    [HttpGet]
    [ProducesResponseType<PagedResponse<JobResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] JobStatus? status,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 100)] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var result = await jobs.ListAsync(status, page, pageSize, cancellationToken);
        return Ok(PagedResponse<JobResponse>.From(result, JobResponse.From));
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<JobResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken);
        return job is null ? JobNotFound(id) : Ok(JobResponse.From(job));
    }

    /// <summary>Per-document results of a completed job, ordered by document index.</summary>
    [HttpGet("{id:guid}/results")]
    [ProducesResponseType<PagedResponse<JobDocumentResult>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> GetResults(
        Guid id,
        [FromQuery, Range(1, int.MaxValue)] int page = 1,
        [FromQuery, Range(1, 200)] int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        var results = await jobs.GetResultsAsync(id, page, pageSize, cancellationToken);
        return results.Outcome switch
        {
            ResultsOutcome.Found => Ok(PagedResponse<JobDocumentResult>.From(results.Page!, r => r)),
            ResultsOutcome.NotFound => JobNotFound(id),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Results not available",
                detail: $"Job {id} is {results.Job!.Status}; results are available once it is Completed.")
        };
    }

    /// <summary>
    /// Server-Sent Events: the current state immediately, then every change (status, progress) until the job
    /// finishes. Try it with <c>curl -N http://localhost:5001/api/jobs/{id}/events</c>.
    /// </summary>
    [HttpGet("{id:guid}/events")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Events(Guid id, CancellationToken cancellationToken)
    {
        var found = await eventStream.StreamAsync(id, Response, cancellationToken);
        return found ? new EmptyResult() : JobNotFound(id);
    }

    /// <summary>Cancels a pending, queued or processing job. Idempotent.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType<JobResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var result = await jobs.CancelAsync(id, cancellationToken);
        return result.Outcome switch
        {
            CancelJobOutcome.Cancelled => Accepted(JobResponse.From(result.Job!)),
            CancelJobOutcome.NotFound => JobNotFound(id),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job already finished",
                detail: $"Job {id} is {result.Job!.Status} and can no longer be cancelled.")
        };
    }

    /// <summary>Deletes a finished (completed, failed or cancelled) job and its documents.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var outcome = await jobs.DeleteAsync(id, cancellationToken);
        return outcome switch
        {
            DeleteJobOutcome.Deleted => NoContent(),
            DeleteJobOutcome.NotFound => JobNotFound(id),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job is still active",
                detail: $"Job {id} is still being processed. Cancel it first.")
        };
    }

    private ObjectResult JobNotFound(Guid id) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found", detail: $"Job {id} does not exist.");
}
