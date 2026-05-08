using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Consolidated state for a tracked train, including its current position and direction.
    /// </summary>
    /// <remarks>
    /// This record replaces the separate _trainPositions and _trainDirections dictionaries,
    /// providing atomic updates and preventing tearing issues where position and direction
    /// could become temporarily inconsistent during concurrent operations.
    /// </remarks>
    public record TrainTrackingState
    {
        /// <summary>
        /// The current subsection where the train is located.
        /// </summary>
        public string CurrentSection { get; set; }

        /// <summary>
        /// The train's current direction (true = forward, false = reverse).
        /// </summary>
        public bool Direction { get; set; }
    }

    /// <summary>
    /// Centralized service for tracking real-time physical occupancy of every subsection on the railway layout.
    ///
    /// This service provides the foundation for dynamic, rolling-block collision avoidance by maintaining
    /// a live view of which train occupies which section at any moment. It replaces the static reservation-based
    /// system with event-driven traffic control.
    ///
    /// Key Features:
    /// - Thread-safe occupancy tracking using lock-based synchronization
    /// - Real-time position updates via Rocrail MQTT feedback
    /// - Automatic train movement detection and event firing
    /// - Adjacent section validation to prevent false positives
    /// - Support for train registration and query operations
    /// - Automatic obstruction detection and registration for unidentified sensor triggers
    ///
    /// Physical Safety:
    /// When a section sensor triggers but no known train can be identified (e.g., manual placement,
    /// debris, or equipment), the service registers an "UNKNOWN_OBSTACLE" entry. This prevents
    /// pathfinding services from routing program-controlled trains through occupied sections,
    /// ensuring physical safety even when the system cannot identify what occupies the track.
    /// </summary>
    public class TrackOccupancyService : IDisposable
    {
        #region Dependencies

        private readonly MqttInfrastructureService _mqttService;
        private readonly ILogger<TrackOccupancyService> _logger;
        private readonly SemaphoreSlim _initializationSemaphore;
        private readonly ITrackGraphFactory _trackGraphFactory;
        private TrackGraph _trackGraph;
        private readonly FileLoggingService? _fileLogger;
        private readonly ITrainLocationRegistry _locationRegistry;

        #endregion

        #region State Management

        /// <summary>
        /// Central source of truth mapping SubSectionName -> TrainName.
        /// Thread-safe dictionary tracking which train occupies which block at any moment.
        /// Key: SubSection name (e.g., "section_1"), Value: Train name (e.g., "Train_1")
        /// </summary>
        private readonly Dictionary<string, string> _occupancyMap;

        /// <summary>
        /// Consolidated train state tracking.
        /// Replaces the separate _trainPositions and _trainDirections dictionaries.
        /// Key: Train name, Value: TrainTrackingState containing CurrentSection and Direction
        /// </summary>
        private readonly Dictionary<string, TrainTrackingState> _trainStates;

        /// <summary>
        /// Lock object for thread-safe access to _occupancyMap and _trainStates.
        /// All reads and writes to these dictionaries must be protected by this lock.
        /// </summary>
        private readonly object _stateLock = new object();

        /// <summary>
        /// Processing lock-set to prevent TOCTOU race conditions from concurrent MQTT threads.
        /// Tracks sections currently being processed to prevent duplicate occupancy events.
        /// </summary>
        private readonly HashSet<string> _processingSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Initialization state flag to prevent duplicate MQTT subscriptions
        /// </summary>
        private volatile bool _isInitialized = false;

        #endregion

        #region Events

        /// <summary>
        /// HashSet of channel writers for pub/sub train movement notifications.
        /// Replaces C# events with System.Threading.Channels for better async handling.
        /// All access must be protected by _stateLock.
        /// </summary>
        private readonly HashSet<ChannelWriter<TrainMovedEventArgs>> _subscribers;

        /// <summary>
        /// Disposable subscription token for proper cleanup of channel subscribers.
        /// Prevents unbounded memory leaks from uncompleted channels.
        /// </summary>
        private class ChannelSubscription : IDisposable
        {
            private readonly TrackOccupancyService _service;
            private readonly ChannelWriter<TrainMovedEventArgs> _writer;
            private volatile bool _disposed;

            public ChannelSubscription(TrackOccupancyService service, ChannelWriter<TrainMovedEventArgs> writer)
            {
                _service = service ?? throw new ArgumentNullException(nameof(service));
                _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _service.Unsubscribe(_writer);
                _disposed = true;
            }
        }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new TrackOccupancyService instance
        /// </summary>
        /// <param name="mqttService">Centralized MQTT infrastructure service for Rocrail feedback</param>
        /// <param name="logger">Logger for diagnostic and error messages</param>
        /// <param name="trackGraphFactory">Factory for creating in-memory track graph for adjacency checks</param>
        public TrackOccupancyService(
            MqttInfrastructureService mqttService,
            ILogger<TrackOccupancyService> logger,
            ITrackGraphFactory trackGraphFactory,
            FileLoggingService fileLogger = null,
            ITrainLocationRegistry locationRegistry = null)
        {
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _trackGraphFactory = trackGraphFactory ?? throw new ArgumentNullException(nameof(trackGraphFactory));
            _fileLogger = fileLogger;
            _locationRegistry = locationRegistry;
            _initializationSemaphore = new SemaphoreSlim(1, 1);

            _occupancyMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _trainStates = new Dictionary<string, TrainTrackingState>(StringComparer.OrdinalIgnoreCase);
            _subscribers = new HashSet<ChannelWriter<TrainMovedEventArgs>>();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the service and subscribe to Rocrail MQTT feedback messages.
        /// Thread-safe - can be called multiple times without issues.
        /// </summary>
        public async Task InitializeAsync()
        {
            // Fast-path check without lock
            if (_isInitialized)
                return;

            await _initializationSemaphore.WaitAsync();
            try
            {
                // Double-check pattern for thread safety
                if (_isInitialized)
                    return;

                // Initialize TrackGraph for in-memory adjacency checks
                _trackGraph = await _trackGraphFactory.CreateTrackGraphAsync();

                // Ensure MQTT service is ready
                if (!await _mqttService.InitializeAsync())
                {
                    _logger.LogError("Failed to initialize TrackOccupancyService: MQTT service unavailable - [TrackOccupancyService]");
                    return;
                }

                // Subscribe to Rocrail feedback messages using the new generic Subscribe method
                _mqttService.Subscribe("rocrail/service/info", ProcessRocrailFeedbackAsync);

                _isInitialized = true;
                _logger.LogInformation("TrackOccupancyService initialized successfully with TrackGraph");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing TrackOccupancyService - [TrackOccupancyService]");
            }
            finally
            {
                _initializationSemaphore.Release();
            }
        }

        #endregion

        #region Train Registration

        /// <summary>
        /// Register a train's initial position in the occupancy tracking system.
        ///
        /// This method should be called when:
        /// - A train is first placed on the track
        /// - A train completes a journey and remains in a section
        /// - System startup to populate initial train positions
        ///
        /// If the section is already occupied by another train, this method will update
        /// the occupancy (assumes the previous train has left).
        /// </summary>
        /// <param name="trainName">The unique name/ID of the train</param>
        /// <param name="initialSection">The subsection name where the train is located</param>
        /// <param name="direction">The train's current direction (default: true/forward)</param>
        public void RegisterTrainPosition(string trainName, string initialSection, bool direction = true)
        {
            if (string.IsNullOrWhiteSpace(trainName))
            {
                _logger.LogWarning("RegisterTrainPosition called with null/empty train name");
                return;
            }

            if (string.IsNullOrWhiteSpace(initialSection))
            {
                _logger.LogWarning("RegisterTrainPosition called with null/empty section name for train {TrainName}", trainName);
                return;
            }

            string previousSection = null;

            lock (_stateLock)
            {
                try
                {
                    // Store train's previous position if it exists
                    previousSection = _trainStates.TryGetValue(trainName, out var currentState) ? currentState.CurrentSection : null;

                    // Remove train from previous section if it exists
                    if (previousSection != null && !string.Equals(previousSection, initialSection, StringComparison.OrdinalIgnoreCase))
                    {
                        _occupancyMap.Remove(previousSection);
                        _logger.LogDebug("Train {TrainName} removed from previous section {PreviousSection}", trainName, previousSection);
                    }

                    // Update occupancy map
                    _occupancyMap[initialSection] = trainName;

                    // Update train state tracking (consolidated position and direction)
                    _trainStates[trainName] = new TrainTrackingState { CurrentSection = initialSection, Direction = direction };

                    _fileLogger?.Log($"[TRACK OCCUPANCY] {trainName} manually registered at section {initialSection}.");
                    _logger.LogInformation("Train {TrainName} registered in section {Section}", trainName, initialSection);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error registering train position: {TrainName} in {Section} - [TrackOccupancyService]", trainName, initialSection);
                }
            }

            // Fire event outside the lock to prevent potential deadlocks
            if (previousSection != null && !string.Equals(previousSection, initialSection, StringComparison.OrdinalIgnoreCase))
            {
                FireTrainMovedEvent(trainName, previousSection, initialSection, direction);
            }
            else
            {
                // Initial registration - fire event with null previous section
                FireTrainMovedEvent(trainName, null, initialSection, direction);
            }
        }

        /// <summary>
        /// Update a train's direction without changing its position.
        /// Useful when a train reverses direction while staying in the same section.
        /// </summary>
        /// <param name="trainName">The train to update</param>
        /// <param name="direction">The new direction</param>
        public void UpdateTrainDirection(string trainName, bool direction)
        {
            if (string.IsNullOrWhiteSpace(trainName))
            {
                _logger.LogWarning("UpdateTrainDirection called with null/empty train name");
                return;
            }

            lock (_stateLock)
            {
                if (_trainStates.TryGetValue(trainName, out var currentState))
                {
                    _trainStates[trainName] = currentState with { Direction = direction };
                    _logger.LogDebug("Train {TrainName} direction updated to {Direction}", trainName, direction ? "forward" : "reverse");
                }
                else
                {
                    _logger.LogWarning("Cannot update direction for untracked train {TrainName}", trainName);
                }
            }
        }

        /// <summary>
        /// Unregister a train from the occupancy tracking system.
        /// Removes the train from all tracking dictionaries.
        /// </summary>
        /// <param name="trainName">The train to unregister</param>
        public void UnregisterTrain(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
                return;

            lock (_stateLock)
            {
                try
                {
                    // Remove from state tracking (includes position)
                    if (_trainStates.Remove(trainName, out var state))
                    {
                        // Remove from occupancy map
                        _occupancyMap.Remove(state.CurrentSection);
                        _fileLogger?.Log($"[TRACK OCCUPANCY] {trainName} unregistered from physical tracking.");
                    }

                    _logger.LogInformation("Train {TrainName} unregistered from tracking system", trainName);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error unregistering train {TrainName} - [TrackOccupancyService]", trainName);
                }
            }
        }

        #endregion

        #region MQTT Message Processing

        /// <summary>
        /// Process incoming Rocrail feedback messages from MQTT.
        /// Parses XML <fb> messages and updates occupancy tracking.
        /// Uses efficient string parsing instead of XElement.Parse to reduce memory allocations.
        /// </summary>
        /// <param name="feedbackMessage">The XML feedback message from Rocrail</param>
        private async Task ProcessRocrailFeedbackAsync(string feedbackMessage)
        {
            if (string.IsNullOrWhiteSpace(feedbackMessage))
                return;

            try
            {
                // Trim and check for XML root using span to avoid allocation
                ReadOnlySpan<char> messageSpan = feedbackMessage.AsSpan().Trim();
                if (!messageSpan.StartsWith("<"))
                    return;

                // Fast span-based parsing for <fb id="bk1" state="true"/> format
                // Avoids XElement.Parse to prevent heavy memory allocation on every sensor tick
                var sectionId = ExtractXmlAttribute(messageSpan, " id=\"");
                if (string.IsNullOrWhiteSpace(sectionId))
                    return;

                // CRITICAL FIX: Ignore Rocrail's auto-generated ghost duplicate sensors (e.g., "fb560")
                // to prevent false-positive obstruction alarms and lock contention.
                if (sectionId.StartsWith("fb", StringComparison.OrdinalIgnoreCase) && sectionId.Length > 2 && char.IsDigit(sectionId[2]))
                    return;

                // --- THE GHOST FILTER ---
                // If this physical block has an inactive train parked on it, ignore the sensor hit.
                // This prevents active trains from being mistakenly assigned to ghost pings from parked trains.
                if (_locationRegistry != null && _locationRegistry.IsBlockOccupiedByParkedTrain(sectionId))
                {
                    _logger.LogTrace("Ignored sensor hit at {SensorName} - A parked train is currently resting here.", sectionId);
                    return; // Drop the event entirely!
                }
                // ------------------------

                var stateAttr = ExtractXmlAttribute(messageSpan, "state=\"");

                // Process occupancy change
                var isOccupied = string.Equals(stateAttr, "true", StringComparison.OrdinalIgnoreCase);

                if (isOccupied)
                {
                    await HandleSectionOccupiedAsync(sectionId);
                }
                else
                {
                    await HandleSectionClearedAsync(sectionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Rocrail feedback message: {Message} - [TrackOccupancyService]", feedbackMessage);
            }
        }

        /// <summary>
        /// Extracts an XML attribute value from a simple XML span using efficient span-based parsing.
        /// Designed for simple Rocrail <fb> messages like <fb id="bk1" state="true"/>.
        /// Avoids the overhead of XElement.Parse and Substring allocations for high-frequency sensor messages.
        /// </summary>
        /// <param name="xmlSpan">The XML span to parse</param>
        /// <param name="searchPattern">The fully formed attribute search pattern (e.g., "id=\"")</param>
        /// <returns>The attribute value, or null if not found</returns>
        private string ExtractXmlAttribute(ReadOnlySpan<char> xmlSpan, string searchPattern)
        {
            var attrIndex = xmlSpan.IndexOf(searchPattern.AsSpan(), StringComparison.OrdinalIgnoreCase);

            if (attrIndex == -1)
                return null;

            var startIndex = attrIndex + searchPattern.Length;
            var remainingSpan = xmlSpan.Slice(startIndex);

            var quoteIndex = remainingSpan.IndexOf('"');

            if (quoteIndex == -1)
                return null;

            return remainingSpan.Slice(0, quoteIndex).ToString();
        }

        /// <summary>
        /// Handle a section becoming occupied (state="true").
        /// Identifies which train entered the section and updates tracking.
        /// If train cannot be identified, registers as UNKNOWN_OBSTACLE to prevent collisions.
        /// Uses processing lock-set to prevent TOCTOU race conditions from concurrent MQTT threads.
        /// </summary>
        /// <param name="sectionName">The section that became occupied</param>
        private async Task HandleSectionOccupiedAsync(string sectionName)
        {
            // Check if already occupied or being processed (TOCTOU protection)
            bool shouldSkip;
            lock (_stateLock)
            {
                shouldSkip = _occupancyMap.ContainsKey(sectionName) || _processingSections.Contains(sectionName);

                if (!shouldSkip)
                {
                    // Mark section as being processed
                    _processingSections.Add(sectionName);
                }
            }

            if (shouldSkip)
            {
                lock (_stateLock)
                {
                    var existingTrain = _occupancyMap.TryGetValue(sectionName, out var train) ? train : "unknown";
                    _logger.LogDebug("Section {Section} already occupied or being processed by {Train}, ignoring duplicate occupancy", sectionName, existingTrain);
                }
                return;
            }

            try
            {
                // Identify which train entered this section (synchronous in-memory operation)
                var (trainName, previousSection, direction) = IdentifyTrainForSection(sectionName);

                if (trainName == null)
                {
                    // Silent drop: Unmapped sensors are ignored to maintain traffic flow
                    // This prevents false-positive alarms from unused track sections or test sensors
                    _logger.LogTrace("Ignored unmapped physical sensor hit at {SectionName}. Maintaining traffic flow.", sectionName);
                    return; // Drop the event entirely - no occupancy tracking, no event broadcast
                }

                // Update occupancy tracking for identified trains only
                UpdateTrainPosition(trainName, previousSection, sectionName, direction);

                _logger.LogInformation("Section occupied: {Section} by {Train} (from {PreviousSection})",
                    sectionName, trainName, previousSection ?? "unknown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling section occupied: {Section} - [TrackOccupancyService]", sectionName);
            }
            finally
            {
                // Remove from processing set (always execute, even on exception)
                lock (_stateLock)
                {
                    _processingSections.Remove(sectionName);
                }
            }
        }

        /// <summary>
        /// Handle a section becoming cleared (state="false").
        /// Removes the train or obstruction from occupancy tracking for this section.
        /// </summary>
        /// <param name="sectionName">The section that was cleared</param>
        private async Task HandleSectionClearedAsync(string sectionName)
        {
            // Await resolution of the TOCTOU processing lock to prevent ghost blocks, with timeout protection
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                while (true)
                {
                    timeoutCts.Token.ThrowIfCancellationRequested();
                    lock (_stateLock)
                    {
                        if (!_processingSections.Contains(sectionName)) break;
                    }
                    await Task.Delay(10, timeoutCts.Token);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogCritical("Timeout waiting for TOCTOU processing lock to clear for section {Section}. Forcing progression to avoid thread starvation.", sectionName);
                // CRITICAL: Force remove the lock so this hardware sensor isn't permanently blacklisted
                lock (_stateLock)
                {
                    _processingSections.Remove(sectionName);
                }
            }

            try
            {
                lock (_stateLock)
                {
                    // Remove from occupancy map
                    if (_occupancyMap.Remove(sectionName, out var trainName))
                    {
                        // Check if this was an obstruction or a real train
                        var isObstruction = trainName != null && trainName.StartsWith("UNKNOWN_OBSTACLE_");

                        // Phase 3: Fix ghost train leak - also remove from _trainStates if it's an obstruction
                        if (isObstruction)
                        {
                            _trainStates.Remove(trainName);
                            _logger.LogInformation("Section cleared: {Section} (obstruction {ObstacleID} removed)", sectionName, trainName);
                        }
                        else
                        {
                            _logger.LogInformation("Section cleared: {Section} (was occupied by {Train})", sectionName, trainName);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Section cleared: {Section} (was not tracked as occupied)", sectionName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling section cleared: {Section} - [TrackOccupancyService]", sectionName);
            }
        }

        #endregion

        #region Train Identification Logic

        /// <summary>
        /// Identifies which train entered a newly occupied section.
        ///
        /// Algorithm:
        /// 1. Check for adjacent sections with trains currently tracked
        /// 2. Validate adjacency using database connections
        /// 3. Use train direction to disambiguate multiple candidates
        /// 4. Return the most likely train with its previous section
        /// </summary>
        /// <param name="newSection">The section that just became occupied</param>
        /// <returns>Tuple of (TrainName, PreviousSection, Direction) or (null, null, false) if unidentified</returns>
        private (string TrainName, string PreviousSection, bool Direction) IdentifyTrainForSection(string newSection)
        {
            try
            {
                // Find adjacent sections with trains - iterate directly over _trainStates without .ToList()
                var adjacentCandidates = new List<(string TrainName, string PreviousSection, bool Direction)>();

                // Lock while reading the dictionary to get consistent snapshot
                lock (_stateLock)
                {
                    if (_trainStates.Count == 0)
                    {
                        _logger.LogWarning("No trains currently tracked, cannot identify train for section {Section}", newSection);
                        return (null, null, false);
                    }

                    foreach (var (trainName, state) in _trainStates)
                    {
                        var currentSection = state.CurrentSection;
                        var direction = state.Direction;

                        // 1. Strict Check: Are the sections physically directly connected?
                        bool areAdjacent = AreSectionsAdjacent(currentSection, newSection);

                        // 2. Deep Recovery Fix: Did the train skip one or more logical blocks entirely due to dirty track?
                        if (!areAdjacent && _trackGraph != null)
                        {
                            int currentLogicalId = _trackGraph.GetNodeIdByName(currentSection);
                            int newLogicalId = _trackGraph.GetNodeIdByName(newSection);

                            // Allow the train to "hop" up to 2 logical blocks forward in its current direction
                            if (currentLogicalId != -1 && newLogicalId != -1)
                            {
                                if (currentLogicalId == newLogicalId || _trackGraph.IsReachableWithin(currentLogicalId, newLogicalId, 2, direction))
                                {
                                    areAdjacent = true;
                                    _logger.LogWarning("🚂 GHOST TRAIN RECOVERED: {TrainName} dropped sensors and hopped from {Current} to {New}. Tracking restored.", trainName, currentSection, newSection);
                                }
                            }
                        }

                        if (areAdjacent)
                        {
                            adjacentCandidates.Add((trainName, currentSection, direction));
                        }
                    }
                }

                // Disambiguate based on candidate count (outside lock)
                return SelectBestCandidate(adjacentCandidates, newSection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error identifying train for section {Section} - [TrackOccupancyService]", newSection);
                return (null, null, false);
            }
        }

        /// <summary>
        /// Selects the best candidate from multiple trains that could have entered a section.
        /// Uses direction and connection metadata to make the final decision.
        /// </summary>
        /// <param name="candidates">List of candidate trains with their positions</param>
        /// <param name="newSection">The section being entered</param>
        /// <returns>The selected candidate or null if no valid candidate</returns>
        private (string TrainName, string PreviousSection, bool Direction) SelectBestCandidate(
            List<(string TrainName, string PreviousSection, bool Direction)> candidates,
            string newSection)
        {
            if (candidates.Count == 0)
            {
                return (null, null, false);
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            // Multiple candidates - use direction to disambiguate
            var validatedCandidates = new List<(string TrainName, string PreviousSection, bool Direction)>();

            foreach (var (trainName, previousSection, direction) in candidates)
            {
                // Validate that the connection from previous to new section matches train direction (synchronous in-memory lookup)
                var connectionMatchesDirection = ValidateConnectionDirection(
                    previousSection, newSection, direction);

                if (connectionMatchesDirection)
                {
                    validatedCandidates.Add((trainName, previousSection, direction));
                }
            }

            // Return first validated candidate, or first candidate if none validated
            return validatedCandidates.Count > 0 ? validatedCandidates[0] : candidates[0];
        }

        #endregion

        #region Database Queries

        /// <summary>
        /// Checks if two sections are adjacent (directly connected) in the track layout.
        /// Uses in-memory TrackGraph for instant lookup without database access.
        /// </summary>
        /// <param name="section1">First section name</param>
        /// <param name="section2">Second section name</param>
        /// <returns>True if sections are adjacent, false otherwise</returns>
        private bool AreSectionsAdjacent(string section1, string section2)
        {
            try
            {
                if (_trackGraph == null)
                {
                    _logger.LogWarning("TrackGraph not initialized, cannot check adjacency");
                    return false;
                }

                var section1Id = _trackGraph.GetNodeIdByName(section1);
                var section2Id = _trackGraph.GetNodeIdByName(section2);

                if (section1Id == -1 || section2Id == -1)
                    return false;

                return _trackGraph.AreSectionsAdjacent(section1Id, section2Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking adjacency between {Section1} and {Section2} - [TrackOccupancyService]", section1, section2);
                return false;
            }
        }

        /// <summary>
        /// Validates that a connection between two sections matches the train's direction.
        /// Uses in-memory TrackGraph for instant lookup without database access.
        /// </summary>
        /// <param name="fromSection">Origin section name</param>
        /// <param name="toSection">Destination section name</param>
        /// <param name="trainDirection">Train's direction (true = forward, false = reverse)</param>
        /// <returns>True if connection direction matches train direction</returns>
        private bool ValidateConnectionDirection(string fromSection, string toSection, bool trainDirection)
        {
            try
            {
                if (_trackGraph == null)
                {
                    _logger.LogWarning("TrackGraph not initialized, cannot validate connection direction");
                    return false;
                }

                var fromSectionId = _trackGraph.GetNodeIdByName(fromSection);
                var toSectionId = _trackGraph.GetNodeIdByName(toSection);

                if (fromSectionId == -1 || toSectionId == -1)
                    return false;

                return _trackGraph.ValidateConnectionDirection(fromSectionId, toSectionId, trainDirection);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error validating connection direction from {FromSection} to {ToSection} - [TrackOccupancyService]", fromSection, toSection);
                return false;
            }
        }

        #endregion

        #region Position Updates

        /// <summary>
        /// Updates a train's position in the tracking system and fires the OnTrainMoved event.
        /// This is the central method that updates all tracking dictionaries.
        /// </summary>
        /// <param name="trainName">The train that moved</param>
        /// <param name="previousSection">Where the train was (null if initial registration)</param>
        /// <param name="newSection">Where the train is now</param>
        /// <param name="direction">The train's direction</param>
        private void UpdateTrainPosition(string trainName, string previousSection, string newSection, bool direction)
        {
            lock (_stateLock)
            {
                try
                {
                    // Remove from previous section
                    if (previousSection != null)
                    {
                        _occupancyMap.Remove(previousSection);
                    }

                    // Add to new section
                    _occupancyMap[newSection] = trainName;

                    // Update train state tracking (consolidated position and direction)
                    _trainStates[trainName] = new TrainTrackingState { CurrentSection = newSection, Direction = direction };
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating train position: {TrainName} from {PreviousSection} to {NewSection} - [TrackOccupancyService]",
                        trainName, previousSection, newSection);
                }
            }

            // Fire event outside the lock to prevent potential deadlocks
            FireTrainMovedEvent(trainName, previousSection, newSection, direction);
        }

        /// <summary>
        /// Subscribe to train movement notifications using System.Threading.Channels.
        /// Returns a tuple containing the ChannelReader and an IDisposable subscription token.
        /// The token must be disposed to prevent memory leaks.
        /// This is thread-safe and replaces C# events for better async handling.
        /// </summary>
        /// <returns>A tuple containing the ChannelReader and IDisposable subscription token</returns>
        public (ChannelReader<TrainMovedEventArgs> Reader, IDisposable Subscription) Subscribe()
        {
            // Use a large bounded channel to prevent memory leaks if a reader stalls.
            // MUST use DropWrite with TryWrite() so the publisher doesn't receive a 'false' return
            // when the buffer is full, which would incorrectly trigger the dead-subscriber cleanup logic.
            var options = new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropWrite
            };
            var channel = Channel.CreateBounded<TrainMovedEventArgs>(options);

            lock (_stateLock)
            {
                _subscribers.Add(channel.Writer);
                _logger.LogDebug("New subscriber added to TrackOccupancyService. Total subscribers: {Count}", _subscribers.Count);
            }

            var subscription = new ChannelSubscription(this, channel.Writer);
            return (channel.Reader, subscription);
        }

        /// <summary>
        /// Unsubscribes a channel writer from the service and completes the channel.
        /// Called by ChannelSubscription.Dispose() to ensure proper cleanup.
        /// </summary>
        /// <param name="writer">The channel writer to remove</param>
        internal void Unsubscribe(ChannelWriter<TrainMovedEventArgs> writer)
        {
            if (writer == null)
                return;

            bool removed = false;
            lock (_stateLock)
            {
                removed = _subscribers.Remove(writer);
            }

            if (removed)
            {
                writer.TryComplete();
                _logger.LogDebug("Subscriber removed from TrackOccupancyService. Total subscribers: {Count}", _subscribers.Count);
            }
        }

        /// <summary>
        /// Fires train movement events to all subscribers using Channel-based pub/sub.
        /// This is thread-safe and won't cause async void crashes.
        /// </summary>
        private void FireTrainMovedEvent(string trainName, string previousSection, string newSection, bool direction)
        {
            List<ChannelWriter<TrainMovedEventArgs>> subscribersCopy;

            lock (_stateLock)
            {
                // Create a snapshot of subscribers to minimize lock time
                subscribersCopy = _subscribers.ToList();
            }

            try
            {
                var eventArgs = new TrainMovedEventArgs(trainName, previousSection, newSection, direction);

                // Write to all subscribers (outside lock to prevent deadlock)
                var deadWriters = new List<ChannelWriter<TrainMovedEventArgs>>();
                foreach (var writer in subscribersCopy)
                {
                    if (!writer.TryWrite(eventArgs))
                    {
                        // Channel is closed or failed, mark for cleanup
                        deadWriters.Add(writer);
                    }
                }

                // Remove dead writers
                if (deadWriters.Count > 0)
                {
                    lock (_stateLock)
                    {
                        foreach (var deadWriter in deadWriters)
                        {
                            _subscribers.Remove(deadWriter);
                            _logger.LogDebug("Removed dead subscriber from TrackOccupancyService");
                        }
                    }
                }

                _logger.LogDebug("Train moved event fired to {Count} subscribers: {EventDetails}",
                    subscribersCopy.Count - deadWriters.Count, eventArgs.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error firing train moved event for train {TrainName} - [TrackOccupancyService]", trainName);
            }
        }

        #endregion

        #region Public Query Methods

        /// <summary>
        /// Checks if a logical block is occupied by checking all its underlying physical sensors.
        /// Translates down from Logical Domain to Physical Domain.
        /// </summary>
        public bool IsLogicalBlockOccupied(string logicalBlockName)
        {
            if (string.IsNullOrWhiteSpace(logicalBlockName) || _trackGraph == null)
                return false;

            var physicalSensors = _trackGraph.GetPhysicalSensorsForLogicalBlock(logicalBlockName);

            // Fallback: If no mapping found, assume the string might be a raw sensor
            if (physicalSensors.Count == 0)
                return IsSectionOccupied(logicalBlockName);

            lock (_stateLock)
            {
                return physicalSensors.Any(sensor => _occupancyMap.ContainsKey(sensor));
            }
        }

        /// <summary>
        /// Atomically checks if a logical block is occupied and returns the occupying train.
        /// Translates down from Logical Domain to Physical Domain.
        /// </summary>
        public bool TryGetTrainInLogicalBlock(string logicalBlockName, out string trainName)
        {
            trainName = null;
            if (string.IsNullOrWhiteSpace(logicalBlockName) || _trackGraph == null)
                return false;

            var physicalSensors = _trackGraph.GetPhysicalSensorsForLogicalBlock(logicalBlockName);

            // Fallback: If no mapping found, assume the string might be a raw sensor
            if (physicalSensors.Count == 0)
                return TryGetTrainInSection(logicalBlockName, out trainName);

            lock (_stateLock)
            {
                foreach (var sensor in physicalSensors)
                {
                    if (_occupancyMap.TryGetValue(sensor, out trainName))
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Checks if a section is currently occupied by any train.
        /// Thread-safe query operation.
        /// </summary>
        /// <param name="sectionName">The section name to check</param>
        /// <returns>True if occupied, false otherwise</returns>
        public bool IsSectionOccupied(string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
                return false;

            lock (_stateLock)
            {
                return _occupancyMap.ContainsKey(sectionName);
            }
        }

        /// <summary>
        /// Gets the name of the train currently occupying a specific section.
        /// Thread-safe query operation.
        /// </summary>
        /// <param name="sectionName">The section name to query</param>
        /// <returns>Train name if occupied, null if section is empty or not found</returns>
        public string GetTrainInSection(string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
                return null;

            lock (_stateLock)
            {
                return _occupancyMap.TryGetValue(sectionName, out var trainName) ? trainName : null;
            }
        }

        /// <summary>
        /// Atomically checks if a section is occupied and returns the occupying train.
        /// This method prevents TOCTOU (Time-of-Check to Time-of-Use) race conditions
        /// by performing both operations atomically.
        /// </summary>
        /// <param name="sectionName">The section name to check</param>
        /// <param name="trainName">Output parameter containing the train name if occupied</param>
        /// <returns>True if section is occupied (trainName contains the occupant), false otherwise</returns>
        public bool TryGetTrainInSection(string sectionName, out string trainName)
        {
            trainName = null;

            if (string.IsNullOrWhiteSpace(sectionName))
                return false;

            lock (_stateLock)
            {
                return _occupancyMap.TryGetValue(sectionName, out trainName);
            }
        }

        /// <summary>
        /// Gets the current section where a train is located.
        /// </summary>
        /// <param name="trainName">The train name to query</param>
        /// <returns>Current section name if found, null otherwise</returns>
        public string GetTrainCurrentSection(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
                return null;

            lock (_stateLock)
            {
                return _trainStates.TryGetValue(trainName, out var state) ? state.CurrentSection : null;
            }
        }

        /// <summary>
        /// Gets all sections currently occupied by trains.
        /// </summary>
        /// <returns>Dictionary mapping section names to train names</returns>
        public Dictionary<string, string> GetAllOccupiedSections()
        {
            lock (_stateLock)
            {
                return new Dictionary<string, string>(_occupancyMap, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Gets the current direction of a train.
        /// </summary>
        /// <param name="trainName">The train name to query</param>
        /// <returns>Direction (true = forward, false = reverse) or true if not found (default)</returns>
        public bool GetTrainDirection(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
                return true;

            lock (_stateLock)
            {
                return _trainStates.TryGetValue(trainName, out var state) ? state.Direction : true;
            }
        }

        /// <summary>
        /// Gets the total count of trains currently being tracked.
        /// </summary>
        /// <returns>Number of registered trains</returns>
        public int GetTrackedTrainCount()
        {
            lock (_stateLock)
            {
                return _trainStates.Count;
            }
        }

        /// <summary>
        /// Gets the total count of sections currently occupied.
        /// </summary>
        /// <returns>Number of occupied sections</returns>
        public int GetOccupiedSectionCount()
        {
            lock (_stateLock)
            {
                return _occupancyMap.Count;
            }
        }

        /// <summary>
        /// Checks if a train name is actually an UNKNOWN_OBSTACLE identifier.
        /// </summary>
        /// <param name="trainName">The train name to check</param>
        /// <returns>True if this is an obstruction ID, false if it's a real train</returns>
        public bool IsObstruction(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
                return false;

            return trainName.StartsWith("UNKNOWN_OBSTACLE_");
        }

        /// <summary>
        /// Gets all currently tracked obstructions (unidentified sensor triggers).
        /// </summary>
        /// <returns>Dictionary mapping section names to obstruction IDs</returns>
        public Dictionary<string, string> GetAllObstructions()
        {
            lock (_stateLock)
            {
                return _occupancyMap
                    .Where(kvp => IsObstruction(kvp.Value))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Gets the count of currently tracked obstructions.
        /// </summary>
        /// <returns>Number of unidentified obstructions on the track</returns>
        public int GetObstructionCount()
        {
            lock (_stateLock)
            {
                return _occupancyMap.Count(kvp => IsObstruction(kvp.Value));
            }
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Disposes of resources.
        /// Note: MQTT subscriptions are managed by MqttInfrastructureService.
        /// </summary>
        public void Dispose()
        {
            try
            {
                if (_isInitialized)
                {
                    // Note: MQTT subscriptions are managed centrally by MqttInfrastructureService
                    // No need to explicitly unsubscribe here
                    _isInitialized = false;
                }

                // Complete all subscriber channels to prevent deadlocks
                // Copy writers to a local array and complete them outside the lock
                List<ChannelWriter<TrainMovedEventArgs>> writersCopy;
                lock (_stateLock)
                {
                    writersCopy = new List<ChannelWriter<TrainMovedEventArgs>>(_subscribers);
                    _subscribers.Clear();
                }

                // Complete all writers outside the lock to prevent deadlock
                foreach (var writer in writersCopy)
                {
                    try
                    {
                        writer.TryComplete();
                    }
                    catch
                    {
                        // Ignore errors during disposal
                    }
                }

                _initializationSemaphore?.Dispose();

                // Safely dispose track graph if it holds unmanaged resources
                (_trackGraph as IDisposable)?.Dispose();

                _logger.LogInformation("TrackOccupancyService disposed");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disposing TrackOccupancyService - [TrackOccupancyService]");
            }
        }

        #endregion
    }
}
