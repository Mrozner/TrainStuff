using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MQTTnet;

namespace ClaudeSepareted.Services
{
    public class TrackHandlerService
    {
        private readonly VirtualClock _virtualClock;
        private readonly ApplicationDbContext _dbContext;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService _statusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly UnifiedPathfindingService _unifiedPathfinder;
        private readonly TrainArrivalMonitorService _arrivalMonitor;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly List<TrainManagerService> _activeTrainManagers;
        private readonly Dictionary<string, DateTime> _recentlyProcessedTrains = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> _lastSwitchCommands = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastSignalCommands = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastTrainCommands = new Dictionary<string, string>();
        private readonly Dictionary<int, TrainManagerService> _trainManagers = new Dictionary<int, TrainManagerService>();
        private readonly object _lockObject = new object();
        private IMqttClient? _mqttClient;
        private bool _isInitialized = false;

        public TrackHandlerService(
            VirtualClock virtualClock,
            ApplicationDbContext dbContext,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            UnifiedPathfindingService unifiedPathfinder,
            TrainArrivalMonitorService arrivalMonitor)
        {
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _unifiedPathfinder = unifiedPathfinder ?? throw new ArgumentNullException(nameof(unifiedPathfinder));
            _arrivalMonitor = arrivalMonitor ?? throw new ArgumentNullException(nameof(arrivalMonitor));
            _cancellationTokenSource = new CancellationTokenSource();
            _activeTrainManagers = new List<TrainManagerService>();

            // Subscribe to midnight reset event
            _virtualClock.MidnightReset += OnMidnightReset;
        }

        public void Start()
        {
            // Start the arrival monitoring service
            _ = Task.Run(async () =>
            {
                try
                {
                    await _arrivalMonitor.StartMonitoringAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrackHandlerService] Error starting arrival monitoring: {ex.Message}");
                }
            });

            var thread = new Thread(async () => await RunAsync(_cancellationTokenSource.Token))
            {
                IsBackground = true,
                Name = "TrackHandlerService"
            };
            thread.Start();
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();

            // Unsubscribe from midnight reset event
            _virtualClock.MidnightReset -= OnMidnightReset;

            // Stop arrival monitoring
            _ = Task.Run(async () =>
            {
                try
                {
                    await _arrivalMonitor.StopAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrackHandlerService] Error stopping arrival monitoring: {ex.Message}");
                }
            });

            // Stop all active train managers
            lock (_lockObject)
            {
                foreach (var manager in _activeTrainManagers)
                {
                    manager.Stop();
                }
                _activeTrainManagers.Clear();
            }
        }

        public bool ShouldSendTrainCommand(string trainName, string command)
        {
            lock (_lockObject)
            {
                var key = $"{trainName}_{command}";
                if (_lastTrainCommands.ContainsKey(key))
                {
                    Console.WriteLine($"[TrackHandlerService] Global command check for {trainName} - Command already sent recently: {command}");
                    return false;
                }

                // Store the command with timestamp
                _lastTrainCommands[key] = command;
                return true;
            }
        }

        public bool ShouldSendTrainPowerCommand(string trainName, bool powerOn)
        {
            lock (_lockObject)
            {
                var powerKey = $"{trainName}_power_{(powerOn ? "on" : "off")}";
                if (_lastTrainCommands.ContainsKey(powerKey))
                {
                    var powerState = powerOn ? "on" : "off";
                    Console.WriteLine($"[TrackHandlerService] Power already {powerState} for {trainName} - skipping power command");
                    return false;
                }

                // Store the power command
                _lastTrainCommands[powerKey] = powerKey;
                return true;
            }
        }

  
        private async Task<List<Switches>> LoadSwitchesFromDatabase()
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    return await dbContext.Switches.ToListAsync();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error loading switches from database: {ex.Message}");
                return new List<Switches>();
            }
        }

        
        private void OnMidnightReset(object? sender, EventArgs e)
        {
            try
            {
                Console.WriteLine("[TrackHandlerService] Midnight reset: resetting all timetable entries to Upcoming");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Get all non-upcoming entries and reset them to upcoming
                    var entriesToReset = dbContext.TimetableEntries
                        .Where(te => te.EntryState != EntryState.Upcoming)
                        .ToList();

                    foreach (var entry in entriesToReset)
                    {
                        entry.EntryState = EntryState.Upcoming;
                        entry.ArrivedTime = null; // Clear arrived time
                    }

                    dbContext.SaveChanges();

                    Console.WriteLine($"[TrackHandlerService] Reset {entriesToReset.Count} timetable entries to Upcoming");
                    _statusService?.ShowInfo($"Éjfél: {entriesToReset.Count} menetrendi bejegyzés visszaállítva 'Érkező' állapotra");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error during midnight reset: {ex.Message}");
                _statusService?.ShowError($"Éjfél alaphelyzetbe állítási hiba: {ex.Message}");
            }
        }

        private async Task<bool> InitializeMqttAsync()
        {
            if (_isInitialized)
                return true;

            try
            {
                Console.WriteLine($"[TrackHandlerService] Initializing MQTT connection to {_mqttConfig.Address}:{_mqttConfig.Port}");
                _statusService?.ShowInfo("Track Handler Service MQTT kapcsolódik...");

                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                var clientId = $"TrackHandler_{Guid.NewGuid():N}";
                Console.WriteLine($"[TrackHandlerService] Using Client ID: {clientId}");

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
                    .WithClientId(clientId)
                    .WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                    .Build();

                Console.WriteLine($"[TrackHandlerService] Attempting to connect to MQTT broker...");

                var result = await _mqttClient.ConnectAsync(options);

                Console.WriteLine($"[TrackHandlerService] MQTT Connection Result: {result.ResultCode}");

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    _isInitialized = true;
                    Console.WriteLine($"[TrackHandlerService] MQTT successfully connected to {_mqttConfig.Address}:{_mqttConfig.Port}");

                    // Test connection with a simple ping message
                    var testMessage = new MqttApplicationMessageBuilder()
                        .WithTopic("test/connection")
                        .WithPayload(System.Text.Encoding.UTF8.GetBytes("ping"))
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    var publishResult = await _mqttClient.PublishAsync(testMessage);
                    Console.WriteLine($"[TrackHandlerService] Test message published");

                    _statusService?.ShowSuccess("Track Handler Service MQTT csatlakoztatva");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[TrackHandlerService] MQTT connection failed: {result.ResultCode}");
                    _statusService?.ShowError($"Track Handler Service MQTT kapcsolat hiba: {result.ResultCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] MQTT initialization error: {ex.Message}");
                Console.WriteLine($"[TrackHandlerService] Stack trace: {ex.StackTrace}");
                _statusService?.ShowError($"Track Handler Service MQTT inicializációs hiba: {ex.Message}");
                return false;
            }
        }

        private async Task SendSwitchCommandAsync(string switchId, string position)
        {
            Console.WriteLine($"[TrackHandlerService] SendSwitchCommand called: switchId={switchId}, position={position}");

            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                Console.WriteLine($"[TrackHandlerService] MQTT not initialized - initializing...");
                if (!await InitializeMqttAsync())
                {
                    Console.WriteLine($"[TrackHandlerService] Failed to initialize MQTT, skipping switch command");
                    return;
                }
            }

            try
            {
                // Rocrail switch command format: <sw id="SwitchID" cmd="straight"/> or <sw id="SwitchID" cmd="turnout"/>
                var rocrailCommand = $"<sw id=\"{switchId}\" cmd=\"{position}\"/>";
                var topic = _mqttConfig.TrackCommandTopic;

                Console.WriteLine($"[TrackHandlerService] MQTT Client Status: IsConnected={_mqttClient.IsConnected}");
                Console.WriteLine($"[TrackHandlerService] Publishing to topic: {topic}");
                Console.WriteLine($"[TrackHandlerService] Publishing message: {rocrailCommand}");

                  // Send the switch command (deduplication is handled in SetSwitchesForRoute)
                Console.WriteLine($"[TrackHandlerService] Sending switch command to MQTT: {rocrailCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(rocrailCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var publishResult = await _mqttClient.PublishAsync(mqttMessage);
                Console.WriteLine($"[TrackHandlerService] Switch command published, result: {publishResult.ReasonCode}");

                      if (publishResult.IsSuccess)
              {
                  Console.WriteLine($"[TrackHandlerService] Switch command sent successfully: {switchId} -> {position}");
                  _statusService?.ShowInfo($"Váltó állítása: {switchId} -> {position}");
              }
              else
              {
                  Console.WriteLine($"[TrackHandlerService] Switch command failed to publish: {publishResult.ReasonCode}");
                  _statusService?.ShowError($"Váltó állítási hiba: {switchId} -> {position}");
              }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error sending switch command: {ex.Message}");
                Console.WriteLine($"[TrackHandlerService] Stack trace: {ex.StackTrace}");
                _statusService?.ShowError($"Váltó állítási hiba: {ex.Message}");
            }
        }

        private async Task SendSystemPowerCommandAsync()
        {
            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                if (!await InitializeMqttAsync())
                    return;
            }

            try
            {
                // Rocrail system power command: <sys cmd="go"/>
                var powerCommand = "<sys cmd=\"go\"/>";

                Console.WriteLine($"[TrackHandlerService] Sending system power command: {powerCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_mqttConfig.TrackCommandTopic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(powerCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);
                await Task.Delay(1000); // Wait for power to come on
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error sending system power command: {ex.Message}");
                _statusService?.ShowError($"Rendszer áramkör hiba: {ex.Message}");
            }
        }

        private async Task SendSignalCommandAsync(string signalId, string aspect)
        {
            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                if (!await InitializeMqttAsync())
                    return;
            }

            try
            {
                // Rocrail signal command format: <sg id="SignalID" aspect="green"/>
                var rocrailCommand = $"<sg id=\"{signalId}\" aspect=\"{aspect}\"/>";

                // Check if this signal command is different from the last one sent
                lock (_lockObject)
                {
                    Console.WriteLine($"[TrackHandlerService] Signal command check for {signalId} - Last: '{(_lastSignalCommands.ContainsKey(signalId) ? _lastSignalCommands[signalId] : "null")}', Current: '{rocrailCommand}'");
                    if (_lastSignalCommands.ContainsKey(signalId) && _lastSignalCommands[signalId] == rocrailCommand)
                    {
                        Console.WriteLine($"[TrackHandlerService] Skipping duplicate signal command: {rocrailCommand}");
                        return;
                    }

                    // Store the signal command that was sent
                    _lastSignalCommands[signalId] = rocrailCommand;
                    Console.WriteLine($"[TrackHandlerService] Stored new signal command for {signalId}: {rocrailCommand}");
                }

                Console.WriteLine($"[TrackHandlerService] Sending signal command: {rocrailCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_mqttConfig.TrackSignalTopic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(rocrailCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);
                _statusService?.ShowInfo($"Jelzés állítása: {signalId} -> {aspect}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error sending signal command: {ex.Message}");
                _statusService?.ShowError($"Jelzés állítási hiba: {ex.Message}");
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("[TrackHandlerService] Started monitoring schedule and virtual clock");

            // Initialize MQTT and system power
            Console.WriteLine("[TrackHandlerService] Starting MQTT initialization...");
            if (await InitializeMqttAsync())
            {
                Console.WriteLine("[TrackHandlerService] MQTT connection established");
                // Send system power command once when service starts
                await SendSystemPowerCommandAsync();
                Console.WriteLine("[TrackHandlerService] System power command sent");
            }
            else
            {
                Console.WriteLine("[TrackHandlerService] MQTT initialization failed - no power command sent");
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var currentTime = _virtualClock.CurrentTime;
                    var currentTimeOnly = currentTime.TimeOfDay;

                    // Get upcoming timetable entries that are due
                    var upcomingSchedules = new List<TimetableEntries>();
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        upcomingSchedules = dbContext.TimetableEntries
                            .Include(te => te.Train)
                            .Include(te => te.SourcePlatform)
                                .ThenInclude(sp => sp.Station)
                            .Include(te => te.DestinationPlatform)
                                .ThenInclude(dp => dp.Station)
                            .Where(te => te.EntryState == EntryState.Upcoming && te.StartTime <= currentTimeOnly)
                            .ToList();
                    }

                    foreach (var timetableEntry in upcomingSchedules)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // Create unique key for this schedule entry (train + time)
                        var scheduleKey = $"{timetableEntry.Train_DB_ID}_{timetableEntry.StartTime:hh\\:mm}";

                        // Check if this train was recently processed within the last minute
                        lock (_lockObject)
                        {
                            if (_recentlyProcessedTrains.ContainsKey(scheduleKey))
                            {
                                var lastProcessed = _recentlyProcessedTrains[scheduleKey];
                                if ((currentTime - lastProcessed).TotalMinutes < 1.0)
                                {
                                    // Skip this entry as it was already processed recently
                                    continue;
                                }
                            }

                            // Mark this train as recently processed
                            _recentlyProcessedTrains[scheduleKey] = currentTime;
                        }

                        Console.WriteLine($"[TrackHandlerService] Processing timetable entry: Train {timetableEntry.Train?.Name} at {currentTime}");

                        // Set EntryState to InProgress when train starts
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            try
                            {
                                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                var trackedEntry = dbContext.TimetableEntries.Find(timetableEntry.DB_ID);
                                if (trackedEntry != null)
                                {
                                    trackedEntry.EntryState = EntryState.InProgress;
                                    dbContext.SaveChanges();
                                    Console.WriteLine($"[TrackHandlerService] Set EntryState to InProgress for train {timetableEntry.Train?.Name} (ID: {trackedEntry.DB_ID})");
                                }
                                else
                                {
                                    Console.WriteLine($"[TrackHandlerService] ERROR: Could not find timetable entry with ID {timetableEntry.DB_ID}");
                                }
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[TrackHandlerService] Database error updating EntryState to InProgress: {ex.GetType().Name}: {ex.Message}");
                                if (ex.InnerException != null)
                                {
                                    Console.WriteLine($"[TrackHandlerService] Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                                }
                            }
                        }

                        // Get train details
                        var train = timetableEntry.Train;
                        if (train == null)
                        {
                            Console.WriteLine($"[TrackHandlerService] ERROR: Train {timetableEntry.Train_DB_ID} not found");
                            continue;
                        }

                        // Set signals to green for departure (only if needed)
                        _ = Task.Run(async () =>
                        {
                            // Set source station signal to green
                            if (timetableEntry.SourceStation?.Name != null)
                            {
                                var signalId = $"{timetableEntry.SourceStation.Name}_signal";
                                bool shouldSendSignal = false;

                                // Check if signal should be sent (outside of lock)
                                lock (_lockObject)
                                {
                                    if (!_lastSignalCommands.ContainsKey(signalId) ||
                                        !_lastSignalCommands[signalId].Contains("green"))
                                    {
                                        shouldSendSignal = true;
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[TrackHandlerService] Signal {signalId} already green, skipping command");
                                    }
                                }

                                // Send signal command outside of lock
                                if (shouldSendSignal)
                                {
                                    await SendSignalCommandAsync(signalId, "green");
                                    await Task.Delay(500); // Small delay between commands
                                }
                            }
                        }, cancellationToken);

  
                        // Check if train already has an active manager
                        TrainManagerService trainManager = null;
                        bool needsNewManager = false;

                        lock (_lockObject)
                        {
                            if (!_trainManagers.ContainsKey(timetableEntry.Train_DB_ID))
                            {
                                // Create new manager only if one doesn't exist for this train
                                needsNewManager = true;
                            }
                            else
                            {
                                // Use existing manager
                                trainManager = _trainManagers[timetableEntry.Train_DB_ID];
                                Console.WriteLine($"[TrackHandlerService] Using existing TrainManagerService for train {train.Name} (ID: {timetableEntry.Train_DB_ID})");
                            }
                        }

                        if (needsNewManager)
                        {
                            // Create new Train Manager Service
                            trainManager = new TrainManagerService(_virtualClock, train, timetableEntry, _mqttConfig, _statusService, _serviceProvider, this);

                            lock (_lockObject)
                            {
                                _trainManagers[timetableEntry.Train_DB_ID] = trainManager;
                                _activeTrainManagers.Add(trainManager);
                            }

                            Console.WriteLine($"[TrackHandlerService] Created new TrainManagerService for train {train.Name} (ID: {timetableEntry.Train_DB_ID})");
                        }

                        // Plan route and configure switches before starting train movement
                        try
                        {
                            var sourceStation = timetableEntry.SourceStation?.Name ?? timetableEntry.SourcePlatform?.Station?.Name;
                            var destStation = timetableEntry.DestinationStation?.Name ?? timetableEntry.DestinationPlatform?.Station?.Name;

                            if (!string.IsNullOrEmpty(sourceStation) && !string.IsNullOrEmpty(destStation))
                            {
                                Console.WriteLine($"[TrackHandlerService] Planning route for {train.Name}: {sourceStation} -> {destStation}, direction: {train.Direction}");

                                // Only platform-level route planning is supported
                                if (timetableEntry.SourcePlatform == null || timetableEntry.DestinationPlatform == null)
                                {
                                    Console.WriteLine($"[TrackHandlerService] Route planning failed for {train.Name}: Source or destination platform not specified");
                                    continue;
                                }

                                var routeResult = await _unifiedPathfinder.PlanAndConfigureRouteAsync(
                                    timetableEntry.SourcePlatform,
                                    timetableEntry.DestinationPlatform,
                                    train.Name,
                                    train.Direction);

                                if (!routeResult.Success)
                                {
                                    Console.WriteLine($"[TrackHandlerService] Route planning failed for {train.Name}: {routeResult.ErrorMessage}");
                                    _statusService?.ShowError($"Route planning failed: {routeResult.ErrorMessage}", train.Name);

                                    // Continue with train movement even if route planning fails
                                    // The train manager will handle basic movement
                                }
                                else
                                {
                                    Console.WriteLine($"[TrackHandlerService] Route planning successful for {train.Name}: {routeResult.ConfiguredSwitches.Count} switches configured");
                                    _statusService?.ShowSuccess($"Route ready: {routeResult.ConfiguredSwitches.Count} switches configured", train.Name);
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[TrackHandlerService] Cannot plan route for {train.Name}: Missing station information");
                                _statusService?.ShowWarning("Route planning skipped: Missing station info", train.Name);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TrackHandlerService] Error in route planning for {train.Name}: {ex.Message}");
                            _statusService?.ShowError($"Route planning error: {ex.Message}", train.Name);
                            // Continue with train movement even if route planning fails
                        }

                        // Start the train manager if it hasn't been started yet
                        if (needsNewManager)
                        {
                            trainManager.Start();
                        }

                        // Register the train for arrival monitoring
                        if (timetableEntry.DestinationPlatform != null)
                        {
                            _arrivalMonitor.RegisterTrainArrival(train, timetableEntry.DestinationPlatform);
                        }

                        Console.WriteLine($"[TrackHandlerService] Spawned TrainManagerService for train {train.Name}");
                        _statusService?.ShowInfo($"Indítás: {train.Name} ({timetableEntry.SourceStation?.Name} -> {timetableEntry.DestinationStation?.Name})");
                    }

                    // Clean up only truly completed train managers (not those that just finished one journey)
                    lock (_lockObject)
                    {
                        // Only remove managers if they are explicitly stopped or in error state
                        var completedManagers = _activeTrainManagers.Where(manager => manager.IsCompleted).ToList();
                        foreach (var manager in completedManagers)
                        {
                            // Find and remove from the train manager dictionary
                            var trainManagerEntry = _trainManagers.FirstOrDefault(kvp => kvp.Value == manager);
                            if (trainManagerEntry.Key != 0)
                            {
                                _trainManagers.Remove(trainManagerEntry.Key);
                                Console.WriteLine($"[TrackHandlerService] Removed TrainManagerService for train ID {trainManagerEntry.Key}");
                            }
                        }
                        _activeTrainManagers.RemoveAll(manager => manager.IsCompleted);

                        // Clean up old entries in recently processed trains (older than 5 minutes)
                        var cutoffTime = currentTime.AddMinutes(-5);
                        var keysToRemove = _recentlyProcessedTrains
                            .Where(kvp => kvp.Value < cutoffTime)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in keysToRemove)
                        {
                            _recentlyProcessedTrains.Remove(key);
                        }

                        // Clean up old train commands to prevent memory growth
                        var oldTrainCommandKeys = _lastTrainCommands.Keys.Where(k => k.Contains("_")).Take(10).ToList();
                        foreach (var key in oldTrainCommandKeys.Take(5)) // Keep only recent entries
                        {
                            _lastTrainCommands.Remove(key);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrackHandlerService] ERROR: {ex.Message}");
                    _statusService?.ShowError($"Track Handler Service hiba: {ex.Message}");
                }

                // Reduced frequency check (1 second) to reduce CPU usage and MQTT spam
                Thread.Sleep(1000);
            }

            Console.WriteLine("[TrackHandlerService] Stopped");
        }
    }
}