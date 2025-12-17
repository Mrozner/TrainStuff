using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Represents the track layout as a graph for route planning
    /// </summary>
    public class TrackGraph
    {
        private readonly Dictionary<int, TrackNode> _nodes;
        private readonly Dictionary<int, List<TrackEdge>> _edges;
        private readonly Dictionary<string, int> _stationToNodeId;
        private readonly Dictionary<string, int> _platformToNodeId;

        public TrackGraph()
        {
            _nodes = new Dictionary<int, TrackNode>();
            _edges = new Dictionary<int, List<TrackEdge>>();
            _stationToNodeId = new Dictionary<string, int>();
            _platformToNodeId = new Dictionary<string, int>();
        }

        /// <summary>
        /// Loads the track graph from the database using Lookup_Section_NextSection tables
        /// </summary>
        public static async Task<TrackGraph> LoadFromDatabaseAsync(ApplicationDbContext dbContext)
        {
            var graph = new TrackGraph();

            try
            {
                Console.WriteLine("[TrackGraph] Loading track layout from database using Lookup tables...");

                // Load all subsections as nodes
                var subsections = await dbContext.SubSections.ToListAsync();
                foreach (var subsection in subsections)
                {
                    graph.AddNode(subsection.DB_ID, subsection.Name, subsection.AllowedSpeed);
                }

                // Load platforms to map stations and platforms to subsections (replaces the non-existent StationTracks table)
                var platforms = await dbContext.Platforms
                    .Include(p => p.Station)
                    .Include(p => p.SubSection)
                    .Where(p => p.IsActive)
                    .ToListAsync();

                Console.WriteLine($"[TrackGraph] Loading {platforms.Count} platforms for station and platform mapping...");

                foreach (var platform in platforms)
                {
                    Console.WriteLine($"[TrackGraph] Processing platform: {platform.Station?.Name}-{platform.Name} -> SubSection {platform.SubSection?.DB_ID} ({platform.SubSection?.Name})");

                    if (platform.SubSection != null)
                    {
                        // Map station to subsection if not already mapped
                        if (platform.Station != null && !graph._stationToNodeId.ContainsKey(platform.Station.Name))
                        {
                            graph.MapStationToNode(platform.Station.Name, platform.SubSection.DB_ID);
                            Console.WriteLine($"[TrackGraph] Mapped station '{platform.Station.Name}' to node {platform.SubSection.DB_ID}");
                        }

                        // Map platform to subsection for platform-level routing
                        graph.MapPlatformToNode(platform.Station?.Name, platform.Name, platform.SubSection.DB_ID);
                        var platformDisplayName = $"{platform.Station?.Name ?? "Unknown"}-{platform.Name ?? "Unknown"}";
                        Console.WriteLine($"[TrackGraph] ✓ Mapped platform '{platformDisplayName}' to subsection {platform.SubSection.DB_ID} ({platform.SubSection.Name})");
                    }
                    else
                    {
                        Console.WriteLine($"[TrackGraph] ⚠ Platform '{platform.Station?.Name}-{platform.Name}' has no SubSection mapping (SubSection_DB_ID: {platform.SubSection_DB_ID})");
                    }
                }

                // Load connections from Lookup_Section_NextSection using raw SQL since this table might not be mapped in EF
                await LoadConnectionsFromLookupTables(dbContext, graph);

                Console.WriteLine($"[TrackGraph] Track graph loaded: {graph._nodes.Count} nodes, {graph._edges.Values.Sum(e => e.Count)} edges");
                Console.WriteLine($"[TrackGraph] Station mappings: {graph._stationToNodeId.Count}");
                Console.WriteLine($"[TrackGraph] Platform mappings: {graph._platformToNodeId.Count}");

                return graph;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackGraph] Error loading track graph: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Loads track connections from Lookup_Section_NextSection tables
        /// </summary>
        private static async Task LoadConnectionsFromLookupTables(ApplicationDbContext dbContext, TrackGraph graph)
        {
            try
            {
                Console.WriteLine("[TrackGraph] Loading connections from base Lookup tables...");

                // Load all connections from Lookup_Section_NextSection (directions as stored in database)
                var allConnections = await dbContext.LookupSectionNextSection
                    .Where(ls => ls.IsActive && ls.NextSection_DB_ID.HasValue)
                    .ToListAsync();

                Console.WriteLine($"[TrackGraph] Found {allConnections.Count} connections from database");

                var addedConnections = 0;
                foreach (var connection in allConnections)
                {
                    if (connection.NextSection_DB_ID.HasValue)
                    {
                        // Add connection in the specified direction only
                        // The database should contain both forward and reverse connections if travel is allowed in both directions
                        graph.AddEdge(
                            connection.Section_DB_ID,
                            connection.NextSection_DB_ID.Value,
                            null,
                            null,
                            connection.Direction,
                            1.0
                        );
                        addedConnections++;

                        Console.WriteLine($"[TrackGraph] Added connection {connection.Section_DB_ID} -> {connection.NextSection_DB_ID.Value} with direction {connection.Direction}");
                    }
                }

                Console.WriteLine($"[TrackGraph] Added {addedConnections} directed connections (respecting database directions)");

                // Try to load enhanced connections with switch constraints from the view
                try
                {
                    var viewConnections = await dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE NextSection_DB_ID IS NOT NULL")
                        .ToListAsync();

                    Console.WriteLine($"[TrackGraph] Found {viewConnections.Count} enhanced connections with switch constraints");

                    var enhancedConnections = 0;
                    foreach (var connection in viewConnections)
                    {
                        if (connection.NextSection_DB_ID.HasValue && !string.IsNullOrEmpty(connection.SwitchConstraints))
                        {
                            var switchConstraints = ParseSwitchConstraints(connection.SwitchConstraints);
                            foreach (var switchConfig in switchConstraints)
                            {
                                // Add directed connection with switch constraints
                                graph.AddEdge(
                                    connection.Section_DB_ID,
                                    connection.NextSection_DB_ID.Value,
                                    switchConfig.SwitchName,
                                    switchConfig.RequiredPosition,
                                    connection.Direction,
                                    1.0
                                );
                                enhancedConnections++;
                            }
                        }
                    }
                    Console.WriteLine($"[TrackGraph] Added {enhancedConnections} enhanced connections with switch constraints");
                }
                catch (Exception viewEx)
                {
                    Console.WriteLine($"[TrackGraph] View with switch constraints not available, using basic connections: {viewEx.Message}");
                }

                Console.WriteLine("[TrackGraph] Track connections loaded successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackGraph] Error loading connections: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Parses switch constraints string into individual switch configurations
        /// Format: "=SW1=straight,=SW2=turned,=SW3=straight"
        /// </summary>
        private static List<(string SwitchName, string RequiredPosition)> ParseSwitchConstraints(string switchConstraints)
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
                    Console.WriteLine($"[TrackGraph] Parsed switch constraint: {switchName} = {normalizedPosition}");
                }
            }

            return configs;
        }

      
        /// <summary>
        /// Adds a node (track subsection) to the graph
        /// </summary>
        private void AddNode(int id, string name, Speed allowedSpeed)
        {
            _nodes[id] = new TrackNode
            {
                Id = id,
                Name = name,
                AllowedSpeed = allowedSpeed
            };
            _edges[id] = new List<TrackEdge>();
        }

        /// <summary>
        /// Maps a station name to a node ID
        /// </summary>
        private void MapStationToNode(string stationName, int nodeId)
        {
            if (string.IsNullOrEmpty(stationName)) return;

            // Add multiple variations for Hungarian station names
            var normalized = stationName.ToLower();
            _stationToNodeId[normalized] = nodeId;

            // Add common variations
            if (normalized.Contains("felso") || normalized.Contains("felső"))
            {
                _stationToNodeId["felso"] = nodeId;
                _stationToNodeId["felső"] = nodeId;
                _stationToNodeId["upper"] = nodeId;
            }
            else if (normalized.Contains("also") || normalized.Contains("alsó"))
            {
                _stationToNodeId["also"] = nodeId;
                _stationToNodeId["alsó"] = nodeId;
                _stationToNodeId["lower"] = nodeId;
            }
        }

        /// <summary>
        /// Maps a platform (station-platform combination) to a node ID for platform-level routing
        /// </summary>
        private void MapPlatformToNode(string stationName, string platformName, int nodeId)
        {
            if (string.IsNullOrEmpty(stationName) || string.IsNullOrEmpty(platformName)) return;

            // Normalize station name - handle different variations
            var normalizedStation = stationName.ToLower()
                .Replace("felső", "felso")
                .Replace("alsó", "also");

            // Normalize platform name - handle different variations
            var normalizedPlatform = platformName.ToLower()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("iii", "3")
                .Replace("ii", "2")
                .Replace("i", "1");

            // Create multiple platform key variations for robustness
            var keys = new[]
            {
                $"{normalizedStation}-{normalizedPlatform}",
                $"{stationName.ToLower()}-{platformName.ToLower()}",
                $"{normalizedStation}-{platformName.ToLower()}",
                $"{stationName.ToLower()}-{normalizedPlatform}"
            };

            foreach (var key in keys)
            {
                if (!_platformToNodeId.ContainsKey(key))
                {
                    _platformToNodeId[key] = nodeId;
                }
            }

            Console.WriteLine($"[TrackGraph] ✓ Mapped platform '{stationName}-{platformName}' to node {nodeId} (keys: {string.Join(", ", keys)})");
        }

        /// <summary>
        /// Adds an edge (track connection) to the graph
        /// </summary>
        private void AddEdge(int sourceId, int targetId, string switchName, string switchPosition, bool direction, double length)
        {
            // Ensure both source and target nodes exist in the edges dictionary
            if (!_edges.ContainsKey(sourceId))
            {
                _edges[sourceId] = new List<TrackEdge>();
                Console.WriteLine($"[TrackGraph] Created edge list for source node {sourceId}");
            }

            if (!_edges.ContainsKey(targetId))
            {
                _edges[targetId] = new List<TrackEdge>();
                Console.WriteLine($"[TrackGraph] Created edge list for target node {targetId}");
            }

            var edge = new TrackEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                SwitchName = switchName,
                RequiredSwitchPosition = switchPosition,
                Length = length
            };

            _edges[sourceId].Add(edge);
        }

        /// <summary>
        /// Simple platform route finding method for TrackGraph compatibility
        /// </summary>
        public static async Task<List<int>?> FindSimplePlatformRouteAsync(ApplicationDbContext dbContext, Platforms sourcePlatform, Platforms destinationPlatform)
        {
            try
            {
                var sourceSectionId = sourcePlatform.SubSection_DB_ID;
                var destSectionId = destinationPlatform.SubSection_DB_ID;

                if (sourceSectionId <= 0 || destSectionId <= 0)
                    return null;

                // Simplified direct routing - if source and destination are same platform, return that section
                if (sourcePlatform.DB_ID == destinationPlatform.DB_ID)
                {
                    Console.WriteLine($"[TrackGraph] Source and destination are same platform: {sourcePlatform.Name}");
                    return new List<int> { sourceSectionId };
                }

                var route = new List<int> { sourceSectionId };
                var currentSection = sourceSectionId;
                var visited = new HashSet<int> { sourceSectionId };
                var maxSteps = 20;

                while (currentSection != destSectionId && visited.Count < maxSteps)
                {
                    var connections = await dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", currentSection)
                        .ToListAsync();

                    var bestConnection = FindBestConnectionTowardsDestination(connections, destSectionId, destinationPlatform.DB_ID);

                    if (bestConnection != null && bestConnection.NextSection_DB_ID.HasValue && !visited.Contains(bestConnection.NextSection_DB_ID.Value))
                    {
                        route.Add(bestConnection.NextSection_DB_ID.Value);
                        visited.Add(bestConnection.NextSection_DB_ID.Value);
                        currentSection = bestConnection.NextSection_DB_ID.Value;

                        Console.WriteLine($"[TrackGraph] Moving to section {currentSection} towards {destinationPlatform.Name}");

                        if (currentSection == destSectionId)
                        {
                            Console.WriteLine($"[TrackGraph] ✓ Reached destination platform {destinationPlatform.Name}");
                            return route;
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[TrackGraph] No valid path found from section {currentSection} to {destinationPlatform.Name}");
                        break;
                    }
                }

                Console.WriteLine($"[TrackGraph] Failed to find route to {destinationPlatform.Name} after {visited.Count} sections");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackGraph] Error finding platform route: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Find the best connection that leads towards the destination platform
        /// </summary>
        private static VLookupSectionNextSection? FindBestConnectionTowardsDestination(List<VLookupSectionNextSection> connections, int destinationSectionId, int destinationPlatformId)
        {
            // Priority 1: Direct connection to destination section
            var directConnection = connections.FirstOrDefault(c => c.NextSection_DB_ID == destinationSectionId);
            if (directConnection != null)
            {
                Console.WriteLine("[TrackGraph] Found direct connection to destination section");
                return directConnection;
            }

            // Priority 2: Connection that mentions the destination platform in its destinations
            var platformConnection = connections.FirstOrDefault(c =>
                !string.IsNullOrEmpty(c.Destinations) &&
                c.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(d => d.Trim())
                    .Any(d => int.TryParse(d, out var id) && id == destinationPlatformId));

            if (platformConnection != null)
            {
                Console.WriteLine($"[TrackGraph] Found connection leading to destination platform ID {destinationPlatformId}");
                return platformConnection;
            }

            // Priority 3: Any connection that moves us forward (avoid loops)
            var forwardConnection = connections.FirstOrDefault(c =>
                c.NextSection_DB_ID.HasValue &&
                c.NextSection_DB_ID.Value != destinationSectionId);

            if (forwardConnection != null)
            {
                Console.WriteLine("[TrackGraph] Using forward connection as fallback");
                return forwardConnection;
            }

            return null;
        }


        /// <summary>
        /// Finds the optimal route between two platforms using direct database queries
        /// </summary>
        public async Task<RoutePlan> FindRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform, ApplicationDbContext dbContext)
        {
            if (sourcePlatform == null || destinationPlatform == null)
            {
                Console.WriteLine("[TrackGraph] Invalid platform objects provided");
                return null;
            }

            Console.WriteLine($"[TrackGraph] Platform routing: {sourcePlatform.Station?.Name}-{sourcePlatform.Name} -> {destinationPlatform.Station?.Name}-{destinationPlatform.Name}");

            try
            {
                // For now, use simple database query approach since the unified service needs all dependencies
                var routeSections = await FindSimplePlatformRouteAsync(dbContext, sourcePlatform, destinationPlatform);

                if (routeSections == null || !routeSections.Any())
                {
                    Console.WriteLine("[TrackGraph] No route found between platforms using direct pathfinder");
                    return null;
                }

                // Convert to RoutePlan format
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

                Console.WriteLine($"[TrackGraph] ✓ Route found with {edgePath.Count} edges: {string.Join(" -> ", routeSections)}");

                return new RoutePlan
                {
                    Path = edgePath,
                    TotalLength = edgePath.Sum(e => e.Length)
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrackGraph] Error finding platform route: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Legacy FindRoute method for backwards compatibility
        /// </summary>
        public RoutePlan FindRoute(Platforms sourcePlatform, Platforms destinationPlatform)
        {
            Console.WriteLine("[TrackGraph] WARNING: Using legacy FindRoute method. Use FindRouteAsync instead.");
            return null; // Force use of async version
        }

        /// <summary>
        /// Generates multiple key variations for platform matching
        /// </summary>
        private string[] GeneratePlatformKeyVariations(string stationName, string platformName)
        {
            if (string.IsNullOrEmpty(stationName) || string.IsNullOrEmpty(platformName))
                return new string[0];

            // Normalize station name - handle different variations
            var normalizedStation = stationName.ToLower()
                .Replace("felső", "felso")
                .Replace("alsó", "also");

            // Normalize platform name - handle different variations including Roman numerals
            var normalizedPlatform = platformName.ToLower()
                .Replace(" ", "")
                .Replace("-", "")
                .Replace("iii", "3")
                .Replace("ii", "2")
                .Replace("i", "1");

            return new[]
            {
                $"{normalizedStation}-{normalizedPlatform}",
                $"{stationName.ToLower()}-{platformName.ToLower()}",
                $"{normalizedStation}-{platformName.ToLower()}",
                $"{stationName.ToLower()}-{normalizedPlatform}",
                $"{normalizedStation}-{platformName}",  // Original station name with normalized platform
                $"{stationName}-{normalizedPlatform}"   // Normalized station with original platform
            };
        }

        /// <summary>
        /// Finds a platform node ID from multiple key variations
        /// </summary>
        private int FindPlatformNodeId(string[] keys)
        {
            foreach (var key in keys)
            {
                if (_platformToNodeId.TryGetValue(key, out var nodeId))
                {
                    Console.WriteLine($"[TrackGraph] ✓ Found platform mapping using key: '{key}' -> node {nodeId}");
                    return nodeId;
                }
            }
            return -1; // Not found
        }

        /// <summary>
        /// BFS algorithm to find shortest path between two nodes
        /// </summary>
        private RoutePlan FindRouteBFS(int sourceId, int destId)
        {
            if (sourceId == destId)
            {
                return new RoutePlan(); // Empty route for same station
            }

            var visited = new HashSet<int>();
            var queue = new Queue<QueueItem>();
            var parent = new Dictionary<int, EdgeInfo>();

            queue.Enqueue(new QueueItem { NodeId = sourceId, Path = new List<EdgeInfo>() });
            visited.Add(sourceId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (current.NodeId == destId)
                {
                    // Found destination, reconstruct path
                    var path = new List<EdgeInfo>();
                    var node = destId;

                    while (parent.ContainsKey(node) && node != sourceId)
                    {
                        path.Insert(0, parent[node]);
                        node = parent[node].SourceNodeId;
                    }

                    Console.WriteLine($"[TrackGraph] Route found with {path.Count} edges");
                    Console.WriteLine($"[TrackGraph] Path details:");
                    foreach (var edge in path)
                    {
                        Console.WriteLine($"  Edge {edge.SourceNodeId} -> {edge.TargetNodeId}: Switch={edge.SwitchName}, Position={edge.RequiredSwitchPosition}, Length={edge.Length}");
                    }
                    return new RoutePlan { Path = path, TotalLength = path.Sum(e => e.Length) };
                }

                if (!_edges.ContainsKey(current.NodeId))
                    continue;

                foreach (var edge in _edges[current.NodeId])
                {
                    if (!visited.Contains(edge.TargetId))
                    {
                        visited.Add(edge.TargetId);

                        var edgeInfo = new EdgeInfo
                        {
                            SourceNodeId = edge.SourceId,
                            TargetNodeId = edge.TargetId,
                            SwitchName = edge.SwitchName,
                            RequiredSwitchPosition = edge.RequiredSwitchPosition,
                            Length = edge.Length
                        };

                        parent[edge.TargetId] = edgeInfo;

                        var newPath = new List<EdgeInfo>(current.Path);
                        newPath.Add(edgeInfo);

                        queue.Enqueue(new QueueItem { NodeId = edge.TargetId, Path = newPath });
                    }
                }
            }

            Console.WriteLine("[TrackGraph] No route found between stations");
            return null; // No route found
        }

        /// <summary>
        /// Gets all stations mapped in the graph
        /// </summary>
  
        /// <summary>
        /// Validates the graph integrity
        /// </summary>
        public bool ValidateGraph(out List<string> errors)
        {
            errors = new List<string>();

            // Check if all platforms have valid node mappings
            foreach (var platformMapping in _platformToNodeId)
            {
                if (!_nodes.ContainsKey(platformMapping.Value))
                {
                    errors.Add($"Platform '{platformMapping.Key}' maps to non-existent node {platformMapping.Value}");
                }
            }

            // Check if all edges reference valid nodes
            foreach (var edgeList in _edges.Values)
            {
                foreach (var edge in edgeList)
                {
                    if (!_nodes.ContainsKey(edge.SourceId))
                    {
                        errors.Add($"Edge references non-existent source node {edge.SourceId}");
                    }
                    if (!_nodes.ContainsKey(edge.TargetId))
                    {
                        errors.Add($"Edge references non-existent target node {edge.TargetId}");
                    }
                }
            }

            return errors.Count == 0;
        }

        /// <summary>
        /// Checks if there's a direct connection between two sections
        /// Used by PathOptimizer for route optimization
        /// </summary>
        public bool HasDirectConnection(int sourceSectionId, int targetSectionId)
        {
            if (!_edges.ContainsKey(sourceSectionId))
                return false;

            return _edges[sourceSectionId].Any(edge => edge.TargetId == targetSectionId);
        }

        /// <summary>
        /// Gets all outgoing connections from a section
        /// </summary>
        public List<int> GetOutgoingConnections(int sectionId)
        {
            if (!_edges.ContainsKey(sectionId))
                return new List<int>();

            return _edges[sectionId].Select(edge => edge.TargetId).ToList();
        }

        /// <summary>
        /// Gets the required switch configuration for moving from source to target
        /// </summary>
        public (string SwitchName, string RequiredPosition)? GetSwitchForConnection(int sourceSectionId, int targetSectionId)
        {
            if (!_edges.ContainsKey(sourceSectionId))
                return null;

            var edge = _edges[sourceSectionId].FirstOrDefault(e => e.TargetId == targetSectionId);
            if (edge != null && !string.IsNullOrEmpty(edge.SwitchName))
            {
                return (edge.SwitchName, edge.RequiredSwitchPosition);
            }

            return null;
        }
    }

    /// <summary>
    /// Represents a node in the track graph (track subsection)
    /// </summary>
    internal class TrackNode
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public Speed AllowedSpeed { get; set; }
    }

    /// <summary>
    /// Represents an edge in the track graph (track connection)
    /// </summary>
    internal class TrackEdge
    {
        public int SourceId { get; set; }
        public int TargetId { get; set; }
        public string SwitchName { get; set; }
        public string RequiredSwitchPosition { get; set; }
        public double Length { get; set; }
    }

    /// <summary>
    /// Queue item for BFS traversal
    /// </summary>
    internal class QueueItem
    {
        public int NodeId { get; set; }
        public List<EdgeInfo> Path { get; set; }
    }

    /// <summary>
    /// Information about a track edge in a route
    /// </summary>
    public class EdgeInfo
    {
        public int SourceNodeId { get; set; }
        public int TargetNodeId { get; set; }
        public string SwitchName { get; set; }
        public string RequiredSwitchPosition { get; set; }
        public double Length { get; set; }
    }

    /// <summary>
    /// Represents a complete route plan between stations
    /// </summary>
    public class RoutePlan
    {
        public List<EdgeInfo> Path { get; set; } = new List<EdgeInfo>();
        public double TotalLength { get; set; }

        /// <summary>
        /// Gets the unique switches that need to be configured for this route
        /// </summary>
        public List<SwitchConfiguration> GetSwitchConfigurations()
        {
            var switchConfigs = new Dictionary<string, string>();

            foreach (var edge in Path)
            {
                if (!string.IsNullOrEmpty(edge.SwitchName) && !string.IsNullOrEmpty(edge.RequiredSwitchPosition))
                {
                    switchConfigs[edge.SwitchName] = edge.RequiredSwitchPosition;
                }
            }

            return switchConfigs.Select(kvp => new SwitchConfiguration(kvp.Key, kvp.Value)).ToList();
        }

        public bool HasPath => Path.Any();
    }

    /// <summary>
    /// Switch configuration for a route (existing class maintained for compatibility)
    /// </summary>
    public class SwitchConfiguration
    {
        public string SwitchName { get; set; }
        public string Position { get; set; } // "straight" or "turnout"

        public SwitchConfiguration(string switchName, string position)
        {
            SwitchName = switchName;
            Position = position;
        }
    }
}