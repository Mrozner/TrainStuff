using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeSepareted
{
    public class TimetableSchedulerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SystemConfiguration _config;

        public TimetableSchedulerService(IServiceProvider serviceProvider, SystemConfiguration config)
        {
            _serviceProvider = serviceProvider;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Timetable Scheduler Service started");

            // Wait until the next whole minute
            var now = DateTime.Now;
            var nextMinute = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
            var delay = nextMinute - now;
            await Task.Delay(delay, stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.Timetable.SchedulerIntervalSeconds));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();

                    timetableManager.LoadEntries();
                    await timetableManager.ProcessScheduledTrains();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in timetable scheduler: {ex.Message}");
                }
            }
        }
    }
}