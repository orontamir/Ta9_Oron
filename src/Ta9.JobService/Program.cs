using System.Text.Json.Serialization;
using Ta9.JobService.Api;
using Ta9.JobService.Application;
using Ta9.ServiceDefaults;
using Ta9.ServiceDefaults.Security;

namespace Ta9.JobService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("job-service");
        // Clients (reader / operator) and the Processor (WebSocket events).
        builder.AddApiKeyAuthentication(Caller.Reader, Caller.Operator, Caller.ProcessorService);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddJobStorage(builder.Configuration);
        builder.Services.AddProcessorIntegration(builder.Configuration);
        builder.Services.AddAuditPublishing(builder.Configuration);
        builder.Services.AddSingleton<JobManager>();
        builder.Services.AddSingleton<ProcessorEventHandler>();
        builder.Services.AddSingleton<ProcessorEventsEndpoint>();
        builder.Services.AddSingleton<JobEventStream>();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();

        builder.Services
            // Our validators own every input rule (e.g. empty content is valid); don't let MVC add an implicit [Required].
            .AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerWithApiKey();

        var app = builder.Build();

        app.UseServiceDefaults();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        app.Map(ProcessorEventsEndpoint.Path, (RequestDelegate)app.Services.GetRequiredService<ProcessorEventsEndpoint>().HandleAsync)
            .RequireAuthorization(AuthPolicies.Service);
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();

        app.Run();
    }
}
