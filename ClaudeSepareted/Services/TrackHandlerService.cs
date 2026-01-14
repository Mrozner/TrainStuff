using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using ClaudeSepareted.Domain;

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
        private readonly MqttInfrastructureService _mqttService;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private readonly List<TrainManagerService> _activeTrainManagers;
        private readonly Dictionary<string, DateTime> _recentlyProcessedTrains = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> _lastSwitchCommands = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastSignalCommands = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastTrainCommands = new Dictionary<string, string>();
        private readonly Dictionary<int, TrainManagerService> _trainManagers = new Dictionary<int, TrainManagerService>();
        private readonly object _lockObject = new object();
        private readonly AutoResetEvent _trainCompletedEvent = new AutoResetEvent(false);

        public TrackHandlerService(
            VirtualClock virtualClock,
            ApplicationDbContext dbContext,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            UnifiedPathfindingService unifiedPathfinder,
            TrainArrivalMonitorService arrivalMonitor,
            MqttInfrastructureService mqttService)
        {
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _unifiedPathfinder = unifiedPathfinder ?? throw new ArgumentNullException(nameof(unifiedPathfinder));
            _arrivalMonitor = arrivalMonitor ?? throw new ArgumentNullException(nameof(arrivalMonitor));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
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

        /// <summary>
        /// Called when a train completes its journey and releases switches.
        /// This triggers immediate reprocessing of waiting trains.
        /// </summary>
        public void OnTrainCompleted(string trainName)
        {
            Console.WriteLine($"[TrackHandlerService] Train {trainName} completed - signaling to reprocess waiting trains");
            _trainCompletedEvent.Set();
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
                Console.WriteLine("[TrackHandlerService] ============================================");
                Console.WriteLine("[TrackHandlerService] VIRTUAL MIDNIGHT RESET - Resetting everything");
                Console.WriteLine("[TrackHandlerService] ============================================");

                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // Reset ALL timetable entries to Upcoming (complete daily reset)
                    var allEntries = dbContext.TimetableEntries.ToList();

                    foreach (var entry in allEntries)
                    {
                        entry.EntryState = EntryState.Upcoming;
                        entry.ArrivedTime = null; // Clear arrived time
                    }

                    dbContext.SaveChanges();

                    Console.WriteLine($"[TrackHandlerService] ✅ Reset {allEntries.Count} timetable entries to Upcoming");
                    _statusService?.ShowInfo($"Éjfél: {allEntries.Count} menetrendi bejegyzés visszaállítva 'Érkező' állapotra");
                }

                // Clear the recently processed trains dictionary to allow reprocessing
                lock (_lockObject)
                {
                    var processedCount = _recentlyProcessedTrains.Count;
                    _recentlyProcessedTrains.Clear();
                    Console.WriteLine($"[TrackHandlerService] ✅ Cleared {processedCount} entries from recently processed list");
                }

                // Reset all train states to Waiting
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var allTrains = dbContext.Trains.ToList();

                    foreach (var train in allTrains)
                    {
                        train.State = TrainState.Waiting;
                        train.CurrentSpeed = Speed.STOP;
                    }

                    dbContext.SaveChanges();
                    Console.WriteLine($"[TrackHandlerService] ✅ Reset {allTrains.Count} trains to Waiting state");
                }

                Console.WriteLine("[TrackHandlerService] ============================================");
                Console.WriteLine("[TrackHandlerService] MIDNIGHT RESET COMPLETE - New virtual day started");
                Console.WriteLine("[TrackHandlerService] ============================================");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] ❌ Error during midnight reset: {ex.Message}");
                _statusService?.ShowError($"Éjfél alaphelyzetbe állítási hiba: {ex.Message}");
            }
        }

        
        private async Task SendSwitchCommandAsync(string switchId, string position)
        {
            Console.WriteLine($"[TrackHandlerService] SendSwitchCommand called: switchId={switchId}, position={position}");

            try
            {
                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.Switch(switchId, position);
                var topic = _mqttConfig.TrackCommandTopic;

                Console.WriteLine($"[TrackHandlerService] Publishing to topic: {topic}");
                Console.WriteLine($"[TrackHandlerService] Publishing message: {rocrailCommand}");

                // Send the switch command using centralized MQTT service (deduplication is handled in SetSwitchesForRoute)
                Console.WriteLine($"[TrackHandlerService] Sending switch command to MQTT: {rocrailCommand}");

                var success = await _mqttService.PublishAsync(topic, rocrailCommand);

                if (success)
                {
                    Console.WriteLine($"[TrackHandlerService] Switch command sent successfully: {switchId} -> {position}");
                    _statusService?.ShowInfo($"Váltó állítása: {switchId} -> {position}");
                }
                else
                {
                    Console.WriteLine($"[TrackHandlerService] Switch command failed to publish via MQTT infrastructure");
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
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.SystemPower(true);

                Console.WriteLine($"[TrackHandlerService] Sending system power command: {powerCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrackCommandTopic, powerCommand);

                if (success)
                {
                    await Task.Delay(1000); // Wait for power to come on
                }
                else
                {
                    Console.WriteLine($"[TrackHandlerService] Failed to publish system power command via MQTT infrastructure");
                    _statusService?.ShowError($"Rendszer áramkör hiba: MQTT sikertelen");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackHandlerService] Error sending system power command: {ex.Message}");
                _statusService?.ShowError($"Rendszer áramkör hiba: {ex.Message}");
            }
        }

        private async Task SendSignalCommandAsync(string signalId, string aspect)
        {

            try
            {
                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.Signal(signalId, aspect);

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

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrackSignalTopic, rocrailCommand);

                if (success)
                {
                    _statusService?.ShowInfo($"Jelzés állítása: {signalId} -> {aspect}");
                }
                else
                {
                    Console.WriteLine($"[TrackHandlerService] Failed to publish signal command via MQTT infrastructure");
                    _statusService?.ShowError($"Jelzés állítási hiba: {signalId} -> {aspect} (MQTT sikertelen)");
                }
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

            // Initialize centralized MQTT and system power
            Console.WriteLine("[TrackHandlerService] Starting MQTT initialization...");
            if (await _mqttService.InitializeAsync())
            {
                Console.WriteLine("[TrackHandlerService] MQTT connection established via centralized infrastructure");
                // Send system power command once when service starts
                await SendSystemPowerCommandAsync();
                Console.WriteLine("[TrackHandlerService] System power command sent");
            }
            else
            {
                Console.WriteLine("[TrackHandlerService] Centralized MQTT initialization failed - no power command sent");
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

                        // Get all upcoming entries
                        upcomingSchedules = dbContext.TimetableEntries
                            .Include(te => te.Train)
                            .Include(te => te.SourcePlatform)
                                .ThenInclude(sp => sp.Station)
                            .Include(te => te.DestinationPlatform)
                                .ThenInclude(dp => dp.Station)
                            .Where(te => te.EntryState == EntryState.Upcoming && te.StartTime <= currentTimeOnly)
                            .OrderBy(te => te.StartTime)
                            .ToList();
                    }

                    foreach (var timetableEntry in upcomingSchedules)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // Create unique key for this schedule entry using the DB_ID to ensure each entry is processed only once
                        var scheduleKey = $"entry_{timetableEntry.DB_ID}";

                        // Check if this exact timetable entry was already processed
                        lock (_lockObject)
                        {
                            if (_recentlyProcessedTrains.ContainsKey(scheduleKey))
                            {
                                // This entry was already processed - skip it completely
                                continue;
                            }

                            // Mark this entry as processed (permanent, not time-based)
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

  
                        // Check if train already has an active manager from a previous journey
                        TrainManagerService trainManager = null;
                        bool skipNewSchedule = false;

                        lock (_lockObject)
                        {
                            if (_trainManagers.ContainsKey(timetableEntry.Train_DB_ID))
                            {
                                var oldManager = _trainManagers[timetableEntry.Train_DB_ID];

                                // Handle based on train state
                                if (train.State == TrainState.Arrived)
                                {
                                    // Train has arrived - just remove the old manager without stopping the train
                                    // The train is already stopped, so we can start a new journey
                                    Console.WriteLine($"[TrackHandlerService] Train {train.Name} arrived - removing old manager for new journey");
                                    _activeTrainManagers.Remove(oldManager);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);

                                    // Reset train state to Waiting for the new journey
                                    train.State = TrainState.Waiting;
                                    Console.WriteLine($"[TrackHandlerService] Reset train {train.Name} state to Waiting for new schedule");
                                }
                                else if (train.State == TrainState.Stopped)
                                {
                                    // Train is stopped - similar to arrived, just remove the manager
                                    Console.WriteLine($"[TrackHandlerService] Train {train.Name} stopped - removing old manager for new journey");
                                    _activeTrainManagers.Remove(oldManager);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);

                                    // Reset train state to Waiting for the new journey
                                    train.State = TrainState.Waiting;
                                }
                                else if (train.State == TrainState.Moving || train.State == TrainState.PrepareToStop)
                                {
                                    // Train is still actively running - cannot start new schedule yet
                                    Console.WriteLine($"[TrackHandlerService] Train {train.Name} still active (state: {train.State}) - cannot start new schedule yet");
                                    skipNewSchedule = true;
                                }
                                else
                                {
                                    // Any other state - just remove the manager and proceed
                                    Console.WriteLine($"[TrackHandlerService] Train {train.Name} in state {train.State} - removing old manager");
                                    _activeTrainManagers.Remove(oldManager);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);
                                    train.State = TrainState.Waiting;
                                }
                            }
                        }

                        // If the train is still running, skip this schedule entry for now
                        if (skipNewSchedule)
                        {
                            Console.WriteLine($"[TrackHandlerService] Skipping schedule entry for train {train.Name} - previous journey still in progress");
                            continue;
                        }

                        // Plan route first to get required switches, then attempt reservation
                        List<string> requiredSwitchNames = new List<string>();
                        bool routePlanningSuccessful = false;
                        bool canStartTrain = false;

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
                                    canStartTrain = false;
                                }
                                else
                                {
                                    // Step 1: Plan route without configuring switches to get required switch list
                                    var routeResult = await _unifiedPathfinder.PlanAndConfigureRouteAsync(
                                        timetableEntry.SourcePlatform,
                                        timetableEntry.DestinationPlatform,
                                        train.Name,
                                        train.Direction,
                                        configureSwitches: false);  // Don't configure switches yet

                                    if (!routeResult.Success)
                                    {
                                        Console.WriteLine($"[TrackHandlerService] Route planning failed for {train.Name}: {routeResult.ErrorMessage}");
                                        _statusService?.ShowError($"Route planning failed: {routeResult.ErrorMessage}", train.Name);
                                        canStartTrain = false;
                                    }
                                    else
                                    {
                                        routePlanningSuccessful = true;
                                        requiredSwitchNames = routeResult.ConfiguredSwitches.Select(sc => sc.SwitchName).ToList();
                                        Console.WriteLine($"[TrackHandlerService] Route planned successfully for {train.Name}: {requiredSwitchNames.Count} switches required");

                                        // Step 2: Attempt to reserve switches
                                        bool reservationSuccess = _unifiedPathfinder.TryReserveSwitches(requiredSwitchNames, train.Name);

                                        if (reservationSuccess)
                                        {
                                            Console.WriteLine($"[TrackHandlerService] ✅ Switch reservation successful for {train.Name}");
                                            canStartTrain = true;
                                        }
                                        else
                                        {
                                            Console.WriteLine($"[TrackHandlerService] ⛔ {train.Name} waiting for resources (switches locked by another train)");
                                            _statusService?.ShowWarning($"Várakozás: {train.Name} (másik vonat foglalja a váltókat)", train.Name);
                                            canStartTrain = false;
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Console.WriteLine($"[TrackHandlerService] Cannot plan route for {train.Name}: Missing station information");
                                _statusService?.ShowWarning("Route planning skipped: Missing station info", train.Name);
                                canStartTrain = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[TrackHandlerService] Error in route planning for {train.Name}: {ex.Message}");
                            _statusService?.ShowError($"Route planning error: {ex.Message}", train.Name);
                            canStartTrain = false;
                        }

                        // If reservation failed, mark entry back to Upcoming and remove from processed to retry next loop
                        if (!canStartTrain && routePlanningSuccessful)
                        {
                            Console.WriteLine($"[TrackHandlerService] Train {train.Name} will wait - resetting to Upcoming state for retry");

                            // Reset the timetable entry state back to Upcoming so it will be retried
                            using (var scope = _serviceProvider.CreateScope())
                            {
                                try
                                {
                                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                    var trackedEntry = dbContext.TimetableEntries.Find(timetableEntry.DB_ID);
                                    if (trackedEntry != null)
                                    {
                                        trackedEntry.EntryState = EntryState.Upcoming;
                                        dbContext.SaveChanges();
                                        Console.WriteLine($"[TrackHandlerService] Reset EntryState to Upcoming for train {train.Name} (will retry)");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TrackHandlerService] Database error resetting EntryState: {ex.Message}");
                                }
                            }

                            // Remove from recently processed so this entry will be reconsidered in next loop
                            lock (_lockObject)
                            {
                                scheduleKey = $"entry_{timetableEntry.DB_ID}";
                                _recentlyProcessedTrains.Remove(scheduleKey);
                            }

                            continue; // Skip this train for this iteration
                        }

                        // Only create train manager and start if we can proceed
                        if (canStartTrain)
                        {
                            // Step 3: Create TrainManagerService with reserved switches
                            trainManager = new TrainManagerService(_virtualClock, train, timetableEntry, _mqttConfig, _statusService, _serviceProvider, _mqttService, this, requiredSwitchNames, _unifiedPathfinder);

                            lock (_lockObject)
                            {
                                _trainManagers[timetableEntry.Train_DB_ID] = trainManager;
                                _activeTrainManagers.Add(trainManager);
                            }

                            Console.WriteLine($"[TrackHandlerService] Created new TrainManagerService for train {train.Name} (ID: {timetableEntry.Train_DB_ID}) with {requiredSwitchNames.Count} reserved switches");

                            // Step 4: Now configure the switches (we have the locks)
                            if (requiredSwitchNames.Any())
                            {
                                try
                                {
                                    var routeResult = await _unifiedPathfinder.PlanAndConfigureRouteAsync(
                                        timetableEntry.SourcePlatform,
                                        timetableEntry.DestinationPlatform,
                                        train.Name,
                                        train.Direction,
                                        configureSwitches: true);  // Now actually configure switches

                                    if (!routeResult.Success)
                                    {
                                        Console.WriteLine($"[TrackHandlerService] Switch configuration failed for {train.Name}: {routeResult.ErrorMessage}");
                                        _statusService?.ShowError($"Switch config failed: {routeResult.ErrorMessage}", train.Name);
                                    }
                                    else
                                    {
                                        Console.WriteLine($"[TrackHandlerService] Switch configuration successful for {train.Name}: {routeResult.ConfiguredSwitches.Count} switches configured");
                                        _statusService?.ShowSuccess($"Route configured: {routeResult.ConfiguredSwitches.Count} switches", train.Name);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TrackHandlerService] Error in switch configuration for {train.Name}: {ex.Message}");
                                    _statusService?.ShowError($"Switch config error: {ex.Message}", train.Name);
                                }
                            }

                            // Start the train manager for the new journey
                            trainManager.Start();

                            // Register the train for arrival monitoring
                            if (timetableEntry.DestinationPlatform != null)
                            {
                                _arrivalMonitor.RegisterTrainArrival(train, timetableEntry.DestinationPlatform);
                            }

                            Console.WriteLine($"[TrackHandlerService] Spawned TrainManagerService for train {train.Name}");
                            _statusService?.ShowInfo($"Indítás: {train.Name} ({timetableEntry.SourceStation?.Name} -> {timetableEntry.DestinationStation?.Name})");
                        }
                        else
                        {
                            // Route planning failed completely - continue without starting train
                            continue;
                        }
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

                        // NOTE: _recentlyProcessedTrains is cleared at virtual midnight in OnMidnightReset()
                        // We don't use time-based cleanup here because the virtual clock resets to 2024-01-01
                        // which would make all timestamps appear to be "in the future" and never expire

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

                // Wait for train completion signal or timeout (1 second)
                // This allows immediate reprocessing when switches are released
                _trainCompletedEvent.WaitOne(1000);
            }

            Console.WriteLine("[TrackHandlerService] Stopped");
        }
    }
}