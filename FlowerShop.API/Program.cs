using FlowerShop.Application;
using FlowerShop.Infrastructure;

namespace FlowerShop.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add controllers
            builder.Services.AddControllers();

            // Get database connection string
            var connectionString = builder.Configuration
                .GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException(
                    "Connection string 'DefaultConnection' not found.");

            // Register Application services
            builder.Services.AddApplication();

            // Register Infrastructure services
            builder.Services.AddInfrastructure(connectionString);

            // OpenAPI
            builder.Services.AddOpenApi();

            var app = builder.Build();

            // HTTP request pipeline
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}
