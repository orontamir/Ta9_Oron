using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Ta9.ServiceDefaults.Security;

/// <summary>
/// Authenticates the <c>X-Api-Key</c> header. No header → anonymous (the endpoint's policy decides: 401);
/// unknown key → failure (401, logged as a Warning, never with the key); known key → caller name + role.
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    ApiKeyRegistry registry) : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string DisabledCallerName = "auth-disabled";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!registry.Enabled)
        {
            return Task.FromResult(Success(DisabledCallerName, Roles.Reader, Roles.Operator, Roles.Service));
        }

        var presented = Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var caller = registry.Find(presented);
        if (caller is null)
        {
            Logger.LogWarning("Rejected {Method} {Path}: invalid API key", Request.Method, Request.Path);
            return Task.FromResult(AuthenticateResult.Fail("Invalid API key."));
        }

        Logger.LogDebug("Authenticated caller {Caller} ({Role})", caller.Name, caller.Role);
        return Task.FromResult(Success(caller.Name, caller.Role));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        await base.HandleChallengeAsync(properties);
        Response.Headers.WWWAuthenticate = $"{SchemeName} header=\"{HeaderName}\"";
    }

    private AuthenticateResult Success(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
