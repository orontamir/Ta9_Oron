using System.Text.Json.Serialization;
using Ta9.ServiceDefaults;
using Ta9.ServiceDefaults.Security;

namespace Ta9.AuditService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("audit-service");
        // Clients read entries; only the Job Service records them.
        builder.AddApiKeyAuthentication(Caller.Reader, Caller.Operator, Caller.JobService);
        builder.Services.AddAudit(builder.Configuration);

        builder.Services
            // The validator owns every input rule; don't let MVC add an implicit [Required].
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
