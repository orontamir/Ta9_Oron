using Microsoft.Extensions.Options;
using Ta9.JobService.Application;
using Ta9.JobService.Infrastructure.Audit;
using Ta9.ServiceDefaults;
using Ta9.ServiceDefaults.Security;

namespace Ta9.JobService;

public static class AuditIntegrationServiceCollectionExtensions
{
    /// <summary>Audit Service client and the background publisher of terminal job summaries.</summary>
    public static IServiceCollection AddAuditPublishing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AuditOptions>()
            .Bind(configuration.GetSection(AuditOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpAuditClient.HttpClientName, (sp, http) =>
            {
                var audit = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
                http.BaseAddress = audit.BaseUrl;
                http.Timeout = audit.RequestTimeout;
            })
            .AddCorrelationIdPropagation()
            .AddApiKey(keys => keys.JobServiceKey);

        services.AddSingleton<IAuditClient, HttpAuditClient>();
        services.AddSingleton<AuditPublisher>();
        services.AddHostedService(sp => sp.GetRequiredService<AuditPublisher>());

        return services;
    }
}
