using Microsoft.Extensions.Options;
using Ta9.JobService.Application;
using Ta9.JobService.Infrastructure.Processor;
using Ta9.ServiceDefaults;

namespace Ta9.JobService;

public static class ProcessorIntegrationServiceCollectionExtensions
{
    /// <summary>Processor REST client and the background dispatcher.</summary>
    public static IServiceCollection AddProcessorIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessorClientOptions>()
            .Bind(configuration.GetSection(ProcessorClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<DispatchOptions>()
            .Bind(configuration.GetSection(DispatchOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpProcessorClient.HttpClientName, (sp, http) =>
            {
                var processor = sp.GetRequiredService<IOptions<ProcessorClientOptions>>().Value;
                http.BaseAddress = processor.BaseUrl;
                http.Timeout = processor.RequestTimeout;
            })
            .AddCorrelationIdPropagation();

        services.AddSingleton<IProcessorClient, HttpProcessorClient>();
        services.AddSingleton<IJobDispatchSignal, JobDispatchSignal>();
        services.AddSingleton<JobDispatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<JobDispatcher>());

        return services;
    }
}
