using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Ta9.Contracts.Processing;
using Ta9.ProcessorService.Application;

namespace Ta9.ProcessorService.Api;

[ApiController]
[Route("api/process")]
[Produces("application/json")]
public sealed class ProcessController(ProcessingService processing, IOptions<ProcessingOptions> options) : ControllerBase
{
    /// <summary>Enqueues a job for processing. Idempotent by jobId.</summary>
    [HttpPost]
    [ProducesResponseType<ProcessJobStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProcessJobStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public IActionResult Submit([FromBody] ProcessJobRequest request)
    {
        var errors = ProcessJobRequestValidator.Validate(request);
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

        var result = processing.Submit(request);
        switch (result.Outcome)
        {
            case SubmitOutcome.Accepted:
                return AcceptedAtAction(nameof(Get), new { jobId = request.JobId }, result.Job!.ToResponse(includeResults: false));
            case SubmitOutcome.AlreadyExists:
                return Ok(result.Job!.ToResponse(includeResults: false));
            default:
                Response.Headers.RetryAfter = options.Value.RetryAfterSeconds.ToString();
                return Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Processor is at capacity",
                    detail: "The processing queue is full. Retry later.");
        }
    }

    /// <summary>Returns the job status; results are included once the job has completed.</summary>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType<ProcessJobStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Get(Guid jobId)
    {
        var job = processing.Get(jobId);
        return job is null ? NotFoundProblem(jobId) : Ok(job.ToResponse(includeResults: true));
    }

    /// <summary>Lists jobs known to this processor instance (diagnostics; results omitted).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ProcessJobStatusResponse>>(StatusCodes.Status200OK)]
    public IActionResult List() =>
        Ok(processing.List()
            .OrderByDescending(job => job.AcceptedAt)
            .Select(job => job.ToResponse(includeResults: false))
            .ToList());

    /// <summary>Cancels a queued or running job.</summary>
    [HttpDelete("{jobId:guid}")]
    [ProducesResponseType<ProcessJobStatusResponse>(StatusCodes.Status202Accepted)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public IActionResult Cancel(Guid jobId)
    {
        var (outcome, job) = processing.Cancel(jobId);
        return outcome switch
        {
            CancelOutcome.Cancelled => Accepted(job!.ToResponse(includeResults: false)),
            CancelOutcome.NotFound => NotFoundProblem(jobId),
            _ => Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Job already finished",
                detail: $"Job {jobId} is {job!.Status} and can no longer be cancelled.")
        };
    }

    private ObjectResult NotFoundProblem(Guid jobId) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Job not found", detail: $"Job {jobId} is not known to this processor.");
}
