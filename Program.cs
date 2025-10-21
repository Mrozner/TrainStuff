using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.Services;
using TrainControlSystem.MQTT;
using TrainControlSystem.Repositories;
using TrainControlSystem.BackgroundServices;

namespace TrainControlSystem
{
    public class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Train Control System - Scalable Machinist Architecture ===");
            Console.WriteLine("Starting all services...\n");

            var host = CreateHostBuilder(args).Build();

            // Initialize services after all dependencies are resolved
            using (var scope = host.Services.CreateScope())
            {
                // Set MQTT connectors for TrackManager
                var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                var trackConnector = scope.ServiceProvider.GetRequiredService<TrackMQTTConnector>();
                var timetableConnector = scope.ServiceProvider.GetRequiredService<TimetableMQTTConnector>();
                trackManager.SetMqttConnectors(trackConnector, timetableConnector);

                // Initialize machinist services for all trains
                var machinistFactory = scope.ServiceProvider.GetRequiredService<MachinistServiceFactory>();
                var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

                Console.WriteLine("Initializing Machinist Services...");
                await machinistFactory.InitializeAllMachinistsAsync();

                logger.LogInformation($"Successfully initialized {machinistFactory.ActiveMachinistCount} machinist services");
                Console.WriteLine($"Active Machinist Services: {string.Join(", ", machinistFactory.GetActiveMachinistTrains())}");
                Console.WriteLine();
            }

            Console.WriteLine("All services started. Press Ctrl+C to stop.");
            Console.WriteLine("Type 'help' for available commands.\n");

            // Setup graceful shutdown
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            lifetime.ApplicationStopping.Register(async () =>
            {
                Console.WriteLine("\nShutting down machinist services...");
                using (var scope = host.Services.CreateScope())
                {
                    var machinistFactory = scope.ServiceProvider.GetRequiredService<MachinistServiceFactory>();
                    await machinistFactory.StopAllMachinistsAsync();
                    Console.WriteLine("All machinist services stopped gracefully.");
                }
            });

            await host.RunAsync();
        }

        static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureLogging(logging =>
                {
                    logging.ClearProviders();
                    logging.AddConsole();
                    logging.SetMinimumLevel(LogLevel.Information);
                })
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                    var config = LoadConfiguration(configPath);
                    services.AddSingleton(config);

                    // Database
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(config.ConnectionString)
                               .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking),
                        ServiceLifetime.Singleton);

                    // Core Services
                    services.AddSingleton<TrackManager>();
                    services.AddSingleton<TimetableManager>();

                    // MQTT Connectors
                    services.AddSingleton<TrackMQTTConnector>();
                    services.AddSingleton<TimetableMQTTConnector>();
                    services.AddSingleton<TrainMQTTConnector>();

                    // Train Manager Service (coordinator)
                    services.AddSingleton<TrainManagerService>();

                    // Machinist Service Factory
                    services.AddSingleton<MachinistServiceFactory>();

                    // MachinistService (transient - factory creates instances)
                    services.AddTransient<BackgroundServices.MachinistService>();

                    // Repositories
                    services.AddSingleton<TimetableRepository>();

                    // Background Services
                    services.AddHostedService<TimetableSchedulerService>();
                    services.AddHostedService<SystemMonitorService>();
                    services.AddHostedService<ConsoleInterface>();

                    // Add logging
                    services.AddLogging();
                });

        static SystemConfiguration LoadConfiguration(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Configuration file not found: {path}");
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<SystemConfiguration>(json)
                   ?? throw new InvalidOperationException("Failed to deserialize configuration");
        }
    }
}