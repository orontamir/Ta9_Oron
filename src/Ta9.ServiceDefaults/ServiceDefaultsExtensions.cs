using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Context;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Ta9.ServiceDefaults.Logging;

namespace Ta9.ServiceDefaults;

/// <summary>
/// Cross-cutting hosting setup shared by all services: structured logging (console + MongoDB), correlation ids,
/// ProblemDetails error responses and liveness/readiness health endpoints.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static WebApplicationBuilder AddServiceDefaults(this WebApplicationBuilder builder, string serviceName)
    {
        var mongoLogSection = builder.Configuration.GetSection(MongoLogOptions.SectionName);
        builder.Services.Configure<MongoLogOptions>(mongoLogSection);
        builder.Services.TryAddSingleton<ILogBatchWriter>(sp => new MongoLogBatchWriter(sp.GetRequiredService<IOptions<MongoLogOptions>>().Value));
        builder.Services.AddSingleton<LogSinkHolder>();
        builder.Services.AddHostedService<ServiceLifecycleLogger>();

        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .ReadFrom.Configuration(context.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("Service", serviceName);

            // Console: the flow (Information and above by default). JSON for containers/log shippers,
            // a readable template for local development.
            var consoleLevel = context.Configuration.GetValue("Logging:Console:MinimumLevel", LogEventLevel.Information);
            if (context.Configuration.GetValue("Logging:UseJsonConsole", true))
            {
                configuration.WriteTo.Console(new CompactJsonFormatter(), restrictedToMinimumLevel: consoleLevel);
            }
            else
            {
                configuration.WriteTo.Console(
                    restrictedToMinimumLevel: consoleLevel,
                    outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Service} {CorrelationId} {JobId} {Message:lj}{NewLine}{Exception}");
            }

            // MongoDB: everything from Debug up (by default), batched in the background, for investigating failures later.
            var mongo = services.GetRequiredService<IOptions<MongoLogOptions>>().Value;
            if (mongo.Enabled)
            {
                var sink = new BatchingLogSink(services.GetRequiredService<ILogBatchWriter>(), serviceName, mongo);
                services.GetRequiredService<LogSinkHolder>().Sink = sink;
                configuration.WriteTo.Sink(sink, mongo.MinimumLevel);
            }
        },
        // Each host owns its logger (disposed with the host, which flushes the Mongo sink); the static Log.Logger
        // is never touched (several hosts run side by side in tests).
        preserveStaticLogger: true);

        builder.Services.AddProblemDetails();
        builder.Services.AddTransient<CorrelationIdHandler>();
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag]);

        return builder;
    }

    /// <summary>Adds X-Correlation-Id propagation to a typed/named HttpClient.</summary>
    public static IHttpClientBuilder AddCorrelationIdPropagation(this IHttpClientBuilder builder) =>
        builder.AddHttpMessageHandler<CorrelationIdHandler>();

    public static WebApplication UseServiceDefaults(this WebApplication app)
    {
        // Correlation first: error handling and request logging then run inside the correlation scope,
        // so error lines (unhandled exceptions, 503 mapping) carry the request's correlation id too.
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseSerilogRequestLogging(options =>
        {
            // The middleware defaults to the static Log.Logger, which this host never configures
            // (preserveStaticLogger): use the host's own logger so request lines reach the console and MongoDB.
            options.Logger = app.Services.GetService<ILogger>();

            // Health probes run every few seconds; keep them out of the default log level.
            options.GetLevel = (httpContext, _, exception) =>
                exception is not null || httpContext.Response.StatusCode >= 500
                    ? LogEventLevel.Error
                    : httpContext.Request.Path.StartsWithSegments("/health")
                        ? LogEventLevel.Verbose
                        : LogEventLevel.Information;
            // Who made the call is stored with the request line (property "Caller").
            options.EnrichDiagnosticContext = (diagnostics, httpContext) =>
                diagnostics.Set("Caller", CallerName(httpContext));
        });

        // Authentication runs inside the correlation scope, so rejected calls are traceable too.
        if (app.Services.GetService<IAuthenticationSchemeProvider>() is not null)
        {
            app.UseAuthentication();
            app.Use(async (context, next) =>
            {
                using (LogContext.PushProperty("Caller", CallerName(context)))
                {
                    await next(context);
                }
            });
            app.UseAuthorization();
        }

        // Health checks stay anonymous: Docker and orchestrators probe them without credentials.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = c => c.Tags.Contains(LiveTag) }).AllowAnonymous();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) }).AllowAnonymous();

        return app;
    }

    private static string CallerName(HttpContext context) =>
        context.User.Identity is { IsAuthenticated: true, Name: { } name } ? name : "anonymous";
}
