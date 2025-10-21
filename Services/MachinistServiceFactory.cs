using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TrainControlSystem.Models;
using TrainControlSystem.Configuration;
using TrainControlSystem.Repositories;
using TrainControlSystem.BackgroundServices;

namespace TrainControlSystem.Services
{
    /// <summary>
    /// Factory for creating and managing individual machinist services for each train
    /// This factory handles the dynamic lifecycle of machinist services
    /// </summary>
    public class MachinistServiceFactory
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly TrackManager _trackManager;
        private readonly ILogger<MachinistServiceFactory> _logger;
        private readonly Dictionary<string, MachinistService> _machinistServices = new Dictionary<string, MachinistService>();
        private readonly object _lockObject = new object();

        public MachinistServiceFactory(
            IServiceProvider serviceProvider,
            TrackManager trackManager,
            ILogger<MachinistServiceFactory> logger)
        {
            _serviceProvider = serviceProvider;
            _trackManager = trackManager;
            _logger = logger;
        }

        /// <summary>
        /// Creates and starts machinist services for all trains
        /// </summary>
        public async Task InitializeAllMachinistsAsync()
        {
            _logger.LogInformation("Initializing machinist services for all trains...");

            foreach (var train in _trackManager.Trains)
            {
                await CreateMachinistForTrainAsync(train);
            }

            _logger.LogInformation($"Initialized {_machinistServices.Count} machinist services");
        }

        /// <summary>
        /// Creates and starts a machinist service for a specific train
        /// </summary>
        public async Task<bool> CreateMachinistForTrainAsync(Models.Trains train)
        {
            lock (_lockObject)
            {
                if (_machinistServices.ContainsKey(train.Name))
                {
                    _logger.LogWarning($"Machinist service already exists for train: {train.Name}");
                    return false;
                }
            }

            try
            {
                // Create the machinist service with its specific dependencies
                var machinistService = new BackgroundServices.MachinistService(
                    train,
                    _serviceProvider.GetRequiredService<SystemConfiguration>(),
                    _serviceProvider.GetRequiredService<TimetableRepository>(),
                    _serviceProvider.GetRequiredService<TrackManager>(),
                    _serviceProvider.GetRequiredService<ILogger<BackgroundServices.MachinistService>>()
                );

                // Start the service
                await machinistService.StartAsync(CancellationToken.None);

                lock (_lockObject)
                {
                    _machinistServices[train.Name] = machinistService;
                }

                _logger.LogInformation($"Created and started machinist service for train: {train.Name}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to create machinist service for train: {train.Name}");
                return false;
            }
        }

        /// <summary>
        /// Stops and removes a machinist service for a specific train
        /// </summary>
        public async Task<bool> StopMachinistForTrainAsync(string trainName)
        {
            BackgroundServices.MachinistService? serviceToRemove = null;

            lock (_lockObject)
            {
                if (_machinistServices.TryGetValue(trainName, out serviceToRemove))
                {
                    _machinistServices.Remove(trainName);
                }
            }

            if (serviceToRemove != null)
            {
                try
                {
                    await serviceToRemove.StopAsync(CancellationToken.None);
                    serviceToRemove.Dispose();

                    _logger.LogInformation($"Stopped machinist service for train: {trainName}");
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to stop machinist service for train: {trainName}");
                    return false;
                }
            }

            _logger.LogWarning($"No machinist service found for train: {trainName}");
            return false;
        }

        /// <summary>
        /// Gets a list of all active machinist services
        /// </summary>
        public List<string> GetActiveMachinistTrains()
        {
            lock (_lockObject)
            {
                return _machinistServices.Keys.ToList();
            }
        }

        /// <summary>
        /// Restarts a machinist service for a specific train
        /// </summary>
        public async Task<bool> RestartMachinistForTrainAsync(Models.Trains train)
        {
            _logger.LogInformation($"Restarting machinist service for train: {train.Name}");

            // Stop existing service
            await StopMachinistForTrainAsync(train.Name);

            // Wait a moment before restarting
            await Task.Delay(1000);

            // Create new service
            return await CreateMachinistForTrainAsync(train);
        }

        /// <summary>
        /// Stops all machinist services
        /// </summary>
        public async Task StopAllMachinistsAsync()
        {
            _logger.LogInformation("Stopping all machinist services...");

            var trainNames = new List<string>();
            lock (_lockObject)
            {
                trainNames.AddRange(_machinistServices.Keys);
            }

            var stopTasks = trainNames.Select(trainName => StopMachinistForTrainAsync(trainName));
            await Task.WhenAll(stopTasks);

            _logger.LogInformation("All machinist services stopped");
        }

        /// <summary>
        /// Gets the status of a specific machinist service
        /// </summary>
        public bool IsMachinistRunning(string trainName)
        {
            lock (_lockObject)
            {
                return _machinistServices.ContainsKey(trainName);
            }
        }

        /// <summary>
        /// Gets the count of active machinist services
        /// </summary>
        public int ActiveMachinistCount
        {
            get
            {
                lock (_lockObject)
                {
                    return _machinistServices.Count;
                }
            }
        }
    }
}