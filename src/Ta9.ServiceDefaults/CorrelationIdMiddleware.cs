using Microsoft.AspNetCore.Http;

namespace Ta9.ServiceDefaults;

/// <summary>
/// Reuses the caller's X-Correlation-Id (if valid) or creates one, exposes it via <see cref="Correlation"/>
/// for the rest of the request and echoes it on the response.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[Correlation.HeaderName].ToString();
        var correlationId = Correlation.IsValid(incoming) ? incoming : Correlation.NewId();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[Correlation.HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (Correlation.BeginScope(correlationId))
        {
            await next(context);
        }
    }
}
