using System;
using System.Collections.Generic;
using System.Linq;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Direct platform-to-platform pathfinder using V_PlatformEntry database view
    /// Simplified approach that directly queries the database for platform connections
    /// </summary>
    public class DirectPlatformPathfinder
    {
        private readonly ApplicationDbContext _dbContext;

        public DirectPlatformPathfinder(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        /// <summary>
        /// Find direct route between two platforms using V_Lookup_Section_NextSection view
        /// </summary>
        public async Task<List<int>?> FindDirectRouteAsync(Platforms sourcePlatform, Platforms destinationPlatform)
        {
            try
            {
                // Validate input parameters
                if (sourcePlatform == null || destinationPlatform == null)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Invalid platform objects");
                    return null;
                }

                if (sourcePlatform.DB_ID <= 0 || destinationPlatform.DB_ID <= 0)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Invalid platform IDs");
                    return null;
                }

                Console.WriteLine($"[DirectPlatformPathfinder] Finding route: {sourcePlatform.Station?.Name}-{sourcePlatform.Name} -> {destinationPlatform.Station?.Name}-{destinationPlatform.Name}");

                // Validate database context
                if (_dbContext == null)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Database context is null");
                    return null;
                }

                var sourceSectionId = sourcePlatform.SubSection_DB_ID;
                var destSectionId = destinationPlatform.SubSection_DB_ID;

                // Validate section IDs
                if (sourceSectionId <= 0 || destSectionId <= 0)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Invalid section IDs: source={sourceSectionId}, dest={destSectionId}");
                    return null;
                }

                // Query V_Lookup_Section_NextSection for routing from source section with proper error handling
                List<VLookupSectionNextSection> connections;
                try
                {
                    connections = await _dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", sourceSectionId)
                        .ToListAsync();
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Database query error: {dbEx.Message}");
                    return null;
                }

                if (connections == null)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Database query returned null");
                    return null;
                }

                Console.WriteLine($"[DirectPlatformPathfinder] Found {connections.Count} connections from section {sourceSectionId}");

                // Build route using the view data
                var route = await BuildRouteFromSectionView(sourceSectionId, destSectionId, destinationPlatform.DB_ID);

                if (route != null && route.Any())
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] ✓ Found route: {string.Join(" -> ", route)}");
                    return route;
                }

                Console.WriteLine("[DirectPlatformPathfinder] No route found using V_Lookup_Section_NextSection");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DirectPlatformPathfinder] Error finding direct route: {ex.Message}");
                Console.WriteLine($"[DirectPlatformPathfinder] Stack trace: {ex.StackTrace}");
                return null;
            }
        }

        /// <summary>
        /// Build route using V_Lookup_Section_NextSection view
        /// </summary>
        private async Task<List<int>?> BuildRouteFromSectionView(int sourceSectionId, int destSectionId, int destinationPlatformId)
        {
            try
            {
                if (sourceSectionId <= 0 || destSectionId <= 0 || destinationPlatformId <= 0)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Invalid parameters for route building");
                    return null;
                }

                Console.WriteLine($"[DirectPlatformPathfinder] Building route from section {sourceSectionId} to {destSectionId}");

                if (_dbContext == null)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Database context is null in route builder");
                    return null;
                }

                var route = new List<int> { sourceSectionId };
                var currentSection = sourceSectionId;
                var visited = new HashSet<int> { sourceSectionId };
                var maxSteps = 20; // Prevent infinite loops - reduced from 6000 for safety
                var steps = 0;

                while (currentSection != destSectionId && steps < maxSteps)
                {
                    steps++;

                    // Get next sections from V_Lookup_Section_NextSection with error handling
                    List<VLookupSectionNextSection> connections;
                    try
                    {
                        connections = await _dbContext.VLookupSectionNextSection
                            .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", currentSection)
                            .ToListAsync();
                    }
                    catch (Exception dbEx)
                    {
                        Console.WriteLine($"[DirectPlatformPathfinder] Database query error in route builder: {dbEx.Message}");
                        break;
                    }

                    if (connections == null || !connections.Any())
                    {
                        Console.WriteLine($"[DirectPlatformPathfinder] No connections found from section {currentSection}");
                        break;
                    }

                    var nextSectionFound = false;

                    foreach (var connection in connections)
                    {
                        if (connection.NextSection_DB_ID.HasValue && !visited.Contains(connection.NextSection_DB_ID.Value))
                        {
                            var nextSection = connection.NextSection_DB_ID.Value;

                            // Check if this route leads towards destination platform
                            if (await DoesSectionLeadToPlatform(nextSection, destinationPlatformId))
                            {
                                route.Add(nextSection);
                                visited.Add(nextSection);
                                currentSection = nextSection;
                                nextSectionFound = true;

                                Console.WriteLine($"[DirectPlatformPathfinder] Step {steps}: {currentSection} -> {nextSection}");

                                if (currentSection == destSectionId)
                                {
                                    Console.WriteLine($"[DirectPlatformPathfinder] ✓ Reached destination section");
                                    return route;
                                }

                                break; // Take the first valid path
                            }
                        }
                    }

                    if (!nextSectionFound)
                    {
                        Console.WriteLine($"[DirectPlatformPathfinder] No valid next section found from {currentSection}");
                        break;
                    }
                }

                Console.WriteLine("[DirectPlatformPathfinder] Could not build complete route");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DirectPlatformPathfinder] Error building route: {ex.Message}");
                return null;
            }
        }

        
        /// <summary>
        /// Check if a section leads towards a specific platform
        /// </summary>
        private async Task<bool> DoesSectionLeadToPlatform(int sectionId, int platformId)
        {
            try
            {
                if (sectionId <= 0 || platformId <= 0)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Invalid parameters: sectionId={sectionId}, platformId={platformId}");
                    return false;
                }

                if (_dbContext == null)
                {
                    Console.WriteLine("[DirectPlatformPathfinder] Database context is null in platform validator");
                    return false;
                }

                // Get the destination platform
                Platforms platform;
                try
                {
                    platform = await _dbContext.Platforms
                        .FirstOrDefaultAsync(p => p.DB_ID == platformId);
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Error fetching platform {platformId}: {dbEx.Message}");
                    return false;
                }

                if (platform?.DB_ID == null || platform?.SubSection_DB_ID == null)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Platform {platformId} not found or has no subsection");
                    return false;
                }

                // If this section is the destination platform's subsection, we're there
                if (sectionId == platform.SubSection_DB_ID)
                    return true;

                // Check V_Lookup_Section_NextSection for routes that include destinations
                List<VLookupSectionNextSection> connections;
                try
                {
                    connections = await _dbContext.VLookupSectionNextSection
                        .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", sectionId)
                        .ToListAsync();
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] Error fetching connections for section {sectionId}: {dbEx.Message}");
                    return false;
                }

                if (connections == null || !connections.Any())
                {
                    Console.WriteLine($"[DirectPlatformPathfinder] No connections found for section {sectionId}");
                    return false;
                }

                foreach (var connection in connections)
                {
                    if (connection.NextSection_DB_ID.HasValue)
                    {
                        // Check if the destinations field contains our platform
                        if (!string.IsNullOrEmpty(connection.Destinations))
                        {
                            var destinations = connection.Destinations.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var dest in destinations)
                            {
                                if (int.TryParse(dest.Trim(), out int destPlatformId) && destPlatformId == platformId)
                                {
                                    Console.WriteLine($"[DirectPlatformPathfinder] Found destination {platformId} in destinations field");
                                    return true;
                                }
                            }
                        }

                        // Recursive check - does the next section lead to the platform?
                        if (await DoesSectionLeadToPlatform(connection.NextSection_DB_ID.Value, platformId))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DirectPlatformPathfinder] Error checking if section leads to platform: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Check if a route (ending at a section) leads to a specific platform (legacy method for compatibility)
        /// </summary>
        private async Task<bool> DoesRouteLeadToPlatform(int finalSectionId, int platformId)
        {
            return await DoesSectionLeadToPlatform(finalSectionId, platformId);
        }

        
        /// <summary>
        /// Get all available platforms for testing
        /// </summary>
        public async Task<List<Platforms>> GetAllPlatformsAsync()
        {
            return await _dbContext.Platforms
                .Include(p => p.Station)
                .Include(p => p.SubSection)
                .Where(p => p.IsActive)
                .ToListAsync();
        }
    }
}