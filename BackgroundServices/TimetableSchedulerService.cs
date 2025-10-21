using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Services;
using TrainControlSystem.MQTT;

namespace TrainControlSystem.BackgroundServices
{
    /// <summary>
    /// Background service that processes scheduled train departures
    /// </summary>
    public class TimetableSchedulerService : BackgroundService
    {
        private readonly TimetableManager _timetableManager;
        private readonly SystemConfiguration _config;
        private readonly ILogger<TimetableSchedulerService> _logger;
        private readonly TimeSpan _schedulerInterval;

        public TimetableSchedulerService(
            TimetableManager timetableManager,
            SystemConfiguration config,
            ILogger<TimetableSchedulerService> logger)
        {
            _timetableManager = timetableManager;
            _config = config;
            _logger = logger;
            _schedulerInterval = TimeSpan.FromSeconds(_config.Timetable.SchedulerIntervalSeconds);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Timetable Scheduler Service started");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogDebug("Processing scheduled trains...");
                    await _timetableManager.ProcessScheduledTrains();
                    await Task.Delay(_schedulerInterval, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in timetable scheduler");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("Timetable Scheduler Service stopped");
        }
    }
}