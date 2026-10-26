using System.Text.Json.Serialization;
using Ta9.JobService.Api;
using Ta9.JobService.Application;
using Ta9.ServiceDefaults;

namespace Ta9.JobService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("job-service");
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddJobStorage(builder.Configuration);
        builder.Services.AddSingleton<JobManager>();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();

        builder.Services
            // Our validators own every input rule (e.g. empty content is valid); don't let MVC add an implicit [Required].
            .AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        app.UseServiceDefaults();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();

        app.Run();
    }
}
