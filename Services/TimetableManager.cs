using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.Repositories;

namespace TrainControlSystem.Services
{
    /// <summary>
    /// Manages timetable operations and scheduling
    /// </summary>
    public class TimetableManager
    {
        private readonly TimetableRepository _repository;
        private readonly TrackManager _trackManager;
        private readonly SystemConfiguration _config;
        private readonly ILogger<TimetableManager> _logger;
        private readonly object _lockObject = new object();

        public List<TimetableEntries> Entries { get; private set; } = new List<TimetableEntries>();

        public TimetableManager(
            TimetableRepository repository,
            TrackManager trackManager,
            SystemConfiguration config,
            ILogger<TimetableManager> logger)
        {
            _repository = repository;
            _trackManager = trackManager;
            _config = config;
            _logger = logger;

            LoadEntries();
        }

        /// <summary>
        /// Loads timetable entries from repository
        /// </summary>
        public void LoadEntries()
        {
            lock (_lockObject)
            {
                Entries = _repository.GetTimetableEntries();
                _logger.LogInformation($"Loaded {Entries.Count} timetable entries");
            }
        }

        /// <summary>
        /// Processes scheduled trains that need to start
        /// </summary>
        public async Task ProcessScheduledTrains()
        {
            lock (_lockObject)
            {
                var now = DateTime.Now;
                var entriesToStart = Entries.Where(e =>
                    e.EntryState == EntryState.Scheduled &&
                    e.StartDate.Date.Add(e.StartTime) <= now &&
                    e.StartDate.Date.Add(e.StartTime) >= now.AddMinutes(-30) // Don't start if too late
                ).ToList();

                foreach (var entry in entriesToStart)
                {
                    _logger.LogInformation($"Processing scheduled entry: {entry.EntryID} - Train {entry.Train?.Name} from {entry.SourceStation?.Name} to {entry.DestinationStation?.Name}");

                    // Update entry state
                    entry.EntryState = EntryState.InTransit;
                    _repository.UpdateEntry(entry);
                }

                if (entriesToStart.Any())
                {
                    _logger.LogInformation($"Processed {entriesToStart.Count} scheduled entries");
                }
            }
        }

        /// <summary>
        /// Adds a new timetable entry
        /// </summary>
        public bool AddTimetableEntry(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                // Validate stations exist
                var sourceStation = _trackManager.GetStation(entry.SourceStation_DB_ID);
                var destinationStation = _trackManager.GetStation(entry.DestinationStation_DB_ID);
                var train = _trackManager.Trains.FirstOrDefault(t => t.DB_ID == entry.Train_DB_ID);

                if (sourceStation == null)
                {
                    _logger.LogError($"Source station with ID {entry.SourceStation_DB_ID} not found");
                    return false;
                }

                if (destinationStation == null)
                {
                    _logger.LogError($"Destination station with ID {entry.DestinationStation_DB_ID} not found");
                    return false;
                }

                if (train == null)
                {
                    _logger.LogError($"Train with ID {entry.Train_DB_ID} not found");
                    return false;
                }

                // Set navigation properties
                entry.SourceStation = sourceStation;
                entry.DestinationStation = destinationStation;
                entry.Train = train;

                // Add to repository
                var success = _repository.AddEntry(entry);
                if (success)
                {
                    Entries.Add(entry);
                    _logger.LogInformation($"Added timetable entry: {entry.EntryID} - Train {train.Name} from {sourceStation.Name} to {destinationStation.Name}");
                }

                return success;
            }
        }

        /// <summary>
        /// Updates an existing timetable entry
        /// </summary>
        public void UpdateTimetableEntry(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                _repository.UpdateEntry(entry);

                // Update local entry if it exists
                var localEntry = Entries.FirstOrDefault(e => e.DB_ID == entry.DB_ID);
                if (localEntry != null)
                {
                    localEntry.EntryState = entry.EntryState;
                    localEntry.RouteState = entry.RouteState;
                    localEntry.ArrivedTime = entry.ArrivedTime;
                }

                _logger.LogInformation($"Updated timetable entry: {entry.EntryID}");
            }
        }

        /// <summary>
        /// Removes a timetable entry
        /// </summary>
        public void RemoveTimetableEntry(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                _repository.DeleteEntry(entry);
                Entries.Remove(entry);
                _logger.LogInformation($"Removed timetable entry: {entry.EntryID}");
            }
        }

        /// <summary>
        /// Gets active entries for a specific train
        /// </summary>
        public List<TimetableEntries> GetActiveEntriesForTrain(int trainId)
        {
            lock (_lockObject)
            {
                return Entries.Where(e => e.Train_DB_ID == trainId &&
                                        (e.EntryState == EntryState.Scheduled ||
                                         e.EntryState == EntryState.InTransit ||
                                         e.EntryState == EntryState.AtStation)).ToList();
            }
        }

        /// <summary>
        /// Gets next scheduled entry for a specific train
        /// </summary>
        public TimetableEntries? GetNextEntryForTrain(int trainId)
        {
            lock (_lockObject)
            {
                return Entries
                    .Where(e => e.Train_DB_ID == trainId &&
                               e.EntryState == EntryState.Scheduled)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .FirstOrDefault();
            }
        }

        /// <summary>
        /// Marks an entry as arrived
        /// </summary>
        public void MarkEntryAsArrived(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                entry.EntryState = EntryState.Arrived;
                entry.ArrivedTime = DateTime.Now;
                _repository.UpdateEntry(entry);

                _logger.LogInformation($"Marked entry as arrived: {entry.EntryID} - Train {entry.Train?.Name} arrived at {entry.DestinationStation?.Name}");
            }
        }

        /// <summary>
        /// Gets entries that need cleanup (old arrived entries)
        /// </summary>
        public List<TimetableEntries> GetEntriesForCleanup()
        {
            lock (_lockObject)
            {
                var cutoffDate = DateTime.Now.AddDays(-7); // Keep entries for 7 days
                return Entries.Where(e =>
                    e.EntryState == EntryState.Arrived &&
                    e.ArrivedTime.HasValue &&
                    e.ArrivedTime.Value < cutoffDate).ToList();
            }
        }

        /// <summary>
        /// Cleans up old arrived entries
        /// </summary>
        public void CleanupOldEntries()
        {
            lock (_lockObject)
            {
                var entriesToCleanup = GetEntriesForCleanup();
                foreach (var entry in entriesToCleanup)
                {
                    RemoveTimetableEntry(entry);
                }

                if (entriesToCleanup.Any())
                {
                    _logger.LogInformation($"Cleaned up {entriesToCleanup.Count} old timetable entries");
                }
            }
        }

        /// <summary>
        /// Gets timetable statistics
        /// </summary>
        public TimetableStatistics GetStatistics()
        {
            lock (_lockObject)
            {
                return new TimetableStatistics
                {
                    TotalEntries = Entries.Count,
                    ScheduledEntries = Entries.Count(e => e.EntryState == EntryState.Scheduled),
                    InTransitEntries = Entries.Count(e => e.EntryState == EntryState.InTransit),
                    AtStationEntries = Entries.Count(e => e.EntryState == EntryState.AtStation),
                    ArrivedEntries = Entries.Count(e => e.EntryState == EntryState.Arrived),
                    ActiveTrains = Entries.Where(e => e.EntryState == EntryState.InTransit || e.EntryState == EntryState.AtStation)
                                         .Select(e => e.Train_DB_ID)
                                         .Distinct()
                                         .Count()
                };
            }
        }
    }

    /// <summary>
    /// Timetable statistics
    /// </summary>
    public class TimetableStatistics
    {
        public int TotalEntries { get; set; }
        public int ScheduledEntries { get; set; }
        public int InTransitEntries { get; set; }
        public int AtStationEntries { get; set; }
        public int ArrivedEntries { get; set; }
        public int ActiveTrains { get; set; }
    }
}