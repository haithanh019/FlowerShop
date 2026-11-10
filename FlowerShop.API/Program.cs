using FlowerShop.API.OData;
using FlowerShop.Application;
using FlowerShop.Infrastructure;
using Microsoft.AspNetCore.OData;

namespace FlowerShop.API;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Register Controllers and OData
        builder.Services
            .AddControllers()
            .AddOData(options =>
            {
                options
                    .Select()
                    .Filter()
                    .OrderBy()
                    .Count()
                    .SetMaxTop(100)
                    .AddRouteComponents(
                        "odata/v1",
                        EdmModelFactory.Create());
            });

        // Get database connection string
        var connectionString = builder.Configuration
            .GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "Connection string 'DefaultConnection' not found.");

        // Register Application
        builder.Services.AddApplication();

        // Register Infrastructure
        builder.Services.AddInfrastructure(connectionString);

        // Register OpenAPI
        builder.Services.AddOpenApi();

        var app = builder.Build();

        // HTTP request pipeline
        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseHttpsRedirection();

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllers();

        app.Run();
    }
}
