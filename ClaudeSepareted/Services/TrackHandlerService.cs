using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ClaudeSepareted.Domain;
using Microsoft.Extensions.Logging;
using ClaudeSepareted;

namespace ClaudeSepareted.Services
{
    public class TrackHandlerService : IDisposable
    {
        private readonly VirtualClock _virtualClock;
        private readonly ApplicationDbContext _dbContext;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService _statusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly PathfindingService _pathfinder;
        private readonly MqttInfrastructureService _mqttService;
        private readonly TrackOccupancyService _trackOccupancyService;
        private readonly ILogger<TrackHandlerService> _logger;
        private readonly FileLoggingService? _fileLogger;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly List<TrainMovementMonitor> _activeTrainManagers;
        private readonly Dictionary<string, DateTime> _recentlyProcessedTrains = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, string> _lastSignalCommands = new Dictionary<string, string>();
        private readonly Dictionary<string, (string Command, DateTime Timestamp)> _lastTrainCommands = new Dictionary<string, (string Command, DateTime Timestamp)>();
        private readonly Dictionary<int, TrainMovementMonitor> _trainManagers = new Dictionary<int, TrainMovementMonitor>();
        private readonly object _lockObject = new object();
        private readonly BlockReservationService _blockReservationService;
        private readonly SwitchConfigurationService _switchConfigurationService;
        private readonly SemaphoreSlim _trainCompletedSemaphore = new SemaphoreSlim(0, 1);
        private readonly SemaphoreSlim _midnightResetLock = new SemaphoreSlim(1, 1);
        private readonly List<Task> _backgroundTasks = new List<Task>();
        private Task? _runTask;

        // In-memory timetable cache for event-driven scheduling (lightweight DTO pattern)
        private List<CachedSchedule> _cachedTimetableEntries = new List<CachedSchedule>();
        private volatile bool _timetableNeedsRefresh = true;

        /// <summary>
        /// Lightweight record for caching timetable schedule data without EF Core entity tracking issues.
        /// Only stores the minimal data needed for timeout calculations and scheduling decisions.
        /// </summary>
        private record CachedSchedule(int Id, TimeSpan StartTime);

        public TrackHandlerService(
            VirtualClock virtualClock,
            ApplicationDbContext dbContext,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            PathfindingService pathfinder,
            MqttInfrastructureService mqttService,
            TrackOccupancyService trackOccupancyService = null,
            BlockReservationService blockReservationService = null,
            SwitchConfigurationService switchConfigurationService = null,
            ILogger<TrackHandlerService> logger = null,
            FileLoggingService fileLogger = null)
        {
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _trackOccupancyService = trackOccupancyService; // Optional for backward compatibility
            _logger = logger;
            _fileLogger = fileLogger;
            _cancellationTokenSource = new CancellationTokenSource();
            _activeTrainManagers = new List<TrainMovementMonitor>();

            // Store additional services for TrainMovementMonitor creation
            _blockReservationService = blockReservationService;
            _switchConfigurationService = switchConfigurationService;

            // Subscribe to midnight reset event
            _virtualClock.MidnightReset += OnMidnightReset;
        }

        public void Start()
        {
            // If the token was previously cancelled (e.g., user went to Admin Panel and came back),
            // we must create a fresh token before starting the background task.
            if (_cancellationTokenSource == null || _cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = new CancellationTokenSource();
            }

            _runTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token), _cancellationTokenSource.Token);
        }

        /// <summary>
        /// Called when a train completes its journey and releases switches.
        /// This triggers immediate reprocessing of waiting trains.
        /// Thread-safe semaphore release with overflow protection.
        /// </summary>
        public void OnTrainCompleted()
        {
            try
            {
                // Rely entirely on the semaphore's internal thread-safe counting mechanism.
                // No need to check CurrentCount - that would introduce a TOCTOU race condition.
                // The try/catch handles all edge cases safely.
                _trainCompletedSemaphore.Release();
            }
            catch (SemaphoreFullException)
            {
                // Ignored: The loop is already signaled to wake up.
                // This occurs when multiple threads release simultaneously.
            }
        }

        public void Stop()
        {
            _cancellationTokenSource.Cancel();

            // Unsubscribe from midnight reset event
            _virtualClock.MidnightReset -= OnMidnightReset;

            // Stop all active train monitors
            lock (_lockObject)
            {
                foreach (var monitor in _activeTrainManagers)
                {
                    monitor.StopMonitoring();
                    monitor.Dispose(); // Ensure resources are released
                }
                _activeTrainManagers.Clear();
            }

            // Await pending background signal tasks to allow graceful shutdown
            // Copy the list to avoid holding the lock during await
            Task[] pendingTasks;
            lock (_lockObject)
            {
                pendingTasks = _backgroundTasks.ToArray();
            }

            if (pendingTasks.Length > 0)
            {
                try
                {
                    // Wait up to 2 seconds for pending signal commands to complete
                    Task.WaitAll(pendingTasks, TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // Ignore exceptions during shutdown - tasks are being cancelled anyway
                }
            }

            // Dispose the semaphore
            _trainCompletedSemaphore.Dispose();
        }

        public bool ShouldSendTrainCommand(string trainName, string command)
        {
            lock (_lockObject)
            {
                // Group by movement category to track state changes
                var key = $"{trainName}_movement";

                // Check if the last sent command matches the new command
                if (_lastTrainCommands.TryGetValue(key, out var last) && last.Command == command)
                {
                    return false; // Same command was sent recently, ignore it
                }

                // Store the command with timestamp
                _lastTrainCommands[key] = (command, DateTime.UtcNow);
                return true;
            }
        }

        public bool ShouldSendTrainPowerCommand(string trainName, bool powerOn)
        {
            lock (_lockObject)
            {
                // Group power commands by train
                var key = $"{trainName}_power";
                var powerCommand = powerOn ? "on" : "off";

                // Check if the last power command matches the new command
                if (_lastTrainCommands.TryGetValue(key, out var last) && last.Command == powerCommand)
                {
                    return false;
                }

                // Store the power command with timestamp
                _lastTrainCommands[key] = (powerCommand, DateTime.UtcNow);
                return true;
            }
        }

        /// <summary>
        /// Refreshes the in-memory timetable cache from the database.
        /// Called on startup and after midnight reset.
        /// Uses lightweight DTO projection to avoid EF Core entity tracking issues.
        /// </summary>
        private void RefreshTimetableCache()
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // 1. Filter and Sort in SQL
                    // 2. Project to an EF-friendly anonymous type
                    // 3. Bring into memory (AsEnumerable)
                    // 4. Create the C# record
                    var schedules = dbContext.TimetableEntries
                        .Where(te => te.EntryState == EntryState.Upcoming)
                        .OrderBy(te => te.StartTime)
                        .Select(te => new { te.DB_ID, te.StartTime })
                        .AsEnumerable()
                        .Select(x => new CachedSchedule(x.DB_ID, x.StartTime))
                        .ToList();

                    lock (_lockObject)
                    {
                        _cachedTimetableEntries = schedules;
                        _timetableNeedsRefresh = false;
                    }

                    _logger?.LogInformation("Timetable cache refreshed: {Count} entries loaded", schedules.Count);
                    _statusService?.ShowInfo($"Timetable cache refreshed: {schedules.Count} entries loaded");
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Timetable cache refresh error: {ex.Message} - [TrackHandlerService]");
                // Don't set _timetableNeedsRefresh to false on error, so it will retry
            }
        }

        /// <summary>
        /// Gets the next upcoming timetable entry from the in-memory cache.
        /// </summary>
        private CachedSchedule? GetNextUpcomingEntry(TimeSpan currentTime)
        {
            lock (_lockObject)
            {
                return _cachedTimetableEntries
                    .Where(te => te.StartTime <= currentTime)
                    .OrderBy(te => te.StartTime)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// Calculates the dynamic timeout until the next scheduled train departure.
        /// </summary>
        private int CalculateDynamicTimeout(TimeSpan currentTime)
        {
            lock (_lockObject)
            {
                var nextEntry = _cachedTimetableEntries
                    .Where(te => te.StartTime > currentTime)
                    .OrderBy(te => te.StartTime)
                    .FirstOrDefault();

                if (nextEntry == null)
                {
                    // No upcoming trains found, use default timeout
                    return 1000;
                }

                var timeUntilNext = nextEntry.StartTime - currentTime;
                var timeoutMs = (int)Math.Max(100, timeUntilNext.TotalMilliseconds); // Minimum 100ms

                _logger?.LogDebug("Next train schedule ID {Id} at {Time}, waiting {Timeout}ms (Virtual Time)",
                    nextEntry.Id, nextEntry.StartTime, timeoutMs);
                _statusService?.ShowInfo($"Next train: ID {nextEntry.Id} at {nextEntry.StartTime}, waiting {timeoutMs}ms (Virtual Time)");
                return timeoutMs;
            }
        }

        private void OnMidnightReset(object? sender, EventArgs e)
        {
            // Trigger the full reset in the background
            _ = Task.Run(async () => await ForceSystemResetAsync());
        }

        public async Task ForceSystemResetAsync()
        {
            await _midnightResetLock.WaitAsync();
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    // 1. Reset ALL timetable entries to Upcoming
                    var allEntries = await dbContext.TimetableEntries.ToListAsync();
                    foreach (var entry in allEntries)
                    {
                        entry.EntryState = EntryState.Upcoming;
                        entry.ArrivedTime = null;
                    }

                    // 2. Reset ALL train states to Waiting
                    var allTrains = await dbContext.Trains.ToListAsync();
                    foreach (var train in allTrains)
                    {
                        train.State = TrainState.Waiting;
                        train.CurrentSpeed = Speed.ZERO;
                    }

                    await dbContext.SaveChangesAsync();

                    _fileLogger?.Log("[TRACK HANDLER] Full system reset triggered. Timetable and trains reset to Waiting/Upcoming.");
                    _logger?.LogInformation("System reset: {Count} timetable entries and {TrainCount} trains reset", allEntries.Count, allTrains.Count);
                    _statusService?.ShowInfo($"Rendszer tisztítva: {allEntries.Count} menetrend és {allTrains.Count} vonat alaphelyzetben.");
                }

                // 3. Clear the recently processed trains dictionary to allow reprocessing
                lock (_lockObject)
                {
                    _recentlyProcessedTrains.Clear();
                }

                // 4. Trigger timetable cache refresh
                _timetableNeedsRefresh = true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to run system reset.");
                _statusService?.ShowError($"Rendszer alaphelyzetbe állítási hiba: {ex.Message} - [TrackHandlerService]");
            }
            finally
            {
                _midnightResetLock.Release();
            }
        }

        private async Task SendSystemPowerCommandAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.SystemPower(true);

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrackCommandTopic, powerCommand);

                if (success)
                {
                    await Task.Delay(1000, cancellationToken); // Wait for power to come on
                }
                else
                {
                    _statusService?.ShowError($"Rendszer áramkör hiba: MQTT sikertelen - [TrackHandlerService]");
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Rendszer áramkör hiba: {ex.Message} - [TrackHandlerService]");
            }
        }

        private async Task SendSignalCommandAsync(string signalId, string aspect, CancellationToken cancellationToken)
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.Signal(signalId, aspect);

                // Check if this signal command is different from the last one sent
                lock (_lockObject)
                {
                    if (_lastSignalCommands.ContainsKey(signalId) && _lastSignalCommands[signalId] == rocrailCommand)
                    {
                        return;
                    }

                    // Store the signal command that was sent
                    _lastSignalCommands[signalId] = rocrailCommand;
                }

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrackSignalTopic, rocrailCommand);

                if (success)
                {
                    _logger?.LogDebug("Signal set: {SignalId} -> {Aspect}", signalId, aspect);
                    _statusService?.ShowInfo($"Jelzés állítása: {signalId} -> {aspect}");
                }
                else
                {
                    _statusService?.ShowError($"Jelzés állítási hiba: {signalId} -> {aspect} (MQTT sikertelen) - [TrackHandlerService]");
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Jelzés állítási hiba: {ex.Message} - [TrackHandlerService]");
            }
        }

        /// <summary>
        /// Helper method to clean up completed background tasks.
        /// Call this at the end of the main while loop to prevent memory leaks.
        /// </summary>
        private void CleanupCompletedTasks()
        {
            lock (_lockObject)
            {
                // Clean up completed background tasks to prevent memory leaks
                var completedTasks = _backgroundTasks.Where(t => t.IsCompleted).ToList();
                foreach (var task in completedTasks)
                {
                    _backgroundTasks.Remove(task);
                }
            }
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {

            // Initialize centralized MQTT and system power
            if (await _mqttService.InitializeAsync())
            {
                // Send system power command once when service starts
                await SendSystemPowerCommandAsync(cancellationToken);
            }
            else
            {
                _logger?.LogCritical("MQTT Service failed to initialize. Background track handler cannot proceed.");
                _statusService?.ShowError("Kritikus hiba: MQTT nem inicializálható. - [TrackHandlerService]");
                return; // Do not enter the while loop if communication is dead
            }

            // --- STARTUP RECOVERY: Run full midnight reset logic to guarantee a clean slate ---
            await ForceSystemResetAsync();

            // Initial timetable cache load for event-driven scheduling
            RefreshTimetableCache();

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    try
                    {
                        // Pause the loop briefly if a midnight database reset is actively running
                        await _midnightResetLock.WaitAsync(cancellationToken);
                        _midnightResetLock.Release();

                        // Refresh timetable cache if needed (e.g., after midnight reset)
                        if (_timetableNeedsRefresh)
                        {
                            RefreshTimetableCache();
                        }

                        var currentTime = _virtualClock.CurrentTime;
                        var currentTimeOnly = currentTime.TimeOfDay;
                        int dynamicTimeout; // Declare at while loop scope level
                        double speedMultiplier;

                        // Get upcoming timetable entries from in-memory cache (lightweight DTO)
                        var upcomingSchedule = GetNextUpcomingEntry(currentTimeOnly);

                        if (upcomingSchedule == null)
                        {
                            // No trains due yet, calculate dynamic wait time
                            dynamicTimeout = CalculateDynamicTimeout(currentTimeOnly);

                            // Apply virtual clock speed multiplier to convert virtual ms to real-time ms.
                            // We use Math.Max to prevent division by zero if the clock is paused or extremely slow.
                            speedMultiplier = Math.Max(0.0001, _virtualClock.SpeedMultiplier);
                            var realTimeout = (int)(dynamicTimeout / speedMultiplier);

                            // Wait for train completion signal or dynamic timeout
                            await _trainCompletedSemaphore.WaitAsync(realTimeout, cancellationToken);
                            continue;
                        }

                        if (cancellationToken.IsCancellationRequested)
                            break;

                        // Create unique key for this schedule entry using the DB_ID to ensure each entry is processed only once
                        var scheduleKey = $"entry_{upcomingSchedule.Id}";

                        // Check if this exact timetable entry was already processed
                        bool shouldSkip = false;
                        lock (_lockObject)
                        {
                            if (_recentlyProcessedTrains.ContainsKey(scheduleKey))
                            {
                                // This entry was already processed - skip it completely
                                shouldSkip = true;
                            }
                            else
                            {
                                // Mark this entry as processed (permanent, not time-based)
                                _recentlyProcessedTrains[scheduleKey] = currentTime;
                            }
                        }

                        if (shouldSkip)
                        {
                            // Calculate dynamic wait time and continue
                            dynamicTimeout = CalculateDynamicTimeout(currentTimeOnly);

                            // Apply virtual clock speed multiplier to convert virtual ms to real-time ms.
                            // We use Math.Max to prevent division by zero if the clock is paused or extremely slow.
                            speedMultiplier = Math.Max(0.0001, _virtualClock.SpeedMultiplier);
                            var realTimeout = (int)(dynamicTimeout / speedMultiplier);

                            await _trainCompletedSemaphore.WaitAsync(realTimeout, cancellationToken);
                            continue;
                        }

                        // Fetch the live entity from database using the cached ID
                        TimetableEntries? timetableEntry = null;
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            try
                            {
                                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                timetableEntry = dbContext.TimetableEntries
                                    .Include(te => te.Train)
                                    .Include(te => te.SourcePlatform)
                                        .ThenInclude(p => p.Station)
                                    .Include(te => te.DestinationPlatform)
                                        .ThenInclude(p => p.Station)
                                    .FirstOrDefault(te => te.DB_ID == upcomingSchedule.Id);

                                if (timetableEntry == null)
                                {
                                    _logger?.LogWarning("Attempted to process TimetableEntry {Id}, but it was not found in the database.", upcomingSchedule.Id);
                                    continue; // Skip this entry if it no longer exists
                                }

                                // Set EntryState to InProgress when train starts
                                var trackedEntry = await dbContext.TimetableEntries.FindAsync(timetableEntry.DB_ID);
                                if (trackedEntry != null)
                                {
                                    trackedEntry.EntryState = EntryState.InProgress;
                                    await dbContext.SaveChangesAsync(cancellationToken);
                                }
                                else
                                {
                                    _logger?.LogWarning("Attempted to update TimetableEntry {Id} to InProgress, but it was not found in the database.", timetableEntry.DB_ID);
                                }
                            }
                            catch (Exception ex)
                            {
                                var innerError = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                                _logger?.LogError(ex, "Failed to update database state to InProgress. Train will still depart. Error: {InnerError} - [TrackHandlerService]", innerError);
                                _statusService?.ShowError($"Adatbázis figyelmeztetés: {innerError} - [TrackHandlerService]");
                                // Notice: We removed 'continue;' so the hardware train departure is not aborted by a software database glitch!
                            }
                        }

                        // Double-check we have a valid entry
                        if (timetableEntry == null)
                        {
                            continue;
                        }

                        // Get train details
                        var train = timetableEntry.Train;
                        if (train == null)
                        {
                            continue;
                        }

                        // Set signals to green for departure (only if needed)
                        var signalTask = Task.Run(async () =>
                        {
                            try
                            {
                                // Set source platform signal to green
                                if (timetableEntry.SourcePlatform?.Name != null)
                                {
                                    var signalId = $"{timetableEntry.SourcePlatform.Name}_signal";
                                    bool shouldSendSignal = false;

                                    // Check if signal should be sent (outside of lock)
                                    lock (_lockObject)
                                    {
                                        if (!_lastSignalCommands.ContainsKey(signalId) ||
                                            !_lastSignalCommands[signalId].Contains("green"))
                                        {
                                            shouldSendSignal = true;
                                        }
                                    }

                                    // Send signal command outside of lock
                                    if (shouldSendSignal)
                                    {
                                        await SendSignalCommandAsync(signalId, "green", cancellationToken);
                                        await Task.Delay(500, cancellationToken); // Small delay between commands
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _statusService?.ShowError($"Hiba a jelzés beállításakor: {ex.Message} - [TrackHandlerService]");
                            }
                        }, cancellationToken);

                        // Track the background task for graceful shutdown
                        lock (_lockObject)
                        {
                            _backgroundTasks.Add(signalTask);
                        }

                        // Check if train already has an active monitor from a previous journey
                        TrainMovementMonitor trainMonitor = null;
                        bool skipNewSchedule = false;

                        lock (_lockObject)
                        {
                            if (_trainManagers.ContainsKey(timetableEntry.Train_DB_ID))
                            {
                                var oldMonitor = _trainManagers[timetableEntry.Train_DB_ID];

                                // Handle based on train state
                                if (train.State == TrainState.Arrived)
                                {
                                    // Train has arrived - just remove the old monitor without stopping the train
                                    // The train is already stopped, so we can start a new journey
                                    _activeTrainManagers.Remove(oldMonitor);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);

                                    // Reset train state to Waiting for the new journey
                                    train.State = TrainState.Waiting;
                                }
                                else if (train.State == TrainState.Stopped)
                                {
                                    // Train is stopped - similar to arrived, just remove the monitor
                                    _activeTrainManagers.Remove(oldMonitor);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);

                                    // Reset train state to Waiting for the new journey
                                    train.State = TrainState.Waiting;
                                }
                                else if (train.State == TrainState.Moving || train.State == TrainState.PrepareToStop)
                                {
                                    // Train is still actively running - cannot start new schedule yet
                                    skipNewSchedule = true;
                                }
                                else
                                {
                                    // Any other state - just remove the monitor and proceed
                                    _activeTrainManagers.Remove(oldMonitor);
                                    _trainManagers.Remove(timetableEntry.Train_DB_ID);
                                    train.State = TrainState.Waiting;
                                }
                            }
                        }

                        // If the train is still running, skip this schedule entry for now
                        if (skipNewSchedule)
                        {
                            continue;
                        }

                        // Simplified route planning: No global locking, just calculate path
                        bool canStartTrain = false;
                        RoutePlan routePlan = null;

                        try
                        {
                            var sourcePlatform = timetableEntry.SourcePlatform?.DisplayName;
                            var destPlatform = timetableEntry.DestinationPlatform?.DisplayName;

                            if (!string.IsNullOrEmpty(sourcePlatform) && !string.IsNullOrEmpty(destPlatform))
                            {
                                _fileLogger?.Log($"[TRACK HANDLER] Attempting to route {train.Name} from {sourcePlatform} to {destPlatform}.");

                                // Plan route without configuring switches or reserving blocks
                                // The block reservation system will handle JIT reservation
                                var routeResult = await _pathfinder.PlanAndConfigureRouteAsync(
                                    timetableEntry.SourcePlatform,
                                    timetableEntry.DestinationPlatform,
                                    train.Name,
                                    train.Direction,
                                    configureSwitches: false);  // Don't configure switches yet

                                if (routeResult.Success)
                                {
                                    routePlan = routeResult.RoutePlan;
                                    canStartTrain = true;
                                }
                                else
                                {
                                    _fileLogger?.Log($"[TRACK HANDLER] Route planning failed for {train.Name}: {routeResult.ErrorMessage}");
                                    _logger?.LogWarning("Route planning failed for train {TrainName}: {Error}",
                                        train.Name, routeResult.ErrorMessage);
                                    _statusService?.ShowError($"Route planning failed: {routeResult.ErrorMessage} - [TrackHandlerService]", train.Name);
                                    canStartTrain = false;
                                }
                            }
                            else
                            {
                                _logger?.LogWarning("Route planning skipped for train {TrainName}: Missing station info", train.Name);
                                _statusService?.ShowWarning("Route planning skipped: Missing station info", train.Name);
                                canStartTrain = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            _statusService?.ShowError($"Route planning error: {ex.Message} - [TrackHandlerService]", train.Name);
                            canStartTrain = false;
                        }

                        // Start the train if route planning succeeded
                        if (canStartTrain && routePlan != null)
                        {
                            // Get RocrailCommandService for this train
                            var commandSender = _serviceProvider.GetRequiredService<RocrailCommandService>();
                            commandSender.ResetCommandHistory();

                            // Get TrackGraph for TrainMovementMonitor
                            var trackGraphFactory = _serviceProvider.GetRequiredService<ITrackGraphFactory>();
                            var trackGraph = await trackGraphFactory.CreateTrackGraphAsync();

                            // Create TrainMovementMonitor directly with all dependencies
                            trainMonitor = new TrainMovementMonitor(
                                train,
                                timetableEntry,
                                _statusService,
                                _serviceProvider,
                                this,
                                commandSender,
                                _virtualClock,
                                trackGraph,
                                _trackOccupancyService,
                                _serviceProvider.GetService<ILogger<TrainMovementMonitor>>(),
                                _blockReservationService,
                                _switchConfigurationService,
                                null, // LightController - optional
                                _fileLogger, // FileLoggingService
                                _serviceProvider.GetRequiredService<TravelTimeMeasurementService>() // Travel Time Service
                            );

                            // Initialize the train monitor asynchronously with route plan (must complete before StartJourney)
                            // This prevents race conditions where the train starts before initialization completes
                            await trainMonitor.InitializeAsync(routePlan);

                            lock (_lockObject)
                            {
                                _trainManagers[timetableEntry.Train_DB_ID] = trainMonitor;
                                _activeTrainManagers.Add(trainMonitor);
                            }

                            // Start the train monitor journey
                            // Block reservation and switch configuration happens JIT in TrainMovementMonitor
                            _fileLogger?.Log($"[TRACK HANDLER] Train {train.Name} successfully started journey.");
                            trainMonitor.StartJourney();

                            _statusService?.ShowInfo($"Indítás: {train.Name} ({timetableEntry.SourcePlatform?.DisplayName} -> {timetableEntry.DestinationPlatform?.DisplayName})");
                        }
                        else
                        {
                            // Route planning failed - skip this train and retry next loop
                            // Reset the timetable entry state back to Upcoming so it will be retried
                            using (var scope = _serviceProvider.CreateScope())
                            {
                                try
                                {
                                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                                    var trackedEntry = await dbContext.TimetableEntries.FindAsync(timetableEntry.DB_ID);
                                    if (trackedEntry != null)
                                    {
                                        trackedEntry.EntryState = EntryState.Upcoming;
                                        await dbContext.SaveChangesAsync(cancellationToken);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogError(ex, "Failed to reset timetable entry {EntryId} back to Upcoming after routing failure - [TrackHandlerService]", timetableEntry.DB_ID);
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

                    // Clean up only truly completed train monitors (not those that just finished one journey)
                    lock (_lockObject)
                    {
                        // Only remove monitors if they are explicitly stopped or in error state
                        var completedMonitors = _activeTrainManagers.Where(monitor => monitor.IsCompleted).ToList();
                        foreach (var monitor in completedMonitors)
                        {
                            // Find and remove from the train monitor dictionary using explicit key filtering
                            var monitorKeysToRemove = _trainManagers
                                .Where(kvp => kvp.Value == monitor)
                                .Select(kvp => kvp.Key)
                                .ToList();

                            foreach (var key in monitorKeysToRemove)
                            {
                                _trainManagers.Remove(key);
                            }

                            monitor.Dispose(); // Plug the memory leak
                        }
                        _activeTrainManagers.RemoveAll(monitor => monitor.IsCompleted);

                        // NOTE: _recentlyProcessedTrains is cleared at virtual midnight in OnMidnightReset()
                        // We don't use time-based cleanup here because the virtual clock resets to 2024-01-01
                        // which would make all timestamps appear to be "in the future" and never expire

                        // Clean up old train commands to prevent memory growth
                        // Remove commands older than 5 minutes using real-time timestamps
                        var threshold = DateTime.UtcNow.AddMinutes(-5);
                        var commandKeysToRemove = _lastTrainCommands
                            .Where(kvp => kvp.Value.Timestamp < threshold)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in commandKeysToRemove)
                        {
                            _lastTrainCommands.Remove(key);
                        }
                    }

                    // Use the helper method for task cleanup
                    CleanupCompletedTasks();

                    // Calculate dynamic timeout for next iteration
                        dynamicTimeout = CalculateDynamicTimeout(_virtualClock.CurrentTime.TimeOfDay);

                    // Apply virtual clock speed multiplier to convert virtual ms to real-time ms.
                    // We use Math.Max to prevent division by zero if the clock is paused or extremely slow.
                    speedMultiplier = Math.Max(0.0001, _virtualClock.SpeedMultiplier);
                    var realTimeoutFinal = (int)(dynamicTimeout / speedMultiplier);

                    // Wait for train completion signal or dynamic timeout
                    // This allows immediate reprocessing when switches are released
                    await _trainCompletedSemaphore.WaitAsync(realTimeoutFinal, cancellationToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Graceful shutdown, exit the loop without logging an error
                        break;
                    }
                    catch (Exception ex)
                    {
                        _statusService?.ShowError($"Track Handler Service hiba: {ex.Message} - [TrackHandlerService]");

                        // Add fallback delay to prevent CPU maxing and log spam on persistent errors
                        try
                        {
                            await Task.Delay(5000, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }
                    }
                }
                finally
                {
                    // CRITICAL: This finally block ensures cleanup happens on every loop iteration,
                    // even when 'continue' statements are executed. This fixes the memory leak where
                    // completed tasks and old train commands accumulate without cleanup.
                    CleanupCompletedTasks();

                    // Clean up old train commands to prevent memory growth
                    // This runs on every iteration, ensuring cleanup happens even after 'continue' statements
                    lock (_lockObject)
                    {
                        var threshold = DateTime.UtcNow.AddMinutes(-5);
                        var keysToRemove = _lastTrainCommands
                            .Where(kvp => kvp.Value.Timestamp < threshold)
                            .Select(kvp => kvp.Key)
                            .ToList();

                        foreach (var key in keysToRemove)
                        {
                            _lastTrainCommands.Remove(key);
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}