using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClaudeSepareted.Common;
using ClaudeSepareted.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using ClaudeSepareted.DataAccess;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Service for caching pre-calculated routes between platforms
    /// Reduces runtime computational overhead by shifting route calculation to initialization
    /// Now uses in-memory TrackGraph for pathfinding instead of database queries
    /// </summary>
    public class RoutingTableCacheService
    {
        private readonly ConcurrentDictionary<RouteKey, PreCalculatedRoute> _routingTable = new ConcurrentDictionary<RouteKey, PreCalculatedRoute>();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly StatusNotificationService _statusService;
        private volatile bool _routingTableBuilt = false;
        private readonly FileLoggingService? _fileLogger;

        public bool IsRoutingTableBuilt => _routingTableBuilt;
        public int CachedRouteCount => _routingTable.Count;

        public RoutingTableCacheService(
            IServiceScopeFactory scopeFactory,
            StatusNotificationService statusService,
            FileLoggingService fileLogger = null)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _fileLogger = fileLogger;
        }

        /// <summary>
        /// Pre-calculates all possible routes between platforms at startup
        /// This shifts the heavy computational work from runtime to initialization time
        /// Uses in-memory TrackGraph for pathfinding instead of database queries
        /// Parallelized for improved performance on multi-core systems
        /// </summary>
        public async Task BuildRoutingTableAsync(TrackGraph trackGraph, bool forceRebuild = false)
        {
            try
            {
                var cacheFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrainControllerLogs", "routing_cache.json");
                var jsonOptions = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.IgnoreCycles, WriteIndented = true };

                if (!forceRebuild && File.Exists(cacheFilePath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(cacheFilePath);
                        var loaded = JsonSerializer.Deserialize<List<CachedRouteExportDto>>(json, jsonOptions);
                        if (loaded != null && loaded.Any())
                        {
                            _routingTable.Clear();
                            foreach (var item in loaded)
                            {
                                _routingTable[new RouteKey(item.SourceId, item.DestId, item.Direction)] = item.Route;
                            }
                            _routingTableBuilt = true;
                            _statusService?.ShowSuccess($"Betöltve {_routingTable.Count} útvonal a gyorsítótárból.");
                            _fileLogger?.Log($"[ROUTING CACHE] Loaded {_routingTable.Count} routes from disk cache.");
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        _fileLogger?.Log($"[ROUTING CACHE] Failed to load disk cache, rebuilding... ({ex.Message})");
                    }
                }

                var stopwatch = Stopwatch.StartNew();

                // Create a scope to resolve the database context and fetch platforms from database
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                var platformList = await dbContext.Platforms
                    .Include(p => p.Station)
                    .Include(p => p.SubSection)
                    .ToListAsync();

                var totalRoutesCalculated = 0;
                var successfulRoutes = 0;
                var failedRoutes = 0;

                // Parallelize outer loop for better performance on multi-core systems
                // Wrap in Task.Run to prevent sync-over-async blocking
                await Task.Run(() =>
                {
                    Parallel.ForEach(platformList, sourcePlatform =>
                    {
                        // Iterate through all platforms as destination
                        foreach (var destPlatform in platformList)
                        {
                            // Skip if source and destination are the same
                            if (sourcePlatform.DB_ID == destPlatform.DB_ID)
                                continue;

                            // Calculate for both directions (true and false)
                            foreach (bool direction in new[] { true, false })
                            {
                                var routeKey = new RouteKey(sourcePlatform.DB_ID, destPlatform.DB_ID, direction);
                                Interlocked.Increment(ref totalRoutesCalculated);

                                try
                                {
                                    // Find route using in-memory TrackGraph
                                    var routePlan = trackGraph.FindPlatformRoute(sourcePlatform, destPlatform, direction);

                                    if (routePlan != null && routePlan.HasPath)
                                    {
                                        // Convert RoutePlan to section list using HashSet for O(1) lookups
                                        var routeSectionsSet = new HashSet<int>();
                                        foreach (var edge in routePlan.Path)
                                        {
                                            routeSectionsSet.Add(edge.SourceNodeId);
                                            routeSectionsSet.Add(edge.TargetNodeId);
                                        }

                                        // Get required switches for this route from RoutePlan edges
                                        var switchConfigurations = routePlan.GetSwitchConfigurations();

                                        // Create successful cached route
                                        var cachedRoute = PreCalculatedRoute.CreateSuccessful(
                                            routeSectionsSet.ToList(),
                                            routePlan,
                                            switchConfigurations,
                                            $"{FormatPlatformName(sourcePlatform)} -> {FormatPlatformName(destPlatform)} (dir: {direction})"
                                        );

                                        _routingTable[routeKey] = cachedRoute;
                                        Interlocked.Increment(ref successfulRoutes);
                                    }
                                    else
                                    {
                                        // Create failed cached route (no path exists)
                                        var cachedRoute = PreCalculatedRoute.CreateFailed(
                                            $"No route from {FormatPlatformName(sourcePlatform)} to {FormatPlatformName(destPlatform)} in direction {direction}"
                                        );

                                        _routingTable[routeKey] = cachedRoute;
                                        Interlocked.Increment(ref failedRoutes);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    // Log error but continue processing other routes
                                    var cachedRoute = PreCalculatedRoute.CreateFailed($"Error: {ex.Message}");
                                    _routingTable[routeKey] = cachedRoute;
                                    Interlocked.Increment(ref failedRoutes);
                                }
                            }
                        }
                    });
                });

                stopwatch.Stop();
                _routingTableBuilt = true;

                // Save to disk
                try
                {
                    var exportList = _routingTable.Select(kvp => new CachedRouteExportDto
                    {
                        SourceId = kvp.Key.SourcePlatformId,
                        DestId = kvp.Key.DestPlatformId,
                        Direction = kvp.Key.Direction,
                        Route = kvp.Value
                    }).ToList();

                    var json = JsonSerializer.Serialize(exportList, jsonOptions);
                    await File.WriteAllTextAsync(cacheFilePath, json);
                    _fileLogger?.Log($"[ROUTING CACHE] Saved routing table to disk.");
                }
                catch (Exception ex)
                {
                    _fileLogger?.Log($"[ROUTING CACHE] Failed to save cache to disk: {ex.Message}");
                }

                // Log routing table build statistics
                _statusService?.ShowInfo(
                    $"Routing table built in {stopwatch.Elapsed.TotalSeconds:F2}s: " +
                    $"{totalRoutesCalculated} total routes, " +
                    $"{successfulRoutes} successful, " +
                    $"{failedRoutes} failed"
                );

                _fileLogger?.Log($"[ROUTING CACHE] Table built in {stopwatch.Elapsed.TotalSeconds:F2}s. Total: {totalRoutesCalculated}, Success: {successfulRoutes}, Failed: {failedRoutes}.");
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Routing table build failed: {ex.Message} - [RoutingTableCacheService]");
            }
        }

        /// <summary>
        /// Get a cached route if it exists
        /// </summary>
        public PreCalculatedRoute GetCachedRoute(int sourcePlatformId, int destPlatformId, bool direction)
        {
            var routeKey = new RouteKey(sourcePlatformId, destPlatformId, direction);
            _routingTable.TryGetValue(routeKey, out var cachedRoute);
            return cachedRoute;
        }

        /// <summary>
        /// Check if a route exists in the cache
        /// </summary>
        public bool HasCachedRoute(int sourcePlatformId, int destPlatformId, bool direction)
        {
            var routeKey = new RouteKey(sourcePlatformId, destPlatformId, direction);
            return _routingTable.ContainsKey(routeKey);
        }

        /// <summary>
        /// Get statistics about the routing table
        /// </summary>
        public Task<(int Total, int Successful, int Failed)> GetRouteStatisticsAsync()
        {
            var total = _routingTable.Count;
            var successful = _routingTable.Values.Count(r => r.RouteExists);
            var failed = _routingTable.Values.Count(r => !r.RouteExists);

            return Task.FromResult((total, successful, failed));
        }

        /// <summary>
        /// Clear the routing table cache
        /// </summary>
        public void ClearCache()
        {
            _routingTable.Clear();
            _routingTableBuilt = false;
            _fileLogger?.Log("[ROUTING CACHE] Routing table cache manually cleared.");
        }

        private string FormatPlatformName(Platforms platform)
        {
            if (platform == null) return "Unknown";
            return $"{platform.Station?.Name ?? "?"} - {platform.Name}";
        }
    }

    public class CachedRouteExportDto
    {
        public int SourceId { get; set; }
        public int DestId { get; set; }
        public bool Direction { get; set; }
        public PreCalculatedRoute Route { get; set; }
    }
}
