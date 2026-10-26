using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Infrastructure;

namespace Ta9.ProcessorService;

public static class ProcessorServiceCollectionExtensions
{
    public static IServiceCollection AddProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessingOptions>()
            .Bind(configuration.GetSection(ProcessingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDocumentAnalyzer, DocumentAnalyzer>();
        services.AddSingleton<IProcessingJobStore, InMemoryProcessingJobStore>();
        services.AddSingleton<IJobQueue, ChannelJobQueue>();
        services.AddSingleton<IJobEventPublisher, LoggingJobEventPublisher>();
        services.AddSingleton<JobRunner>();
        services.AddSingleton<ProcessingService>();
        services.AddHostedService<JobWorkerService>();
        services.AddHostedService<ProcessingJobCleanupService>();

        return services;
    }
}
