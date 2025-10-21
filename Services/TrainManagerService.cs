using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;

namespace TrainControlSystem.Services
{
    /// <summary>
    /// Train Manager Service - Coordinates train operations (now in coordinator role)
    /// </summary>
    public class TrainManagerService
    {
        private readonly TrackManager _trackManager;
        private readonly MachinistServiceFactory _machinistFactory;
        private readonly ILogger<TrainManagerService> _logger;
        private readonly SystemConfiguration _config;

        public TrainManagerService(
            TrackManager trackManager,
            MachinistServiceFactory machinistFactory,
            ILogger<TrainManagerService> logger,
            SystemConfiguration config)
        {
            _trackManager = trackManager;
            _machinistFactory = machinistFactory;
            _logger = logger;
            _config = config;

            _logger.LogInformation("TrainManagerService initialized in coordinator mode (train control delegated to machinist services)");
        }

        /// <summary>
        /// Requests emergency stop for a specific train (delegates to machinist)
        /// </summary>
        public async Task<bool> EmergencyStopTrainAsync(string trainName)
        {
            _logger.LogWarning($"Emergency stop requested for train: {trainName}");

            try
            {
                // Find the train
                var train = _trackManager.GetTrain(trainName);
                if (train == null)
                {
                    _logger.LogError($"Train not found: {trainName}");
                    return false;
                }

                // Restart the machinist service (which will stop the train)
                var success = await _machinistFactory.RestartMachinistForTrainAsync(train);
                if (success)
                {
                    _logger.LogInformation($"Emergency stop completed for train: {trainName}");
                }
                else
                {
                    _logger.LogError($"Failed to emergency stop train: {trainName}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during emergency stop for train: {trainName}");
                return false;
            }
        }

        /// <summary>
        /// Gets train by name
        /// </summary>
        public Models.Trains? GetTrain(string trainName)
        {
            return _trackManager.GetTrain(trainName);
        }

        /// <summary>
        /// Lists all available trains with their machinist status
        /// </summary>
        public void ListTrains()
        {
            Console.WriteLine("\nAvailable Trains:");
            Console.WriteLine("===============================================");
            Console.WriteLine("Train Name | State    | Speed  | Machinist Status | Location");
            Console.WriteLine("------------|----------|--------|------------------|----------");

            foreach (var train in _trackManager.Trains)
            {
                var machinistStatus = _machinistFactory.IsMachinistRunning(train.Name) ? "ACTIVE" : "STOPPED";
                var location = train.SubSection?.Name ?? "Unknown";

                Console.WriteLine($"{train.Name,-11} | {train.State,-8} | {train.CurrentSpeed,-6} | {machinistStatus,-16} | {location}");
            }
            Console.WriteLine("===============================================");
        }

        /// <summary>
        /// Gets the status of all machinist services
        /// </summary>
        public void ListMachinistStatus()
        {
            Console.WriteLine("\nMachinist Service Status:");
            Console.WriteLine("================================");
            Console.WriteLine($"Total Active Machinists: {_machinistFactory.ActiveMachinistCount}");

            var activeTrains = _machinistFactory.GetActiveMachinistTrains();
            if (activeTrains.Any())
            {
                Console.WriteLine("Active Machinists:");
                foreach (var trainName in activeTrains)
                {
                    var train = GetTrain(trainName);
                    var status = train?.State.ToString() ?? "Unknown";
                    Console.WriteLine($"  - {trainName}: {status}");
                }
            }
            else
            {
                Console.WriteLine("No active machinist services");
            }
            Console.WriteLine("================================");
        }

        /// <summary>
        /// Gets system overview
        /// </summary>
        public void GetSystemOverview()
        {
            Console.WriteLine("\nTrain Control System Overview:");
            Console.WriteLine("===============================");
            Console.WriteLine($"Total Trains: {_trackManager.Trains.Count}");
            Console.WriteLine($"Active Machinists: {_machinistFactory.ActiveMachinistCount}");
            Console.WriteLine($"Architecture: Scalable Machinist Services");
            Console.WriteLine($"MQTT Broker: {_config.MQTT.Address}:{_config.MQTT.Port}");
            Console.WriteLine("===============================");
        }
    }
}