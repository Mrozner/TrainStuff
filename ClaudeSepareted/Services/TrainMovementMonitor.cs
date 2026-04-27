using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ClaudeSepareted.Domain;
using ClaudeSepareted;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Static class containing train command constants.
    /// Provides type-safe, centralized command string management.
    /// </summary>
    public static class TrainCommands
    {
        /// <summary>Command to start/resume train movement</summary>
        public const string Start = "start";

        /// <summary>Command to stop train movement</summary>
        public const string Stop = "stop";
    }

    /// <summary>
    /// Enhanced train movement monitor with dynamic block enforcement.
    ///
    /// This service provides automatic stop/resume functionality for collision avoidance:
    /// - Monitors train position via TrackOccupancyService
    /// - Looks ahead to next block on route
    /// - Stops train if next block is occupied (red signal)
    /// - Auto-resumes when track clears (green signal)
    ///
    /// This implements rolling-block operation where trains follow each other
    /// at safe distances, automatically stopping and resuming based on real-time
    /// track occupancy.
    /// </summary>
    public class TrainMovementMonitor : IDisposable
    {
        #region Dependencies

        private readonly Train _train;
        private readonly TimetableEntries _timetableEntry;
        private readonly StatusNotificationService _statusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly TrackHandlerService _trackHandlerService;
        private readonly RocrailCommandService _rocrailCommandService;
        private CancellationTokenSource _cancellationTokenSource;
        private readonly VirtualClock _virtualClock;
        private readonly TrackOccupancyService _trackOccupancyService;
        private readonly ILogger<TrainMovementMonitor> _logger;
        private readonly BlockReservationService _blockReservationService;
        private readonly SwitchConfigurationService _switchConfigurationService;
        private readonly ClaudeSepareted.Lights.LightController _lightController;
        private readonly FileLoggingService? _fileLogger;
        private readonly TravelTimeMeasurementService _travelTimeService;

        #endregion

        #region Route Tracking

        /// <summary>
        /// The full route plan for this train's journey.
        /// Used to calculate the next expected section for look-ahead logic.
        /// </summary>
        private volatile RoutePlan _routePlan;

        /// <summary>
        /// Current position in the route plan (index into Path list).
        /// Tracks which edge the train is currently traversing.
        /// </summary>
        private int _currentRouteIndex = 0;

        /// <summary>
        /// Lock object for thread-safe route index manipulation.
        /// Prevents race conditions when reading/writing the route index across multiple threads.
        /// </summary>
        private readonly object _routeIndexLock = new object();

        /// <summary>
        /// Lock object for thread-safe train state mutations.
        /// Prevents torn reads/writes when modifying _train.State and _train.CurrentSpeed
        /// across the main controller thread and background channel reader.
        /// </summary>
        private readonly object _trainStateLock = new object();

        /// <summary>
        /// Track graph for ID-to-Name and Name-to-ID resolution.
        /// Replaces local dictionaries with centralized in-memory lookup.
        /// </summary>
        private readonly TrackGraph _trackGraph;

        #endregion

        #region Block Enforcement State

        /// <summary>
        /// The section we're waiting to clear (when in WaitingForClearance state).
        /// Used to auto-resume when the specific section becomes free.
        /// </summary>
        private string _waitingForSectionToClear;

        /// <summary>
        /// The background task running the train's journey loop.
        /// Used to track task lifecycle and prevent fire-and-forget issues.
        /// </summary>
        private Task _journeyTask;

        /// <summary>
        /// Lock object for thread-safe subscription management.
        /// Prevents race conditions during rapid start/stop commands.
        /// </summary>
        private readonly object _subscriptionLock = new object();

        /// <summary>
        /// Flag indicating whether we're subscribed to TrackOccupancyService events.
        /// Prevents duplicate subscriptions. Protected by _subscriptionLock.
        /// </summary>
        private bool _isSubscribedToOccupancyService = false;

        /// <summary>
        /// TaskCompletionSource for signaling journey completion.
        /// Used to replace the CPU-wasting polling loop with event-driven completion.
        /// </summary>
        private TaskCompletionSource<bool> _journeyCompletionSource;

        /// <summary>
        /// Channel reader for train movement events.
        /// Replaces C# event-based system with Channel-based pub/sub.
        /// Protected by _subscriptionLock.
        /// </summary>
        private ChannelReader<TrainMovedEventArgs> _movementReader;

        /// <summary>
        /// Disposable subscription token for TrackOccupancyService.
        /// Must be disposed to prevent memory leaks. Protected by _subscriptionLock.
        /// </summary>
        private IDisposable _movementSubscription;

        /// <summary>
        /// Flag to track disposal state and prevent double-dispose.
        /// </summary>
        private bool _disposed = false;

        #endregion

        #region Destination Monitoring

        private string? _destinationSectionName;

        public event EventHandler<TrainArrivedEventArgs>? TrainArrived;

        #endregion

        #region Journey Timing

        /// <summary>
        /// Track whether the train journey is currently running
        /// </summary>
        public bool IsRunning { get; private set; }

        /// <summary>
        /// Track whether the TrainMovementMonitor has been properly initialized.
        /// Prevents starting journeys before initialization completes.
        /// </summary>
        private bool _isInitialized = false;

        /// <summary>
        /// Lock object for thread-safe initialization state management.
        /// </summary>
        private readonly object _initializationLock = new object();

        /// <summary>
        /// Journey start time in virtual clock time
        /// </summary>
        public DateTime? JourneyStartTimeDateTime { get; private set; }

        /// <summary>
        /// Estimated journey duration
        /// </summary>
        public TimeSpan? JourneyDuration { get; private set; }

        /// <summary>
        /// Track whether the journey is completed
        /// </summary>
        public bool IsCompleted { get; private set; }

        #endregion

        #region Constructor

        public TrainMovementMonitor(
            Train train,
            TimetableEntries timetableEntry,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            TrackHandlerService trackHandlerService,
            RocrailCommandService rocrailCommandService,
            VirtualClock virtualClock,
            TrackGraph trackGraph,
            TrackOccupancyService trackOccupancyService = null,
            ILogger<TrainMovementMonitor> logger = null,
            BlockReservationService blockReservationService = null,
            SwitchConfigurationService switchConfigurationService = null,
            ClaudeSepareted.Lights.LightController lightController = null,
            FileLoggingService fileLogger = null,
            TravelTimeMeasurementService travelTimeService = null)
        {
            _train = train ?? throw new ArgumentNullException(nameof(train));
            _timetableEntry = timetableEntry ?? throw new ArgumentNullException(nameof(timetableEntry));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _trackHandlerService = trackHandlerService;
            _rocrailCommandService = rocrailCommandService ?? throw new ArgumentNullException(nameof(rocrailCommandService));
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _trackGraph = trackGraph ?? throw new ArgumentNullException(nameof(trackGraph));
            _trackOccupancyService = trackOccupancyService;
            _logger = logger;
            _blockReservationService = blockReservationService;
            _switchConfigurationService = switchConfigurationService;
            _lightController = lightController;
            _fileLogger = fileLogger;
            _travelTimeService = travelTimeService;

            // Initialize journey state
            IsRunning = false;
            IsCompleted = false;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Unified asynchronous initialization method that must be called before StartJourney.
        /// Initializes both destination section name and section mappings to prevent race conditions.
        /// </summary>
        /// <param name="routePlan">The calculated route plan from PathfindingService</param>
        public async Task InitializeAsync(RoutePlan routePlan)
        {
            try
            {
                // Set route plan if provided and valid
                if (routePlan != null && routePlan.HasPath)
                {
                    _routePlan = routePlan;
                    _currentRouteIndex = 0;

                    _logger?.LogInformation("Route plan set for train {TrainName}: {EdgeCount} edges, total length: {Length}m",
                        _train.Name, routePlan.Path.Count, routePlan.TotalLength);
                }
                else if (routePlan != null)
                {
                    _logger?.LogWarning("Invalid route plan provided to TrainMovementMonitor for train {TrainName}", _train.Name);
                }

                // Initialize destination section name for arrival monitoring
                await InitializeDestinationSectionNameAsync();

                // Mark as initialized (thread-safe)
                lock (_initializationLock)
                {
                    _isInitialized = true;
                }

                _logger?.LogInformation("TrainMovementMonitor initialized successfully for train {TrainName}", _train.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing TrainMovementMonitor for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                throw;
            }
        }

        #endregion

        #region Look-Ahead Logic

        /// <summary>
        /// Calculate the next expected subsection on the train's route.
        ///
        /// This method navigates the RoutePlan to determine which section
        /// the train will enter next, enabling look-ahead collision avoidance.
        ///
        /// Algorithm:
        /// 1. Get current position (or starting position)
        /// 2. Find the current edge in the route plan
        /// 3. Return the target node of that edge as next expected section
        /// </summary>
        /// <param name="currentSubSection">The train's current subsection name (can be null)</param>
        /// <returns>The next expected subsection name, or null if at destination</returns>
        public string GetNextExpectedSubSection(string currentSubSection)
        {
            try
            {
                if (_routePlan?.Path == null || !_routePlan.Path.Any())
                {
                    _logger?.LogWarning("Cannot calculate next expected section: no route plan for train {TrainName}", _train.Name);
                    return null;
                }

                // Thread-safe: Capture current route index to minimize lock contention
                int currentRouteIndex;
                lock (_routeIndexLock)
                {
                    currentRouteIndex = _currentRouteIndex;
                }

                // If current section is null, use the starting position
                if (string.IsNullOrEmpty(currentSubSection))
                {
                    if (currentRouteIndex < _routePlan.Path.Count)
                    {
                        var firstEdge = _routePlan.Path[currentRouteIndex];
                        var nextSectionName = _trackGraph.GetSectionNameById(firstEdge.TargetNodeId);
                        if (nextSectionName != null)
                        {
                            _logger?.LogDebug("Next expected section from start: {Section} (index: {Index})",
                                nextSectionName, currentRouteIndex);
                            return nextSectionName;
                        }
                    }
                    return null;
                }

                // Convert current subsection name to section ID
                int currentSectionId = _trackGraph.GetNodeIdByName(currentSubSection);
                if (currentSectionId == -1)
                {
                    _logger?.LogWarning("Current subsection {SubSection} not found in track graph for train {TrainName}",
                        currentSubSection, _train.Name);
                    return null;
                }

                // Find the edge where this section is the target (just completed)
                // Look ahead from current position
                for (int i = currentRouteIndex; i < _routePlan.Path.Count; i++)
                {
                    var edge = _routePlan.Path[i];

                    // Check if we're at the source of this edge (about to traverse it)
                    if (edge.SourceNodeId == currentSectionId)
                    {
                        // This is the edge we're traversing, the target is next
                        var nextSectionName = _trackGraph.GetSectionNameById(edge.TargetNodeId);
                        if (nextSectionName != null)
                        {
                            _logger?.LogDebug("Next expected section: {Section} (from current: {Current}, edge index: {Index})",
                                nextSectionName, currentSubSection, i);
                            return nextSectionName;
                        }
                    }

                    // Check if we're at the target of this edge (just completed it)
                    if (edge.TargetNodeId == currentSectionId)
                    {
                        // Move to next edge
                        if (i + 1 < _routePlan.Path.Count)
                        {
                            var nextEdge = _routePlan.Path[i + 1];
                            var nextSectionName = _trackGraph.GetSectionNameById(nextEdge.TargetNodeId);
                            if (nextSectionName != null)
                            {
                                _logger?.LogDebug("Next expected section: {Section} (from edge index: {Index})",
                                    nextSectionName, i);
                                return nextSectionName;
                            }
                        }
                        else
                        {
                            // We're at the last edge, next is destination
                            _logger?.LogDebug("Train {TrainName} at final edge, next is destination", _train.Name);
                            return _destinationSectionName;
                        }
                    }
                }

                _logger?.LogDebug("Could not determine next expected section for train {TrainName} from current: {Current}",
                    _train.Name, currentSubSection);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error calculating next expected section for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                return null;
            }
        }

        /// <summary>
        /// Advance the route index when a train physically enters a new section.
        /// This MUST be called ONLY after physical movement is confirmed via MQTT feedback.
        /// </summary>
        /// <param name="currentSubSection">The section the train just entered</param>
        public void AdvanceRouteIndex(string currentSubSection)
        {
            try
            {
                if (_routePlan?.Path == null || !_routePlan.Path.Any())
                {
                    _logger?.LogWarning("Cannot advance route index: no route plan for train {TrainName}", _train.Name);
                    return;
                }

                if (string.IsNullOrEmpty(currentSubSection))
                {
                    _logger?.LogWarning("Cannot advance route index: current subsection is null for train {TrainName}", _train.Name);
                    return;
                }

                // Convert current subsection name to section ID
                int currentSectionId = _trackGraph.GetNodeIdByName(currentSubSection);
                if (currentSectionId == -1)
                {
                    _logger?.LogWarning("Current subsection {SubSection} not found in track graph for train {TrainName}",
                        currentSubSection, _train.Name);
                    return;
                }

                // Find the edge where this section is the target (just completed)
                // and advance the index to the next edge
                bool foundEdge = false;

                for (int i = _currentRouteIndex; i < _routePlan.Path.Count; i++)
                {
                    var edge = _routePlan.Path[i];

                    // Check if we're at the target of this edge (just completed it)
                    if (edge.TargetNodeId == currentSectionId)
                    {
                        foundEdge = true;

                        // Advance to next edge (thread-safe)
                        if (i + 1 < _routePlan.Path.Count)
                        {
                            lock (_routeIndexLock)
                            {
                                _currentRouteIndex = i + 1;
                            }
                            _logger?.LogDebug("Route index advanced to {Index} for train {TrainName} after entering {Section}",
                                _currentRouteIndex, _train.Name, currentSubSection);
                        }
                        else
                        {
                            _logger?.LogDebug("Train {TrainName} at final edge, no further index advancement", _train.Name);
                        }
                        return;
                    }
                }

                // Fallback mechanism: Train diverged from route plan (likely a missed sensor read)
                if (!foundEdge)
                {
                    _logger?.LogWarning("Train {TrainName} missed sequential edge for {Section}. Attempting to fast-forward index.",
                        _train.Name, currentSubSection);

                    // Search the ENTIRE remainder of the route plan for the current section
                    for (int i = _currentRouteIndex; i < _routePlan.Path.Count; i++)
                    {
                        if (_routePlan.Path[i].TargetNodeId == currentSectionId)
                        {
                            lock (_routeIndexLock)
                            {
                                _currentRouteIndex = i + 1;
                            }
                            _logger?.LogInformation("Successfully fast-forwarded train {TrainName} route index to {Index}.",
                                _train.Name, _currentRouteIndex);
                            return;
                        }
                    }

                    _logger?.LogError("Train {TrainName} critically diverged from route plan. Section {Section} not found anywhere in remaining path. - [TrainMovementMonitor]",
                        _train.Name, currentSubSection);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error advancing route index for train {TrainName} - [TrainMovementMonitor]", _train.Name);
            }
        }

        #endregion

        #region Block Enforcement

        /// <summary>
        /// Check if the train should stop due to occupied track ahead.
        /// Implements N-block look-ahead with just-in-time block reservation.
        /// Called when train movement feedback is received.
        /// </summary>
        /// <param name="currentSubSection">The train's current subsection</param>
        private async Task CheckBlockAheadAsync(string currentSubSection)
        {
            try
            {
                // Only check if train is moving and not already waiting (thread-safe check)
                bool isMoving;
                lock (_trainStateLock)
                {
                    isMoving = _train.State == TrainState.Moving || _train.State == TrainState.PrepareToStop;
                }
                if (!isMoving) return;

                // Get next expected section on route
                var nextExpectedSection = GetNextExpectedSubSection(currentSubSection);

                if (string.IsNullOrEmpty(nextExpectedSection))
                {
                    // No next section (at destination or route complete)
                    return;
                }

                // Step 1: Attempt to reserve the next block (logical + physical check)
                if (_blockReservationService != null)
                {
                    bool reservationSuccess = _blockReservationService.TryReserveBlock(nextExpectedSection, _train.Name);

                    if (!reservationSuccess)
                    {
                        // BLOCK OCCUPIED/RESERVED: Stop the train
                        var blockingTrain = _blockReservationService.GetBlockOwner(nextExpectedSection);

                        _logger?.LogInformation(
                            "🚦 BLOCK AHEAD: Train {TrainName} must stop at {CurrentSection}. " +
                            "Next section {NextSection} is reserved by {BlockingTrain}",
                            _train.Name, currentSubSection, nextExpectedSection, blockingTrain ?? "unknown");

                        // CRITICAL: Stop the train FIRST (safety-critical operation)
                        // This must happen before any auxiliary operations like light control
                        await StopTrainForRedSignalAsync(nextExpectedSection, blockingTrain);

                        // Set lights to RED (non-critical auxiliary operation)
                        // Wrapped in try/catch to prevent light controller failures from affecting safety
                        //if (_lightController != null && _routePlan?.HasPath == true)
                        //{
                        //    try
                        //    {
                        //        await _lightController.LightsToRed(nextExpectedSection, currentSubSection, _train.Direction);
                        //    }
                        //    catch (Exception ex)
                        //    {
                        //        // Log error but don't interrupt the safety loop
                        //        _logger?.LogError(ex,
                        //            "Failed to set RED lights for train {TrainName} at section {Section}. " +
                        //            "Train has been stopped safely for red signal.",
                        //            _train.Name, currentSubSection);
                        //    }
                        //}

                        return;
                    }
                }

                // Step 2: Block is clear - proceed safely
                _logger?.LogDebug("✅ BLOCK CLEAR: {NextSection} is free for train {TrainName}",
                    nextExpectedSection, _train.Name);

                // Set lights to GREEN
                //if (_lightController != null && _routePlan?.HasPath == true)
                //{
                //    await _lightController.LightsToGreen(nextExpectedSection, currentSubSection, _train.Direction);
                //}

                // Configure switches for the next block using JIT approach
                if (_switchConfigurationService != null && _routePlan != null)
                {
                    int nextSectionId = _trackGraph.GetNodeIdByName(nextExpectedSection);
                    bool switchConfigSuccess = await _switchConfigurationService.ConfigureSwitchesForBlockAsync(
                        nextSectionId, nextExpectedSection, _routePlan, _train.Name);

                    if (!switchConfigSuccess)
                    {
                        _logger?.LogWarning("⚠️ Switch configuration failed for train {TrainName} entering {Section}",
                            _train.Name, nextExpectedSection);
                    }
                }

                _logger?.LogInformation("✅ SAFE TO PROCEED: Train {TrainName} entering {Section}",
                    _train.Name, nextExpectedSection);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking block ahead for train {TrainName} - [TrainMovementMonitor]", _train.Name);

                // FAIL-SAFE: Emergency stop on any tracking or safety check failure
                // Never allow train to blindly move forward if safety systems fail
                try
                {
                    await StopTrainNowAsync();
                }
                catch (Exception stopEx)
                {
                    _logger?.LogError(stopEx, "Error executing emergency stop for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                }
            }
        }

        /// <summary>
        /// Stop the train due to red signal (occupied block ahead).
        /// </summary>
        /// <param name="occupiedSection">The section that's occupied</param>
        /// <param name="blockingTrain">The train that's blocking the track</param>
        private async Task StopTrainForRedSignalAsync(string occupiedSection, string blockingTrain)
        {
            try
            {
                _fileLogger?.Log($"[TRAIN MONITOR] COLLISION AVOIDANCE: {_train.Name} stopped at red signal. Block {occupiedSection} is occupied by {blockingTrain ?? "unknown"}.");

                // Send stop command immediately
                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _train.Direction ? Direction.Forward : Direction.Backward);

                // Update train state (thread-safe)
                lock (_trainStateLock)
                {
                    _train.CurrentSpeed = Speed.ZERO;
                    _train.State = TrainState.WaitingForClearance;
                }
                _waitingForSectionToClear = occupiedSection;

                _statusService?.ShowWarning(
                    $"🛑 RED SIGNAL: {_train.Name} stopped at red. " +
                    $"Waiting for {blockingTrain ?? "train"} to clear {occupiedSection}",
                    _train.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping train {TrainName} for red signal - [TrainMovementMonitor]", _train.Name);
            }
        }

        /// <summary>
        /// Resume the train after track ahead clears.
        /// </summary>
        private async Task ResumeTrainForGreenSignalAsync()
        {
            try
            {
                // Send start command
                var resumeSpeed = _train.MaxSpeed == Speed.ZERO ? Speed.MEDIUM : _train.MaxSpeed;
                var success = await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, resumeSpeed, _train.Direction ? Direction.Forward : Direction.Backward);

                if (success)
                {
                    _fileLogger?.Log($"[TRAIN MONITOR] Track clear. {_train.Name} resuming journey.");

                    // Update train state (thread-safe)
                    lock (_trainStateLock)
                    {
                        _train.CurrentSpeed = resumeSpeed;
                        _train.State = TrainState.Moving;
                    }
                    _waitingForSectionToClear = null;

                    _statusService?.ShowSuccess(
                        $"✅ GREEN SIGNAL: {_train.Name} resuming journey (speed: {resumeSpeed})",
                        _train.Name);

                    _logger?.LogInformation("Train {TrainName} resumed from WaitingForClearance", _train.Name);
                }
                else
                {
                    _logger?.LogError("Failed to resume train {TrainName} after green signal - [TrainMovementMonitor]", _train.Name);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error resuming train {TrainName} for green signal - [TrainMovementMonitor]", _train.Name);
            }
        }

        #endregion

        #region Speed Control

        /// <summary>
        /// Start the train journey with appropriate speed
        /// </summary>
        public async Task<bool> StartTrainAsync()
        {
            try
            {
                // Calculate estimated journey duration dynamically based on route length and speed
                TimeSpan estimatedJourneyDuration;

                if (_routePlan?.HasPath == true && _routePlan.TotalLength > 0)
                {
                    // Calculate duration based on route distance and train speed
                    var trainSpeed = _train.MaxSpeed == Speed.ZERO ? Speed.MEDIUM : _train.MaxSpeed;
                    int speedKmPerHour = trainSpeed switch
                    {
                        Speed.SLOW => 30,
                        Speed.MEDIUM => 60,
                        Speed.HIGH => 90,
                        _ => 60
                    };

                    // Convert route length from meters to kilometers
                    var distanceKm = _routePlan.TotalLength / 1000.0;

                    // Calculate duration: time (hours) = distance (km) / speed (km/h)
                    var durationHours = distanceKm / speedKmPerHour;

                    // Convert to minutes and apply a safety factor (1.5x for delays, stops, etc.)
                    var durationMinutes = Math.Max(5.0, durationHours * 60 * 1.5);

                    // Use calculated duration without artificial minimum constraint
                    // This allows short physical layouts to have accurate journey timing
                    estimatedJourneyDuration = TimeSpan.FromMinutes(durationMinutes);

                    _logger?.LogInformation(
                        "Calculated dynamic journey duration for train {TrainName}: {Duration} minutes " +
                        "(Distance: {Distance}m, Speed: {Speed} km/h)",
                        _train.Name, estimatedJourneyDuration.TotalMinutes, _routePlan.TotalLength, speedKmPerHour);
                }
                else
                {
                    // Fallback to default duration if no route plan available
                    estimatedJourneyDuration = TimeSpan.FromMinutes(30);
                    _logger?.LogWarning(
                        "No route plan available for train {TrainName}, using default journey duration of {Duration} minutes",
                        _train.Name, estimatedJourneyDuration.TotalMinutes);
                }

                // Turn on train power only if needed (avoid unnecessary power commands)
                var lastPowerCmd = _rocrailCommandService.GetLastPowerCommand(_train.Name);
                var needsPowerOn = lastPowerCmd?.Contains("on") != true;
                if (needsPowerOn)
                {
                    await _rocrailCommandService.PowerOnTrainAsync(_train.Name);
                }

                // Start the train with appropriate speed
                var departureSpeed = _train.MaxSpeed == Speed.ZERO ? Speed.MEDIUM : _train.MaxSpeed;
                var success = await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, departureSpeed, _train.Direction ? Direction.Forward : Direction.Backward);

                if (success)
                {
                    // Update train state (thread-safe)
                    lock (_trainStateLock)
                    {
                        _train.CurrentSpeed = departureSpeed;
                        _train.State = TrainState.Moving;
                    }
                    _statusService?.ShowInfo($"Vonat indul: {_train.Name} - Sebesség: {departureSpeed}");

                    // Set journey timing info
                    JourneyStartTimeDateTime = _virtualClock.CurrentTime;
                    JourneyDuration = estimatedJourneyDuration;
                    _fileLogger?.Log($"[TRAIN MONITOR] Train {_train.Name} departing. Estimated duration: {estimatedJourneyDuration.TotalMinutes:F1} mins.");
                    IsRunning = true;
                }

                return success;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Vonat indítási hiba: {_train.Name} - {ex.Message} - [TrainMovementMonitor]");
                return false;
            }
        }

        /// <summary>
        /// Immediately stop the train
        /// </summary>
        public async Task<bool> StopTrainNowAsync()
        {
            try
            {
                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _train.Direction ? Direction.Forward : Direction.Backward);

                // Also send power off command to fully stop the train
                await _rocrailCommandService.PowerOffTrainAsync(_train.Name);

                // Update train state (thread-safe)
                lock (_trainStateLock)
                {
                    _train.CurrentSpeed = Speed.ZERO;
                    _train.State = TrainState.Stopped;
                }

                // Release all logical block reservations held by this train
                if (_blockReservationService != null)
                {
                    _blockReservationService.ReleaseAllForTrain(_train.Name);
                }

                // Notify TrackHandlerService to reprocess waiting trains
                if (_trackHandlerService != null)
                {
                    _trackHandlerService.OnTrainCompleted();
                }

                _statusService?.ShowSuccess($"Megállt: {_train.Name}");
                IsRunning = false;

                return true;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Vonat megállási hiba: {_train.Name} - {ex.Message} - [TrainMovementMonitor]");
                return false;
            }
        }

        /// <summary>
        /// Get journey progress as a percentage
        /// </summary>
        public double GetJourneyProgress()
        {
            if (!IsRunning || JourneyStartTimeDateTime == null || JourneyDuration == null)
                return 0;

            var elapsedTime = _virtualClock.CurrentTime - JourneyStartTimeDateTime.Value;
            return Math.Min(100, elapsedTime.TotalMinutes / JourneyDuration.Value.TotalMinutes * 100);
        }

        /// <summary>
        /// Check if train should prepare to stop (80% of journey)
        /// </summary>
        public bool ShouldPrepareToStop()
        {
            var progress = GetJourneyProgress();
            return progress >= 80 && _train.State == TrainState.Moving;
        }

        /// <summary>
        /// Calculate stop time based on journey duration
        /// </summary>
        public DateTime CalculateStopTime()
        {
            if (JourneyStartTimeDateTime == null || JourneyDuration == null)
                return DateTime.MinValue;

            return JourneyStartTimeDateTime.Value + JourneyDuration.Value;
        }

        #endregion

        #region Journey Lifecycle

        /// <summary>
        /// Registers the train's starting position with the TrackOccupancyService.
        /// This MUST be called before the train begins movement to ensure proper
        /// tracking in the rolling-block collision avoidance system.
        ///
        /// The starting position is derived from the timetable entry's source platform.
        /// If the train is already at a platform, that subsection becomes the initial position.
        /// </summary>
        private async Task<bool> RegisterTrainPositionAsync()
        {
            try
            {
                if (_trackOccupancyService == null)
                {
                    _logger?.LogWarning("TrackOccupancyService not available, skipping train position registration for {TrainName}", _train.Name);
                    return false;
                }

                // Ensure the occupancy service is initialized
                await _trackOccupancyService.InitializeAsync();

                // Derive the starting subsection name from the source platform
                // The timetable entry now contains the source platform directly
                var startingSubSection = await GetSubSectionFromPlatformAsync(_timetableEntry.SourcePlatform_DB_ID);

                if (string.IsNullOrWhiteSpace(startingSubSection))
                {
                    _logger?.LogWarning("Could not determine starting subsection for train {TrainName} (SourcePlatform: {Platform})",
                        _train.Name, _timetableEntry.SourcePlatform?.DisplayName ?? "unknown");
                    return false;
                }

                // Register the train's initial position with the occupancy tracking service
                _trackOccupancyService.RegisterTrainPosition(
                    _train.Name,
                    startingSubSection,
                    _train.Direction
                );

                _logger?.LogInformation("Train {TrainName} registered at starting position: {SubSection} (direction: {Direction})",
                    _train.Name, startingSubSection, _train.Direction ? "forward" : "reverse");

                // Set train state to indicate it's ready to move
                _train.State = TrainState.Waiting;

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error registering train position for {TrainName} - [TrainMovementMonitor]", _train.Name);
                return false;
            }
        }

        /// <summary>
        /// Start the train's journey as a background task.
        /// This method initializes the lifecycle loop that manages the entire journey.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown if InitializeAsync() was not called first</exception>
        public void StartJourney()
        {
            // CRITICAL: Ensure initialization was completed before starting journey
            lock (_initializationLock)
            {
                if (!_isInitialized)
                {
                    throw new InvalidOperationException(
                        $"TrainMovementMonitor for train {_train.Name} must be initialized via InitializeAsync() before starting journey. " +
                        "Call InitializeAsync() first to set up destination section name and route mappings.");
                }
            }

            // Clean up any existing cancellation token before starting a new journey
            if (_cancellationTokenSource != null)
            {
                try
                {
                    if (!_cancellationTokenSource.IsCancellationRequested)
                    {
                        _cancellationTokenSource.Cancel();
                    }
                    // DO NOT Dispose() here. The dying _journeyTask is still referencing this token.
                    // Let the Garbage Collector handle the token cleanup to prevent ObjectDisposedException
                    // race conditions on the background thread.
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
            }

            // Clean up any existing journey completion source
            _journeyCompletionSource?.TrySetCanceled();

            // Create a fresh cancellation token for this journey
            _cancellationTokenSource = new CancellationTokenSource();

            // Initialize the journey completion TCS for event-driven completion
            _journeyCompletionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Safely capture the old task to ensure it has fully spun down before starting the new loop
            var previousTask = _journeyTask;

            if (!_cancellationTokenSource.IsCancellationRequested)
            {
                _journeyTask = Task.Run(async () =>
                {
                    if (previousTask != null)
                    {
                        try
                        {
                            await previousTask;
                        }
                        catch
                        {
                            /* Ignore exceptions from the previous cancelled task */
                        }
                    }
                    await RunJourneyAsync(_cancellationTokenSource.Token);
                });
            }
        }

        /// <summary>
        /// Run the complete journey lifecycle for this train.
        /// This method manages the entire journey from start to completion.
        /// </summary>
        private async Task RunJourneyAsync(CancellationToken cancellationToken)
        {
            try
            {
                // CRITICAL: Register train position BEFORE movement starts
                // This initializes the rolling-block collision avoidance system
                // and ensures the TrainOccupancyService tracks this train from the start
                bool registered = await RegisterTrainPositionAsync();

                if (!registered)
                {
                    _statusService?.ShowError($"Failed to register train position for {_train.Name}. Journey aborted. - [TrainMovementMonitor]");
                    IsCompleted = true;
                    return;
                }

                // Start tracking this train's movement via TrackOccupancyService events
                // This replaces raw MQTT feedback with train-specific event handling
                StartTracking(cancellationToken);

                // Start the train
                if (!await StartTrainAsync())
                {
                    _statusService?.ShowError($"Failed to start train: {_train.Name} - [TrainMovementMonitor]");
                    IsCompleted = true;
                    return;
                }

                // Monitor virtual clock until journey time elapsed
                var stopTime = CalculateStopTime();

                while (!cancellationToken.IsCancellationRequested && _virtualClock.CurrentTime < stopTime)
                {
                    // Check progress every 100ms real-time
                    await Task.Delay(100, cancellationToken);

                    // Check if we should start preparing to stop (80% of journey)
                    if (ShouldPrepareToStop())
                    {
                        // Update train state (thread-safe)
                        lock (_trainStateLock)
                        {
                            _train.State = TrainState.PrepareToStop;
                        }
                    }
                }

                if (!cancellationToken.IsCancellationRequested && _train.State != TrainState.Arrived)
                {
                    // Journey completed based on virtual clock
                    _statusService?.ShowInfo($"Útvonal befejezve: {_train.Name} (vonat tovább halad)");

                    // Event-driven wait for journey completion - no more CPU waste!
                    await _journeyCompletionSource.Task.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Graceful shutdown - do NOT trigger emergency stop
                _logger?.LogInformation("Train {TrainName} journey gracefully cancelled", _train.Name);
                _statusService?.ShowInfo($"Vonat út leállítva: {_train.Name} (rendes leállás)");
            }
            catch (ObjectDisposedException)
            {
                // Graceful shutdown during system teardown/app exit - do NOT trigger emergency stop
                _logger?.LogInformation("Train {TrainName} journey stopped due to system disposal", _train.Name);
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Vonat kezelés hiba ({_train.Name}): {ex.Message} - [TrainMovementMonitor]");

                // Emergency stop on error (only for non-cancellation exceptions)
                try
                {
                    await StopTrainNowAsync();
                }
                catch
                {
                    // Ignore errors during emergency stop
                }
            }
            finally
            {
                // Clean up
                StopMonitoring();

                // Ensure background task exits (fixes task memory leak)
                _cancellationTokenSource?.Cancel();

                // Mark as completed
                IsCompleted = true;
                _logger?.LogInformation("Train {TrainName} journey completed", _train.Name);
            }
        }

        #endregion

        #region Event Handlers

        /// <summary>
        /// Background task that processes train movements from the Channel reader.
        /// Replaces async void event handler with proper async/await pattern.
        ///
        /// This method ONLY processes events for THIS train (no identity theft).
        ///
        /// Responsibilities:
        /// 1. Check if the movement event belongs to this train
        /// 2. Update block reservation when train leaves a section
        /// 3. Check for arrival at destination
        /// 4. Trigger look-ahead logic for next block
        /// 5. Handle resumption from waiting state
        /// </summary>
        /// <param name="reader">Channel reader for train movement events</param>
        /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
        private async Task ProcessMovementsAsync(ChannelReader<TrainMovedEventArgs> reader, CancellationToken cancellationToken)
        {
            try
            {
                _logger?.LogInformation("TrainMovementMonitor background processor started for train {TrainName}", _train.Name);

                await foreach (var args in reader.ReadAllAsync(cancellationToken))
                {
                    // Ensure immediate collapse upon disposal
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        // 1. Check if the event belongs to this train
                        bool isMyTrain = string.Equals(args.TrainId, _train.Name, StringComparison.OrdinalIgnoreCase);

                        // 2. Check if this event represents the clearance of a block we are actively waiting for
                        bool isWaitingForThisClearance = false;
                        lock (_trainStateLock)
                        {
                            isWaitingForThisClearance = _train.State == TrainState.WaitingForClearance
                                && !string.IsNullOrEmpty(_waitingForSectionToClear)
                                && (string.Equals(args.PreviousSubSection, _waitingForSectionToClear, StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(args.CurrentSubSection, _waitingForSectionToClear, StringComparison.OrdinalIgnoreCase));
                        }

                        // 3. Filter out completely irrelevant events
                        if (!isMyTrain && !isWaitingForThisClearance)
                            continue;

                        // 4. If the event is JUST a clearance event from a blocking train, handle resumption and skip the rest
                        if (!isMyTrain && isWaitingForThisClearance)
                        {
                            bool isPhysicallyFree = _trackOccupancyService == null || !_trackOccupancyService.IsSectionOccupied(_waitingForSectionToClear);
                            bool isLogicallyFree = _blockReservationService == null || _blockReservationService.GetBlockOwner(_waitingForSectionToClear) == null;

                            if (isPhysicallyFree && isLogicallyFree)
                            {
                                _logger?.LogInformation("🟢 TRACK CLEARED: Section {Section} is now free. Resuming {TrainName}.", _waitingForSectionToClear, _train.Name);
                                await ResumeTrainForGreenSignalAsync();
                            }
                            continue;
                        }

                        _logger?.LogDebug("TrainMovementMonitor processing movement for {TrainName}: {Previous} -> {Current}",
                            _train.Name, args.PreviousSubSection ?? "start", args.CurrentSubSection);

                        // 0. Advance route index AFTER physical movement is confirmed (CQS compliance)
                        if (!string.IsNullOrEmpty(args.CurrentSubSection))
                        {
                            AdvanceRouteIndex(args.CurrentSubSection);
                        }

                        // 1. Release the logical block reservation for the section we just left
                        if (!string.IsNullOrEmpty(args.PreviousSubSection) && _blockReservationService != null)
                        {
                            _blockReservationService.ReleaseBlock(args.PreviousSubSection, _train.Name);
                            _logger?.LogDebug("🔓 BLOCK RELEASED: {Section} released by train {TrainName}",
                                args.PreviousSubSection, _train.Name);
                        }

                        // Safely capture current state to prevent torn reads
                        TrainState currentState;
                        lock (_trainStateLock)
                        {
                            currentState = _train.State;
                        }

                        // 2. Check for arrival at destination
                        if (!string.IsNullOrEmpty(_destinationSectionName) &&
                            (currentState == TrainState.Moving || currentState == TrainState.PrepareToStop) &&
                            string.Equals(args.CurrentSubSection, _destinationSectionName, StringComparison.OrdinalIgnoreCase))
                        {
                            _statusService?.ShowSuccess($"🚂 {_train.Name} ÉRKEZETT: {_destinationSectionName}", _train.Name);
                            await StopTrainOnArrivalAsync();
                            OnTrainArrived();
                            return; // Exit background task on arrival
                        }

                        // 3. Check for block ahead (look-ahead logic)
                        if (!string.IsNullOrEmpty(args.CurrentSubSection) && (currentState == TrainState.Moving || currentState == TrainState.PrepareToStop))
                        {
                            await CheckBlockAheadAsync(args.CurrentSubSection);
                        }

                        // 4. Resumption logic is now handled at the top of the method.
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing movement event for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                    }
                }

                _logger?.LogInformation("TrainMovementMonitor background processor completed for train {TrainName}", _train.Name);
            }
            catch (OperationCanceledException)
            {
                _logger?.LogInformation("TrainMovementMonitor background processor cancelled for train {TrainName}", _train.Name);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "TrainMovementMonitor background processor failed for train {TrainName} - [TrainMovementMonitor]", _train.Name);
            }
        }

        #endregion

        #region Arrival Handling

        /// <summary>
        /// Initialize destination section name for train arrival detection.
        /// </summary>
        public async Task InitializeDestinationSectionNameAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var destPlatform = await dbContext.Platforms
                    .Include(p => p.SubSection)
                    .FirstOrDefaultAsync(p => p.DB_ID == _timetableEntry.DestinationPlatform_DB_ID);
                _destinationSectionName = destPlatform?.SubSection?.Name;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error initializing destination section name for train {TrainName} - [TrainMovementMonitor]", _train.Name);
            }
        }

        /// <summary>
        /// Get subsection name from a station by finding an available platform.
        /// </summary>
        private async Task<string> GetSubSectionFromPlatformAsync(int platformId)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                var sourcePlatform = await dbContext.Platforms
                    .Include(p => p.SubSection)
                    .FirstOrDefaultAsync(p => p.DB_ID == platformId);

                return sourcePlatform?.SubSection?.Name;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error getting subsection for platform {PlatformId} - [TrainMovementMonitor]", platformId);
                return null;
            }
        }

        /// <summary>
        /// Stop the train when it arrives at destination.
        /// </summary>
        private async Task StopTrainOnArrivalAsync()
        {
            try
            {
                _fileLogger?.Log($"[TRAIN MONITOR] Train {_train.Name} arrived successfully at destination.");

                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _train.Direction ? Direction.Forward : Direction.Backward);
                await _rocrailCommandService.PowerOffTrainAsync(_train.Name);

                // Update train state (thread-safe)
                lock (_trainStateLock)
                {
                    _train.CurrentSpeed = Speed.ZERO;
                    _train.State = TrainState.Arrived;
                }

                // Release all logical block reservations for this train
                if (_blockReservationService != null)
                {
                    _blockReservationService.ReleaseAllForTrain(_train.Name);
                }

                // Notify TrackHandlerService
                if (_trackHandlerService != null)
                {
                    _trackHandlerService.OnTrainCompleted();
                }

                // Unsubscribe from occupancy service (background task will exit automatically)
                // Thread-safe: Use lock to prevent race conditions
                lock (_subscriptionLock)
                {
                    _isSubscribedToOccupancyService = false;
                    _movementReader = null;
                }

                await SaveArrivalRecordAsync();

                // Record travel time for future estimates
                if (_travelTimeService != null && JourneyStartTimeDateTime != null)
                {
                    var journeyDuration = _virtualClock.CurrentTime - JourneyStartTimeDateTime.Value;
                    // Fire and forget the save operation
                    _ = _travelTimeService.RecordTravelTimeAsync(
                        _timetableEntry.Train_DB_ID,
                        _timetableEntry.SourcePlatform_DB_ID,
                        _timetableEntry.DestinationPlatform_DB_ID,
                        journeyDuration);
                }

                // Signal journey completion
                _journeyCompletionSource?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping train {TrainName} on arrival - [TrainMovementMonitor]", _train.Name);
            }
        }

        /// <summary>
        /// Save arrival record to database.
        /// Uses fresh DbContext instance to ensure proper EF Core Change Tracking.
        /// </summary>
        private async Task SaveArrivalRecordAsync()
        {
            try
            {
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var trackedEntry = await dbContext.TimetableEntries.FindAsync(_timetableEntry.DB_ID);
                    if (trackedEntry != null)
                    {
                        // Only modify the tracked entry from DbContext to ensure EF Core Change Tracking works correctly
                        trackedEntry.EntryState = EntryState.Arrived;
                        trackedEntry.ArrivedTime = _virtualClock.CurrentTime;
                        await dbContext.SaveChangesAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error saving arrival record for train {TrainName} - [TrainMovementMonitor]", _train.Name);
            }
        }

        #endregion

        #region Monitoring Control

        /// <summary>
        /// Start tracking this train's movement via TrackOccupancyService Channel.
        /// This replaces the raw MQTT feedback approach with train-specific event handling.
        /// Uses background task with Channel-based pub/sub instead of C# events.
        /// Thread-safe: Uses lock to prevent race conditions during rapid start/stop commands.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for controlling the background task lifecycle</param>
        public void StartTracking(CancellationToken cancellationToken)
        {
            lock (_subscriptionLock)
            {
                if (_trackOccupancyService != null && !_isSubscribedToOccupancyService)
                {
                    // Subscribe to train movement notifications via Channel
                    var (reader, subscription) = _trackOccupancyService.Subscribe();
                    _movementReader = reader;
                    _movementSubscription = subscription;

                    // Start background processing task with provided cancellation token
                    _ = Task.Run(() => ProcessMovementsAsync(_movementReader, cancellationToken));

                    _isSubscribedToOccupancyService = true;
                    _logger?.LogInformation("TrainMovementMonitor started tracking train {TrainName} via Channel", _train.Name);
                }
            }
        }

        /// <summary>
        /// Stop monitoring the destination section and cleanup subscription.
        /// Thread-safe: Uses lock to prevent race conditions during rapid start/stop commands.
        /// Note: The cancellation token is managed externally by the journey controller.
        /// </summary>
        public void StopMonitoring()
        {
            lock (_subscriptionLock)
            {
                // Dispose the channel subscription to prevent memory leaks
                // Null-check and dispose within lock to prevent race conditions
                if (_movementSubscription != null)
                {
                    try
                    {
                        _movementSubscription.Dispose();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Already disposed, ignore
                    }
                    _movementSubscription = null;
                }

                // Unsubscribe from occupancy service (background task will exit automatically via cancellation token)
                _isSubscribedToOccupancyService = false;
                _movementReader = null;

                _logger?.LogInformation("TrainMovementMonitor stopped tracking train {TrainName}", _train.Name);
            }
        }

        protected virtual void OnTrainArrived()
        {
            TrainArrived?.Invoke(this, new TrainArrivedEventArgs(_train, _destinationSectionName ?? ""));
        }

        public string? GetDestinationSectionName() => _destinationSectionName;

        #endregion

        #region IDisposable Implementation

        /// <summary>
        /// Public Dispose method that follows the standard dispose pattern.
        /// Ensures proper cleanup of resources including background tasks.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Protected virtual Dispose method that performs the actual cleanup.
        /// Called by Dispose() and optionally by a finalizer.
        /// </summary>
        /// <param name="disposing">True if disposing managed resources</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                // Stop any active monitoring and background tasks (uses internal lock)
                StopMonitoring();

                // Prevent hanging tasks by signaling completion source
                _journeyCompletionSource?.TrySetCanceled();

                // Cancel and dispose the cancellation token source
                try
                {
                    if (_cancellationTokenSource != null)
                    {
                        if (!_cancellationTokenSource.IsCancellationRequested)
                        {
                            _cancellationTokenSource.Cancel();
                        }
                        _cancellationTokenSource.Dispose();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Already disposed, ignore
                }
            }

            _disposed = true;
            _logger?.LogInformation("TrainMovementMonitor disposed for train {TrainName}", _train.Name);
        }

        #endregion
    }

    /// <summary>
    /// Event args for train arrival event.
    /// </summary>
    public class TrainArrivedEventArgs : EventArgs
    {
        public Train Train { get; }
        public string DestinationSection { get; }

        public TrainArrivedEventArgs(Train train, string destinationSection)
        {
            Train = train;
            DestinationSection = destinationSection;
        }
    }
}
