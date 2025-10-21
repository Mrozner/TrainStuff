using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ClaudeSepareted
{
    public class TrackManager
    {
        private readonly ApplicationDbContext _db;
        private readonly SystemConfiguration _config;
        private readonly object _lockObject = new object();

        // MQTT Connectors - will be set after construction
        private TrackMQTTConnector _trackConnector;
        private TimetableMQTTConnector _timetableConnector;

        public List<Train> Trains { get; private set; } = new List<Train>();
        public List<Sections> Sections { get; private set; } = new List<Sections>();
        public List<SubSections> SubSections { get; private set; } = new List<SubSections>();
        public List<Platforms> Platforms { get; private set; } = new List<Platforms>();
        public List<Switches> Switches { get; private set; } = new List<Switches>();
        public List<Objects> Objects { get; private set; } = new List<Objects>();
        public List<Signal> Signals { get; private set; } = new List<Signal>();
        public List<SubSections> OccupiedSubSections { get; private set; } = new List<SubSections>();

        // Route planning cache
        private Dictionary<string, List<SubSections>> _routeCache = new Dictionary<string, List<SubSections>>();

        public TrackManager(
            ApplicationDbContext db,
            SystemConfiguration config)
        {
            _db = db;
            _config = config;
            Console.WriteLine("Track Manager created (waiting for MQTT connectors)");
        }

        /// <summary>
        /// Sets the MQTT connectors and initializes the track configuration
        /// </summary>
        public void SetMqttConnectors(TrackMQTTConnector trackConnector, TimetableMQTTConnector timetableConnector)
        {
            _trackConnector = trackConnector;
            _timetableConnector = timetableConnector;
            InitializeTrackConfiguration();
        }

        private void InitializeTrackConfiguration()
        {
            Console.WriteLine("Initializing track configuration...");

            // Load from database
            Trains = _db.Trains.Where(t => t.IsActive).ToList();
            Sections = _db.Sections.ToList();
            SubSections = _db.SubSections.ToList();
            Platforms = _db.Platforms.ToList();
            Switches = _db.Switches.ToList();
            Objects = _db.Objects.ToList();

            // Build section-subsection relationships
            foreach (var section in Sections)
            {
                section.SubSections = SubSections.Where(ss => ss.Section?.DB_ID == section.DB_ID).ToList();
                foreach (var subSection in section.SubSections)
                {
                    subSection.Section = section;
                }
            }

            // Initialize signals
            Signals = Objects
                .Where(o => o.ObjectType == ObjectType.Signal)
                .Select(o => new Signal { ID = o.ObjectID, Speed = Speed.STOP })
                .ToList();

            // Link platform relationships
            foreach (var platform in Platforms)
            {
                platform.SubSection = SubSections.FirstOrDefault(s => s.DB_ID == platform.SubSection_DB_ID);
            }

            // Initialize train positions and relationships
            foreach (var train in Trains)
            {
                var platform = Platforms.FirstOrDefault(p => p.DB_ID == train.Platform_DB_ID);
                if (platform != null)
                {
                    train.Source = platform;
                    train.SubSection = platform.SubSection;
                    train.Section = platform.SubSection?.Section;

                    if (train.SubSection != null)
                    {
                        OccupiedSubSections.Add(train.SubSection);
                    }
                }
            }

            // Set all signals to STOP
            if (_trackConnector != null)
            {
                foreach (var signal in Signals)
                {
                    _trackConnector.SendSignalUpdate(signal);
                }
            }

            Console.WriteLine($"Track initialized: {Trains.Count} trains, {Sections.Count} sections, {SubSections.Count} subsections, {Platforms.Count} platforms");
        }

        /// <summary>
        /// Tries to start a train based on timetable entry
        /// </summary>
        public void TryStartTrain(TimetableEntries entry)
        {
            lock (_lockObject)
            {
                Console.WriteLine($"Attempting to start train for entry: {entry.EntryID}");

                var train = Trains.FirstOrDefault(t => t.DB_ID == entry.Train_DB_ID && t.State == TrainState.Waiting);
                if (train == null)
                {
                    Console.WriteLine($"Train not found or not waiting: {entry.Train_DB_ID}");
                    _timetableConnector?.SendStartResponse(entry.EntryID, false).Wait();
                    return;
                }

                var destinationPlatform = Platforms.FirstOrDefault(p =>
                    p.Station_DB_ID == entry.DestinationStation_DB_ID &&
                    !Trains.Any(t => t.Destination?.DB_ID == p.DB_ID));

                if (destinationPlatform == null)
                {
                    Console.WriteLine($"No available destination platform for station: {entry.DestinationStation_DB_ID}");
                    _timetableConnector?.SendStartResponse(entry.EntryID, false).Wait();
                    return;
                }

                // Set train destination and plan route
                train.Destination = destinationPlatform;
                train.Source = null;
                train.CurrentEntryID = entry.EntryID;

                // Plan route from current position to destination
                var route = PlanRoute(train.SubSection, destinationPlatform.SubSection);
                if (route == null || route.Count == 0)
                {
                    Console.WriteLine($"No valid route found for train {train.Name}");
                    _timetableConnector?.SendStartResponse(entry.EntryID, false).Wait();
                    return;
                }

                // Set the next subsection to move to
                train.NextSubsection_DB_ID = route[0].DB_ID;
                Console.WriteLine($"Planned route for {train.Name}: {train.SubSection.Name} -> {string.Join(" -> ", route.Select(r => r.Name))}");

                bool success = HandleTrainMovement(train);
                Console.WriteLine($"Train {train.Name} start {(success ? "successful" : "failed")}");
                _timetableConnector?.SendStartResponse(entry.EntryID, success).Wait();
            }
        }

        /// <summary>
        /// Manually start a train (for testing via console)
        /// </summary>
        public bool ManualStartTrain(int trainId, int destinationStationId)
        {
            lock (_lockObject)
            {
                Console.WriteLine($"ManualStartTrain called: trainId={trainId}, destinationStationId={destinationStationId}");

                var train = Trains.FirstOrDefault(t => t.DB_ID == trainId);
                if (train == null)
                {
                    Console.WriteLine($"Train not found with ID: {trainId}");
                    return false;
                }

                if (train.State != TrainState.Waiting)
                {
                    Console.WriteLine($"Train {train.Name} is not in Waiting state. Current state: {train.State}");
                    return false;
                }

                // Find available platforms for the destination station
                var availablePlatforms = Platforms.Where(p =>
                    p.Station_DB_ID == destinationStationId &&
                    p.isActive).ToList();

                Console.WriteLine($"Available platforms for station {destinationStationId}: {availablePlatforms.Count}");
                foreach (var platform in availablePlatforms)
                {
                    Console.WriteLine($"  Platform: {platform.Name} (ID: {platform.DB_ID}), SubSection: {platform.SubSection?.Name}");
                }

                var destinationPlatform = availablePlatforms.FirstOrDefault(p =>
                    !Trains.Any(t => t.Destination?.DB_ID == p.DB_ID));

                if (destinationPlatform == null)
                {
                    Console.WriteLine($"No available destination platform for station: {destinationStationId}");
                    Console.WriteLine("Currently occupied platforms:");
                    foreach (var occupiedTrain in Trains.Where(t => t.Destination != null))
                    {
                        Console.WriteLine($"  Train {occupiedTrain.Name} -> Platform {occupiedTrain.Destination.Name}");
                    }
                    return false;
                }

                Console.WriteLine($"Selected destination platform: {destinationPlatform.Name}");

                // Set train destination and plan route
                train.Destination = destinationPlatform;
                train.Source = null;

                // Debug current and destination positions
                Console.WriteLine($"Current position: {train.SubSection?.Name} (ID: {train.SubSection?.DB_ID})");
                Console.WriteLine($"Destination position: {destinationPlatform.SubSection?.Name} (ID: {destinationPlatform.SubSection?.DB_ID})");

                // Plan route from current position to destination
                var route = PlanRoute(train.SubSection, destinationPlatform.SubSection);
                if (route == null || route.Count == 0)
                {
                    Console.WriteLine($"No valid route found for train {train.Name} from {train.SubSection?.Name} to {destinationPlatform.SubSection?.Name}");
                    return false;
                }

                // Set the next subsection to move to
                train.NextSubsection_DB_ID = route[0].DB_ID;
                Console.WriteLine($"Planned route for {train.Name}: {train.SubSection.Name} -> {string.Join(" -> ", route.Select(r => r.Name))}");

                bool success = HandleTrainMovement(train);
                Console.WriteLine($"Manual start result for {train.Name}: {(success ? "SUCCESS" : "FAILED")}");
                return success;
            }
        }

        /// <summary>
        /// Plans a route from start to destination subsection using BFS
        /// </summary>
        private List<SubSections> PlanRoute(SubSections start, SubSections destination)
        {
            if (start == null || destination == null)
            {
                Console.WriteLine("Route planning failed: start or destination is null");
                return null;
            }

            // Check cache first
            var cacheKey = $"{start.DB_ID}-{destination.DB_ID}";
            if (_routeCache.ContainsKey(cacheKey))
            {
                return _routeCache[cacheKey];
            }

            Console.WriteLine($"Planning route from {start.Name} to {destination.Name}");

            var visited = new HashSet<int>();
            var queue = new Queue<List<SubSections>>();
            queue.Enqueue(new List<SubSections> { start });

            while (queue.Count > 0)
            {
                var path = queue.Dequeue();
                var current = path.Last();

                if (current.DB_ID == destination.DB_ID)
                {
                    Console.WriteLine($"Route found with {path.Count} segments");
                    // Skip the first element (current position)
                    var result = path.Skip(1).ToList();
                    _routeCache[cacheKey] = result;
                    return result;
                }

                if (visited.Contains(current.DB_ID))
                    continue;

                visited.Add(current.DB_ID);

                // Find connected subsections
                var connectedSubsections = FindConnectedSubsections(current);
                foreach (var next in connectedSubsections)
                {
                    if (!visited.Contains(next.DB_ID))
                    {
                        var newPath = new List<SubSections>(path) { next };
                        queue.Enqueue(newPath);
                    }
                }
            }

            Console.WriteLine("No route found after exploring all possibilities");
            return null;
        }

        /// <summary>
        /// Finds subsections connected to the given subsection
        /// </summary>
        private List<SubSections> FindConnectedSubsections(SubSections subsection)
        {
            var connected = new List<SubSections>();

            if (subsection?.Section == null) return connected;

            // Find subsections in the same section
            var sameSectionSubs = SubSections.Where(s =>
                s.Section?.DB_ID == subsection.Section.DB_ID &&
                s.DB_ID != subsection.DB_ID).ToList();

            connected.AddRange(sameSectionSubs);

            // Find subsections in adjacent sections (simplified topology)
            // In real implementation, this would use proper track topology with switches
            var adjacentSections = Sections.Where(s => s.DB_ID != subsection.Section.DB_ID).ToList();
            foreach (var adjSection in adjacentSections)
            {
                // Add first and last subsections of adjacent sections as potential connections
                var firstSub = adjSection.SubSections.FirstOrDefault();
                var lastSub = adjSection.SubSections.LastOrDefault();

                if (firstSub != null && !connected.Contains(firstSub))
                    connected.Add(firstSub);
                if (lastSub != null && !connected.Contains(lastSub))
                    connected.Add(lastSub);
            }

            return connected;
        }

        public void TrainAppeared(string subsectionName)
        {
            lock (_lockObject)
            {
                Console.WriteLine($"Train appeared at: {subsectionName}");

                var subSection = SubSections.FirstOrDefault(s => s.Name == subsectionName);
                if (subSection == null)
                {
                    Console.WriteLine($"Subsection not found: {subsectionName}");
                    return;
                }

                // Check if this subsection is already occupied
                if (OccupiedSubSections.Any(s => s.DB_ID == subSection.DB_ID))
                {
                    Console.WriteLine($"Subsection {subsectionName} is already occupied");
                    return;
                }

                OccupiedSubSections.Add(subSection);

                // Find train that was moving to this subsection
                var train = Trains.FirstOrDefault(t =>
                    t.State == TrainState.Moving &&
                    t.NextSubsection_DB_ID == subSection.DB_ID);

                if (train != null)
                {
                    var oldSubsection = train.SubSection;
                    train.SubSection = subSection;
                    train.Section = subSection.Section;

                    Console.WriteLine($"Train {train.Name} moved to: {subSection.Name}");

                    // Update signals for old subsection
                    HandleSignals(oldSubsection, Speed.STOP);

                    if (train.Arrived())
                    {
                        HandleTrainArrival(train);
                    }
                    else
                    {
                        // Plan next movement
                        UpdateNextSubsection(train);
                        HandleTrainMovement(train);
                    }
                }
                else
                {
                    Console.WriteLine($"No train found moving to subsection {subsectionName}");
                }
            }
        }

        public void TrainLeft(string subsectionName)
        {
            lock (_lockObject)
            {
                Console.WriteLine($"Train left: {subsectionName}");

                var subSection = SubSections.FirstOrDefault(s => s.Name == subsectionName);
                if (subSection == null)
                {
                    Console.WriteLine($"Subsection not found: {subsectionName}");
                    return;
                }

                OccupiedSubSections.RemoveAll(s => s.DB_ID == subSection.DB_ID);

                // Try to restart stopped trains that were waiting for this section to clear
                var stoppedTrains = Trains.Where(t => t.State == TrainState.Stopped).ToList();
                foreach (var train in stoppedTrains)
                {
                    Console.WriteLine($"Attempting to restart stopped train: {train.Name}");
                    HandleTrainMovement(train);
                }
            }
        }

        /// <summary>
        /// Updates the next subsection for a train based on its route to destination
        /// </summary>
        private void UpdateNextSubsection(Train train)
        {
            if (train.Destination == null) return;

            // Re-plan route from current position
            var route = PlanRoute(train.SubSection, train.Destination.SubSection);
            if (route != null && route.Count > 0)
            {
                train.NextSubsection_DB_ID = route[0].DB_ID;
                Console.WriteLine($"Updated next subsection for {train.Name}: {route[0].Name}");
            }
            else
            {
                train.NextSubsection_DB_ID = null;
                Console.WriteLine($"No route found for {train.Name}, stopping");
            }
        }

        private bool HandleTrainMovement(Train train)
        {
            if (train.Destination == null)
            {
                Console.WriteLine($"Train {train.Name} has no destination");
                return false;
            }

            // Update route if needed
            if (train.NextSubsection_DB_ID == null)
            {
                UpdateNextSubsection(train);
            }

            bool canMove = CheckIfCanMove(train);

            if (canMove)
            {
                train.State = TrainState.Moving;
                train.CurrentSpeed = GetSafeSpeed(train);
                HandleSignals(train);
                Console.WriteLine($"Train {train.Name} is moving to subsection {train.NextSubsection_DB_ID}");
                return true;
            }
            else
            {
                train.State = TrainState.Stopped;
                train.CurrentSpeed = Speed.STOP;
                HandleSignals(train);
                Console.WriteLine($"Train {train.Name} is stopped, cannot move to next subsection");
                return false;
            }
        }

        private Speed GetSafeSpeed(Train train)
        {
            // Determine safe speed based on next subsection's allowed speed and train's max speed
            var nextSub = SubSections.FirstOrDefault(s => s.DB_ID == train.NextSubsection_DB_ID);
            if (nextSub == null) return Speed.SLOW;

            Speed allowedSpeed = nextSub.AllowedSpeed;
            return allowedSpeed > train.MaxSpeed ? train.MaxSpeed : allowedSpeed;
        }

        private bool CheckIfCanMove(Train train)
        {
            if (train.NextSubsection_DB_ID == null) return false;

            // Check if next subsection is occupied
            bool isOccupied = OccupiedSubSections.Any(s => s.DB_ID == train.NextSubsection_DB_ID);
            if (isOccupied)
            {
                Console.WriteLine($"Next subsection {train.NextSubsection_DB_ID} is occupied");
                return false;
            }

            // Check if path is clear (simplified - in real app, check switches, signals, etc.)
            return true;
        }

        private void HandleTrainArrival(Train train)
        {
            Console.WriteLine($"Train {train.Name} arrived at destination");

            train.State = TrainState.Waiting;
            train.Source = train.Destination;
            train.Destination = null;
            train.NextSubsection_DB_ID = null;
            train.Platform_DB_ID = train.Source.DB_ID;
            train.LockedSections.Clear();
            train.LockedSwitches.Clear();
            train.CurrentSpeed = Speed.STOP;

            // Free up the occupied subsection
            if (train.SubSection != null)
            {
                OccupiedSubSections.RemoveAll(s => s.DB_ID == train.SubSection.DB_ID);
            }

            HandleSignals(train);

            if (train.CurrentEntryID != null)
            {
                _timetableConnector?.SendTrackStatus(train.CurrentEntryID, TrainState.Waiting).Wait();
                train.CurrentEntryID = null;
            }
        }

        private void HandleSignals(Train train)
        {
            if (_trackConnector == null) return;

            var signalObj = Objects.FirstOrDefault(o =>
                o.Direction == train.Direction &&
                o.SubSection_DB_ID == train.SubSection.DB_ID &&
                o.ObjectType == ObjectType.Signal);

            if (signalObj != null)
            {
                var signal = Signals.FirstOrDefault(s => s.ID == signalObj.ObjectID);
                if (signal != null)
                {
                    if (train.State == TrainState.Stopped || train.State == TrainState.Waiting)
                    {
                        signal.Speed = Speed.STOP;
                        signal.NextSpeed = null;
                    }
                    else if (train.State == TrainState.Moving)
                    {
                        signal.Speed = GetSafeSpeed(train);
                        signal.NextSpeed = Speed.SLOW; // Simplified next signal
                    }

                    _trackConnector.SendSignalUpdate(signal);
                    Console.WriteLine($"Signal {signal.ID} updated to {signal.Speed} for train {train.Name}");
                }
            }
        }

        private void HandleSignals(SubSections subSection, Speed speed)
        {
            if (_trackConnector == null) return;

            var signals = Objects.Where(o =>
                o.SubSection_DB_ID == subSection.DB_ID &&
                o.ObjectType == ObjectType.Signal);

            foreach (var signalObj in signals)
            {
                var signal = Signals.FirstOrDefault(s => s.ID == signalObj.ObjectID);
                if (signal != null)
                {
                    signal.Speed = speed;
                    signal.NextSpeed = null;
                    _trackConnector.SendSignalUpdate(signal);
                }
            }
        }

        public Signal GetSignalForTrain(string trainName)
        {
            var train = Trains.FirstOrDefault(t => t.Name == trainName);
            if (train == null) return null;

            var signalObj = Objects.FirstOrDefault(o =>
                o.SubSection_DB_ID == train.SubSection.DB_ID &&
                o.Direction == train.Direction &&
                o.ObjectType == ObjectType.Signal);

            if (signalObj == null) return null;

            return Signals.FirstOrDefault(s => s.ID == signalObj.ObjectID);
        }

        /// <summary>
        /// Gets detailed information about track layout for debugging
        /// </summary>
        public void DebugTrackLayout()
        {
            Console.WriteLine("\n=== Track Layout Debug ===");

            Console.WriteLine("\nSections and SubSections:");
            foreach (var section in Sections.OrderBy(s => s.Name))
            {
                Console.WriteLine($"\n{section.Name} (ID: {section.DB_ID}):");
                foreach (var subSection in section.SubSections.OrderBy(ss => ss.Name))
                {
                    var platforms = Platforms.Where(p => p.SubSection_DB_ID == subSection.DB_ID);
                    var isOccupied = OccupiedSubSections.Any(oss => oss.DB_ID == subSection.DB_ID);
                    Console.WriteLine($"  {subSection.Name} (ID: {subSection.DB_ID}) - Platforms: {string.Join(", ", platforms.Select(p => p.Name))} - Occupied: {isOccupied}");
                }
            }

            Console.WriteLine("\nPlatforms:");
            foreach (var platform in Platforms.OrderBy(p => p.Name))
            {
                Console.WriteLine($"  {platform.Name} (ID: {platform.DB_ID}) -> Station ID: {platform.Station_DB_ID}, SubSection: {platform.SubSection?.Name}");
            }

            Console.WriteLine("\nTrains and their locations:");
            foreach (var train in Trains.OrderBy(t => t.Name))
            {
                Console.WriteLine($"  {train.Name} (ID: {train.DB_ID}) -> {train.SubSection?.Name} (State: {train.State})");
            }
        }

        /// <summary>
        /// Debug route between two subsections
        /// </summary>
        public void DebugRoute(int startSubSectionId, int endSubSectionId)
        {
            var startSub = SubSections.FirstOrDefault(s => s.DB_ID == startSubSectionId);
            var endSub = SubSections.FirstOrDefault(s => s.DB_ID == endSubSectionId);

            if (startSub == null || endSub == null)
            {
                Console.WriteLine("Invalid subsection IDs");
                return;
            }

            Console.WriteLine($"\n=== Route Debug: {startSub.Name} -> {endSub.Name} ===");
            var route = PlanRoute(startSub, endSub);

            if (route != null && route.Count > 0)
            {
                Console.WriteLine($"Route found: {startSub.Name} -> {string.Join(" -> ", route.Select(r => r.Name))}");
            }
            else
            {
                Console.WriteLine("No route found");

                // Show connected subsections for debugging
                Console.WriteLine($"Subsections connected to {startSub.Name}:");
                var connected = FindConnectedSubsections(startSub);
                foreach (var conn in connected)
                {
                    Console.WriteLine($"  - {conn.Name} (ID: {conn.DB_ID})");
                }
            }
        }

        /// <summary>
        /// Clears the route cache (useful when track layout changes)
        /// </summary>
        public void ClearRouteCache()
        {
            _routeCache.Clear();
            Console.WriteLine("Route cache cleared");
        }
    }
}