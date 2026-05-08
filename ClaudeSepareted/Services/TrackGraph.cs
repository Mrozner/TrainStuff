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
        private readonly Dictionary<int, int> _subSectionIdToSectionId;

        // NEW: Stores the physical sensors in exact order based on travel direction (True = Dir1, False = Dir0)
        private readonly Dictionary<string, Dictionary<bool, List<string>>> _logicalBlockOrderedSensors;

        // NEW: Stores the explicit departure nodes for each platform
        private readonly Dictionary<int, (int Dir0NodeId, int Dir1NodeId)> _platformDepartureNodes;

        public TrackGraph()
        {
            _nodes = new Dictionary<int, TrackNode>();
            _edges = new Dictionary<int, List<TrackEdge>>();
            _stationToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _platformToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _nameToNodeId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _subSectionIdToSectionId = new Dictionary<int, int>();
            _logicalBlockOrderedSensors = new Dictionary<string, Dictionary<bool, List<string>>>(StringComparer.OrdinalIgnoreCase);
            _platformDepartureNodes = new Dictionary<int, (int, int)>();
        }

        /// <summary>
        /// Loads the track graph from the database using Lookup_Section_NextSection tables
        /// </summary>
        public static async Task<TrackGraph> LoadFromDatabaseAsync(ApplicationDbContext dbContext, ILogger<TrackGraph> logger = null, FileLoggingService fileLogger = null)
        {
            var graph = new TrackGraph();

            try
            {
                // Load all Logical Sections as nodes (The graph must map blocks, not physical sensors)
                var sections = await dbContext.Sections.Where(s => s.IsActive).ToListAsync();
                foreach (var section in sections)
                {
                    // Default to MEDIUM speed for the logical block node
                    graph.AddNode(section.DB_ID, section.Name, Speed.MEDIUM);
                }

                // --- NEW: Map physical SubSection sensors to their parent Section IDs ---
                var connection = dbContext.Database.GetDbConnection();
                bool connectionWasClosed = connection.State != System.Data.ConnectionState.Open;

                if (connectionWasClosed)
                {
                    await connection.OpenAsync();
                }

                try
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = @"
                            SELECT s.[Name], l.[Section_DB_ID], s.[DB_ID], l.[Direction], l.[Order]
                            FROM [dbo].[SubSections] s
                            INNER JOIN [dbo].[Lookup_Sections_SubSections] l ON s.[DB_ID] = l.[SubSection_DB_ID]
                            ORDER BY l.[Section_DB_ID], l.[Direction], l.[Order]";

                        using (var reader = await command.ExecuteReaderAsync())
                        {
                            while (await reader.ReadAsync())
                            {
                                string sensorName = reader.GetString(0);
                                int parentSectionId = reader.GetInt32(1);
                                int sensorId = reader.GetInt32(2);
                                bool isDir1 = reader.GetBoolean(3); // 1 = true, 0 = false

                                // Map the sensor name directly to the logical Section ID for routing lookups
                                graph._nameToNodeId[sensorName] = parentSectionId;

                                // NEW: Map SubSection ID to parent Section ID for platform routing
                                graph._subSectionIdToSectionId[sensorId] = parentSectionId;

                                // NEW: Map Logical Block to its strict ordered sequence of sensors
                                string sectionName = graph.GetSectionNameById(parentSectionId);
                                if (!string.IsNullOrEmpty(sectionName))
                                {
                                    if (!graph._logicalBlockOrderedSensors.ContainsKey(sectionName))
                                    {
                                        graph._logicalBlockOrderedSensors[sectionName] = new Dictionary<bool, List<string>>
                                        {
                                            { false, new List<string>() },
                                            { true, new List<string>() }
                                        };
                                    }
                                    // The SQL ORDER BY ensures these are added in the correct sequence
                                    graph._logicalBlockOrderedSensors[sectionName][isDir1].Add(sensorName);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (connectionWasClosed && connection.State == System.Data.ConnectionState.Open)
                    {
                        await connection.CloseAsync();
                    }
                }
                // ------------------------------------------------------------------------

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
        /// Loads track connections directly from the normalized base tables,
        /// bypassing the SQL View and string parsing entirely.
        /// </summary>
        private static async Task LoadConnectionsFromLookupTables(ApplicationDbContext dbContext, TrackGraph graph, ILogger<TrackGraph> logger = null, FileLoggingService fileLogger = null)
        {
            try
            {
                // Dictionary to hold grouped connections: Key is (Source, Target, Direction), Value is List of Switches
                var connectionGroups = new Dictionary<(int Source, int Target, bool Direction), List<SwitchRequirement>>();

                var connection = dbContext.Database.GetDbConnection();
                await dbContext.Database.OpenConnectionAsync();

                using (var command = connection.CreateCommand())
                {
                    // This C# query hits the normalized tables directly to build the graph
                    command.CommandText = @"
                        SELECT
                            lsn.[Section_DB_ID],
                            lsn.[NextSection_DB_ID],
                            lsn.[Direction],
                            sw.[Name] AS SwitchName,
                            lsw.[SwitchState]
                        FROM [dbo].[Lookup_Section_NextSection] lsn
                        LEFT JOIN [dbo].[Lookup_SectionNextSection_Switches] lsw ON lsn.[DB_ID] = lsw.[Lookup_DB_ID]
                        LEFT JOIN [dbo].[Switches] sw ON lsw.[Switch_DB_ID] = sw.[DB_ID]
                        WHERE lsn.[NextSection_DB_ID] IS NOT NULL AND lsn.[IsActive] = 1";

                    using (var reader = await command.ExecuteReaderAsync())
                    {
                        while (await reader.ReadAsync())
                        {
                            int sourceId = reader.GetInt32(reader.GetOrdinal("Section_DB_ID"));
                            int targetId = reader.GetInt32(reader.GetOrdinal("NextSection_DB_ID"));
                            bool direction = reader.GetBoolean(reader.GetOrdinal("Direction"));

                            var key = (sourceId, targetId, direction);
                            if (!connectionGroups.ContainsKey(key))
                            {
                                connectionGroups[key] = new List<SwitchRequirement>();
                            }

                            // If a switch exists for this connection, add it to the list
                            if (!reader.IsDBNull(reader.GetOrdinal("SwitchName")) && !reader.IsDBNull(reader.GetOrdinal("SwitchState")))
                            {
                                string switchName = reader.GetString(reader.GetOrdinal("SwitchName"));
                                string switchState = reader.GetString(reader.GetOrdinal("SwitchState"));

                                string normalizedState = switchState.ToLower() switch
                                {
                                    "straight" or "s" => "straight",
                                    "turned" or "t" or "turnout" => "turned",
                                    _ => switchState.ToLower()
                                };

                                connectionGroups[key].Add(new SwitchRequirement
                                {
                                    SwitchName = switchName,
                                    RequiredPosition = normalizedState
                                });
                            }
                        }
                    }
                }

                int totalConnections = 0;
                int enhancedConnections = 0;

                // Add all collected edges to the TrackGraph
                foreach (var kvp in connectionGroups)
                {
                    graph.AddEdge(kvp.Key.Source, kvp.Key.Target, kvp.Value, kvp.Key.Direction, 1.0);
                    totalConnections++;
                    if (kvp.Value.Any()) enhancedConnections++;
                }

                logger?.LogInformation("Loaded {TotalCount} total connections ({EnhancedCount} with switches) directly from DB tables via C#", totalConnections, enhancedConnections);
                fileLogger?.Log($"[TRACK GRAPH] Loaded {totalConnections} total connections ({enhancedConnections} with switches) bypassing SQL View.");
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to load track connections bypassing SQL View");
                throw;
            }
            finally
            {
                // Ensure connection is closed if we opened it manually
                if (dbContext.Database.GetDbConnection().State == System.Data.ConnectionState.Open)
                {
                    await dbContext.Database.CloseConnectionAsync();
                }
            }
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
        private void AddEdge(int sourceId, int targetId, List<SwitchRequirement> switchRequirements, bool direction, double length)
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
                SwitchRequirements = switchRequirements ?? new List<SwitchRequirement>(),
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
            var switchRequirements = new List<SwitchRequirement>();
            if (!string.IsNullOrEmpty(switchName) && !string.IsNullOrEmpty(switchPosition))
            {
                switchRequirements.Add(new SwitchRequirement
                {
                    SwitchName = switchName,
                    RequiredPosition = switchPosition
                });
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
        /// Find optimal route between two SECTION IDs using in-memory BFS pathfinding.
        /// CRITICAL: Parameters must be Logical Section IDs, not physical SubSection IDs.
        /// This is the primary pathfinding method that uses only in-memory graph data
        /// </summary>
        public RoutePlan FindRouteInMemory(SectionId sourceSectionId, SectionId destSectionId, bool? direction = null)
        {
            // REMOVED: Early exit for source == dest to allow for full scenic laps

            var visited = new HashSet<int>();
            var queue = new Queue<QueueItem>();

            queue.Enqueue(new QueueItem { NodeId = sourceSectionId.Value, CurrentEdge = null, Parent = null });
            visited.Add(sourceSectionId.Value);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                // MODIFIED VICTORY CONDITION: We must have actually moved (Parent != null) to claim we arrived.
                // This prevents the search from instantly succeeding on the very first step.
                if (current.NodeId == destSectionId.Value && current.Parent != null)
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

                    // CRITICAL BFS FIX: Normally we ignore visited nodes to prevent infinite loops.
                    // However, to complete a scenic lap, we MUST allow re-entry into the destination node.
                    if (!visited.Contains(edge.TargetId) || edge.TargetId == destSectionId.Value)
                    {
                        visited.Add(edge.TargetId);
                        var edgeInfo = new EdgeInfo
                        {
                            SourceNodeId = edge.SourceId,
                            TargetNodeId = edge.TargetId,
                            SwitchRequirements = new List<SwitchRequirement>(edge.SwitchRequirements),
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

            // Translate SubSection_DB_ID to Section_DB_ID for edge lookup
            if (!_subSectionIdToSectionId.TryGetValue(sourcePlatform.SubSection_DB_ID, out int sourceSectionId))
                return null;

            if (!_subSectionIdToSectionId.TryGetValue(destinationPlatform.SubSection_DB_ID, out int destSectionId))
                return null;

            if (sourceSectionId <= 0 || destSectionId <= 0)
                return null;

            return FindRouteInMemory(new SectionId(sourceSectionId), new SectionId(destSectionId), direction);
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
        public List<SwitchRequirement>? GetSwitchesForConnection(int sourceSectionId, int targetSectionId)
        {
            if (!_edges.ContainsKey(sourceSectionId))
                return null;

            var edge = _edges[sourceSectionId].FirstOrDefault(e => e.TargetId == targetSectionId);
            if (edge != null && edge.SwitchRequirements.Any())
            {
                return new List<SwitchRequirement>(edge.SwitchRequirements);
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
        /// Translates a physical Sensor ID into its parent Logical Block ID.
        /// </summary>
        public SectionId? GetSectionIdBySubSectionId(SubSectionId subSectionId)
        {
            if (_subSectionIdToSectionId.TryGetValue(subSectionId.Value, out var sectionId))
            {
                return new SectionId(sectionId);
            }
            return null;
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

        /// <summary>
        /// Gets all physical sensor names mapped to a logical block.
        /// </summary>
        public List<string> GetPhysicalSensorsForLogicalBlock(string logicalBlockName)
        {
            if (string.IsNullOrWhiteSpace(logicalBlockName) || !_logicalBlockOrderedSensors.TryGetValue(logicalBlockName, out var dirMap))
                return new List<string>();

            // Combine both directions and deduplicate to get all physical sensors for standard occupancy checks
            return dirMap[false].Concat(dirMap[true]).Distinct().ToList();
        }

        /// <summary>
        /// Gets the exact departure node ID for a platform based on physical train direction
        /// </summary>
        public int? GetDepartureNodeForPlatform(int platformId, bool direction)
        {
            if (_platformDepartureNodes.TryGetValue(platformId, out var nodes))
            {
                return direction ? nodes.Dir1NodeId : nodes.Dir0NodeId;
            }
            return null;
        }

        /// <summary>
        /// Gets the ordered sequence of sensors for a block based on direction
        /// </summary>
        public List<string> GetOrderedSensors(string logicalBlockName, bool direction)
        {
            if (_logicalBlockOrderedSensors.TryGetValue(logicalBlockName, out var dirMap))
            {
                return dirMap[direction];
            }
            return new List<string>();
        }

        /// <summary>
        /// Checks if a target node is reachable from a start node within a maximum depth,
        /// strictly adhering to the physical travel direction. Used for ghost-train recovery.
        /// </summary>
        public bool IsReachableWithin(int startNodeId, int targetNodeId, int maxDepth, bool direction)
        {
            if (startNodeId == targetNodeId) return true;

            var queue = new Queue<(int NodeId, int Depth)>();
            var visited = new HashSet<int>();

            queue.Enqueue((startNodeId, 0));
            visited.Add(startNodeId);

            while(queue.Count > 0)
            {
                var current = queue.Dequeue();

                if (current.Depth >= maxDepth) continue;
                if (!_edges.ContainsKey(current.NodeId)) continue;

                foreach(var edge in _edges[current.NodeId])
                {
                    if (edge.Direction != direction) continue;
                    if (edge.TargetId == targetNodeId) return true;

                    if (!visited.Contains(edge.TargetId))
                    {
                        visited.Add(edge.TargetId);
                        queue.Enqueue((edge.TargetId, current.Depth + 1));
                    }
                }
            }
            return false;
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
        public List<SwitchRequirement> SwitchRequirements { get; set; } = new List<SwitchRequirement>();
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