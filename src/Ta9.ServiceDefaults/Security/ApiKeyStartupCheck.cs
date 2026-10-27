using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ta9.ServiceDefaults.Security;

/// <summary>States at startup how this service authenticates, and warns about anything that will reject callers.</summary>
public sealed class ApiKeyStartupCheck(ApiKeyRegistry registry, ILogger<ApiKeyStartupCheck> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!registry.Enabled)
        {
            logger.LogWarning("Authentication is DISABLED (Auth:Enabled=false): every request is allowed");
            return Task.CompletedTask;
        }

        logger.LogInformation("Authentication: API key ({Header}) required; accepted callers: {Callers}",
            ApiKeyAuthenticationHandler.HeaderName, string.Join(", ", registry.Accepted.Select(c => c.Name)));
        foreach (var missing in registry.Missing)
        {
            logger.LogWarning("No API key configured for {Caller}: its requests will be rejected (set Auth__{Setting})",
                missing.Name, $"{missing.Caller}Key");
        }

        foreach (var weak in registry.TooShort)
        {
            logger.LogWarning("The API key of {Caller} is shorter than {MinLength} characters", weak.Name, ApiKeyRegistry.MinimumKeyLength);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
