using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Enhanced pathfinding service for railway track management
    /// Integrates blueprint pathfinding logic with existing database structure
    /// </summary>
    public class RailwayPathfinderService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly ITrackGraphFactory _trackGraphFactory;
        private TrackGraph _trackGraph;
        private readonly Dictionary<int, Sections> _sections;
        private readonly Dictionary<int, Platforms> _platforms;
        private bool _isInitialized = false;

        public RailwayPathfinderService(ApplicationDbContext dbContext, ITrackGraphFactory trackGraphFactory)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _trackGraphFactory = trackGraphFactory ?? throw new ArgumentNullException(nameof(trackGraphFactory));
            _sections = new Dictionary<int, Sections>();
            _platforms = new Dictionary<int, Platforms>();
        }

        /// <summary>
        /// Initializes the pathfinder with sections and platforms data
        /// </summary>
        public async Task InitializeAsync()
        {
            try
            {
                if (_isInitialized)
                    return;

                Console.WriteLine("[RailwayPathfinder] Initializing pathfinder service...");

                // Initialize TrackGraph first
                _trackGraph = await _trackGraphFactory.CreateTrackGraphAsync();
                Console.WriteLine("[RailwayPathfinder] TrackGraph loaded successfully");

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
                Console.WriteLine($"[RailwayPathfinder] Initialized with {_sections.Count} sections and {_platforms.Count} platforms");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Initialization error: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Calculate and lock path from current position to destination
        /// Core pathfinding algorithm - extracts route planning logic
        /// </summary>
        public async Task<List<int>> CalculatePathAsync(Train train, List<Train> otherTrains)
        {
            if (train?.SubSection == null)
            {
                Console.WriteLine("[RailwayPathfinder] Train has no current subsection, cannot calculate path");
                return null;
            }

            var path = new List<int>();
            var currentSection = train.SubSection.DB_ID;
            var destinationSection = train.Destination?.SubSection_DB_ID;

            if (destinationSection == null)
            {
                Console.WriteLine("[RailwayPathfinder] Train has no destination subsection, cannot calculate path");
                return null;
            }

            Console.WriteLine($"[RailwayPathfinder] Calculating path for train {train.Name}: {currentSection} -> {destinationSection}, Direction: {train.Direction}");

            // Start with current section
            path.Add(currentSection);

            // Iterative pathfinding using graph traversal
            while (currentSection != destinationSection)
            {
                var possibleNextSections = await GetPossibleNextSectionsAsync(currentSection, destinationSection.Value, train.Direction);

                // Single path available - straightforward traversal
                if (possibleNextSections.Count == 1)
                {
                    var nextSection = possibleNextSections.FirstOrDefault();

                    // Check for conflicts with opposing trains
                    if (HasOpposingTrainConflict(nextSection, train, otherTrains))
                    {
                        Console.WriteLine($"[RailwayPathfinder] Path blocked: opposing train conflict at section {nextSection}");
                        return null;
                    }

                    // Check for conflicts with same-direction trains
                    if (HasSameDirectionConflict(nextSection, train, otherTrains))
                    {
                        Console.WriteLine($"[RailwayPathfinder] Path blocked: same-direction train conflict at section {nextSection}");
                        return null;
                    }

                    path.Add(nextSection);
                    currentSection = nextSection;
                    continue;
                }

                // Multiple path choices - conflict resolution required
                if (possibleNextSections.Count > 1)
                {
                    var chosenSection = await ResolveMultiplePathsAsync(possibleNextSections, train, otherTrains);
                    if (chosenSection == 0)
                    {
                        Console.WriteLine("[RailwayPathfinder] No valid path found after conflict resolution");
                        return null;
                    }

                    path.Add(chosenSection);
                    currentSection = chosenSection;
                    continue;
                }

                // No available paths
                Console.WriteLine($"[RailwayPathfinder] No available paths from section {currentSection}");
                return null;
            }

            Console.WriteLine($"[RailwayPathfinder] Path calculated successfully: {string.Join(" -> ", path)}");
            return path;
        }

        /// <summary>
        /// Gets possible next sections from current section towards destination using platform destinations as guidance
        /// </summary>
        private async Task<List<int>> GetPossibleNextSectionsAsync(int currentSectionId, int destinationSectionId, bool direction)
        {
            var possibleSections = new List<int>();

            try
            {
                // Query the database view for possible connections
                var connections = await _dbContext.VLookupSectionNextSection
                    .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0} AND Direction = {1}",
                               currentSectionId, direction ? 1 : 0)
                    .ToListAsync();

                Console.WriteLine($"[RailwayPathfinder] Found {connections.Count} connections from section {currentSectionId} in direction {direction}");

                foreach (var connection in connections)
                {
                    if (!connection.NextSection_DB_ID.HasValue)
                        continue;

                    // Check if this connection leads towards our destination platform
                    if (IsConnectionTowardsDestination(connection, destinationSectionId))
                    {
                        possibleSections.Add(connection.NextSection_DB_ID.Value);
                        Console.WriteLine($"[RailwayPathfinder] Valid connection: {currentSectionId} -> {connection.NextSection_DB_ID.Value} (Leads to destination platform {destinationSectionId})");
                    }
                    else
                    {
                        Console.WriteLine($"[RailwayPathfinder] Connection not towards destination: {currentSectionId} -> {connection.NextSection_DB_ID.Value}");
                    }
                }

                Console.WriteLine($"[RailwayPathfinder] Found {possibleSections.Count} valid connections towards destination {destinationSectionId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Error getting possible next sections: {ex.Message}");
                throw;
            }

            return possibleSections;
        }

        /// <summary>
        /// Checks if a connection leads towards the destination platform
        /// Uses the Destinations field to determine if the path is correct
        /// </summary>
        private bool IsConnectionTowardsDestination(VLookupSectionNextSection connection, int destinationSectionId)
        {
            // If no destinations specified, return all possible connections
            if (string.IsNullOrEmpty(connection.Destinations))
            {
                Console.WriteLine($"[RailwayPathfinder] Connection {connection.Section_DB_ID}->{connection.NextSection_DB_ID} has no destinations, treating as neutral path");
                return true;
            }

            // Parse destinations (comma-separated platform IDs)
            var destinationIds = connection.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .Where(d => int.TryParse(d, out _))
                .Select(int.Parse)
                .ToList();

            // Check if any destination leads to a platform in the destination station
            foreach (var destPlatformId in destinationIds)
            {
                var platform = _platforms.Values.FirstOrDefault(p => p.DB_ID == destPlatformId);
                if (platform != null && platform.Station_DB_ID != 0)
                {
                    // Get the station of our destination section
                    var destinationStationId = GetSectionStationId(destinationSectionId);

                    // Check if this destination platform is in the same station as our destination section
                    if (destinationStationId != 0 && platform.Station_DB_ID == destinationStationId)
                    {
                        Console.WriteLine($"[RailwayPathfinder] Connection leads to destination station (Platform {platform.Name} -> Section {destinationSectionId})");
                        return true;
                    }
                }
            }

            // If no direct station match, check if this connection moves us closer to destination
            // This is a fallback for complex routing scenarios
            return IsGeneralDirectionTowardsDestination(connection, destinationSectionId);
        }

        /// <summary>
        /// Fallback method to determine if a connection is generally in the right direction
        /// </summary>
        private bool IsGeneralDirectionTowardsDestination(VLookupSectionNextSection connection, int destinationSectionId)
        {
            // This is a simplified heuristic - in a real implementation, you might use
            // geographic proximity, predefined direction maps, or other heuristics
            // For now, we'll accept all connections as potentially valid
            Console.WriteLine($"[RailwayPathfinder] Using general direction check for {connection.Section_DB_ID} -> {connection.NextSection_DB_ID.Value} towards {destinationSectionId}");
            return true;
        }

        /// <summary>
        /// Gets the station ID for a given section ID
        /// </summary>
        private int GetSectionStationId(int sectionId)
        {
            // Find which section contains this subsection
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
        /// Check if opposing train occupies the section
        /// Safety-critical collision prevention
        /// </summary>
        private bool HasOpposingTrainConflict(int sectionId, Train train, List<Train> otherTrains)
        {
            return otherTrains
                .Where(x => x.Direction != train.Direction && x.DB_ID != train.DB_ID)
                .Any(x => x.LockedSections.Contains(sectionId) &&
                         (x.LockedSections.IndexOf(sectionId) < x.LockedSections.IndexOf(x.Destination?.SubSection_DB_ID ?? -1) ||
                          x.LockedSections.IndexOf(x.Destination?.SubSection_DB_ID ?? -1) == -1));
        }

        /// <summary>
        /// Check for same-direction train conflicts
        /// Prevents train catch-up scenarios
        /// </summary>
        private bool HasSameDirectionConflict(int sectionId, Train train, List<Train> otherTrains)
        {
            return otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction == train.Direction && x.State == TrainState.Moving)
                .Any(x => x.LockedSections.Contains(sectionId) || x.SubSection?.DB_ID == sectionId);
        }

        /// <summary>
        /// Resolve path selection when multiple routes are available
        /// Prioritizes paths without conflicts
        /// </summary>
        private async Task<int> ResolveMultiplePathsAsync(List<int> possibleSections, Train train, List<Train> otherTrains)
        {
            // Get all sections locked by opposing-direction trains
            var opposingLockedSections = otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction != train.Direction)
                .SelectMany(x => x.LockedSections)
                .ToList();

            // Get all sections locked by same-direction trains
            var sameDirectionLockedSections = otherTrains
                .Where(x => x.DB_ID != train.DB_ID && x.Direction == train.Direction)
                .SelectMany(x => x.LockedSections)
                .ToList();

            // Choose first section not locked by opposing trains
            var chosenSection = possibleSections.FirstOrDefault(x => !opposingLockedSections.Contains(x));

            if (chosenSection == 0)
            {
                Console.WriteLine("[RailwayPathfinder] All paths blocked by opposing traffic");
                return 0;
            }

            // Check if chosen section can accommodate same-direction traffic
            var sameDirectionLockCount = sameDirectionLockedSections.Count(x => x == chosenSection);

            // Get subsection capacity for this section
            var section = _sections.Values.FirstOrDefault(s => s.SubSections.Any(ss => ss.DB_ID == chosenSection));
            var sectionCapacity = section?.SubSections.Count ?? 1;

            if (sameDirectionLockCount >= sectionCapacity)
            {
                Console.WriteLine($"[RailwayPathfinder] Section {chosenSection} at capacity ({sameDirectionLockCount}/{sectionCapacity})");
                return 0;
            }

            return chosenSection;
        }

        /// <summary>
        /// Get required switch states for a given route
        /// Essential for track configuration before train movement
        /// </summary>
        public async Task<List<(string SwitchName, string RequiredPosition)>> GetRequiredSwitchesAsync(List<int> path, bool direction)
        {
            var requiredSwitches = new List<(string SwitchName, string RequiredPosition)>();

            try
            {
                for (int i = 0; i < path.Count - 1; i++)
                {
                    var currentSection = path[i];
                    var nextSection = path[i + 1];

                    var connections = await _dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0} AND NextSection_DB_ID = {1} AND Direction = {2}",
                                   currentSection, nextSection, direction ? 1 : 0)
                        .ToListAsync();

                    foreach (var connection in connections)
                    {
                        if (!string.IsNullOrEmpty(connection.SwitchConstraints))
                        {
                            var switchConstraints = ParseSwitchConstraints(connection.SwitchConstraints);
                            requiredSwitches.AddRange(switchConstraints);
                        }
                    }
                }

                // Remove duplicates
                requiredSwitches = requiredSwitches.Distinct().ToList();
                Console.WriteLine($"[RailwayPathfinder] Required switches for path: {string.Join(", ", requiredSwitches.Select(s => $"{s.SwitchName}={s.RequiredPosition}"))}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Error getting required switches: {ex.Message}");
            }

            return requiredSwitches;
        }

        /// <summary>
        /// Parses switch constraints string into individual switch configurations
        /// Format: "=SW1=straight,=SW2=turned,=SW3=straight"
        /// </summary>
        private List<(string SwitchName, string RequiredPosition)> ParseSwitchConstraints(string switchConstraints)
        {
            var configs = new List<(string SwitchName, string RequiredPosition)>();

            if (string.IsNullOrEmpty(switchConstraints))
                return configs;

            // Split by comma and parse each switch constraint
            var constraints = switchConstraints.Split(',', StringSplitOptions.RemoveEmptyEntries);

            foreach (var constraint in constraints)
            {
                // Format: "=SW1=straight"
                var parts = constraint.Split('=', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3)
                {
                    var switchName = parts[1];
                    var position = parts[2];

                    // Map position to standard values
                    var normalizedPosition = position.ToLower() switch
                    {
                        "straight" or "s" => "straight",
                        "turned" or "t" or "turnout" => "turned",
                        _ => position.ToLower()
                    };

                    configs.Add((switchName, normalizedPosition));
                }
            }

            return configs;
        }

        /// <summary>
        /// Validate if a path is currently viable
        /// Checks all sections along the path for conflicts
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

                    if (HasOpposingTrainConflict(sectionId, train, otherTrains))
                    {
                        Console.WriteLine($"[RailwayPathfinder] Path not viable: opposing train conflict at section {sectionId}");
                        return false;
                    }

                    if (HasSameDirectionConflict(sectionId, train, otherTrains))
                    {
                        Console.WriteLine($"[RailwayPathfinder] Path not viable: same-direction train conflict at section {sectionId}");
                        return false;
                    }
                }

                Console.WriteLine("[RailwayPathfinder] Path is viable");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Error validating path viability: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find nearest available platform at destination station
        /// Platform selection algorithm for arrival
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
                    Console.WriteLine($"[RailwayPathfinder] Found available platform: {availablePlatform.DB_ID} at station {stationId}");
                    return availablePlatform.DB_ID;
                }

                Console.WriteLine($"[RailwayPathfinder] No available platforms found at station {stationId}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Error finding available platform: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get initial sections needed for platform departure
        /// Handles station exit path setup
        /// </summary>
        public async Task<List<int>> GetPlatformDepartureSectionsAsync(int platformId, bool direction)
        {
            var departureSections = new List<int>();

            try
            {
                if (_platforms.TryGetValue(platformId, out var platform))
                {
                    // Query for routes leading away from this platform's subsection
                    var connections = await _dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0} AND Direction = {1}",
                                   platform.SubSection_DB_ID, direction ? 1 : 0)
                        .ToListAsync();

                    departureSections = connections.Select(c => c.NextSection_DB_ID ?? 0).Where(id => id > 0).ToList();
                    Console.WriteLine($"[RailwayPathfinder] Platform {platformId} departure sections: {string.Join(", ", departureSections)}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RailwayPathfinder] Error getting platform departure sections: {ex.Message}");
            }

            return departureSections;
        }
    }

    /// <summary>
    /// Path optimization utilities for advanced pathfinding
    /// </summary>
    public static class PathOptimizer
    {
        /// <summary>
        /// Optimize path by removing unnecessary detours
        /// Path smoothing algorithm
        /// </summary>
        public static List<int> OptimizePath(List<int> originalPath, TrackGraph trackGraph)
        {
            if (originalPath == null || originalPath.Count <= 2)
                return originalPath ?? new List<int>();

            var optimized = new List<int> { originalPath.First() };

            for (int i = 1; i < originalPath.Count - 1; i++)
            {
                var current = originalPath[i - 1];
                var next = originalPath[i + 1];

                // Check if direct connection exists, bypassing intermediate section
                var hasDirectConnection = trackGraph.HasDirectConnection(current, next);

                if (!hasDirectConnection)
                {
                    optimized.Add(originalPath[i]);
                }
            }

            optimized.Add(originalPath.Last());
            Console.WriteLine($"[PathOptimizer] Path optimized: {originalPath.Count} -> {optimized.Count} sections");

            return optimized;
        }

        /// <summary>
        /// Calculate path length in sections
        /// Used for route comparison and optimization
        /// </summary>
        public static int CalculatePathLength(List<int> path)
        {
            return path?.Count - 1 ?? 0;
        }
    }
}