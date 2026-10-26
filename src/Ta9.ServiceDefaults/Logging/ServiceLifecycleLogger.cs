using System.Reflection;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog.Events;

namespace Ta9.ServiceDefaults.Logging;

/// <summary>Holds the Mongo log sink of this host so it can be flushed before a crash terminates the process.</summary>
public sealed class LogSinkHolder
{
    public BatchingLogSink? Sink { get; set; }
}

/// <summary>
/// Writes the "where is this service and how is it logging" lines at startup, start/stop markers, and any
/// unhandled exception (Critical) — the first things to look at when a service does not come up or crashes.
/// </summary>
public sealed class ServiceLifecycleLogger(
    IHostApplicationLifetime lifetime,
    IHostEnvironment environment,
    IConfiguration configuration,
    IOptions<MongoLogOptions> mongoOptions,
    LogSinkHolder sinkHolder,
    IServiceProvider services,
    ILogger<ServiceLifecycleLogger> logger) : IHostedService
{
    private readonly List<CancellationTokenRegistration> _registrations = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var mongo = mongoOptions.Value;
        logger.LogInformation(
            "Logging: console >= {ConsoleLevel}; MongoDB {MongoLogging}",
            configuration.GetValue("Logging:Console:MinimumLevel", LogEventLevel.Information),
            mongo.Enabled
                ? $"{mongo.Database}.{mongo.Collection} >= {mongo.MinimumLevel} (retention {mongo.RetentionDays} days)"
                : "disabled");

        _registrations.Add(lifetime.ApplicationStarted.Register(() => logger.LogInformation(
            "Service started: environment {Environment}, version {Version}, listening on {Urls}",
            environment.EnvironmentName,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            string.Join(", ", services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses ?? Array.Empty<string>()))));
        _registrations.Add(lifetime.ApplicationStopping.Register(() => logger.LogInformation("Service stopping")));
        _registrations.Add(lifetime.ApplicationStopped.Register(() => logger.LogInformation("Service stopped")));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        return Task.CompletedTask;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        logger.LogCritical(args.ExceptionObject as Exception, "Unhandled exception (process terminating: {IsTerminating})", args.IsTerminating);
        if (args.IsTerminating)
        {
            // Flush buffered records so the crash itself reaches the log database.
            sinkHolder.Sink?.Dispose();
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args) =>
        logger.LogError(args.Exception, "Unobserved task exception");
}
