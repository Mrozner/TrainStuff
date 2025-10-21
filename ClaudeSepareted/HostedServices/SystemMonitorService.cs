using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeSepareted
{
    public class SystemMonitorService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public SystemMonitorService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("System Monitor Service started");

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();

                    Console.WriteLine("\n=== System Status ===");
                    Console.WriteLine($"Active Train: {trackManager.Trains.Count(t => t.State != TrainState.Waiting)}");
                    Console.WriteLine($"Occupied Sections: {trackManager.OccupiedSubSections.Count}");
                    Console.WriteLine($"Pending Entries: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Upcoming)}");
                    Console.WriteLine($"In Progress: {timetableManager.Entries.Count(e => e.EntryState == EntryState.InProgress)}");
                    Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine("=====================\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in system monitor: {ex.Message}");
                }
            }
        }
    }
}