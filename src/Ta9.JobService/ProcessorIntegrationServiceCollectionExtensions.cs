using Microsoft.Extensions.Options;
using Ta9.JobService.Application;
using Ta9.JobService.Infrastructure.Processor;
using Ta9.ServiceDefaults;

namespace Ta9.JobService;

public static class ProcessorIntegrationServiceCollectionExtensions
{
    /// <summary>Processor REST client, the background dispatcher and the reconciler.</summary>
    public static IServiceCollection AddProcessorIntegration(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessorClientOptions>()
            .Bind(configuration.GetSection(ProcessorClientOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ReconcileOptions>()
            .Bind(configuration.GetSection(ReconcileOptions.SectionName))
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
        services.AddSingleton<JobReconciler>();
        services.AddHostedService(sp => sp.GetRequiredService<JobReconciler>());

        return services;
    }
}
