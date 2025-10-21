using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Services;
using TrainControlSystem.MQTT;

namespace TrainControlSystem.BackgroundServices
{
    /// <summary>
    /// Background service that monitors system health and performance
    /// </summary>
    public class SystemMonitorService : BackgroundService
    {
        private readonly TimetableManager _timetableManager;
        private readonly TrackManager _trackManager;
        private readonly MachinistServiceFactory _machinistFactory;
        private readonly SystemConfiguration _config;
        private readonly ILogger<SystemMonitorService> _logger;
        private readonly TimeSpan _monitoringInterval = TimeSpan.FromMinutes(5);

        public SystemMonitorService(
            TimetableManager timetableManager,
            TrackManager trackManager,
            MachinistServiceFactory machinistFactory,
            SystemConfiguration config,
            ILogger<SystemMonitorService> logger)
        {
            _timetableManager = timetableManager;
            _trackManager = trackManager;
            _machinistFactory = machinistFactory;
            _config = config;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("System Monitor Service started");

            // Perform initial health check
            await PerformHealthCheck();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PerformHealthCheck();
                    await Task.Delay(_monitoringInterval, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in system monitor");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
            }

            _logger.LogInformation("System Monitor Service stopped");
        }

        private async Task PerformHealthCheck()
        {
            _logger.LogDebug("Performing system health check...");

            try
            {
                // Check timetable statistics
                var timetableStats = _timetableManager.GetStatistics();
                _logger.LogInformation($"Timetable Status: {timetableStats.ScheduledEntries} scheduled, {timetableStats.InTransitEntries} in transit, {timetableStats.ArrivedEntries} arrived");

                // Check track occupancy
                var occupiedSections = _trackManager.SubSections.Count(s => s.IsOccupied);
                _logger.LogInformation($"Track Status: {occupiedSections}/{_trackManager.SubSections.Count} subsections occupied");

                // Check machinist services
                var activeMachinists = _machinistFactory.ActiveMachinistCount;
                var totalTrains = _trackManager.Trains.Count;
                _logger.LogInformation($"Machinist Services: {activeMachinists}/{totalTrains} active");

                // Check for stale entries and cleanup
                _timetableManager.CleanupOldEntries();

                // Alert if there are issues
                if (activeMachinists < totalTrains)
                {
                    _logger.LogWarning($"Not all machinist services are active: {totalTrains - activeMachinists} services missing");
                }

                if (occupiedSections > _config.Track.MaxOccupiedSections)
                {
                    _logger.LogWarning($"Track occupancy above threshold: {occupiedSections} > {_config.Track.MaxOccupiedSections}");
                }

                if (timetableStats.InTransitEntries > 0)
                {
                    _logger.LogInformation($"System actively managing {timetableStats.InTransitEntries} train journeys");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during health check");
            }
        }
    }
}