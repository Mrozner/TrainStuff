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
        private readonly ITrainLocationRegistry _locationRegistry;

        #endregion

        #region State Caches & Concurrency

        /// <summary>
        /// Tracks blocks whose switches have already been configured by this train.
        /// Prevents sending redundant DCC commands to physical hardware.
        /// </summary>
        private readonly HashSet<string> _configuredBlocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();

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
            TravelTimeMeasurementService travelTimeService = null,
            ITrainLocationRegistry locationRegistry = null)
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
            _locationRegistry = locationRegistry;

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
        /// Check if a block is blocked by another train (physical OR logical).
        /// Used for N=3 lookahead collision detection.
        /// </summary>
        /// <param name="sectionName">Section name to check</param>
        /// <returns>True if blocked by another train, false if clear or owned by this train</returns>
        private bool IsBlockBlockedByOtherTrain(string sectionName)
        {
            try
            {
                if (string.IsNullOrEmpty(sectionName))
                    return false;

                // Check physical occupancy
                if (_trackOccupancyService != null)
                {
                    bool isOccupied = _trackOccupancyService.IsLogicalBlockOccupied(sectionName);

                    if (isOccupied)
                    {
                        // Get the name of the occupying train
                        if (_trackOccupancyService.TryGetTrainInLogicalBlock(sectionName, out string occupyingTrain))
                        {
                            if (!string.IsNullOrEmpty(occupyingTrain) &&
                                !string.Equals(occupyingTrain, _train.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger?.LogDebug("🚦 BLOCKED (physical): {Section} occupied by {Train}",
                                    sectionName, occupyingTrain);
                                return true;
                            }
                        }
                    }
                }

                // Check logical reservation
                if (_blockReservationService != null)
                {
                    var reservingTrain = _blockReservationService.GetBlockOwner(sectionName);
                    if (!string.IsNullOrEmpty(reservingTrain) &&
                        !string.Equals(reservingTrain, _train.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.LogDebug("🚦 BLOCKED (logical): {Section} reserved by {Train}",
                            sectionName, reservingTrain);
                        return true;
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error checking if block is blocked for {Section} - [TrainMovementMonitor]", sectionName);
                return true; // Assume blocked on error for safety
            }
        }

        /// <summary>
        /// Get the next N expected subsections on the train's route for ETCS-style lookahead.
        ///
        /// This method traverses the RoutePlan to determine which sections
        /// the train will enter next, enabling N-block look-ahead collision avoidance.
        ///
        /// Algorithm:
        /// 1. Get current position (or starting position)
        /// 2. Find the current edge in the route plan
        /// 3. Return the next N target nodes chronologically
        /// </summary>
        /// <param name="currentSubSection">The train's current subsection name (can be null)</param>
        /// <param name="count">Number of sections to look ahead (default: 3 for ETCS N=3)</param>
        /// <returns>List of next N expected subsection names, may be less than N if approaching destination</returns>
        public List<string> GetNextNExpectedSubSections(string currentSubSection, int count = 3)
        {
            try
            {
                var nextSections = new List<string>();

                if (_routePlan?.Path == null || !_routePlan.Path.Any())
                {
                    _logger?.LogWarning("Cannot calculate next expected sections: no route plan for train {TrainName}", _train.Name);
                    return nextSections;
                }

                // Thread-safe: Capture current route index to minimize lock contention
                int currentRouteIndex;
                lock (_routeIndexLock)
                {
                    currentRouteIndex = _currentRouteIndex;
                }

                // Convert current subsection name to section ID
                int currentSectionId = -1;
                if (!string.IsNullOrEmpty(currentSubSection))
                {
                    currentSectionId = _trackGraph.GetNodeIdByName(currentSubSection);
                    if (currentSectionId == -1)
                    {
                        _logger?.LogWarning("Current subsection {SubSection} not found in track graph for train {TrainName}",
                            currentSubSection, _train.Name);
                        return nextSections;
                    }
                }

                // Find our current position in the route
                int edgeIndex = currentRouteIndex;

                // If current section is null, start from the beginning
                if (currentSectionId == -1)
                {
                    edgeIndex = currentRouteIndex;
                }
                else
                {
                    // Find the edge where we are currently at
                    for (int i = currentRouteIndex; i < _routePlan.Path.Count; i++)
                    {
                        var edge = _routePlan.Path[i];

                        // Check if we're at the source of this edge (about to traverse it)
                        if (edge.SourceNodeId == currentSectionId)
                        {
                            edgeIndex = i;
                            break;
                        }

                        // Check if we're at the target of this edge (just completed it)
                        if (edge.TargetNodeId == currentSectionId)
                        {
                            edgeIndex = i + 1;
                            break;
                        }
                    }
                }

                // Collect the next N sections
                int collectedCount = 0;
                for (int i = edgeIndex; i < _routePlan.Path.Count && collectedCount < count; i++)
                {
                    var edge = _routePlan.Path[i];
                    var nextSectionName = _trackGraph.GetSectionNameById(edge.TargetNodeId);

                    if (!string.IsNullOrEmpty(nextSectionName))
                    {
                        nextSections.Add(nextSectionName);
                        collectedCount++;

                        _logger?.LogDebug("Next section #{Count}: {Section} (edge index: {Index})",
                            collectedCount, nextSectionName, i);
                    }
                }

                _logger?.LogDebug("Next {Count} expected sections for train {TrainName}: {Sections}",
                    nextSections.Count, _train.Name, string.Join(", ", nextSections));

                return nextSections;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error calculating next N expected sections for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                return new List<string>();
            }
        }

        /// <summary>
        /// Legacy method for backward compatibility - uses GetNextNExpectedSubSections with count=1
        /// </summary>
        public string GetNextExpectedSubSection(string currentSubSection)
        {
            var nextSections = GetNextNExpectedSubSections(currentSubSection, 1);
            return nextSections.FirstOrDefault();
        }

        /// <summary>
        /// Universal translator: Converts a raw physical sensor name into its parent logical block name.
        /// </summary>
        private string GetLogicalNameFromSensor(string sensorName)
        {
            if (string.IsNullOrEmpty(sensorName)) return null;

            int logicalId = _trackGraph.GetNodeIdByName(sensorName);
            if (logicalId == -1) return null;

            return _trackGraph.GetSectionNameById(logicalId);
        }

        /// <summary>
        /// Safely configure switches for a block with caching to prevent duplicate DCC commands.
        /// Uses a cache to track which blocks have already been configured by this train,
        /// preventing network spam ("double-arming") of physical switches.
        /// </summary>
        /// <param name="sourceBlockId">Source block ID (current section)</param>
        /// <param name="targetBlock">Target block name to configure switches for</param>
        /// <param name="fireAndForget">If true, run configuration in background without awaiting (for N+2 pre-arming)</param>
        private async Task ConfigureSwitchSafelyAsync(int sourceBlockId, string targetBlock, bool fireAndForget)
        {
            try
            {
                // Check cache first (thread-safe)
                bool alreadyConfigured;
                lock (_cacheLock)
                {
                    alreadyConfigured = _configuredBlocks.Contains(targetBlock);
                    if (!alreadyConfigured)
                    {
                        _configuredBlocks.Add(targetBlock);
                    }
                }

                if (alreadyConfigured)
                {
                    _logger?.LogDebug("⚡ SWITCH CACHE HIT: {TargetBlock} already configured for train {TrainName}",
                        targetBlock, _train.Name);
                    return;
                }

                // Configure switches (with fire-and-forget support)
                Task configurationTask = Task.Run(async () =>
                {
                    try
                    {
                        if (_switchConfigurationService != null && _routePlan != null)
                        {
                            int targetBlockId = _trackGraph.GetNodeIdByName(targetBlock);
                            await _switchConfigurationService.ConfigureSwitchesForBlockAsync(
                                sourceBlockId, targetBlockId, targetBlock, _routePlan, _train.Name);

                            _logger?.LogDebug("🔧 SWITCHES CONFIGURED: {TargetBlock} for train {TrainName}",
                                targetBlock, _train.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Fail-safe: Remove from cache so it can be retried later
                        lock (_cacheLock)
                        {
                            _configuredBlocks.Remove(targetBlock);
                        }

                        _logger?.LogError(ex, "Failed to configure switches for {TargetBlock} - removed from cache for retry - [TrainMovementMonitor]",
                            targetBlock);
                    }
                });

                // Wait for configuration if not fire-and-forget
                if (!fireAndForget)
                {
                    await configurationTask;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in ConfigureSwitchSafelyAsync for {TargetBlock} - [TrainMovementMonitor]", targetBlock);
            }
        }

        /// <summary>
        /// Advance the route index when a train physically enters a new section.
        /// Made PRIVATE to prevent external services from bypassing the TrainId filter.
        /// </summary>
        private void AdvanceRouteIndex(string currentSubSection)
        {
            try
            {
                if (_routePlan?.Path == null || !_routePlan.Path.Any() || string.IsNullOrEmpty(currentSubSection))
                {
                    return;
                }

                int currentSectionId = _trackGraph.GetNodeIdByName(currentSubSection);
                if (currentSectionId == -1) return;

                // --- 1. Sensor Bounce Bypass ---
                if (_currentRouteIndex < _routePlan.Path.Count)
                {
                    if (_routePlan.Path[_currentRouteIndex].SourceNodeId == currentSectionId) return;
                    if (_currentRouteIndex > 0 && _routePlan.Path[_currentRouteIndex - 1].SourceNodeId == currentSectionId) return;
                }
                else if (_currentRouteIndex == _routePlan.Path.Count && _routePlan.Path.Count > 0)
                {
                    if (_routePlan.Path.Last().TargetNodeId == currentSectionId || _routePlan.Path.Last().SourceNodeId == currentSectionId) return;
                }

                // --- 2. Plausibility Guard (Windowed Lookahead) ---
                bool foundEdge = false;

                // ONLY look ahead 2 edges max. This allows for 1 physically missed/failed sensor,
                // but prevents the train from teleporting across the map to another train's location.
                int lookaheadLimit = Math.Min(_currentRouteIndex + 2, _routePlan.Path.Count);

                for (int i = _currentRouteIndex; i < lookaheadLimit; i++)
                {
                    var edge = _routePlan.Path[i];

                    if (edge.TargetNodeId == currentSectionId)
                    {
                        foundEdge = true;
                        lock (_routeIndexLock)
                        {
                            _currentRouteIndex = i + 1;
                        }
                        _logger?.LogDebug("Route index advanced to {Index} for train {TrainName} after entering {Section}",
                            _currentRouteIndex, _train.Name, currentSubSection);
                        return;
                    }
                }

                // --- 3. Handle Implausible Sensors Gracefully ---
                if (!foundEdge)
                {
                    // If the sensor hit isn't immediately adjacent to our current location,
                    // DO NOT search the whole route and DO NOT trigger an emergency stop.
                    // In a multi-train layout, this is 99% cross-talk from another train or a ghost read.
                    _logger?.LogDebug("Train {TrainName} ignored physically implausible sensor hit at {Section}. Likely cross-talk from an untracked train.",
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
        /// Unified movement authority evaluation with ETCS-style N=3 lookahead.
        ///
        /// Implements dynamic deceleration based on block occupancy:
        /// - N+1 (next block): MANDATORY SAFETY - reserve or stop
        /// - N+2 (second block): Pre-arm switches with fire-and-forget
        /// - N+3 (third block): Double Yellow signal - reduce to MEDIUM
        /// - Clear path: Maintain assigned cruise speed (never default to MAX)
        ///
        /// This method replaces the scattered lookahead logic with a single,
        /// authoritative movement authority calculation.
        /// </summary>
        /// <param name="currentSubSection">The train's current subsection</param>
        /// <param name="isStartingFromStop">True if resuming from a red signal or initial departure</param>
        private async Task EvaluateMovementAuthorityAsync(string currentSubSection, bool isStartingFromStop = false)
        {
            try
            {
                // Allow evaluation if actively moving, preparing to stop, OR if explicitly starting/resuming from a dead stop
                bool canEvaluate;
                lock (_trainStateLock)
                {
                    canEvaluate = isStartingFromStop ||
                                  _train.State == TrainState.Moving ||
                                  _train.State == TrainState.PrepareToStop;
                }
                if (!canEvaluate) return;

                // === STEP 1: Get next 3 expected sections ===
                var nextBlocks = GetNextNExpectedSubSections(currentSubSection, 3);

                // === STEP 2: Set target speed (never default to MAX, always maintain assigned cruise speed) ===
                Speed targetSpeed = _train.MaxSpeed == Speed.ZERO ? Speed.MEDIUM : _train.MaxSpeed;
                string speedReason = "Maintaining assigned cruise speed";

                if (nextBlocks.Any())
                {
                    // === STEP 3: N+1 SAFETY CHECK ===
                    string n1Block = nextBlocks[0];

                    if (_blockReservationService != null)
                    {
                        bool reservationSuccess = _blockReservationService.TryReserveBlock(n1Block, _train.Name);

                        if (!reservationSuccess)
                        {
                            var blockingTrain = _blockReservationService.GetBlockOwner(n1Block);
                            _logger?.LogWarning("🛑 RED SIGNAL: Train {TrainName} must stop at {CurrentSection}. Next block {NextBlock} is reserved by {BlockingTrain}", _train.Name, currentSubSection, n1Block, blockingTrain ?? "unknown");
                            await StopTrainForRedSignalAsync(n1Block, blockingTrain);
                            return;
                        }
                        _logger?.LogDebug("✅ N+1 BLOCK RESERVED: {NextBlock} secured for train {TrainName}", n1Block, _train.Name);
                    }

                    int currentSectionId = _trackGraph.GetNodeIdByName(currentSubSection);
                    await ConfigureSwitchSafelyAsync(currentSectionId, n1Block, fireAndForget: !isStartingFromStop);

                    // === STEP 4: N+2 PRE-ARMING & N+3 SPEED CHECK ===
                    if (nextBlocks.Count >= 2)
                    {
                        string n2Block = nextBlocks[1];
                        bool n2Reserved = _blockReservationService != null && _blockReservationService.TryReserveBlock(n2Block, _train.Name);

                        if (n2Reserved)
                        {
                            int n1Id = _trackGraph.GetNodeIdByName(nextBlocks[0]);
                            await ConfigureSwitchSafelyAsync(n1Id, n2Block, fireAndForget: true);

                            if (nextBlocks.Count >= 3)
                            {
                                string n3Block = nextBlocks[2];
                                if (IsBlockBlockedByOtherTrain(n3Block))
                                {
                                    targetSpeed = Speed.MEDIUM;
                                    speedReason = $"Double Yellow - N+3 block ({n3Block}) occupied";
                                    _logger?.LogInformation("🟡🟡 DOUBLE YELLOW: Train {TrainName} reducing to MEDIUM. N+3 is occupied", _train.Name);
                                }
                            }
                        }
                        else
                        {
                            targetSpeed = Speed.SLOW;
                            speedReason = $"Yellow signal - N+2 block ({n2Block}) occupied";
                            _logger?.LogInformation("🟡 YELLOW SIGNAL: Train {TrainName} reducing to SLOW. N+2 is occupied", _train.Name);
                        }
                    }
                    else
                    {
                        targetSpeed = Speed.SLOW;
                        speedReason = "Approaching terminus";
                        _logger?.LogDebug("Approaching destination - forcing deceleration to SLOW");
                    }
                }
                else
                {
                    _logger?.LogDebug("No route lookahead available for {TrainName}. Reverting to standard sensor running.", _train.Name);
                }

                // === STEP 5: APPLY SPEED ADJUSTMENT (only if changed or starting from stop) ===
                Speed currentSpeed;
                lock (_trainStateLock)
                {
                    currentSpeed = _train.CurrentSpeed;
                }

                if (currentSpeed != targetSpeed || isStartingFromStop)
                {
                    _logger?.LogInformation("🚄 SPEED CHANGE: Train {TrainName} adjusting speed from {OldSpeed} to {NewSpeed}. Reason: {Reason}",
                        _train.Name, currentSpeed, targetSpeed, speedReason);

                    var success = await _rocrailCommandService.SendTrainSpeedCommandAsync(
                        _train.Name,
                        targetSpeed,
                        _timetableEntry.Direction ? Direction.Forward : Direction.Backward);

                    if (success)
                    {
                        // Update train state (thread-safe)
                        lock (_trainStateLock)
                        {
                            _train.CurrentSpeed = targetSpeed;

                            // Update state to Moving if starting from stop
                            if (isStartingFromStop)
                            {
                                _train.State = TrainState.Moving;
                            }
                        }
                    }
                    else
                    {
                        _logger?.LogError("Failed to adjust speed for train {TrainName} - [TrainMovementMonitor]", _train.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error evaluating movement authority for train {TrainName} - [TrainMovementMonitor]", _train.Name);

                // FAIL-SAFE: Emergency stop on any tracking or safety check failure
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
                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _timetableEntry.Direction ? Direction.Forward : Direction.Backward);

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
                var success = await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, resumeSpeed, _timetableEntry.Direction ? Direction.Forward : Direction.Backward);

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

                // Use unified movement authority for departure (handles N+1/N+2 reservations, switch config, and speed)
                var startingSubSection = await GetSubSectionFromPlatformAsync(_timetableEntry.SourcePlatform_DB_ID);
                await EvaluateMovementAuthorityAsync(startingSubSection, isStartingFromStop: true);

                // A successful start means the train is moving, blocked by traffic, or safely unrouted
                bool isSuccessfullyStarted;
                bool isMoving;

                lock (_trainStateLock)
                {
                    isSuccessfullyStarted = _train.State == TrainState.Moving ||
                                            _train.State == TrainState.WaitingForClearance ||
                                            _train.State == TrainState.Waiting;
                    isMoving = _train.State == TrainState.Moving;
                }

                if (isSuccessfullyStarted)
                {
                    JourneyStartTimeDateTime = _virtualClock.CurrentTime;
                    JourneyDuration = estimatedJourneyDuration;
                    IsRunning = true;

                    // --- NEW: Free up the source platform ---
                    if (_locationRegistry != null)
                    {
                        _locationRegistry.ClearParkedLocation(_timetableEntry.Train_DB_ID);
                        _logger?.LogDebug("Train {TrainName} departed. Platform {PlatformId} is now physically free.",
                            _train.Name, _timetableEntry.SourcePlatform_DB_ID);
                    }
                    // ----------------------------------------

                    if (isMoving)
                    {
                        _fileLogger?.Log($"[TRAIN MONITOR] Train {_train.Name} departing. Est. duration: {estimatedJourneyDuration.TotalMinutes:F1} mins.");
                        _statusService?.ShowInfo($"Vonat indul: {_train.Name}");
                    }
                    else
                    {
                        _fileLogger?.Log($"[TRAIN MONITOR] Train {_train.Name} initialized but waiting at departure.");
                        _logger?.LogInformation("Train {TrainName} is safely waiting at departure", _train.Name);
                        _statusService?.ShowWarning($"Indulás várakozik: {_train.Name}", _train.Name);
                    }
                    return true;
                }
                else
                {
                    _logger?.LogWarning("Train {TrainName} failed to enter a valid starting state. Current State: {State}", _train.Name, _train.State);
                    return false;
                }
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
                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _timetableEntry.Direction ? Direction.Forward : Direction.Backward);

                // Also send power off command to fully stop the train
                await _rocrailCommandService.PowerOffTrainAsync(_train.Name);

                // Update train state (thread-safe)
                lock (_trainStateLock)
                {
                    _train.CurrentSpeed = Speed.ZERO;
                    _train.State = TrainState.Stopped;
                }

                // DO NOT release logical block reservations on emergency stop
                // The train is still physically on the track and must maintain its safety bubble
                // Other trains must not be allowed to reserve these blocks while this train is stopped
                // Reservations will be released when the train properly arrives at its destination
                // if (_blockReservationService != null)
                // {
                //     _blockReservationService.ReleaseAllForTrain(_train.Name);
                // }

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
                    _timetableEntry.Direction
                );

                _logger?.LogInformation("Train {TrainName} registered at starting position: {SubSection} (direction: {Direction})",
                    _train.Name, startingSubSection, _timetableEntry.Direction ? "forward" : "reverse");

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

            // CRITICAL: Explicitly stop any existing monitoring before starting a new journey
            // This prevents subscription leaks and ensures the previous background task is dead
            StopMonitoring();

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
                    // Wait for previous journey to fully complete (including cleanup)
                    if (previousTask != null)
                    {
                        try
                        {
                            await previousTask;
                            _logger?.LogDebug("Previous journey task completed for train {TrainName}", _train.Name);
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

                // Calculate stop time for logging purposes only - no longer controls loop lifecycle
                var stopTime = CalculateStopTime();

                // The loop should run until the train physically arrives, regardless of the virtual clock
                while (!cancellationToken.IsCancellationRequested && _train.State != TrainState.Arrived)
                {
                    await Task.Delay(100, cancellationToken);

                    // Only log virtual clock completion once
                    if (_virtualClock.CurrentTime >= stopTime && _train.State == TrainState.Moving)
                    {
                         // Optional: Log that the timetable slot has expired, but the train is still physically moving
                         _logger?.LogDebug($"Virtual timetable slot expired for {_train.Name}, awaiting physical arrival.");
                    }

                    if (ShouldPrepareToStop())
                    {
                        lock (_trainStateLock)
                        {
                            _train.State = TrainState.PrepareToStop;
                        }
                    }
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
        /// <summary>
        /// Background task that processes train movements from the Channel reader.
        /// Features "Sweep Release" to prevent Ghost Locks, "Universal Wakeup" to cure traffic jams,
        /// and a polling heartbeat to wake up trains when logical releases happen without physical events.
        /// </summary>
        private async Task ProcessMovementsAsync(ChannelReader<TrainMovedEventArgs> reader, CancellationToken cancellationToken)
        {
            try
            {
                _logger?.LogInformation("TrainMovementMonitor background processor started for train {TrainName}", _train.Name);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    // Wait for a real event OR a 1-second timeout if we're waiting for clearance
                    bool isWaiting;
                    lock (_trainStateLock)
                    {
                        isWaiting = _train.State == TrainState.WaitingForClearance
                            && !string.IsNullOrEmpty(_waitingForSectionToClear);
                    }

                    TrainMovedEventArgs args = null;
                    bool timedOut = false;

                    if (isWaiting)
                    {
                        // Race: either a real event arrives, or we self-tick after 1 second
                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(TimeSpan.FromSeconds(1));
                        try
                        {
                            await reader.WaitToReadAsync(cts.Token);
                            reader.TryRead(out args);
                        }
                        catch (OperationCanceledException)
                        {
                            // Timeout fired (not cancellation)
                            timedOut = !cancellationToken.IsCancellationRequested;
                        }
                    }
                    else
                    {
                        // Normal blocking wait - no timeout needed
                        if (!await reader.WaitToReadAsync(cancellationToken)) break;
                        reader.TryRead(out args);
                    }

                    // === HEARTBEAT WAKEUP PATH ===
                    if (timedOut)
                    {
                        // Self-triggered check: re-evaluate the waiting block
                        string sectionToCheck;
                        lock (_trainStateLock) { sectionToCheck = _waitingForSectionToClear; }

                        if (!string.IsNullOrEmpty(sectionToCheck))
                        {
                            bool isPhysicallyFree = _trackOccupancyService == null
                                || !_trackOccupancyService.IsLogicalBlockOccupied(sectionToCheck);
                            bool isLogicallyFree = _blockReservationService == null
                                || _blockReservationService.GetBlockOwner(sectionToCheck) == null;

                            if (isPhysicallyFree && isLogicallyFree)
                            {
                                _logger?.LogInformation("⏱️ HEARTBEAT WAKEUP: {Section} now free for {Train}",
                                    sectionToCheck, _train.Name);

                                if (_blockReservationService == null || _blockReservationService.TryReserveBlock(sectionToCheck, _train.Name))
                                {
                                    int currentId = -1;
                                    lock (_routeIndexLock)
                                    {
                                        if (_currentRouteIndex < _routePlan.Path.Count)
                                            currentId = _routePlan.Path[_currentRouteIndex].SourceNodeId;
                                    }
                                    if (currentId != -1)
                                    {
                                        string currentSectionName = _trackGraph.GetSectionNameById(currentId);
                                        await EvaluateMovementAuthorityAsync(currentSectionName, isStartingFromStop: true);
                                    }
                                }
                            }
                        }
                        continue;
                    }

                    // Exit if no event and didn't time out (channel closed)
                    if (args == null) break;

                    // === NORMAL EVENT PROCESSING PATH ===
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    try
                    {
                        // TRANSLATE PHYSICAL SENSORS TO LOGICAL BLOCKS IMMEDIATELY
                        string logicalPreviousBlock = GetLogicalNameFromSensor(args.PreviousSubSection);
                        string logicalCurrentBlock = GetLogicalNameFromSensor(args.CurrentSubSection);

                        bool isMyTrain = string.Equals(args.TrainId, _train.Name, StringComparison.OrdinalIgnoreCase);

                        // --- FIX PART 2: THE UNIVERSAL WAKEUP ---
                        // Instead of filtering to see if the event specifically mentions our blocked section,
                        // we evaluate our target block on EVERY physical layout tick if we are waiting.
                        bool isWaitingForClearance = false;
                        lock (_trainStateLock)
                        {
                            isWaitingForClearance = _train.State == TrainState.WaitingForClearance
                                && !string.IsNullOrEmpty(_waitingForSectionToClear);
                        }

                        // Filter out irrelevant events (but KEEP events if we are waiting for traffic to clear)
                        if (!isMyTrain && !isWaitingForClearance)
                            continue;

                        // If we are waiting, check if our target block is free right now
                        if (!isMyTrain && isWaitingForClearance)
                        {
                            bool isPhysicallyFree = _trackOccupancyService == null || !_trackOccupancyService.IsLogicalBlockOccupied(_waitingForSectionToClear);
                            bool isLogicallyFree = _blockReservationService == null || _blockReservationService.GetBlockOwner(_waitingForSectionToClear) == null;

                            if (isPhysicallyFree && isLogicallyFree)
                            {
                                _logger?.LogInformation("🟢 TRACK CLEARED: Attempting to reserve {Section} for {TrainName}.", _waitingForSectionToClear, _train.Name);

                                // Attempt to win the lock
                                if (_blockReservationService == null || _blockReservationService.TryReserveBlock(_waitingForSectionToClear, _train.Name))
                                {
                                    int currentId = -1;
                                    lock (_routeIndexLock)
                                    {
                                        if (_currentRouteIndex < _routePlan.Path.Count)
                                            currentId = _routePlan.Path[_currentRouteIndex].SourceNodeId;
                                    }

                                    if (currentId != -1)
                                    {
                                        string currentSectionName = _trackGraph.GetSectionNameById(currentId);
                                        await EvaluateMovementAuthorityAsync(currentSectionName, isStartingFromStop: true);
                                    }
                                }
                            }
                            continue;
                        }

                        _logger?.LogDebug("TrainMovementMonitor processing movement for {TrainName}: {Previous} -> {Current}",
                            _train.Name, args.PreviousSubSection ?? "start", args.CurrentSubSection);

                        // Advance route index AFTER physical movement is confirmed
                        if (!string.IsNullOrEmpty(args.CurrentSubSection))
                        {
                            AdvanceRouteIndex(args.CurrentSubSection);
                        }

                        // --- FIX PART 1: THE SWEEP RELEASE ---
                        // Sweep the route plan and release ALL blocks behind our current physical location.
                        // This cures "Ghost Locks" left behind by missed hardware sensors.
                        if (!string.IsNullOrEmpty(logicalCurrentBlock) && _blockReservationService != null && _routePlan != null)
                        {
                            int currentIndex;
                            lock (_routeIndexLock)
                            {
                                currentIndex = _currentRouteIndex;
                            }

                            // Sweep all nodes behind us
                            for (int i = 0; i < currentIndex; i++)
                            {
                                var edge = _routePlan.Path[i];
                                string pastSectionName = _trackGraph.GetSectionNameById(edge.SourceNodeId);

                                // Release any past block, EXCEPT the block we are currently inside
                                if (!string.IsNullOrEmpty(pastSectionName) &&
                                    !string.Equals(pastSectionName, logicalCurrentBlock, StringComparison.OrdinalIgnoreCase))
                                {
                                    _blockReservationService.ReleaseBlock(pastSectionName, _train.Name);

                                    lock (_cacheLock)
                                    {
                                        _configuredBlocks.Remove(pastSectionName);
                                    }
                                }
                            }

                            // Fallback: Ensure the explicitly provided previous block is released
                            if (!string.IsNullOrEmpty(logicalPreviousBlock) &&
                                !string.Equals(logicalPreviousBlock, logicalCurrentBlock, StringComparison.OrdinalIgnoreCase))
                            {
                                _blockReservationService.ReleaseBlock(logicalPreviousBlock, _train.Name);
                                lock (_cacheLock)
                                {
                                    _configuredBlocks.Remove(logicalPreviousBlock);
                                }
                            }
                        }

                        // Safely capture current state to prevent torn reads
                        TrainState currentState;
                        lock (_trainStateLock)
                        {
                            currentState = _train.State;
                        }

                        // Check for arrival at destination
                        if (!string.IsNullOrEmpty(_destinationSectionName) &&
                            (currentState == TrainState.Moving || currentState == TrainState.PrepareToStop) &&
                            string.Equals(args.CurrentSubSection, _destinationSectionName, StringComparison.OrdinalIgnoreCase))
                        {
                            _statusService?.ShowSuccess($"🚂 {_train.Name} ÉRKEZETT: {_destinationSectionName}", _train.Name);
                            await StopTrainOnArrivalAsync();
                            OnTrainArrived();
                            return; // Exit background task on arrival
                        }

                        // Evaluate movement authority (unified lookahead logic)
                        if (!string.IsNullOrEmpty(args.CurrentSubSection) && (currentState == TrainState.Moving || currentState == TrainState.PrepareToStop))
                        {
                            await EvaluateMovementAuthorityAsync(args.CurrentSubSection, isStartingFromStop: false);
                        }
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

                await _rocrailCommandService.SendTrainSpeedCommandAsync(_train.Name, Speed.ZERO, _timetableEntry.Direction ? Direction.Forward : Direction.Backward);
                await _rocrailCommandService.PowerOffTrainAsync(_train.Name);

                // Update train state (thread-safe)
                lock (_trainStateLock)
                {
                    _train.CurrentSpeed = Speed.ZERO;
                    _train.State = TrainState.Arrived;
                }

                // --- NEW: Lock the destination platform ---
                if (_locationRegistry != null && !string.IsNullOrEmpty(_destinationSectionName))
                {
                    _locationRegistry.SetParkedLocation(_timetableEntry.Train_DB_ID, _timetableEntry.DestinationPlatform_DB_ID, _destinationSectionName);
                    _logger?.LogDebug("Train {TrainName} arrived. Platform {PlatformId} (Block {BlockName}) is now physically occupied.",
                        _train.Name, _timetableEntry.DestinationPlatform_DB_ID, _destinationSectionName);
                }
                // ----------------------------------------

                // Release all logical block reservations for this train
                if (_blockReservationService != null)
                {
                    _blockReservationService.ReleaseAllForTrain(_train.Name);

                    // Clear all switch configuration cache for this train
                    // This ensures clean state if the train starts a new journey
                    lock (_cacheLock)
                    {
                        _configuredBlocks.Clear();
                    }
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
                // CRITICAL: Prevent subscription leaks - stop any existing monitoring first
                // This ensures we don't create multiple background tasks for the same train
                if (_isSubscribedToOccupancyService)
                {
                    _logger?.LogWarning("TrainMovementMonitor for train {TrainName} is already subscribed. Stopping previous subscription before starting new one.", _train.Name);
                    StopMonitoring();
                }

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

                // Clear switch configuration cache for clean disposal
                lock (_cacheLock)
                {
                    _configuredBlocks.Clear();
                }

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
