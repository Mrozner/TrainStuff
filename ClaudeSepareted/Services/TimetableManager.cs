namespace ClaudeSepareted
{
    public class TimetableManager
    {
        private readonly TimetableRepository _repository;
        private readonly TrackManager _trackManager;
        private readonly SystemConfiguration _config;
        private readonly object _lockObject = new object();

        public List<TimetableEntries> Entries { get; private set; } = new List<TimetableEntries>();

        public TimetableManager(
            TimetableRepository repository,
            TrackManager trackManager,
            SystemConfiguration config)
        {
            _repository = repository;
            _trackManager = trackManager;
            _config = config;

            LoadEntries();
        }

        public void LoadEntries()
        {
            lock (_lockObject)
            {
                Entries = _repository.GetTimetableEntries();
                Console.WriteLine($"Loaded {Entries.Count} timetable entries");
            }
        }

        public async Task ProcessScheduledTrains()
        {
            lock (_lockObject)
            {
                var now = DateTime.Now;
                var trainsToStart = Entries.Where(e =>
                    e.EntryState == EntryState.Upcoming &&
                    e.StartDate.Date <= now.Date &&
                    e.StartTime <= now.TimeOfDay).ToList();

                Console.WriteLine($"Found {trainsToStart.Count} trains to start at {now:HH:mm:ss}");

                foreach (var entry in trainsToStart)
                {
                    Console.WriteLine($"Auto-starting train for entry: {entry.EntryID}");
                    _trackManager.TryStartTrain(entry);

                    // Update entry state
                    entry.EntryState = EntryState.InProgress;
                    _repository.UpdateEntry(entry);
                }

                // Reload entries to reflect changes
                LoadEntries();
            }
        }

        public void HandleStatusUpdate(string entryID, string status)
        {
            lock (_lockObject)
            {
                var entry = Entries.FirstOrDefault(e => e.EntryID == entryID);
                if (entry == null) return;

                switch (status)
                {
                    case "OK":
                        entry.EntryState = EntryState.InProgress;
                        entry.RouteState = RouteState.InTime;
                        break;
                    case "NOK":
                        entry.RouteState = RouteState.Delay;
                        break;
                    case "Arrived":
                        entry.EntryState = EntryState.Arrived;
                        entry.ArrivedTime = DateTime.Now;
                        entry.RouteState = RouteState.InTime;
                        break;
                    case "Delay":
                        entry.RouteState = RouteState.Delay;
                        break;
                }

                _repository.UpdateEntry(entry);
                Console.WriteLine($"Entry {entryID} updated: {status}");
            }
        }

        public bool AddEntry(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                bool success = _repository.AddEntry(entry);
                if (success)
                {
                    // Reload entries to include the new one
                    LoadEntries();

                    // Check if this entry should start immediately
                    var now = DateTime.Now;
                    if (entry.StartDate.Date <= now.Date && entry.StartTime <= now.TimeOfDay.Add(TimeSpan.FromMinutes(1)))
                    {
                        Console.WriteLine($"New entry {entry.EntryID} is scheduled to start now, starting immediately");
                        _trackManager.TryStartTrain(entry);
                        entry.EntryState = EntryState.InProgress;
                        _repository.UpdateEntry(entry);
                    }

                    Console.WriteLine($"Entry added: {entry.EntryID}");
                }
                return success;
            }
        }

        public void DeleteEntry(string entryID)
        {
            lock (_lockObject)
            {
                var entry = Entries.FirstOrDefault(e => e.EntryID == entryID);
                if (entry != null)
                {
                    _repository.DeleteEntry(entry);
                    LoadEntries();
                    Console.WriteLine($"Entry deleted: {entryID}");
                }
            }
        }
    }
}