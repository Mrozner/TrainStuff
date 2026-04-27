using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSepareted.Domain;
using ClaudeSepareted.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Central pathfinding service for railway network navigation
    /// Provides platform routing, train-aware pathfinding, switch configuration, and conflict detection
    /// </summary>
    public class PathfindingService
    {
        private readonly StatusNotificationService _statusService;
        private readonly ITrackGraphFactory _trackGraphFactory;
        private TrackGraph _trackGraph;
        private readonly ConcurrentDictionary<int, Platforms> _platforms;
        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initLock = new SemaphoreSlim(1, 1);
        private readonly FileLoggingService? _fileLogger;

        // Routing table cache service
        private readonly RoutingTableCacheService _routingCache;

        // Switch configuration service
        private readonly SwitchConfigurationService _switchConfiguration;

        // Service scope factory for database access
        private readonly IServiceScopeFactory _scopeFactory;

        public PathfindingService(
            StatusNotificationService statusService,
            ITrackGraphFactory trackGraphFactory,
            IServiceScopeFactory scopeFactory,
            ConcurrentDictionary<int, Platforms> platforms,
            RoutingTableCacheService routingCache = null,
            SwitchConfigurationService switchConfiguration = null,
            FileLoggingService fileLogger = null)
        {
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _trackGraphFactory = trackGraphFactory ?? throw new ArgumentNullException(nameof(trackGraphFactory));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _platforms = platforms ?? throw new ArgumentNullException(nameof(platforms));
            _fileLogger = fileLogger;

            // Initialize routing cache service
            _routingCache = routingCache ?? throw new ArgumentNullException(nameof(routingCache));

            // Initialize switch configuration service
            _switchConfiguration = switchConfiguration ?? throw new ArgumentNullException(nameof(switchConfiguration));
        }

        #region Initialization

        /// <summary>
        /// Initializes the unified pathfinding service with all necessary data
        /// Thread-safe: Uses semaphore lock to prevent race conditions
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            // Fast path if already initialized
            if (_isInitialized)
                return true;

            await _initLock.WaitAsync();
            try
            {
                // Double-check after acquiring lock
                if (_isInitialized)
                    return true;

                // Initialize TrackGraph for advanced pathfinding
                _trackGraph = await _trackGraphFactory.CreateTrackGraphAsync();

                // Load platforms for platform-level routing
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    var platforms = await dbContext.Platforms
                        .Include(p => p.Station)
                        .Include(p => p.SubSection)
                        .Where(p => p.IsActive)
                        .ToListAsync();

                    foreach (var platform in platforms)
                    {
                        _platforms[platform.DB_ID] = platform;
                    }
                }

                _isInitialized = true;

                // Build the routing table for all possible routes
                await BuildRoutingTableAsync();

                return true;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Pathfinding init failed: {ex.Message} - [PathfindingService]");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        #endregion

        #region Routing Table Integration

        /// <summary>
        /// Build the routing table cache using in-memory TrackGraph
        /// </summary>
        public async Task BuildRoutingTableAsync(bool forceRebuild = false)
        {
            await _routingCache.BuildRoutingTableAsync(_trackGraph, forceRebuild);
        }

        /// <summary>
        /// Get a cached route if available
        /// </summary>
        public PreCalculatedRoute GetCachedRoute(int sourcePlatformId, int destPlatformId, bool direction)
        {
            return _routingCache.GetCachedRoute(sourcePlatformId, destPlatformId, direction);
        }

        #endregion

        #region Platform-to-Platform Pathfinding Integration

        /// <summary>
        /// Find direct route between two platforms using in-memory graph considering train direction
        /// Uses TrackGraph for memory-based pathfinding
        /// </summary>
        public async Task<List<int>?> FindDirectPlatformRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform, bool direction)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            var routePlan = _trackGraph.FindPlatformRoute(sourcePlatform, destinationPlatform, direction);

            if (routePlan == null)
                return null;

            // Convert RoutePlan to List<int> using efficient LINQ
            var routeSections = routePlan.Path
                .SelectMany(e => new[] { e.SourceNodeId, e.TargetNodeId })
                .Distinct()
                .ToList();

            return routeSections;
        }

        /// <summary>
        /// Backwards compatibility overload - defaults to forward direction (true)
        /// </summary>
        public Task<List<int>?> FindDirectPlatformRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform)
        {
            return FindDirectPlatformRouteAsync(sourcePlatform, destinationPlatform, true);
        }

        /// <summary>
        /// Find direct route plan between two platforms - returns raw RoutePlan with switch data
        /// </summary>
        public async Task<RoutePlan?> FindDirectPlatformRoutePlanAsync(Platforms sourcePlatform, Platforms destinationPlatform, bool direction)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            return _trackGraph.FindPlatformRoute(sourcePlatform, destinationPlatform, direction);
        }

        #endregion

        #region Train-Aware Pathfinding

        /// <summary>
        /// Calculate path from current train position to destination using in-memory graph
        /// Simplified version: Only calculates the physical track route, ignoring other trains.
        /// Conflict detection and avoidance is now handled by the block reservation system.
        /// </summary>
        public async Task<List<int>?> CalculateTrainPathAsync(Train train)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            if (train?.SubSection == null || train?.Destination == null)
            {
                return null;
            }

            var sourceSectionId = train.SubSection.DB_ID;
            var destSectionId = train.Destination.SubSection_DB_ID;

            if (sourceSectionId <= 0 || destSectionId <= 0)
                return null;

            var routePlan = _trackGraph.FindRouteInMemory(sourceSectionId, destSectionId, train.Direction);

            if (routePlan == null)
                return null;

            // Convert RoutePlan to List<int> using efficient LINQ
            var routeSections = new[] { sourceSectionId }.Concat(routePlan.Path.Select(e => e.TargetNodeId)).Distinct().ToList();

            return routeSections;
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
                    .Where(x => x.SubSection != null &&
                               (x.State == TrainState.Waiting ||
                                x.State == TrainState.Moving ||
                                x.State == TrainState.PrepareToStop ||
                                x.State == TrainState.Arrived))
                    .Select(x => x.SubSection.DB_ID)
                    .ToList();

                var availablePlatform = stationPlatforms
                    .FirstOrDefault(x => !occupiedPlatformSubsections.Contains(x.SubSection_DB_ID));

                if (availablePlatform != null)
                {
                    return availablePlatform.DB_ID;
                }
                return null;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Failed to find available platform: {ex.Message} - [PathfindingService]");
                return null;
            }
        }

        /// <summary>
        /// Get platform departure sections using in-memory graph
        /// </summary>
        public async Task<List<int>> GetPlatformDepartureSectionsAsync(int platformId, bool direction)
        {
            if (!_isInitialized)
            {
                await InitializeAsync();
            }

            var departureSections = new List<int>();

            try
            {
                if (_platforms.TryGetValue(platformId, out var platform))
                {
                    departureSections = _trackGraph.GetOutgoingConnections(platform.SubSection_DB_ID);
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Failed to get platform departure sections: {ex.Message} - [PathfindingService]");
            }

            return departureSections;
        }

        /// <summary>
        /// Validate if a path is currently viable.
        /// Simplified version: Only checks if path exists, not traffic conflicts.
        /// Conflict detection is handled by the block reservation system.
        /// </summary>
        public Task<bool> IsPathViableAsync(List<int> path, Train train)
        {
            if (path == null || path.Count < 2)
                return Task.FromResult(false);

            try
            {
                // Path exists - trust the block system to handle traffic
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Failed to validate path: {ex.Message} - [PathfindingService]");
                return Task.FromResult(false);
            }
        }

        #endregion

        #region Switch Configuration

        /// <summary>
        /// Plan complete route with switch configuration considering train direction
        /// Uses pre-calculated routing table for instant lookup (if available)
        /// Delegates to SwitchConfigurationService
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

            if (sourcePlatform == null || destinationPlatform == null)
            {
                return new RoutePlanResult
                {
                    Success = false,
                    ErrorMessage = "No active platforms found for the selected platforms."
                };
            }

            try
            {
                var sourcePlatformName = FormatPlatformName(sourcePlatform);
                var destPlatformName = FormatPlatformName(destinationPlatform);
                _statusService?.ShowInfo($"Route planning: {sourcePlatformName} → {destPlatformName} (dir: {direction})", trainName);

                // Check routing table cache first
                var cachedRoute = GetCachedRoute(sourcePlatform.DB_ID, destinationPlatform.DB_ID, direction);
                if (cachedRoute != null)
                {
                    if (cachedRoute.RouteExists)
                    {
                        // If we only want to plan/reserve but NOT move switches yet:
                        if (!configureSwitches)
                        {
                            return new RoutePlanResult
                            {
                                Success = true,
                                RoutePlan = cachedRoute.RoutePlan,
                                ConfiguredSwitches = cachedRoute.RequiredSwitches
                            };
                        }

                        // Proceed with dynamic switch configuration and lock checking
                        var switchConfigResult = await _switchConfiguration.ConfigureSwitchesForRouteAsync(cachedRoute.RoutePlan, trainName);
                        if (!switchConfigResult.Success)
                        {
                            return new RoutePlanResult
                            {
                                Success = false,
                                ErrorMessage = $"Switch configuration failed: {switchConfigResult.ErrorMessage}",
                                RoutePlan = cachedRoute.RoutePlan
                            };
                        }
                        _fileLogger?.Log($"[PATHFINDER] Cached route found and configured for {trainName}: {sourcePlatformName} -> {destPlatformName}");
                        _statusService?.ShowSuccess($"Route configured", trainName);

                        return new RoutePlanResult
                        {
                            Success = true,
                            RoutePlan = cachedRoute.RoutePlan,
                            ConfiguredSwitches = switchConfigResult.ConfiguredSwitches
                        };
                    }
                    else
                    {
                        // Route doesn't exist - return cached failure
                        var errorMsg = $"No route found from {sourcePlatformName} to {destPlatformName} in direction {direction} (cached)";
                        _statusService?.ShowError($"{errorMsg} - [PathfindingService]", trainName);

                        return new RoutePlanResult
                        {
                            Success = false,
                            ErrorMessage = errorMsg
                        };
                    }
                }

                // Cache miss - fall back to dynamic pathfinding
                var routePlan = await FindDirectPlatformRoutePlanAsync(sourcePlatform, destinationPlatform, direction);
                if (routePlan == null || !routePlan.Path.Any())
                {
                    var errorMsg = $"No route found from {sourcePlatformName} to {destPlatformName} in direction {direction}";
                    _statusService?.ShowError($"{errorMsg} - [PathfindingService]", trainName);

                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                // If we only want to plan/reserve but NOT move switches yet:
                if (!configureSwitches)
                {
                    return new RoutePlanResult
                    {
                        Success = true,
                        RoutePlan = routePlan,
                        ConfiguredSwitches = routePlan.GetSwitchConfigurations()
                    };
                }

                // Configure switches for the route
                var dynamicSwitchConfigResult = await _switchConfiguration.ConfigureSwitchesForRouteAsync(routePlan, trainName);
                if (!dynamicSwitchConfigResult.Success)
                {
                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = $"Switch configuration failed: {dynamicSwitchConfigResult.ErrorMessage}",
                        RoutePlan = routePlan
                    };
                }
                _fileLogger?.Log($"[PATHFINDER] Dynamic route generated and configured for {trainName}: {sourcePlatformName} -> {destPlatformName}");
                _statusService?.ShowSuccess($"Route configured", trainName);

                return new RoutePlanResult
                {
                    Success = true,
                    RoutePlan = routePlan,
                    ConfiguredSwitches = dynamicSwitchConfigResult.ConfiguredSwitches
                };
            }
            catch (Exception ex)
            {
                _fileLogger?.Log($"[PATHFINDER] Route planning failed for {trainName}: {ex.Message}");
                var errorMsg = $"Route planning error: {ex.Message}";
                _statusService?.ShowError($"{errorMsg} - [PathfindingService]", trainName);
                return new RoutePlanResult
                {
                    Success = false,
                    ErrorMessage = errorMsg
                };
            }
        }

        /// <summary>
        /// Configure switches for a single block using just-in-time approach.
        /// Delegates to SwitchConfigurationService
        /// </summary>
        public async Task<bool> ConfigureSwitchesForBlockAsync(string nextSection, RoutePlan routePlan, string trainName)
        {
            int nextSectionId = _trackGraph.GetNodeIdByName(nextSection);
            return await _switchConfiguration.ConfigureSwitchesForBlockAsync(nextSectionId, nextSection, routePlan, trainName);
        }

        #endregion

        #region Private Helper Methods

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