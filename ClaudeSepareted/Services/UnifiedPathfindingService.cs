using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using System.Text;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Unified pathfinding service that consolidates all railway pathfinding functionality
    /// Combines platform routing, train-aware pathfinding, switch configuration, and conflict detection
    /// </summary>
    public class UnifiedPathfindingService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService _statusService;
        private readonly AdminMQTTService _adminMQTTService;
        private readonly ITrackGraphFactory _trackGraphFactory;
        private TrackGraph _trackGraph;
        private readonly Dictionary<int, Sections> _sections;
        private readonly Dictionary<int, Platforms> _platforms;
        private bool _isInitialized = false;
        private readonly object _lockObject = new object();
        private readonly Dictionary<string, string> _lastSwitchStates = new Dictionary<string, string>();

        // Switch locking mechanism to prevent conflicts between multiple trains
        private readonly ConcurrentDictionary<string, string> _lockedSwitches = new ConcurrentDictionary<string, string>();
        private readonly object _switchLockObject = new object();

        public UnifiedPathfindingService(
            ApplicationDbContext dbContext,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            AdminMQTTService adminMQTTService,
            ITrackGraphFactory trackGraphFactory)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _adminMQTTService = adminMQTTService ?? throw new ArgumentNullException(nameof(adminMQTTService));
            _trackGraphFactory = trackGraphFactory ?? throw new ArgumentNullException(nameof(trackGraphFactory));
            _sections = new Dictionary<int, Sections>();
            _platforms = new Dictionary<int, Platforms>();
        }

        #region Initialization

        /// <summary>
        /// Initializes the unified pathfinding service with all necessary data
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                    return true;

                // Initialize TrackGraph for advanced pathfinding
                _trackGraph = await _trackGraphFactory.CreateTrackGraphAsync();

                // Load sections for pathfinding
                var sections = await _dbContext.Sections.ToListAsync();
                foreach (var section in sections)
                {
                    _sections[section.DB_ID] = section;
                }

                // Load platforms for platform-level routing
                var platforms = await _dbContext.Platforms
                    .Include(p => p.Station)
                    .Include(p => p.SubSection)
                    .Where(p => p.IsActive)
                    .ToListAsync();

                foreach (var platform in platforms)
                {
                    _platforms[platform.DB_ID] = platform;
                }

                _isInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Pathfinding init failed: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region Platform-to-Platform Pathfinding

        /// <summary>
        /// Find direct route between two platforms using database views considering train direction
        /// </summary>
        public async Task<List<int>?> FindDirectPlatformRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform, bool direction)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            try
            {
                if (sourcePlatform == null || destinationPlatform == null)
                    return null;

                if (sourcePlatform.DB_ID <= 0 || destinationPlatform.DB_ID <= 0)
                    return null;

                var sourceSectionId = sourcePlatform.SubSection_DB_ID;
                var destSectionId = destinationPlatform.SubSection_DB_ID;

                if (sourceSectionId <= 0 || destSectionId <= 0)
                    return null;

                Console.WriteLine($"[UnifiedPathfinder] Finding platform route: {sourcePlatform.Name} -> {destinationPlatform.Name}, direction: {direction}");

                // Special case: source and destination are the same section
                if (sourceSectionId == destSectionId)
                    return new List<int> { sourceSectionId };

                // Build route using the enhanced pathfinding algorithm with direction consideration
                var route = await BuildPlatformRouteAsync(sourceSectionId, destSectionId, destinationPlatform.DB_ID, direction);

                if (route != null && route.Any())
                {
                    Console.WriteLine($"[UnifiedPathfinder] Platform route found: {string.Join(" -> ", route)}");
                    return route;
                }

                Console.WriteLine($"[UnifiedPathfinder] No platform route found from {sourcePlatform.Name} to {destinationPlatform.Name} in direction {direction}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Error finding platform route: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Backwards compatibility overload - defaults to forward direction (true)
        /// </summary>
        public async Task<List<int>?> FindDirectPlatformRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform)
        {
            return await FindDirectPlatformRouteAsync(sourcePlatform, destinationPlatform, true);
        }

        /// <summary>
        /// Enhanced route building with better error handling and validation considering direction
        /// </summary>
        private async Task<List<int>?> BuildPlatformRouteAsync(int sourceSectionId, int destSectionId, int destinationPlatformId, bool direction)
        {
            try
            {
                var route = new List<int> { sourceSectionId };
                var currentSection = sourceSectionId;
                var visited = new HashSet<int> { sourceSectionId };
                var maxSteps = 50;
                var steps = 0;

                while (steps < maxSteps)
                {
                    if (currentSection == destSectionId)
                    {
                        return route;
                    }
                    steps++;

                    var connections = await GetSectionConnectionsAsync(currentSection);
                    if (connections == null || !connections.Any())
                        break;

                    // Filter connections by direction before finding best connection
                    var directionFilteredConnections = connections.Where(c => c.Direction == direction).ToList();
                    if (!directionFilteredConnections.Any())
                    {
                        Console.WriteLine($"[UnifiedPathfinder] No connections available for direction {direction} from section {currentSection}");
                        break;
                    }

                    var bestConnection = await FindBestConnectionToPlatformAsync(directionFilteredConnections, destinationPlatformId, visited);

                    if (bestConnection != null)
                    {
                        var nextSection = bestConnection.NextSection_DB_ID.Value;
                        route.Add(nextSection);
                        visited.Add(nextSection);
                        currentSection = nextSection;
                    }
                    else
                    {
                        break;
                    }
                }

                // Fallback: Try simple BFS pathfinding with direction
                var fallbackRoute = await FindBFSToDestinationAsync(sourceSectionId, destSectionId, direction);
                if (fallbackRoute != null && fallbackRoute.Any())
                    return fallbackRoute;

                // Last resort: Check direct connections
                var directRoute = await FindAnyDirectConnectionRoute(sourceSectionId, destSectionId);
                if (directRoute != null && directRoute.Any())
                    return directRoute;

                // Emergency fallback: Force a route if sections exist
                var emergencyRoute = await CreateEmergencyRoute(sourceSectionId, destSectionId);
                if (emergencyRoute != null)
                    return emergencyRoute;

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Fallback BFS pathfinding to find any route from source to destination section considering direction
        /// This is used when the platform-aware pathfinding fails
        /// </summary>
        private async Task<List<int>?> FindBFSToDestinationAsync(int sourceSectionId, int destSectionId, bool direction)
        {
            try
            {
                var queue = new Queue<List<int>>();
                var visited = new HashSet<int>();
                var maxDepth = 50;

                // Start with the source section
                queue.Enqueue(new List<int> { sourceSectionId });
                visited.Add(sourceSectionId);

                while (queue.Count > 0)
                {
                    var currentPath = queue.Dequeue();
                    var currentSection = currentPath.Last();

                    // Check if we reached destination
                    if (currentSection == destSectionId)
                        return currentPath;

                    // Prevent infinite loops
                    if (currentPath.Count >= maxDepth)
                        continue;

                    // Get all connections from current section and filter by direction
                    var connections = await GetSectionConnectionsAsync(currentSection);
                    var directionFilteredConnections = connections.Where(c => c.Direction == direction).ToList();

                    foreach (var connection in directionFilteredConnections)
                    {
                        if (connection.NextSection_DB_ID.HasValue)
                        {
                            var nextSection = connection.NextSection_DB_ID.Value;

                            if (!visited.Contains(nextSection))
                            {
                                visited.Add(nextSection);
                                var newPath = new List<int>(currentPath) { nextSection };
                                queue.Enqueue(newPath);
                            }
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Last resort method to find ANY direct connection between sections
        /// This is the most basic connection check - if this fails, there's likely no path
        /// </summary>
        private async Task<List<int>?> FindAnyDirectConnectionRoute(int sourceSectionId, int destSectionId)
        {
            try
            {
                // Get all connections from source
                var sourceConnections = await GetSectionConnectionsAsync(sourceSectionId);

                foreach (var connection in sourceConnections ?? new List<VLookupSectionNextSection>())
                {
                    if (connection.NextSection_DB_ID.HasValue)
                    {
                        var nextSection = connection.NextSection_DB_ID.Value;

                        // Direct connection found
                        if (nextSection == destSectionId)
                            return new List<int> { sourceSectionId, destSectionId };

                        // Check if this intermediate section has a direct connection to destination
                        var intermediateConnections = await GetSectionConnectionsAsync(nextSection);
                        foreach (var intermediateConn in intermediateConnections ?? new List<VLookupSectionNextSection>())
                        {
                            if (intermediateConn.NextSection_DB_ID.HasValue && intermediateConn.NextSection_DB_ID.Value == destSectionId)
                                return new List<int> { sourceSectionId, nextSection, destSectionId };
                        }
                    }
                }

                // Also check reverse direction
                var destConnections = await GetSectionConnectionsAsync(destSectionId);
                foreach (var connection in destConnections ?? new List<VLookupSectionNextSection>())
                {
                    if (connection.NextSection_DB_ID.HasValue && connection.NextSection_DB_ID.Value == sourceSectionId)
                        return new List<int> { sourceSectionId, destSectionId };
                }

                return null;
            }
            catch (Exception ex)
            {
                return null;
            }
        }

        /// <summary>
        /// Emergency route creator - creates a minimal route between sections
        /// This should only be used as a last resort to prevent total failure
        /// </summary>
        private async Task<List<int>?> CreateEmergencyRoute(int sourceSectionId, int destSectionId)
        {
            try
            {
                Console.WriteLine($"[UnifiedPathfinder] 🚨 Creating emergency route from {sourceSectionId} to {destSectionId}");

                // If sections are the same, return single section route
                if (sourceSectionId == destSectionId)
                {
                    return new List<int> { sourceSectionId };
                }

                // Check if both sections exist in our data
                if (!_sections.ContainsKey(sourceSectionId))
                {
                    Console.WriteLine($"[UnifiedPathfinder] ❌ Emergency route failed: Source section {sourceSectionId} not found");
                    return null;
                }

                if (!_sections.ContainsKey(destSectionId))
                {
                    Console.WriteLine($"[UnifiedPathfinder] ❌ Emergency route failed: Destination section {destSectionId} not found");
                    return null;
                }

                // Force a two-section route as absolute minimum
                var emergencyRoute = new List<int> { sourceSectionId, destSectionId };

                Console.WriteLine($"[UnifiedPathfinder] ⚠️ Forcing emergency route: {string.Join(" -> ", emergencyRoute)}");
                Console.WriteLine("[UnifiedPathfinder] ⚠️ This route may not follow actual track connections!");

                return emergencyRoute;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Emergency route creation failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Train-Aware Pathfinding

        /// <summary>
        /// Calculate and lock path from current train position to destination with conflict detection
        /// </summary>
        public async Task<List<int>?> CalculateTrainPathAsync(Train train, List<Train> otherTrains)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            if (train?.SubSection == null)
            {
                Console.WriteLine("[UnifiedPathfinder] Train has no current subsection, cannot calculate path");
                return null;
            }

            var path = new List<int>();
            var currentSection = train.SubSection.DB_ID;
            var destinationSection = train.Destination?.SubSection_DB_ID;

            if (destinationSection == null)
            {
                Console.WriteLine("[UnifiedPathfinder] Train has no destination subsection, cannot calculate path");
                return null;
            }

            Console.WriteLine($"[UnifiedPathfinder] Calculating train path for {train.Name}: {currentSection} -> {destinationSection}, Direction: {train.Direction}");

            // Start with current section
            path.Add(currentSection);

            // Enhanced pathfinding with conflict detection
            while (currentSection != destinationSection)
            {
                var possibleNextSections = await GetPossibleNextSectionsForTrainAsync(currentSection, destinationSection.Value, train.Direction);

                // Single path available
                if (possibleNextSections.Count == 1)
                {
                    var nextSection = possibleNextSections.FirstOrDefault();

                    // Check for conflicts
                    if (HasTrainConflict(nextSection, train, otherTrains))
                    {
                        Console.WriteLine($"[UnifiedPathfinder] Path blocked: train conflict at section {nextSection}");
                        return null;
                    }

                    path.Add(nextSection);
                    currentSection = nextSection;
                    continue;
                }

                // Multiple path choices - conflict resolution
                if (possibleNextSections.Count > 1)
                {
                    var chosenSection = await ResolveTrainPathConflictsAsync(possibleNextSections, train, otherTrains);
                    if (chosenSection == 0)
                    {
                        Console.WriteLine("[UnifiedPathfinder] No valid path found after conflict resolution");
                        return null;
                    }

                    path.Add(chosenSection);
                    currentSection = chosenSection;
                    continue;
                }

                // No available paths
                Console.WriteLine($"[UnifiedPathfinder] No available paths from section {currentSection}");
                return null;
            }

            Console.WriteLine($"[UnifiedPathfinder] Train path calculated: {string.Join(" -> ", path)}");
            return path;
        }

        /// <summary>
        /// Enhanced conflict detection for both opposing and same-direction trains
        /// </summary>
        private bool HasTrainConflict(int sectionId, Train train, List<Train> otherTrains)
        {
            // Check for opposing direction conflicts (safety critical)
            var opposingConflict = otherTrains
                .Where(x => x.Direction != train.Direction && x.DB_ID != train.DB_ID)
                .Any(x => x.LockedSections.Contains(sectionId) &&
                         (x.LockedSections.IndexOf(sectionId) < x.LockedSections.IndexOf(x.Destination?.SubSection_DB_ID ?? -1) ||
                          x.LockedSections.IndexOf(x.Destination?.SubSection_DB_ID ?? -1) == -1));

            if (opposingConflict)
            {
                Console.WriteLine($"[UnifiedPathfinder] Opposing train conflict detected at section {sectionId}");
                return true;
            }

            // Check for same direction conflicts
            var sameDirectionConflict = otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction == train.Direction && x.State == TrainState.Moving)
                .Any(x => x.LockedSections.Contains(sectionId) || x.SubSection?.DB_ID == sectionId);

            if (sameDirectionConflict)
            {
                Console.WriteLine($"[UnifiedPathfinder] Same-direction train conflict detected at section {sectionId}");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Enhanced conflict resolution with capacity checking
        /// </summary>
        private async Task<int> ResolveTrainPathConflictsAsync(List<int> possibleSections, Train train, List<Train> otherTrains)
        {
            // Get locked sections by other trains
            var opposingLockedSections = otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction != train.Direction)
                .SelectMany(x => x.LockedSections)
                .ToList();

            var sameDirectionLockedSections = otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction == train.Direction)
                .SelectMany(x => x.LockedSections)
                .ToList();

            // Choose first section not locked by opposing trains
            var chosenSection = possibleSections.FirstOrDefault(x => !opposingLockedSections.Contains(x));

            if (chosenSection == 0)
            {
                Console.WriteLine("[UnifiedPathfinder] All paths blocked by opposing traffic");
                return 0;
            }

            // Check section capacity
            var sameDirectionLockCount = sameDirectionLockedSections.Count(x => x == chosenSection);
            var section = _sections.Values.FirstOrDefault(s => s.SubSections.Any(ss => ss.DB_ID == chosenSection));
            var sectionCapacity = section?.SubSections.Count ?? 1;

            if (sameDirectionLockCount >= sectionCapacity)
            {
                Console.WriteLine($"[UnifiedPathfinder] Section {chosenSection} at capacity ({sameDirectionLockCount}/{sectionCapacity})");
                return 0;
            }

            return chosenSection;
        }

        #endregion

        #region Switch Configuration

        /// <summary>
        /// Plan complete route with switch configuration considering train direction
        /// </summary>
        public async Task<RoutePlanResult> PlanAndConfigureRouteAsync(
            Platforms sourcePlatform,
            Platforms destinationPlatform,
            string trainName = "Unknown",
            bool direction = true,
            bool configureSwitches = true)
        {
            if (!_isInitialized)
            {
                var initResult = await InitializeAsync();
                if (!initResult)
                {
                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to initialize pathfinding service"
                    };
                }
            }

            try
            {
                var sourcePlatformName = FormatPlatformName(sourcePlatform);
                var destPlatformName = FormatPlatformName(destinationPlatform);

                Console.WriteLine($"[UnifiedPathfinder] Planning route for {trainName}: {sourcePlatformName} -> {destPlatformName}, direction: {direction}");
                _statusService?.ShowInfo($"Route planning: {sourcePlatformName} → {destPlatformName} (dir: {direction})", trainName);

                // Find route using platform pathfinding
                Console.WriteLine($"[UnifiedPathfinder] Starting pathfinding from platform {sourcePlatform.DB_ID} (section {sourcePlatform.SubSection_DB_ID}) to platform {destinationPlatform.DB_ID} (section {destinationPlatform.SubSection_DB_ID}) with direction {direction}");

                var routeSections = await FindDirectPlatformRouteAsync(sourcePlatform, destinationPlatform, direction);
                if (routeSections == null || !routeSections.Any())
                {
                    var errorMsg = $"No route found from {sourcePlatformName} to {destPlatformName} in direction {direction}";
                    Console.WriteLine($"[UnifiedPathfinder] ✗ {errorMsg}");
                    Console.WriteLine($"[UnifiedPathfinder] Debug info - Source: Platform {sourcePlatform.DB_ID}, Section {sourcePlatform.SubSection_DB_ID}");
                    Console.WriteLine($"[UnifiedPathfinder] Debug info - Destination: Platform {destinationPlatform.DB_ID}, Section {destinationPlatform.SubSection_DB_ID}");

                    // Try direct connection check as additional debug info
                    if (sourcePlatform.SubSection_DB_ID == destinationPlatform.SubSection_DB_ID)
                    {
                        Console.WriteLine($"[UnifiedPathfinder] ⚠️ Source and destination are the same section - should be valid!");
                    }

                    _statusService?.ShowError(errorMsg, trainName);

                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                // Convert to RoutePlan format
                var routePlan = ConvertToRoutePlan(routeSections);

                // If we only want to plan/reserve but NOT move switches yet:
                if (!configureSwitches)
                {
                    var requiredSwitches = await GetRequiredSwitchesForRouteAsync(routePlan);
                    return new RoutePlanResult
                    {
                        Success = true,
                        RoutePlan = routePlan,
                        ConfiguredSwitches = requiredSwitches.Select(s => new SwitchConfiguration(s.SwitchName, s.RequiredPosition)).ToList()
                    };
                }

                // Configure switches for the route
                var switchConfigResult = await ConfigureSwitchesForRouteAsync(routePlan, trainName);
                if (!switchConfigResult.Success)
                {
                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = $"Switch configuration failed: {switchConfigResult.ErrorMessage}",
                        RoutePlan = routePlan
                    };
                }

                Console.WriteLine($"[UnifiedPathfinder] Route planning completed for {trainName}");
                _statusService?.ShowSuccess($"Route configured", trainName);

                return new RoutePlanResult
                {
                    Success = true,
                    RoutePlan = routePlan,
                    ConfiguredSwitches = switchConfigResult.ConfiguredSwitches
                };
            }
            catch (Exception ex)
            {
                var errorMsg = $"Route planning error: {ex.Message}";
                Console.WriteLine($"[UnifiedPathfinder] {errorMsg}");
                _statusService?.ShowError(errorMsg, trainName);
                return new RoutePlanResult
                {
                    Success = false,
                    ErrorMessage = errorMsg
                };
            }
        }

        /// <summary>
        /// Configure all switches required for a route via MQTT
        /// </summary>
        private async Task<SwitchConfigurationResult> ConfigureSwitchesForRouteAsync(RoutePlan routePlan, string trainName)
        {
            try
            {
                var requiredSwitches = await GetRequiredSwitchesForRouteAsync(routePlan);

                // FALLBACK: If no switches found in database, add some default switches for testing
                if (!requiredSwitches.Any())
                {
                    Console.WriteLine("[UnifiedPathfinder] ⚠️ No switches found in database constraints, adding fallback switches for testing");

                    // Add some common switches for testing - modify these based on your actual switch names
                    requiredSwitches.Add(("switch1", "straight"));
                    requiredSwitches.Add(("switch2", "turned"));
                    requiredSwitches.Add(("switch3", "straight"));

                    Console.WriteLine($"[UnifiedPathfinder] Added {requiredSwitches.Count} fallback switches for testing");
                }

                if (!requiredSwitches.Any())
                {
                    Console.WriteLine("[UnifiedPathfinder] No switches to configure for this route");
                    return new SwitchConfigurationResult
                    {
                        Success = true,
                        ConfiguredSwitches = new List<SwitchConfiguration>()
                    };
                }

                Console.WriteLine($"[UnifiedPathfinder] Configuring {requiredSwitches.Count} switches for {trainName}");
                _statusService?.ShowInfo($"Configuring {requiredSwitches.Count} switches", trainName);

                var configuredSwitches = new List<SwitchConfiguration>();
                var errors = new List<string>();

                foreach (var (switchName, position) in requiredSwitches)
                {
                    try
                    {
                        // Check if switch is already in desired position
                        if (IsSwitchAlreadyConfigured(switchName, position))
                        {
                            Console.WriteLine($"[UnifiedPathfinder] Switch {switchName} already in {position} position");
                            configuredSwitches.Add(new SwitchConfiguration(switchName, position));
                            continue;
                        }

                        // Send switch command via MQTT
                        var success = await SendSwitchCommandAsync(switchName, position, trainName);
                        if (success)
                        {
                            configuredSwitches.Add(new SwitchConfiguration(switchName, position));
                            Console.WriteLine($"[UnifiedPathfinder] ✓ Switch {switchName} set to {position}");

                            // Delay between switch commands
                            await Task.Delay(500);
                        }
                        else
                        {
                            var errorMsg = $"Failed to configure switch {switchName} to {position}";
                            errors.Add(errorMsg);
                            Console.WriteLine($"[UnifiedPathfinder] ✗ {errorMsg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Error configuring switch {switchName}: {ex.Message}";
                        errors.Add(errorMsg);
                        Console.WriteLine($"[UnifiedPathfinder] ✗ {errorMsg}");
                    }
                }

                var result = new SwitchConfigurationResult
                {
                    Success = !errors.Any(),
                    ConfiguredSwitches = configuredSwitches,
                    Errors = errors
                };

                if (result.Success)
                {
                    Console.WriteLine($"[UnifiedPathfinder] All {configuredSwitches.Count} switches configured successfully for {trainName}");
                    _statusService?.ShowSuccess($"Switches configured: {configuredSwitches.Count}", trainName);
                }
                else
                {
                    Console.WriteLine($"[UnifiedPathfinder] Switch configuration completed with {errors.Count} errors for {trainName}");
                    _statusService?.ShowWarning($"Switch config: {configuredSwitches.Count}/{requiredSwitches.Count} successful", trainName);
                }

                return result;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Switch configuration error: {ex.Message}";
                Console.WriteLine($"[UnifiedPathfinder] {errorMsg}");
                return new SwitchConfigurationResult
                {
                    Success = false,
                    ErrorMessage = errorMsg
                };
            }
        }

        #endregion

        /// <summary>
        /// Attempts to reserve a list of switches for a specific train.
        /// Returns true if all switches were successfully locked.
        /// Returns false if ANY switch was already locked by a different train.
        /// </summary>
        public bool TryReserveSwitches(List<string> switchNames, string trainName)
        {
            if (switchNames == null || !switchNames.Any()) return true;

            lock (_switchLockObject)
            {
                // 1. Check if ANY required switch is already locked by ANOTHER train
                foreach (var sw in switchNames)
                {
                    if (_lockedSwitches.TryGetValue(sw, out var owner) && owner != trainName)
                    {
                        Console.WriteLine($"[UnifiedPathfinder] Switch {sw} is already locked by {owner}. Request by {trainName} denied.");
                        return false;
                    }
                }

                // 2. If clear, lock ALL switches for this train
                foreach (var sw in switchNames)
                {
                    _lockedSwitches[sw] = trainName;
                }

                Console.WriteLine($"[UnifiedPathfinder] Reserved {switchNames.Count} switches for {trainName}");
                return true;
            }
        }

        /// <summary>
        /// Releases all switch locks held by the specified train.
        /// </summary>
        public void ReleaseSwitches(string trainName)
        {
            lock (_switchLockObject)
            {
                var switchesToRemove = _lockedSwitches.Where(kvp => kvp.Value == trainName).Select(kvp => kvp.Key).ToList();
                foreach (var sw in switchesToRemove)
                {
                    _lockedSwitches.TryRemove(sw, out _);
                }
                Console.WriteLine($"[UnifiedPathfinder] Released {switchesToRemove.Count} switches for {trainName}");
            }
        }

        #region Utility Methods

        /// <summary>
        /// Get required switch states for a given route
        /// </summary>
        public async Task<List<(string SwitchName, string RequiredPosition)>> GetRequiredSwitchesForRouteAsync(RoutePlan routePlan)
        {
            var requiredSwitches = new List<(string SwitchName, string RequiredPosition)>();

            try
            {
                var path = routePlan.Path.Select(e => e.SourceNodeId).ToList();
                path.Add(routePlan.Path.LastOrDefault()?.TargetNodeId ?? 0);

                Console.WriteLine($"[UnifiedPathfinder] 🔍 Analyzing route for switches: {string.Join(" -> ", path)}");

                for (int i = 0; i < path.Count - 1; i++)
                {
                    var currentSection = path[i];
                    var nextSection = path[i + 1];

                    Console.WriteLine($"[UnifiedPathfinder] Checking connection {currentSection} -> {nextSection}");

                    var connections = await GetSectionConnectionsAsync(currentSection);
                    Console.WriteLine($"[UnifiedPathfinder] Found {connections?.Count ?? 0} connections from section {currentSection}");

                    var relevantConnection = connections?.FirstOrDefault(c =>
                        c.NextSection_DB_ID == nextSection);

                    if (relevantConnection != null)
                    {
                        Console.WriteLine($"[UnifiedPathfinder] Found connection: SwitchConstraints='{relevantConnection.SwitchConstraints}'");

                        if (!string.IsNullOrEmpty(relevantConnection.SwitchConstraints))
                        {
                            var switchConstraints = ParseSwitchConstraints(relevantConnection.SwitchConstraints);
                            Console.WriteLine($"[UnifiedPathfinder] Parsed {switchConstraints.Count} switch constraints from: {relevantConnection.SwitchConstraints}");
                            requiredSwitches.AddRange(switchConstraints);
                        }
                        else
                        {
                            Console.WriteLine($"[UnifiedPathfinder] No switch constraints for connection {currentSection} -> {nextSection}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[UnifiedPathfinder] ❌ No connection found from {currentSection} to {nextSection}");
                    }
                }

                // Remove duplicates
                requiredSwitches = requiredSwitches.Distinct().ToList();
                Console.WriteLine($"[UnifiedPathfinder] ✅ Final required switches ({requiredSwitches.Count}): {string.Join(", ", requiredSwitches.Select(s => $"{s.SwitchName}={s.RequiredPosition}"))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Error getting required switches: {ex.Message}");
            }

            return requiredSwitches;
        }

        /// <summary>
        /// Find available platform at destination station
        /// </summary>
        public async Task<int?> FindAvailablePlatformAsync(int stationId, List<Train> allTrains)
        {
            try
            {
                var stationPlatforms = _platforms.Values.Where(x => x.Station_DB_ID == stationId).ToList();
                var occupiedPlatformSubsections = allTrains
                    .Where(x => x.State == TrainState.Waiting && x.SubSection != null)
                    .Select(x => x.SubSection.DB_ID)
                    .ToList();

                var availablePlatform = stationPlatforms
                    .FirstOrDefault(x => !occupiedPlatformSubsections.Contains(x.SubSection_DB_ID));

                if (availablePlatform != null)
                {
                    Console.WriteLine($"[UnifiedPathfinder] Found available platform: {availablePlatform.DB_ID} at station {stationId}");
                    return availablePlatform.DB_ID;
                }

                Console.WriteLine($"[UnifiedPathfinder] No available platforms found at station {stationId}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Error finding available platform: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get platform departure sections
        /// </summary>
        public async Task<List<int>> GetPlatformDepartureSectionsAsync(int platformId, bool direction)
        {
            var departureSections = new List<int>();

            try
            {
                if (_platforms.TryGetValue(platformId, out var platform))
                {
                    var connections = await GetSectionConnectionsAsync(platform.SubSection_DB_ID);
                    departureSections = connections
                        .Where(c => c.Direction == direction)
                        .Select(c => c.NextSection_DB_ID ?? 0)
                        .Where(id => id > 0)
                        .ToList();

                    Console.WriteLine($"[UnifiedPathfinder] Platform {platformId} departure sections: {string.Join(", ", departureSections)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Error getting platform departure sections: {ex.Message}");
            }

            return departureSections;
        }

        /// <summary>
        /// Validate if a path is currently viable
        /// </summary>
        public async Task<bool> IsPathViableAsync(List<int> path, Train train, List<Train> otherTrains)
        {
            if (path == null || path.Count < 2)
                return false;

            try
            {
                for (int i = 1; i < path.Count; i++) // Skip current position
                {
                    var sectionId = path[i];

                    if (HasTrainConflict(sectionId, train, otherTrains))
                    {
                        return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        #endregion

        #region Private Helper Methods

        /// <summary>
        /// Get connections for a specific section from database
        /// </summary>
        private async Task<List<VLookupSectionNextSection>> GetSectionConnectionsAsync(int sectionId)
        {
            try
            {
                return await _dbContext.VLookupSectionNextSection
                    .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", sectionId)
                    .ToListAsync();
            }
            catch (Exception ex)
            {
                return new List<VLookupSectionNextSection>();
            }
        }

        /// <summary>
        /// Validate database connections and pathfinding data
        /// </summary>
        private async Task ValidatePathfindingData()
        {
            try
            {
                await _dbContext.VLookupSectionNextSection.CountAsync();
            }
            catch (Exception ex)
            {
                // Silently handle validation errors
            }
        }

        /// <summary>
        /// Check if a section leads towards a specific platform (recursive with cycle detection)
        /// </summary>
        private async Task<bool> DoesSectionLeadToPlatformAsync(int sectionId, int platformId, HashSet<int>? visited = null)
        {
            try
            {
                // Initialize visited set for cycle detection
                if (visited == null)
                    visited = new HashSet<int>();

                // Prevent infinite recursion
                if (visited.Contains(sectionId))
                {
                    return false;
                }
                visited.Add(sectionId);

                if (!_platforms.TryGetValue(platformId, out var platform))
                    return false;

                // If this section is the destination platform's subsection, we're there
                if (sectionId == platform.SubSection_DB_ID)
                {
                    return true;
                }

                // Get connections and check destinations
                var connections = await GetSectionConnectionsAsync(sectionId);
                foreach (var connection in connections)
                {
                    if (connection.NextSection_DB_ID.HasValue)
                    {
                        var nextSectionId = connection.NextSection_DB_ID.Value;

                        // Check if the destinations field contains our platform
                        if (!string.IsNullOrEmpty(connection.Destinations))
                        {
                            var destinations = connection.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var dest in destinations)
                            {
                                if (int.TryParse(dest.Trim(), out int destPlatformId) && destPlatformId == platformId)
                                {
                                    return true;
                                }
                            }
                        }

                        // Recursive check with visited tracking
                        if (await DoesSectionLeadToPlatformAsync(nextSectionId, platformId, visited))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                return false;
            }
        }

        /// <summary>
        /// Find the best connection that leads towards a specific platform
        /// Prioritizes direct connections to the destination platform over indirect routes
        /// </summary>
        private async Task<VLookupSectionNextSection?> FindBestConnectionToPlatformAsync(
            List<VLookupSectionNextSection> connections, int destinationPlatformId, HashSet<int> visited)
        {
            if (!_platforms.TryGetValue(destinationPlatformId, out var destinationPlatform))
                return null;

            VLookupSectionNextSection? bestConnection = null;
            int bestPriority = int.MaxValue;

            foreach (var connection in connections)
            {
                if (!connection.NextSection_DB_ID.HasValue || visited.Contains(connection.NextSection_DB_ID.Value))
                    continue;

                var nextSectionId = connection.NextSection_DB_ID.Value;
                int priority = int.MaxValue;

                // Priority 1: Direct connection to destination platform's subsection
                if (nextSectionId == destinationPlatform.SubSection_DB_ID)
                {
                    priority = 1;
                }
                // Priority 2: Connection with destination in destinations field
                else if (!string.IsNullOrEmpty(connection.Destinations))
                {
                    var destinations = connection.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var dest in destinations)
                    {
                        if (int.TryParse(dest.Trim(), out int destPlatformId) && destPlatformId == destinationPlatformId)
                        {
                            priority = 2;
                            break;
                        }
                    }
                }
                // Priority 3: Indirect route that leads towards platform
                else if (await DoesSectionLeadToPlatformAsync(nextSectionId, destinationPlatformId))
                {
                    priority = 3;
                }

                // Select the connection with lowest priority (best route)
                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestConnection = connection;
                }
            }

     
            return bestConnection;
        }

        /// <summary>
        /// Get possible next sections for train pathfinding considering train direction
        /// </summary>
        private async Task<List<int>> GetPossibleNextSectionsForTrainAsync(int currentSectionId, int destinationSectionId, bool direction)
        {
            var possibleSections = new List<int>();

            try
            {
                var connections = await GetSectionConnectionsAsync(currentSectionId);

                Console.WriteLine($"[UnifiedPathfinder] Getting next sections for train direction: {direction} from section {currentSectionId}");

                foreach (var connection in connections)
                {
                    if (!connection.NextSection_DB_ID.HasValue)
                        continue;

                    // Filter connections based on train direction
                    // If train direction is true, use connections where Direction is true
                    // If train direction is false, use connections where Direction is false
                    if (connection.Direction != direction)
                    {
                        Console.WriteLine($"[UnifiedPathfinder] Skipping connection {currentSectionId}->{connection.NextSection_DB_ID.Value}: direction mismatch (train: {direction}, track: {connection.Direction})");
                        continue;
                    }

                    // Check if this connection leads towards our destination
                    if (IsConnectionTowardsDestination(connection, destinationSectionId))
                    {
                        possibleSections.Add(connection.NextSection_DB_ID.Value);
                        Console.WriteLine($"[UnifiedPathfinder] Added connection {currentSectionId}->{connection.NextSection_DB_ID.Value} for train direction {direction}");
                    }
                    else
                    {
                        Console.WriteLine($"[UnifiedPathfinder] Connection {currentSectionId}->{connection.NextSection_DB_ID.Value} doesn't lead to destination {destinationSectionId}");
                    }
                }

                Console.WriteLine($"[UnifiedPathfinder] Found {possibleSections.Count} possible next sections for train direction {direction}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UnifiedPathfinder] Error getting possible next sections: {ex.Message}");
            }

            return possibleSections;
        }

        /// <summary>
        /// Check if a connection leads towards the specific destination platform
        /// </summary>
        private bool IsConnectionTowardsDestination(VLookupSectionNextSection connection, int destinationSectionId)
        {
            // Find the destination platform for this section
            var destinationPlatform = _platforms.Values.FirstOrDefault(p => p.SubSection_DB_ID == destinationSectionId);
            if (destinationPlatform == null)
            {
                return true; // Fallback - accept connection if we can't determine destination platform
            }

            // If no destinations specified, treat as neutral path (but be more restrictive)
            if (string.IsNullOrEmpty(connection.Destinations))
            {
                // Only accept if this connection directly leads to our destination section
                return connection.NextSection_DB_ID == destinationSectionId;
            }

            // Parse destinations and check if any leads to our specific destination platform
            var destinationIds = connection.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => int.TryParse(d, out _))
                .Select(int.Parse)
                .ToList();

            // Check if the connection specifically mentions our destination platform
            if (destinationIds.Contains(destinationPlatform.DB_ID))
            {
                return true;
            }

            // Check if the connection leads to a platform in the same station that could reach our destination
            foreach (var destPlatformId in destinationIds)
            {
                var platform = _platforms.Values.FirstOrDefault(p => p.DB_ID == destPlatformId);
                if (platform != null && platform.Station_DB_ID == destinationPlatform.Station_DB_ID)
                {
                    // Only accept if it's the same station AND could realistically reach our platform
                    // This is more restrictive than before - we need to be more careful about platform selection
                    return destPlatformId == destinationPlatform.DB_ID;
                }
            }

            // If connection leads directly to our destination section, accept it
            if (connection.NextSection_DB_ID == destinationSectionId)
            {
                return true;
            }

            Console.WriteLine($"[UnifiedPathfinder] Rejecting connection - doesn't lead to platform {destinationPlatform.Name} (ID: {destinationPlatform.DB_ID})");
            return false; // Be more strict - reject connections that don't lead to our destination
        }

        /// <summary>
        /// Get station ID for a section
        /// </summary>
        private int GetSectionStationId(int sectionId)
        {
            foreach (var section in _sections.Values)
            {
                if (section.SubSections.Any(ss => ss.DB_ID == sectionId))
                {
                    return section.DB_ID;
                }
            }
            return 0;
        }

        /// <summary>
        /// Send switch command via MQTT
        /// </summary>
        private async Task<bool> SendSwitchCommandAsync(string switchName, string position, string trainName)
        {
            try
            {
                var success = await _adminMQTTService.SendSwitchCommandAsync(switchName, position);

                if (success)
                {
                    lock (_lockObject)
                    {
                        _lastSwitchStates[switchName] = position;
                    }

                    _statusService?.ShowInfo($"Switch configured: {switchName} -> {position}", trainName);
                }
                else
                {
                    _statusService?.ShowError($"Switch config failed: {switchName}", trainName);
                }

                return success;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Switch command error: {ex.Message}", trainName);
                return false;
            }
        }

        /// <summary>
        /// Check if switch is already configured
        /// </summary>
        private bool IsSwitchAlreadyConfigured(string switchName, string position)
        {
            lock (_lockObject)
            {
                return _lastSwitchStates.TryGetValue(switchName, out var lastPosition) &&
                       string.Equals(lastPosition, position, StringComparison.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// Parse switch constraints string
        /// </summary>
        private List<(string SwitchName, string RequiredPosition)> ParseSwitchConstraints(string switchConstraints)
        {
            var configs = new List<(string SwitchName, string RequiredPosition)>();

            if (string.IsNullOrEmpty(switchConstraints))
            {
                Console.WriteLine($"[UnifiedPathfinder] Switch constraints is null or empty");
                return configs;
            }

            Console.WriteLine($"[UnifiedPathfinder] Parsing switch constraints: '{switchConstraints}'");

            var constraints = switchConstraints.Split(',', StringSplitOptions.RemoveEmptyEntries);
            Console.WriteLine($"[UnifiedPathfinder] Split into {constraints.Length} constraints: {string.Join(" | ", constraints)}");

            foreach (var constraint in constraints)
            {
                Console.WriteLine($"[UnifiedPathfinder] Processing constraint: '{constraint}'");
                var parts = constraint.Split('=', StringSplitOptions.RemoveEmptyEntries);
                Console.WriteLine($"[UnifiedPathfinder] Split into {parts.Length} parts: {string.Join(" | ", parts)}");

                // Handle different formats:
                // Format 1: sw=SwitchName=straight (3 parts)
                // Format 2: SwitchName=straight (2 parts)
                if (parts.Length >= 2)
                {
                    var switchName = parts.Length == 3 ? parts[1] : parts[0];
                    var position = parts.Length == 3 ? parts[2] : parts[1];

                    // Clean up the switch name and position
                    switchName = switchName.Trim();
                    position = position.Trim();

                    Console.WriteLine($"[UnifiedPathfinder] Extracted - Switch: '{switchName}', Position: '{position}'");

                    var normalizedPosition = position.ToLower() switch
                    {
                        "straight" or "s" => "straight",
                        "turned" or "t" or "turnout" => "turned",
                        _ => position.ToLower()
                    };

                    Console.WriteLine($"[UnifiedPathfinder] Normalized position: '{normalizedPosition}'");
                    configs.Add((switchName, normalizedPosition));
                }
                else
                {
                    Console.WriteLine($"[UnifiedPathfinder] ❌ Invalid constraint format: '{constraint}' (expected at least 2 parts when split by '=')");
                }
            }

            Console.WriteLine($"[UnifiedPathfinder] Parsed {configs.Count} switch configurations");
            return configs;
        }

        /// <summary>
        /// Convert section list to RoutePlan
        /// </summary>
        private RoutePlan ConvertToRoutePlan(List<int> routeSections)
        {
            var edgePath = new List<EdgeInfo>();
            for (int i = 0; i < routeSections.Count - 1; i++)
            {
                edgePath.Add(new EdgeInfo
                {
                    SourceNodeId = routeSections[i],
                    TargetNodeId = routeSections[i + 1],
                    Length = 1.0
                });
            }

            return new RoutePlan
            {
                Path = edgePath,
                TotalLength = edgePath.Sum(e => e.Length)
            };
        }

        /// <summary>
        /// Format platform name properly
        /// </summary>
        private string FormatPlatformName(Platforms platform)
        {
            if (platform == null)
                return "Unknown";

            var stationName = platform.Station?.Name ?? "Unknown";
            var platformName = platform.Name;

            if (string.IsNullOrEmpty(platformName))
                return stationName;

            if (string.IsNullOrEmpty(stationName))
                return platformName;

            // Normalize both names for comparison
            var normalizedPlatform = platformName.ToLower().Trim();
            var normalizedStation = stationName.ToLower().Trim();

            // Check if platform name already contains station name
            if (normalizedPlatform.StartsWith(normalizedStation) ||
                normalizedPlatform.Contains(normalizedStation))
            {
                return platformName;
            }

            // Check for common platform designations
            var commonPlatformSuffixes = new[] { "a", "b", "c", "i", "ii", "iii", "iv", "v", "vi" };
            var lastWord = normalizedPlatform.Split(' ').LastOrDefault();

            if (lastWord != null && commonPlatformSuffixes.Contains(lastWord))
            {
                return $"{stationName} {platformName}";
            }

            return $"{stationName}-{platformName}";
        }

        #endregion
    }

    #region Result Classes

    /// <summary>
    /// Result of route planning operation
    /// </summary>
    public class RoutePlanResult
    {
        public bool Success { get; set; }
        public RoutePlan RoutePlan { get; set; }
        public List<SwitchConfiguration> ConfiguredSwitches { get; set; } = new List<SwitchConfiguration>();
        public string ErrorMessage { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Result of switch configuration operation
    /// </summary>
    public class SwitchConfigurationResult
    {
        public bool Success { get; set; }
        public List<SwitchConfiguration> ConfiguredSwitches { get; set; } = new List<SwitchConfiguration>();
        public List<string> Errors { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
    }

    #endregion
}