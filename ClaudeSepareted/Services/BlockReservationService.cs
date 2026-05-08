using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Logical traffic cop for N-block look-ahead reservation system.
    ///
    /// This service manages LOGICAL reservations (intent) separate from PHYSICAL occupancy.
    /// It implements just-in-time block reservation where trains reserve blocks as they
    /// approach them, not before starting the journey.
    ///
    /// Key Features:
    /// - Logical block reservation (SectionName -> TrainName mapping)
    /// - Collision detection by checking both physical occupancy and logical reservation
    /// - Rolling block release as trains progress through their route
    /// - Supports dynamic traffic flow without deadlocking on global route locks
    ///
    /// Physical vs Logical:
    /// - PHYSICAL: TrackOccupancyService tracks where trains actually are right now
    /// - LOGICAL: BlockReservationService tracks where trains INTEND to go next
    /// </summary>
    public class BlockReservationService
    {
        #region Dependencies

        private readonly TrackOccupancyService _occupancyService;
        private readonly ILogger<BlockReservationService> _logger;
        private readonly FileLoggingService? _fileLogger;

        #endregion

        #region State Management

        /// <summary>
        /// Logical reservation mapping: SectionName -> TrainName
        /// Tracks which train has reserved which block for next movement.
        /// This is SEPARATE from physical occupancy - a train can reserve
        /// a block before physically entering it.
        /// </summary>
        private readonly ConcurrentDictionary<string, string> _logicalReservations;

        /// <summary>
        /// Lock object for synchronizing reservation operations to prevent TOCTOU race conditions.
        /// Ensures atomic check-then-act operations for block reservations.
        /// </summary>
        private readonly object _reservationLock = new object();

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new BlockReservationService instance
        /// </summary>
        /// <param name="occupancyService">Physical occupancy tracking service</param>
        /// <param name="logger">Logger for diagnostic messages</param>
        public BlockReservationService(
            TrackOccupancyService occupancyService,
            ILogger<BlockReservationService> logger = null,
            FileLoggingService fileLogger = null)
        {
            _occupancyService = occupancyService ?? throw new ArgumentNullException(nameof(occupancyService));
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<BlockReservationService>.Instance;
            _fileLogger = fileLogger;
            _logicalReservations = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the block reservation system by loading all logical blocks from the database.
        ///
        /// This service manages LOGICAL blocks (Sections), not physical sensors (SubSections).
        /// The initialization loads all active Sections to establish the logical routing topology.
        ///
        /// Physical vs Logical:
        /// - PHYSICAL: SubSections (hardware sensors) managed by TrackOccupancyService
        /// - LOGICAL: Sections (routing blocks) managed by BlockReservationService
        /// </summary>
        /// <param name="dbContext">Database context to load Sections from</param>
        public async Task InitializeAsync(ApplicationDbContext dbContext)
        {
            if (dbContext == null)
            {
                throw new ArgumentNullException(nameof(dbContext));
            }

            try
            {
                // PURIFIED: Service now speaks strictly Logical Blocks
                // Load all active Sections (logical routing blocks), not SubSections (physical sensors)
                var sections = await dbContext.Sections.Where(s => s.IsActive).ToListAsync();
                foreach (var section in sections)
                {
                    // Initialize all logical blocks as unreserved
                    // This establishes the complete routing topology for the reservation system
                    _logicalReservations.TryAdd(section.Name, null);
                }

                _logger.LogInformation(
                    "🚦 BLOCK RESERVATION INITIALIZED: {SectionCount} logical blocks loaded for reservation management",
                    sections.Count);
                _fileLogger?.Log($"[BLOCK RESERVATION] INITIALIZED: Loaded {sections.Count} logical blocks (Sections) for reservation management.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize BlockReservationService - [BlockReservationService]");
                throw;
            }
        }

        #endregion

        #region Block Reservation Methods

        /// <summary>
        /// Attempts to reserve a block for a specific train.
        ///
        /// This implements the N-block look-ahead reservation system:
        /// - Checks if the block is PHYSICALLY occupied by another train
        /// - Checks if the block is LOGICALLY reserved by another train
        /// - If both checks pass, reserves the block for the requesting train
        ///
        /// Returns FALSE if:
        /// - The section is physically occupied by a DIFFERENT train
        /// - The section is logically reserved by a DIFFERENT train
        ///
        /// Returns TRUE if:
        /// - The section is free (physically and logically)
        /// - The section is already owned by this train (renewal)
        ///
        /// Thread Safety:
        /// This method is now synchronized using _reservationLock to prevent TOCTOU race conditions.
        /// All checks and the reservation assignment are performed atomically within the lock.
        /// </summary>
        /// <param name="sectionName">The section name to reserve</param>
        /// <param name="trainName">The train requesting the reservation</param>
        /// <returns>True if reservation succeeded, false if blocked</returns>
        public bool TryReserveBlock(string sectionName, string trainName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
            {
                _logger.LogWarning("TryReserveBlock called with null/empty section name for train {TrainName}", trainName);
                return false;
            }

            if (string.IsNullOrWhiteSpace(trainName))
            {
                _logger.LogWarning("TryReserveBlock called with null/empty train name for section {Section}", sectionName);
                return false;
            }

            lock (_reservationLock)
            {
                try
                {
                    // Check 1: Is the section PHYSICALLY occupied by a DIFFERENT train?
                    // Use atomic TryGetTrainInLogicalBlock to prevent TOCTOU race condition
                    if (_occupancyService.TryGetTrainInLogicalBlock(sectionName, out var physicalOccupant))
                    {
                        // Check if the physical occupant is this train (self-occupancy is OK)
                        if (!string.Equals(physicalOccupant, trainName, StringComparison.OrdinalIgnoreCase))
                        {
                            _fileLogger?.Log($"[BLOCK RESERVATION] DENIED: {trainName} cannot reserve {sectionName}. Physically occupied by {physicalOccupant ?? "UNKNOWN"}.");
                            _logger.LogDebug(
                                "🚫 BLOCK RESERVATION FAILED: {Section} is physically occupied by {OtherTrain}. Requested by {TrainName}",
                                sectionName, physicalOccupant ?? "UNKNOWN", trainName);
                            return false;
                        }
                    }

                    // Check 2: Is the section LOGICALLY reserved by a DIFFERENT train?
                    if (_logicalReservations.TryGetValue(sectionName, out var logicalOwner))
                    {
                        // Check if the logical owner is this train (renewal is OK)
                        if (!string.Equals(logicalOwner, trainName, StringComparison.OrdinalIgnoreCase))
                        {
                            _fileLogger?.Log($"[BLOCK RESERVATION] DENIED: {trainName} cannot reserve {sectionName}. Logically reserved by {logicalOwner}.");
                            _logger.LogDebug(
                                "🚫 BLOCK RESERVATION FAILED: {Section} is logically reserved by {OtherTrain}. Requested by {TrainName}",
                                sectionName, logicalOwner, trainName);
                            return false;
                        }
                    }

                    // Check 3: Reserve the block (add or update)
                    // Safe to use indexer inside the lock
                    _logicalReservations[sectionName] = trainName;

                    _fileLogger?.Log($"[BLOCK RESERVATION] GRANTED: {sectionName} logically reserved for {trainName}.");
                    _logger.LogDebug(
                        "✅ BLOCK RESERVED: {Section} reserved for {TrainName} (logical)",
                        sectionName, trainName);

                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in TryReserveBlock for train {TrainName}, section {Section} - [BlockReservationService]",
                        trainName, sectionName);
                    return false;
                }
            }
        }

        /// <summary>
        /// Releases a logical block reservation.
        ///
        /// Only releases the reservation if the requesting train owns it.
        /// This prevents trains from releasing reservations held by other trains.
        ///
        /// Thread Safety:
        /// Uses _reservationLock to prevent TOCTOU race conditions with reservation checks.
        /// Ensures atomic check-and-remove operation.
        /// </summary>
        /// <param name="sectionName">The section to release</param>
        /// <param name="trainName">The train requesting the release</param>
        public void ReleaseBlock(string sectionName, string trainName)
        {
            if (string.IsNullOrWhiteSpace(sectionName) || string.IsNullOrWhiteSpace(trainName))
            {
                _logger.LogWarning("ReleaseBlock called with invalid parameters: Section={Section}, Train={Train}",
                    sectionName, trainName);
                return;
            }

            lock (_reservationLock)
            {
                try
                {
                    // Use atomic conditional removal to prevent race condition
                    // This only removes if the key-value pair matches exactly
                    var kvp = new KeyValuePair<string, string>(sectionName, trainName);
                    bool removed = ((ICollection<KeyValuePair<string, string>>)_logicalReservations).Remove(kvp);

                    if (removed)
                    {
                        _logger.LogDebug("🔓 BLOCK RELEASED: {Section} released by {TrainName}",
                            sectionName, trainName);
                    }
                    else
                    {
                        // Check if section exists but belongs to another train
                        if (_logicalReservations.TryGetValue(sectionName, out var owner))
                        {
                            _logger.LogDebug(
                                "❌ BLOCK RELEASE FAILED: {Section} is reserved by {OtherTrain}, not {TrainName}",
                                sectionName, owner, trainName);
                        }
                        else
                        {
                            _logger.LogDebug("⚠️ BLOCK NOT RESERVED: {Section} was not reserved", sectionName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ReleaseBlock for train {TrainName}, section {Section} - [BlockReservationService]",
                        trainName, sectionName);
                }
            }
        }

        /// <summary>
        /// Releases ALL logical reservations held by a specific train.
        ///
        /// Use this when:
        /// - A train completes its journey
        /// - A train is removed from the layout
        /// - Emergency release of all train reservations
        ///
        /// Thread Safety:
        /// Uses _reservationLock to prevent TOCTOU race conditions during teardown.
        /// The entire find-and-release operation is performed atomically within the lock.
        /// </summary>
        /// <param name="trainName">The train whose reservations should be released</param>
        public void ReleaseAllForTrain(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
            {
                _logger.LogWarning("ReleaseAllForTrain called with null/empty train name");
                return;
            }

            lock (_reservationLock)
            {
                try
                {
                    // Find all sections reserved by this train
                    var sectionsReserved = _logicalReservations
                        .Where(kvp => string.Equals(kvp.Value, trainName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    var releasedCount = 0;

                    // Release each section using atomic removal
                    foreach (var kvp in sectionsReserved)
                    {
                        var removeKvp = new KeyValuePair<string, string>(kvp.Key, kvp.Value);
                        bool removed = ((ICollection<KeyValuePair<string, string>>)_logicalReservations).Remove(removeKvp);
                        if (removed)
                        {
                            releasedCount++;
                        }
                    }

                    _logger.LogInformation(
                        "🔓 ALL BLOCKS RELEASED: {TrainCount} reservations released for train {TrainName}",
                        releasedCount, trainName);
                    _fileLogger?.Log($"[BLOCK RESERVATION] RELEASED: {releasedCount} blocks released for {trainName} (Journey end/Reset).");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in ReleaseAllForTrain for train {TrainName} - [BlockReservationService]", trainName);
                }
            }
        }

        /// <summary>
        /// Gets the current logical owner of a block.
        ///
        /// Returns null if:
        /// - The section is not logically reserved
        /// - The section name is invalid
        /// </summary>
        /// <param name="sectionName">The section to query</param>
        /// <returns>The train name that holds the logical reservation, or null if free</returns>
        public string GetBlockOwner(string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
                return null;

            _logicalReservations.TryGetValue(sectionName, out var owner);
            return owner;
        }

        #endregion

        #region Diagnostic Methods

        /// <summary>
        /// Gets all current logical reservations.
        /// Useful for debugging and system monitoring.
        /// </summary>
        /// <returns>Dictionary mapping section names to train names</returns>
        public Dictionary<string, string> GetAllReservations()
        {
            return new Dictionary<string, string>(_logicalReservations);
        }

        /// <summary>
        /// Gets the total count of logical reservations.
        /// </summary>
        /// <returns>Number of reserved blocks</returns>
        public int GetReservationCount()
        {
            return _logicalReservations.Count;
        }

        /// <summary>
        /// Checks if a section is logically reserved (by any train).
        /// </summary>
        /// <param name="sectionName">The section to check</param>
        /// <returns>True if logically reserved, false otherwise</returns>
        public bool IsSectionReserved(string sectionName)
        {
            if (string.IsNullOrWhiteSpace(sectionName))
                return false;

            return _logicalReservations.ContainsKey(sectionName);
        }

        /// <summary>
        /// Gets all reservations held by a specific train.
        /// </summary>
        /// <param name="trainName">The train to query</param>
        /// <returns>List of section names reserved by this train</returns>
        public List<string> GetReservationsForTrain(string trainName)
        {
            if (string.IsNullOrWhiteSpace(trainName))
                return new List<string>();

            return _logicalReservations
                .Where(kvp => string.Equals(kvp.Value, trainName, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
        }

        #endregion
    }
}
