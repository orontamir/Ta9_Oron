using System.Text.Json.Serialization;
using Ta9.ServiceDefaults;

namespace Ta9.ProcessorService;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults("processor-service");

        builder.Services
            .AddControllers()
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
