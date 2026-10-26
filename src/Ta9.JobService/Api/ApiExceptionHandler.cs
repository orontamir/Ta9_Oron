using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Ta9.JobService.Application;

namespace Ta9.JobService.Api;

/// <summary>
/// Maps infrastructure failures to meaningful HTTP statuses instead of a generic 500:
/// storage unreachable → 503 (retryable), persistent write contention → 409.
/// Anything else falls through to the default ProblemDetails 500.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            StorageUnavailableException => (StatusCodes.Status503ServiceUnavailable, "Storage unavailable"),
            JobConcurrencyException => (StatusCodes.Status409Conflict, "Concurrent modification"),
            _ => (0, string.Empty)
        };

        if (status == 0)
        {
            return false;
        }

        // Storage down is an error the operator must act on; a write conflict is the client's to retry.
        if (status == StatusCodes.Status503ServiceUnavailable)
        {
            logger.LogError(exception, "Request failed: {Title}", title);
        }
        else
        {
            logger.LogWarning(exception, "Request failed: {Title}", title);
        }
        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status503ServiceUnavailable
                    ? "The job store is temporarily unreachable. Retry later."
                    : "The job was modified concurrently. Retry the request."
            }
        });
    }
}
