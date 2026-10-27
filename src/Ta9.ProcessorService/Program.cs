using System.Text.Json.Serialization;
using Ta9.ServiceDefaults;
using Ta9.ServiceDefaults.Security;

namespace Ta9.ProcessorService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("processor-service");
        // Only the Job Service may submit, query or cancel work.
        builder.AddApiKeyAuthentication(Caller.JobService);
        builder.Services.AddProcessing(builder.Configuration);

        builder.Services
            // Our validators own every input rule (e.g. empty content is valid); don't let MVC add an implicit [Required].
            .AddControllers(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true)
            .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerWithApiKey();

        var app = builder.Build();

        app.UseServiceDefaults();
        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();

        app.Run();
    }
}
