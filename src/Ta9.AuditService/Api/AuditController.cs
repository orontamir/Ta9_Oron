using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Ta9.AuditService.Application;
using Ta9.Contracts.Audit;
using Ta9.ServiceDefaults.Security;

namespace Ta9.AuditService.Api;

[ApiController]
[Route("api/audit")]
public sealed class AuditController(AuditLog audit) : ControllerBase
{
    /// <summary>Records the summary of a finished job. Idempotent: 201 the first time, 200 with the original entry afterwards.</summary>
    [HttpPost]
    [Authorize(Policy = AuthPolicies.Service)]
    [ProducesResponseType<JobAuditResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<JobAuditResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Record([FromBody] JobAuditRequest request, CancellationToken cancellationToken)
    {
        var errors = JobAuditRequestValidator.Validate(request);
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

        var result = await audit.RecordAsync(request, cancellationToken);
        return result.Created
            ? CreatedAtAction(nameof(Get), new { jobId = request.JobId }, result.Entry.ToResponse())
            : Ok(result.Entry.ToResponse());
    }

    [HttpGet("{jobId:guid}")]
    [Authorize(Policy = AuthPolicies.Read)]
    [ProducesResponseType<JobAuditResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid jobId, CancellationToken cancellationToken)
    {
        var entry = await audit.GetAsync(jobId, cancellationToken);
        return entry is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Audit entry not found", detail: $"No audit entry for job {jobId}.")
            : Ok(entry.ToResponse());
    }
}
