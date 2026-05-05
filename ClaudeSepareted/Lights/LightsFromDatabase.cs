using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ClaudeSepareted.DataAccess;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Lights
{
    /// <summary>
    /// Calculates and controls railway model network's light signals based on MÁV F.1 signaling rules.
    /// Reads speed data from the database and forwards it to the LightController.
    ///
    /// Operating Logic:
    /// - Queries the database for all active adjacency graphs (V_Lookup_Section_NextSection)
    /// - For each section, determines the signal state based on AllowedSpeed values
    /// - Maps speed strings to ASCII codes expected by the hardware
    /// - Calls LightController.SetLightSpeedAsync() to update physical signals
    ///
    /// Separation of Concerns:
    /// This class is NOT responsible for calculating train occupancies, pathfinding, or block reservations.
    /// It exclusively sets signals based on whatever is currently stored in the database's AllowedSpeed fields.
    /// </summary>
    public class LightsFromDatabase
    {
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly LightController _lightController;

        public LightsFromDatabase(
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            LightController lightController)
        {
            _dbContextFactory = dbContextFactory;
            _lightController = lightController;
        }

        /// <summary>
        /// Initializes all signals when the application starts.
        /// Should be called during application startup after database is ready.
        /// </summary>
        public async Task InitializeSignalsAsync(CancellationToken cancellationToken = default)
        {
            Console.WriteLine("[LightsFromDatabase] Initializing all signals from database...");
            await UpdateSignalsFromDatabaseAsync(cancellationToken);
            Console.WriteLine("[LightsFromDatabase] Signal initialization completed.");
        }

        /// <summary>
        /// Public method that can be called when the database changes to re-read the tables and update the physical signals.
        /// Queries the database for all active adjacency graphs and updates the light signals accordingly.
        ///
        /// NEW LOGIC (Forward-Looking + Bidirectional):
        /// - Treats each VLookupSectionNextSection entry as a signal boundary
        /// - beforeSection = entry.Section_DB_ID (section BEFORE the signal)
        /// - currentSection = entry.NextSection_DB_ID (section the signal PROTECTS)
        /// - currentSpeedAscii = based on currentSection's AllowedSpeed
        /// - nextSpeedAscii = looks ahead to find the section after currentSection
        /// - For each valid connection, calls SetLightSpeedAsync TWICE (dir: true and dir: false)
        /// - This ensures ALL physical hardware signals are initialized regardless of logical direction
        /// </summary>
        public async Task UpdateSignalsFromDatabaseAsync(CancellationToken cancellationToken = default)
        {
            using var dbContext = _dbContextFactory.CreateDbContext();

            Console.WriteLine("[LightsFromDatabase] Reading track topology from database...");

            // 1. Query all active adjacency graphs from the database view
            var allAdjacencyEntries = await dbContext.VLookupSectionNextSection
                .AsNoTracking()
                .ToListAsync(cancellationToken);

            Console.WriteLine($"[LightsFromDatabase] Found {allAdjacencyEntries.Count} adjacency entries.");

            if (!allAdjacencyEntries.Any())
            {
                Console.WriteLine("[LightsFromDatabase] WARNING: No adjacency entries found in database. No signals will be updated.");
                return;
            }

            // 2. Load all necessary data in memory for efficient processing
            var allSectionIds = allAdjacencyEntries
                .Select(e => e.Section_DB_ID)
                .Distinct()
                .ToList();

            var allNextSectionIds = allAdjacencyEntries
                .Where(e => e.NextSection_DB_ID.HasValue)
                .Select(e => e.NextSection_DB_ID.Value)
                .Distinct()
                .ToList();

            var allRelevantSectionIds = allSectionIds.Concat(allNextSectionIds).Distinct().ToList();

            // Get all SubSections for these sections via LookupSectionsSubSections
            var sectionSubSectionMappings = await dbContext.LookupSectionsSubSections
                .AsNoTracking()
                .Where(l => allRelevantSectionIds.Contains(l.Section_DB_ID))
                .ToListAsync(cancellationToken);

            var allSubSectionIds = sectionSubSectionMappings
                .Select(m => m.SubSection_DB_ID)
                .Distinct()
                .ToList();

            // Get all SubSections with their speed data
            var allSubSections = await dbContext.SubSections
                .AsNoTracking()
                .Where(s => allSubSectionIds.Contains(s.DB_ID))
                .ToListAsync(cancellationToken);

            // Get all Sections for name lookup
            var allSections = await dbContext.Sections
                .AsNoTracking()
                .Where(s => allRelevantSectionIds.Contains(s.DB_ID))
                .ToListAsync(cancellationToken);

            Console.WriteLine($"[LightsFromDatabase] Loaded {allSubSections.Count} subsections and {allSections.Count} sections.");

            // 3. Build lookup dictionaries for efficient access
            var sectionToSubSectionsMap = new Dictionary<int, List<SubSections>>();
            foreach (var mapping in sectionSubSectionMappings)
            {
                if (!sectionToSubSectionsMap.ContainsKey(mapping.Section_DB_ID))
                {
                    sectionToSubSectionsMap[mapping.Section_DB_ID] = new List<SubSections>();
                }

                var subSection = allSubSections.FirstOrDefault(s => s.DB_ID == mapping.SubSection_DB_ID);
                if (subSection != null)
                {
                    sectionToSubSectionsMap[mapping.Section_DB_ID].Add(subSection);
                }
            }

            var sectionNameMap = allSections.ToDictionary(s => s.DB_ID, s => s.Name);
            var subSectionNameMap = allSubSections.ToDictionary(s => s.DB_ID, s => s.Name);

            // 4. Process each adjacency entry with FORWARD-LOOKING logic
            int signalsSet = 0;
            int signalsSkipped = 0;

            foreach (var entry in allAdjacencyEntries)
            {
                try
                {
                    // NEW PERSPECTIVE: Treat this entry as the signal boundary
                    // beforeSection = entry.Section_DB_ID (section BEFORE the signal)
                    // currentSection = entry.NextSection_DB_ID (section the signal PROTECTS)

                    // Skip entries without a next section - these represent track termini where no signal is needed
                    if (!entry.NextSection_DB_ID.HasValue)
                    {
                        Console.WriteLine($"[LightsFromDatabase] INFO: Entry {entry.DB_ID} has no next section (track terminus). Skipping.");
                        signalsSkipped++;
                        continue;
                    }

                    int beforeSectionId = entry.Section_DB_ID;
                    int currentSectionId = entry.NextSection_DB_ID.Value;

                    // Verify currentSection has subsections (the section we're protecting)
                    if (!sectionToSubSectionsMap.ContainsKey(currentSectionId) ||
                        !sectionToSubSectionsMap[currentSectionId].Any())
                    {
                        Console.WriteLine($"[LightsFromDatabase] WARNING: No subsections found for current section {currentSectionId}. Skipping.");
                        signalsSkipped++;
                        continue;
                    }

                    var currentSubSections = sectionToSubSectionsMap[currentSectionId];
                    var currentSubSection = currentSubSections.First();
                    string currentSectionName = subSectionNameMap[currentSubSection.DB_ID];

                    // Determine currentSpeedAscii based on currentSection's AllowedSpeed
                    int currentSpeedAscii = MapSpeedToAscii(currentSubSection.AllowedSpeed);

                    // Look ahead to find nextSpeedAscii
                    // Find a future entry where its Section_DB_ID equals currentSectionId AND matches entry.Direction
                    int nextSpeedAscii = 48; // Default to STOP ('0')

                    var subsequentEntries = allAdjacencyEntries
                        .Where(e => e.Section_DB_ID == currentSectionId &&
                                    e.NextSection_DB_ID.HasValue &&
                                    e.Direction == entry.Direction)
                        .ToList();

                    if (subsequentEntries.Any())
                    {
                        // Found a subsequent section - get its speed
                        var nextSectionId = subsequentEntries.First().NextSection_DB_ID.Value;

                        if (sectionToSubSectionsMap.ContainsKey(nextSectionId) &&
                            sectionToSubSectionsMap[nextSectionId].Any())
                        {
                            var nextSubSection = sectionToSubSectionsMap[nextSectionId].First();
                            nextSpeedAscii = MapSpeedToAscii(nextSubSection.AllowedSpeed);
                        }
                    }
                    // If no subsequent entry or no subsections found, nextSpeedAscii remains 48 (STOP - dead end ahead)

                    // Resolve beforeSectionName for the signal position
                    string beforeSectionName;

                    if (sectionToSubSectionsMap.ContainsKey(beforeSectionId) &&
                        sectionToSubSectionsMap[beforeSectionId].Any())
                    {
                        var beforeSubSection = sectionToSubSectionsMap[beforeSectionId].First();
                        beforeSectionName = subSectionNameMap[beforeSubSection.DB_ID];
                    }
                    else
                    {
                        // Fallback: Use a safe placeholder name to prevent null exceptions
                        beforeSectionName = $"UNKNOWN_SECTION_{beforeSectionId}";
                        Console.WriteLine($"[LightsFromDatabase] WARNING: Could not resolve name for beforeSection {beforeSectionId}. Using fallback: {beforeSectionName}");
                    }

                    // BIDIRECTIONAL INITIALIZATION:
                    // Call SetLightSpeedAsync TWICE for each valid connection pair.
                    // Once with dir: true, once with dir: false.
                    // The underlying ObjectsLibrary will determine if a physical light exists for that direction.
                    // This ensures ALL physical hardware signals on the layout are initialized at startup.

                    Console.WriteLine($"[LightsFromDatabase] Setting bidirectional signal: {beforeSectionName} -> {currentSectionName} | Current Speed: {currentSpeedAscii} ('{((char)currentSpeedAscii)}'), Next Speed: {nextSpeedAscii} ('{((char)nextSpeedAscii)}')");

                    // Direction: TRUE
                    try
                    {
                        await _lightController.SetLightSpeedAsync(
                            Section: currentSectionName,
                            beforeSection: beforeSectionName,
                            dir: true,
                            currentSpeedAscii: currentSpeedAscii,
                            nextSpeedAscii: nextSpeedAscii,
                            cancellationToken: cancellationToken
                        );
                        signalsSet++;
                        Console.WriteLine($"[LightsFromDatabase] ✓ Set signal for dir=true: {beforeSectionName} -> {currentSectionName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LightsFromDatabase] WARNING: Failed to set signal for dir=true: {ex.Message}");
                    }

                    // Direction: FALSE
                    try
                    {
                        await _lightController.SetLightSpeedAsync(
                            Section: currentSectionName,
                            beforeSection: beforeSectionName,
                            dir: false,
                            currentSpeedAscii: currentSpeedAscii,
                            nextSpeedAscii: nextSpeedAscii,
                            cancellationToken: cancellationToken
                        );
                        signalsSet++;
                        Console.WriteLine($"[LightsFromDatabase] ✓ Set signal for dir=false: {beforeSectionName} -> {currentSectionName}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[LightsFromDatabase] WARNING: Failed to set signal for dir=false: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[LightsFromDatabase] ERROR processing entry {entry.DB_ID}: {ex.Message}");
                    signalsSkipped++;
                }
            }

            Console.WriteLine($"[LightsFromDatabase] Signal update completed. Signals set: {signalsSet}, Signals skipped: {signalsSkipped}.");
        }

        /// <summary>
        /// Maps speed enum values to ASCII codes expected by the hardware.
        ///
        /// Mapping:
        /// 48 ('0') = 0 km/h (Stop / Red) - STOP
        /// 49 ('1') = 40 km/h (Reduced speed) - SLOW
        /// 50 ('2') = 80 km/h (Medium speed) - MEDIUM
        /// 51 ('3') = 120 km/h (High speed) - HIGH
        /// 52 ('4') = Max speed - MAX (if present in database)
        /// </summary>
        private int MapSpeedToAscii(Speed speed)
        {
            return speed switch
            {
                Speed.ZERO => 48,     // '0'
                Speed.SLOW => 49,     // '1'
                Speed.MEDIUM => 50,   // '2'
                Speed.HIGH => 51,     // '3'
                Speed.MAX => 52,
                _ => 48               // Default to STOP for unknown values
            };
        }

        /// <summary>
        /// Maps speed string values from the database to ASCII codes.
        /// This overload handles cases where speed might be stored as a string or extended values like "MAX".
        /// </summary>
        private int MapSpeedToAscii(string speedString)
        {
            if (string.IsNullOrWhiteSpace(speedString))
            {
                return 48; // Default to STOP
            }

            // Normalize the string for comparison
            var normalizedSpeed = speedString.Trim().ToUpperInvariant();

            return normalizedSpeed switch
            {
                "STOP" => 48,        // '0'
                "SLOW" => 49,        // '1'
                "MEDIUM" => 50,      // '2'
                "HIGH" => 51,        // '3'
                "MAX" => 52,         // '4'
                _ => 48              // Default to STOP for unknown values
            };
        }
    }
}
