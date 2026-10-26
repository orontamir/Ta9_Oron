using Microsoft.Extensions.Options;
using Ta9.ProcessorService.Application;
using Ta9.ProcessorService.Domain;
using Ta9.ProcessorService.Infrastructure;
using Ta9.ProcessorService.Infrastructure.Events;
using Ta9.ServiceDefaults;

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
        services.AddSingleton<JobRunner>();
        services.AddSingleton<ProcessingService>();
        services.AddHostedService<JobWorkerService>();
        services.AddHostedService<ProcessingJobCleanupService>();

        services.AddJobServiceEvents(configuration);

        return services;
    }

    /// <summary>WebSocket delivery of job events to the Job Service (outbox + ack + reconnect).</summary>
    public static IServiceCollection AddJobServiceEvents(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JobServiceEventsOptions>()
            .Bind(configuration.GetSection(JobServiceEventsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton(sp => new EventOutbox(
            sp.GetRequiredService<IOptions<JobServiceEventsOptions>>().Value.OutboxCapacity,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IJobEventPublisher, WebSocketJobEventPublisher>();
        services.AddSingleton<IWebSocketConnector, ClientWebSocketConnector>();
        services.AddHostedService<JobServiceEventsClient>();
        services.AddHealthChecks()
            .AddCheck<JobServiceConnectionHealthCheck>("job-service-events", tags: [ServiceDefaultsExtensions.ReadyTag]);

        return services;
    }
}
