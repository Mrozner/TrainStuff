using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

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
        private readonly Dictionary<string, int> _nameToNodeId;

        public TrackGraph()
        {
            _nodes = new Dictionary<int, TrackNode>();
            _edges = new Dictionary<int, List<TrackEdge>>();
            _stationToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _platformToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _nameToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Loads the track graph from the database using Lookup_Section_NextSection tables
        /// </summary>
        public static async Task<TrackGraph> LoadFromDatabaseAsync(ApplicationDbContext dbContext, ILogger<TrackGraph> logger = null, FileLoggingService fileLogger = null)
        {
            var graph = new TrackGraph();

            try
            {
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

                foreach (var platform in platforms)
                {
                    if (platform.SubSection != null)
                    {
                        // Map station to subsection if not already mapped
                        if (platform.Station != null && !graph._stationToNodeId.ContainsKey(platform.Station.Name))
                        {
                            graph.MapStationToNode(platform.Station.Name, platform.SubSection.DB_ID);
                        }

                        // Map platform to subsection for platform-level routing
                        graph.MapPlatformToNode(platform.Station?.Name, platform.Name, platform.SubSection.DB_ID);
                    }
                }

                // Load connections from Lookup_Section_NextSection using raw SQL since this table might not be mapped in EF
                await LoadConnectionsFromLookupTables(dbContext, graph, logger, fileLogger);

                fileLogger?.Log($"[TRACK GRAPH] Topology built from database: {graph._nodes.Count} nodes loaded.");
                return graph;
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load track graph from database");
                throw;
            }
        }

        /// <summary>
        /// Loads track connections from Lookup_Section_NextSection tables
        /// </summary>
        private static async Task LoadConnectionsFromLookupTables(ApplicationDbContext dbContext, TrackGraph graph, ILogger<TrackGraph> logger = null, FileLoggingService fileLogger = null)
        {
            try
            {
                // Load enhanced connections with switch constraints from the view
                // This is CRITICAL for safety - if this view is missing, we MUST fail fast
                // to prevent operating trains without proper switch constraint enforcement
                var viewConnections = await dbContext.VLookupSectionNextSection
                    .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE NextSection_DB_ID IS NOT NULL")
                    .ToListAsync();

                // Group connections by source-target pairs to collect all switches for each edge
                var connectionGroups = viewConnections
                    .Where(c => c.NextSection_DB_ID.HasValue && !string.IsNullOrEmpty(c.SwitchConstraints))
                    .GroupBy(c => new { c.Section_DB_ID, c.NextSection_DB_ID, c.Direction });

                var enhancedConnections = 0;
                foreach (var connectionGroup in connectionGroups)
                {
                    var firstConnection = connectionGroup.First();
                    var allSwitchRequirements = new List<(string SwitchName, string RequiredPosition)>();

                    // Collect all switch configurations for this connection
                    foreach (var connection in connectionGroup)
                    {
                        var switchConstraints = ParseSwitchConstraints(connection.SwitchConstraints);
                        allSwitchRequirements.AddRange(switchConstraints);
                    }

                    // Add a single edge with all switch requirements
                    graph.AddEdge(
                        firstConnection.Section_DB_ID,
                        firstConnection.NextSection_DB_ID.Value,
                        allSwitchRequirements,
                        firstConnection.Direction,
                        1.0
                    );
                    enhancedConnections++;
                }

                logger?.LogInformation("Loaded {ConnectionCount} connections with switch constraints from database view", enhancedConnections);
                fileLogger?.Log($"[TRACK GRAPH] Loaded {enhancedConnections} connections with switch constraints.");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load track connections from lookup tables");
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
            _nameToNodeId[name] = id;
        }

        /// <summary>
        /// Maps a station name to a node ID
        /// </summary>
        private void MapStationToNode(string stationName, int nodeId)
        {
            if (string.IsNullOrEmpty(stationName)) return;

            // Add the original station name (dictionary handles case-insensitivity)
            _stationToNodeId[stationName] = nodeId;

            // Add common variations for Hungarian station names (manual character replacements)
            var stationLower = stationName.ToLower();
            if (stationLower.Contains("felso") || stationLower.Contains("felső"))
            {
                _stationToNodeId["felso"] = nodeId;
                _stationToNodeId["felső"] = nodeId;
                _stationToNodeId["upper"] = nodeId;
            }
            else if (stationLower.Contains("also") || stationLower.Contains("alsó"))
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

            var keys = GeneratePlatformKeyVariations(stationName, platformName);

            foreach (var key in keys)
            {
                if (!_platformToNodeId.ContainsKey(key))
                {
                    _platformToNodeId[key] = nodeId;
                }
            }
        }

        /// <summary>
        /// Adds an edge (track connection) to the graph
        /// </summary>
        private void AddEdge(int sourceId, int targetId, List<(string SwitchName, string RequiredPosition)> switchRequirements, bool direction, double length)
        {
            // Ensure both source and target nodes exist in the edges dictionary
            if (!_edges.ContainsKey(sourceId))
            {
                _edges[sourceId] = new List<TrackEdge>();
            }

            if (!_edges.ContainsKey(targetId))
            {
                _edges[targetId] = new List<TrackEdge>();
            }

            var edge = new TrackEdge
            {
                SourceId = sourceId,
                TargetId = targetId,
                SwitchRequirements = switchRequirements ?? new List<(string SwitchName, string RequiredPosition)>(),
                Length = length,
                Direction = direction
            };

            _edges[sourceId].Add(edge);
        }

        /// <summary>
        /// Adds an edge (track connection) to the graph with a single switch requirement
        /// Overload for backward compatibility
        /// </summary>
        private void AddEdge(int sourceId, int targetId, string switchName, string switchPosition, bool direction, double length)
        {
            var switchRequirements = new List<(string SwitchName, string RequiredPosition)>();
            if (!string.IsNullOrEmpty(switchName) && !string.IsNullOrEmpty(switchPosition))
            {
                switchRequirements.Add((switchName, switchPosition));
            }
            AddEdge(sourceId, targetId, switchRequirements, direction, length);
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
        /// Find optimal route between two subsection IDs using in-memory BFS pathfinding
        /// This is the primary pathfinding method that uses only in-memory graph data
        /// </summary>
        public RoutePlan FindRouteInMemory(int sourceSubSectionId, int destSubSectionId, bool? direction = null)
        {
            if (sourceSubSectionId == destSubSectionId)
            {
                return new RoutePlan(); // Empty route for same location
            }

            var visited = new HashSet<int>();
            var queue = new Queue<QueueItem>();

            queue.Enqueue(new QueueItem { NodeId = sourceSubSectionId, CurrentEdge = null, Parent = null });
            visited.Add(sourceSubSectionId);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (current.NodeId == destSubSectionId)
                {
                    // Reconstruct path backwards
                    var path = new List<EdgeInfo>();
                    var currNode = current;
                    while (currNode.Parent != null)
                    {
                        path.Insert(0, currNode.CurrentEdge);
                        currNode = currNode.Parent;
                    }
                    return new RoutePlan { Path = path, TotalLength = path.Sum(e => e.Length) };
                }

                if (!_edges.ContainsKey(current.NodeId)) continue;

                foreach (var edge in _edges[current.NodeId])
                {
                    if (!IsEdgeDirectionMatch(edge, direction)) continue;

                    if (!visited.Contains(edge.TargetId))
                    {
                        visited.Add(edge.TargetId);
                        var edgeInfo = new EdgeInfo
                        {
                            SourceNodeId = edge.SourceId,
                            TargetNodeId = edge.TargetId,
                            SwitchRequirements = new List<(string SwitchName, string RequiredPosition)>(edge.SwitchRequirements),
                            Length = edge.Length
                        };
                        queue.Enqueue(new QueueItem { NodeId = edge.TargetId, CurrentEdge = edgeInfo, Parent = current });
                    }
                }
            }

            return null; // No route found
        }

        /// <summary>
        /// Check if an edge matches the desired direction
        /// If no direction is specified, all edges are allowed
        /// </summary>
        private bool IsEdgeDirectionMatch(TrackEdge edge, bool? desiredDirection)
        {
            // If no direction is specified, allow the edge (undirected search)
            if (!desiredDirection.HasValue)
                return true;

            // The edge's direction is stored in the graph - it was loaded from the database
            // with the Direction field from Lookup_Section_NextSection
            return edge.Direction == desiredDirection.Value;
        }

        /// <summary>
        /// Find route between two platforms using in-memory pathfinding
        /// </summary>
        public RoutePlan FindPlatformRoute(Platforms sourcePlatform, Platforms destinationPlatform, bool direction)
        {
            if (sourcePlatform == null || destinationPlatform == null)
                return null;

            var sourceSectionId = sourcePlatform.SubSection_DB_ID;
            var destSectionId = destinationPlatform.SubSection_DB_ID;

            if (sourceSectionId <= 0 || destSectionId <= 0)
                return null;

            return FindRouteInMemory(sourceSectionId, destSectionId, direction);
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
        public List<(string SwitchName, string RequiredPosition)>? GetSwitchesForConnection(int sourceSectionId, int targetSectionId)
        {
            if (!_edges.ContainsKey(sourceSectionId))
                return null;

            var edge = _edges[sourceSectionId].FirstOrDefault(e => e.TargetId == targetSectionId);
            if (edge != null && edge.SwitchRequirements.Any())
            {
                return new List<(string SwitchName, string RequiredPosition)>(edge.SwitchRequirements);
            }

            return null;
        }

        /// <summary>
        /// Gets the node ID for a section name.
        /// Uses O(1) dictionary lookup instead of O(N) iteration.
        /// </summary>
        /// <param name="sectionName">The section name to search for</param>
        /// <returns>The node ID if found, -1 otherwise</returns>
        public int GetNodeIdByName(string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
                return -1;

            return _nameToNodeId.TryGetValue(sectionName, out var id) ? id : -1;
        }

        /// <summary>
        /// Checks if two sections are adjacent (directly connected) in either direction.
        /// This is a synchronous in-memory lookup that doesn't hit the database.
        /// </summary>
        /// <param name="section1Id">First section ID</param>
        /// <param name="section2Id">Second section ID</param>
        /// <returns>True if sections are directly connected, false otherwise</returns>
        public bool AreSectionsAdjacent(int section1Id, int section2Id)
        {
            // Check if there's a connection from section1 to section2
            if (_edges.ContainsKey(section1Id))
            {
                if (_edges[section1Id].Any(e => e.TargetId == section2Id))
                    return true;
            }

            // Check if there's a connection from section2 to section1
            if (_edges.ContainsKey(section2Id))
            {
                if (_edges[section2Id].Any(e => e.TargetId == section1Id))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Validates that a connection between two sections matches the requested direction.
        /// This is a synchronous in-memory lookup that doesn't hit the database.
        /// </summary>
        /// <param name="fromId">Source section ID</param>
        /// <param name="toId">Target section ID</param>
        /// <param name="direction">Requested direction (true = forward, false = reverse)</param>
        /// <returns>True if connection exists and direction matches, false otherwise</returns>
        public bool ValidateConnectionDirection(int fromId, int toId, bool direction)
        {
            if (!_edges.ContainsKey(fromId))
                return false;

            var edge = _edges[fromId].FirstOrDefault(e => e.TargetId == toId);
            if (edge == null)
                return false;

            // Check if the edge's direction matches the requested direction
            return edge.Direction == direction;
        }

        /// <summary>
        /// Gets the section name for a given node ID
        /// </summary>
        /// <param name="nodeId">The node ID to look up</param>
        /// <returns>The section name if found, null otherwise</returns>
        public string GetSectionNameById(int nodeId)
        {
            if (_nodes.TryGetValue(nodeId, out var node))
            {
                return node.Name;
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
        public List<(string SwitchName, string RequiredPosition)> SwitchRequirements { get; set; } = new List<(string SwitchName, string RequiredPosition)>();
        public double Length { get; set; }
        public bool Direction { get; set; } // true = forward, false = reverse
    }

    /// <summary>
    /// Queue item for BFS traversal
    /// </summary>
    internal class QueueItem
    {
        public int NodeId { get; set; }
        public EdgeInfo CurrentEdge { get; set; }
        public QueueItem Parent { get; set; }
    }
}