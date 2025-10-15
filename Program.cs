// Program.cs - Main Entry Point
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Newtonsoft.Json;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text;

namespace TrainControlSystem
{
    // ============================================================================
    // PROGRAM.CS - Fixed Dependency Injection for TrainManagerService
    // ============================================================================

    public class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== Train Control System - Monolithic Application ===");
            Console.WriteLine("Starting all services...\n");

            var host = CreateHostBuilder(args).Build();

            // Fix: Set MQTT connectors for TrackManager after all services are built
            using (var scope = host.Services.CreateScope())
            {
                var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                var trackConnector = scope.ServiceProvider.GetRequiredService<TrackMQTTConnector>();
                var timetableConnector = scope.ServiceProvider.GetRequiredService<TimetableMQTTConnector>();

                trackManager.SetMqttConnectors(trackConnector, timetableConnector);
            }

            await host.RunAsync();
        }

        static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configuration
                    var configPath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
                    var config = LoadConfiguration(configPath);
                    services.AddSingleton(config);

                    // Database
                    services.AddDbContext<ApplicationDbContext>(options =>
                        options.UseSqlServer(config.ConnectionString)
                               .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking),
                        ServiceLifetime.Singleton);

                    // Core Services - Register TrackManager first
                    services.AddSingleton<TrackManager>();
                    services.AddSingleton<TimetableManager>();

                    // MQTT Connectors
                    services.AddSingleton<TrackMQTTConnector>();
                    services.AddSingleton<TimetableMQTTConnector>();
                    services.AddSingleton<TrainMQTTConnector>();

                    // TrainManagerService must be registered after MQTT connectors
                    services.AddSingleton<TrainManagerService>();

                    // Central Message Handler
                    services.AddSingleton<MQTTMessageHandler>();

                    // Background Services
                    services.AddHostedService<TimetableSchedulerService>();
                    services.AddHostedService<SystemMonitorService>();
                    services.AddHostedService<ConsoleInterface>();

                    // Repositories
                    services.AddSingleton<TimetableRepository>();
                });

        static SystemConfiguration LoadConfiguration(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"Configuration file not found: {path}");
            }

            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<SystemConfiguration>(json);
        }
    }

    // ============================================================================
    // CONFIGURATION CLASSES
    // ============================================================================

    public class SystemConfiguration
    {
        public string ConnectionString { get; set; }
        public MQTTConfiguration MQTT { get; set; }
        public TrackConfiguration Track { get; set; }
        public TimetableConfiguration Timetable { get; set; }
        public string SignalChangedMessage { get; set; } = "SignalChanged";
    }

    public class MQTTConfiguration
    {
        public string Address { get; set; } = "localhost";
        public int Port { get; set; } = 1883;

        // Track Topics
        public string TrackSectionTopic { get; set; } = "rocrail/service/info/fb";
        public string TrackPositionTopic { get; set; } = "track/info/hall";
        public string TrackRFIDTopic { get; set; } = "track/info/rfid";
        public string TrackCommandTopic { get; set; } = "rocrail/service/client";
        public string TrackSignalTopic { get; set; } = "track/command/signal";

        // Train Topics
        public string TrainSignalRequestTopic { get; set; } = "train/signal/request";
        public string TrainSignalResponseTopic { get; set; } = "train/signal/response";
        public string TrainSignalChangedTopic { get; set; } = "train/signal/changed";
        public string TrainSpeedCommandTopic { get; set; } = "rocrail/service/client";

        // Timetable Topics
        public string TimetableStartRequestTopic { get; set; } = "train/start/request";
        public string TimetableStatusTopic { get; set; } = "train/status";
    }

    public class TrackConfiguration
    {
        public int MaxOccupiedSections { get; set; } = 100;
    }

    public class TimetableConfiguration
    {
        public int SchedulerIntervalSeconds { get; set; } = 30;
        public int MaxArrivedEntriesToKeep { get; set; } = 3;
    }

    // ============================================================================
    // DATA MODELS
    // ============================================================================

    public enum Speed
    {
        STOP = 0,
        SLOW = 30,
        MEDIUM = 60,
        HIGH = 90
    }

    public enum TrainState
    {
        Waiting,
        Moving,
        Stopped,
        PrepareToStop
    }

    public enum EntryState
    {
        Upcoming,
        InProgress,
        Arrived
    }

    public enum RouteState
    {
        InTime,
        Delay
    }

    public enum ObjectType
    {
        Signal,
        Hall,
        RFID
    }

    public class Train
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool Direction { get; set; }
        public bool IsActive { get; set; }
        public int Platform_DB_ID { get; set; }
        public Speed MaxSpeed { get; set; }
        [NotMapped]
        public Speed CurrentSpeed { get; set; } = Speed.STOP;
        [NotMapped]
        public TrainState State { get; set; } = TrainState.Waiting;

        // Navigation
        [NotMapped]
        public Platforms Source { get; set; }
        [NotMapped]
        public Platforms Destination { get; set; }
        [NotMapped]
        public SubSections SubSection { get; set; }
        [NotMapped]
        public Sections Section { get; set; }
        [NotMapped]
        public Sections NextSection { get; set; }
        [NotMapped]
        public int? NextSubsection_DB_ID { get; set; }

        // Runtime data
        [NotMapped]
        public List<int> LockedSections { get; set; } = new List<int>();
        [NotMapped]
        public List<Switches> LockedSwitches { get; set; } = new List<Switches>();
        [NotMapped]
        public List<string> CarriageIdentifiers { get; set; } = new List<string>();
        [NotMapped]
        public string StopHall { get; set; }
        [NotMapped]
        public string CurrentEntryID { get; set; }

        public bool Arrived()
        {
            return SubSection?.DB_ID == Destination?.SubSection_DB_ID ||
                   SubSection?.DB_ID == Source?.SubSection_DB_ID;
        }
    }

    public class Sections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool isActive { get; set; }
        [NotMapped]
        public List<SubSections> SubSections { get; set; } = new List<SubSections>();
    }

    public class SubSections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        [NotMapped]
        public Sections Section { get; set; }
        public Speed AllowedSpeed { get; set; }
    }

    public class Platforms
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public int SubSection_DB_ID { get; set; }
        [NotMapped]
        public SubSections SubSection { get; set; }
        public int Station_DB_ID { get; set; }
        public bool isActive { get; set; }
    }

    public class Stations
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }

    public class Switches
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        [NotMapped]
        public string State { get; set; }

        [NotMapped]
        public int? TrainDB_ID { get; set; }
    }
    [NotMapped]
    public class Signal
    {
        [Key]
        public string ID { get; set; }
        public Speed Speed { get; set; }
        public Speed? NextSpeed { get; set; }
    }

    public class Objects
    {
        [Key]
        public int DB_ID { get; set; }
        public int SubSection_DB_ID { get; set; }
        public bool Direction { get; set; }
        public ObjectType ObjectType { get; set; }
        public string ObjectID { get; set; }
    }

    public class TimetableEntries
    {
        [Key]
        public int DB_ID { get; set; }
        public string EntryID { get; set; } = Guid.NewGuid().ToString();
        public int SourceStation_DB_ID { get; set; }
        public int DestinationStation_DB_ID { get; set; }
        public int Train_DB_ID { get; set; }
        public DateTime StartDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public EntryState EntryState { get; set; }
        public RouteState RouteState { get; set; }
        [NotMapped]
        public DateTime? ArrivedTime { get; set; }

        // Navigation
        [NotMapped]
        [ForeignKey(nameof(SourceStation_DB_ID))]
        public Stations SourceStation { get; set; }
        [NotMapped]
        [ForeignKey(nameof(DestinationStation_DB_ID))]
        public Stations DestinationStation { get; set; }
        [NotMapped]
        [ForeignKey(nameof(Train_DB_ID))]
        public Train Train { get; set; }
    }

    // ============================================================================
    // DATABASE CONTEXT
    // ============================================================================

    public class ApplicationDbContext : DbContext
    {
        public DbSet<Train> Trains { get; set; }
        public DbSet<Stations> Stations { get; set; }
        public DbSet<Platforms> Platforms { get; set; }
        public DbSet<Sections> Sections { get; set; }
        public DbSet<SubSections> SubSections { get; set; }
        public DbSet<Switches> Switches { get; set; }
        public DbSet<Objects> Objects { get; set; }
        public DbSet<TimetableEntries> TimetableEntries { get; set; }

        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Train>()
                .Property(e => e.MaxSpeed)
                .HasConversion<string>();

            modelBuilder.Entity<SubSections>()
                .Property(e => e.AllowedSpeed)
                .HasConversion<string>();

            modelBuilder.Entity<Objects>()
                .Property(e => e.ObjectType)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.EntryState)
                .HasConversion<string>();

            modelBuilder.Entity<TimetableEntries>()
                .Property(e => e.RouteState)
                .HasConversion<string>();
        }
    }

    // ============================================================================
    // TRACK MANAGER - Complete Implementation
    // ============================================================================

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

    // ============================================================================
    // MQTT CONNECTORS (MQTTnet 5.0.1 compatible - No Factory)
    // ============================================================================

    public class TrackMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public TrackMQTTConnector(SystemConfiguration config)
        {
            _config = config;

            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            ConnectAsync().Wait();
            Console.WriteLine("Track MQTT Connector initialized");
        }

        // Remove the SetTrackManager method since we don't need TrackManager reference

        private async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Track MQTT Connected to broker");

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackSectionTopic)
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackPositionTopic)
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackRFIDTopic)
                    .Build());
            };

            // Remove the ApplicationMessageReceivedAsync handler from here
            // We'll handle messages elsewhere

            await _mqttClient.ConnectAsync(options);
        }

        public async void SendSignalUpdate(Signal signal)
        {
            try
            {
                var signalMessage = new
                {
                    ID = signal.ID,
                    Speed = signal.Speed,
                    NextSpeed = signal.NextSpeed
                };

                var payload = JsonConvert.SerializeObject(signalMessage);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TrackSignalTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Signal Update: {signal.ID} -> {signal.Speed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending signal update: {ex.Message}");
            }
        }

        public async void SwitchControl(Switches sw)
        {
            try
            {
                var switchCommand = $"<sw id=\"{sw.Name}\" state=\"{(sw.State == "1" ? "1" : "0")}\"/>";

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TrackCommandTopic)
                    .WithPayload(switchCommand)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Switch Control: {sw.Name} -> {sw.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending switch control: {ex.Message}");
            }
        }
    }

    public class TimetableMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public TimetableMQTTConnector(SystemConfiguration config)
        {
            _config = config;
            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            ConnectAsync().Wait();
            Console.WriteLine("Timetable MQTT Connector initialized");
        }

        private async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Timetable MQTT Connected to broker");

                // Subscribe to timetable topics
                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TimetableStartRequestTopic)
                    .Build());
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (topic == _config.MQTT.TimetableStartRequestTopic)
                {
                    await HandleStartRequest(payload);
                }
            };

            await _mqttClient.ConnectAsync(options);
        }

        private async Task HandleStartRequest(string payload)
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<TimetableEntries>(payload);
                if (entry != null)
                {
                    Console.WriteLine($"Received start request for entry: {entry.EntryID}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling start request: {ex.Message}");
            }
        }

        public async Task SendStartResponse(string entryID, bool canStart)
        {
            try
            {
                var response = new
                {
                    EntryID = entryID,
                    Status = canStart ? "OK" : "NOK",
                    Timestamp = DateTime.Now
                };

                var payload = JsonConvert.SerializeObject(response);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TimetableStatusTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Start Response: {entryID} -> {(canStart ? "OK" : "NOK")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending start response: {ex.Message}");
            }
        }

        public async Task SendTrackStatus(string entryID, TrainState state)
        {
            try
            {
                string status = state == TrainState.Waiting ? "Arrived" : "Delay";

                var statusMessage = new
                {
                    EntryID = entryID,
                    Status = status,
                    Timestamp = DateTime.Now
                };

                var payload = JsonConvert.SerializeObject(statusMessage);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TimetableStatusTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Track Status: {entryID} -> {status}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending track status: {ex.Message}");
            }
        }
    }

    public class TrainMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private readonly TrackManager _trackManager;
        private IMqttClient _mqttClient;

        public TrainMQTTConnector(SystemConfiguration config, TrackManager trackManager)
        {
            _config = config;
            _trackManager = trackManager;

            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            ConnectAsync().Wait();
            Console.WriteLine("Train MQTT Connector initialized");
        }

        private async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Train MQTT Connected to broker");

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrainSignalRequestTopic)
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrainSignalChangedTopic)
                    .Build());
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                await HandleIncomingMessage(topic, payload);
            };

            await _mqttClient.ConnectAsync(options);
        }

        private async Task HandleIncomingMessage(string topic, string payload)
        {
            try
            {
                if (topic == _config.MQTT.TrainSignalRequestTopic)
                {
                    await HandleSignalRequest(payload);
                }
                else if (topic == _config.MQTT.TrainSignalChangedTopic)
                {
                    await HandleSignalChanged(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling train MQTT message: {ex.Message}");
            }
        }

        private async Task HandleSignalRequest(string payload)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<SignalRequest>(payload);
                if (request != null)
                {
                    await SendSignalStatus(request.Name);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling signal request: {ex.Message}");
            }
        }

        private async Task HandleSignalChanged(string payload)
        {
            try
            {
                var signalChange = JsonConvert.DeserializeObject<SignalStateChangeResponse>(payload);
                if (signalChange != null)
                {
                    await RequestCurrentSignal(signalChange.ID);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling signal changed: {ex.Message}");
            }
        }

        public async Task SendSignalStatus(string trainName)
        {
            try
            {
                var signal = _trackManager.GetSignalForTrain(trainName);
                if (signal != null)
                {
                    var response = new SignalResponse
                    {
                        ID = signal.ID,
                        Speed = signal.Speed,
                        NextSpeed = signal.NextSpeed
                    };

                    var payload = JsonConvert.SerializeObject(response);
                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(_config.MQTT.TrainSignalResponseTopic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _mqttClient.PublishAsync(message);
                    Console.WriteLine($"Signal Status for {trainName}: {signal.Speed}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending signal status: {ex.Message}");
            }
        }

        public async Task RequestCurrentSignal(string trainName)
        {
            try
            {
                var train = _trackManager.Trains.FirstOrDefault(t => t.Name == trainName);
                if (train != null)
                {
                    var request = new SignalRequest { Name = trainName };
                    var payload = JsonConvert.SerializeObject(request);

                    var message = new MqttApplicationMessageBuilder()
                        .WithTopic(_config.MQTT.TrainSignalRequestTopic)
                        .WithPayload(payload)
                        .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build();

                    await _mqttClient.PublishAsync(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error requesting current signal: {ex.Message}");
            }
        }

        public async Task ChangeSpeed(Train train, Speed speed)
        {
            try
            {
                Speed speedToChange = speed > train.MaxSpeed ? train.MaxSpeed : speed;

                // Update the train's current speed in memory
                train.CurrentSpeed = speedToChange;

                // Rocrail speed command format: <lc id="Train1" v="90" dir="true"/>
                var speedCommand = $"<lc id=\"{train.Name}\" v=\"{((int)train.CurrentSpeed)}\" dir=\"{train.Direction.ToString().ToLower()}\"/>";

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TrainSpeedCommandTopic)
                    .WithPayload(speedCommand)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Train {train.Name} speed set to: {speedToChange} (MQTT command sent)");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error changing train speed: {ex.Message}");
            }
        }

        public static async Task SendSignalChanged(IMqttClient mqttClient, string topic)
        {
            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload("SignalChanged")
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await mqttClient.PublishAsync(message);
                Console.WriteLine("Signal Changed notification sent");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending signal changed: {ex.Message}");
            }
        }
    }

    // ============================================================================
    // ADDITIONAL DATA CLASSES FOR MQTT
    // ============================================================================

    public class SignalStateChangeResponse
    {
        public string ID { get; set; }
        public Speed Speed { get; set; }
    }

    // ============================================================================
    // TIMETABLE MANAGER
    // ============================================================================

    // ============================================================================
    // TIMETABLE MANAGER - Enhanced with Automatic Processing
    // ============================================================================

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

    // ============================================================================
    // TRAIN MANAGER SERVICE
    // ============================================================================

    public class TrainManagerService
    {
        private readonly TrackManager _trackManager;
        private readonly SystemConfiguration _config;
        private readonly TrainMQTTConnector _trainMQTTConnector;
        private readonly Dictionary<string, TrainController> _trainControllers = new Dictionary<string, TrainController>();

        public TrainManagerService(TrackManager trackManager, SystemConfiguration config, TrainMQTTConnector trainMQTTConnector)
        {
            _trackManager = trackManager;
            _config = config;
            _trainMQTTConnector = trainMQTTConnector;

            InitializeTrainControllers();
        }

        private void InitializeTrainControllers()
        {
            foreach (var train in _trackManager.Trains)
            {
                var controller = new TrainController(train, _config, _trainMQTTConnector);
                _trainControllers[train.Name] = controller;
            }

            Console.WriteLine($"Initialized {_trainControllers.Count} train controllers");
        }

        public void UpdateTrainSpeed(string trainName, Speed speed)
        {
            if (_trainControllers.TryGetValue(trainName, out var controller))
            {
                controller.SetSpeed(speed);
            }
            else
            {
                Console.WriteLine($"Train controller not found for: {trainName}");
            }
        }

        /// <summary>
        /// Gets train by name
        /// </summary>
        public Train GetTrain(string trainName)
        {
            return _trackManager.Trains.FirstOrDefault(t => t.Name == trainName);
        }

        /// <summary>
        /// Lists all available trains
        /// </summary>
        public void ListTrains()
        {
            Console.WriteLine("\nAvailable Trains:");
            foreach (var train in _trackManager.Trains)
            {
                Console.WriteLine($"  {train.Name}: State={train.State}, Speed={train.CurrentSpeed}, Location={train.SubSection?.Name}");
            }
        }
    }

    public class TrainController
    {
        private readonly Train _train;
        private readonly SystemConfiguration _config;
        private readonly TrainMQTTConnector _mqttConnector;

        public TrainController(Train train, SystemConfiguration config, TrainMQTTConnector mqttConnector)
        {
            _train = train;
            _config = config;
            _mqttConnector = mqttConnector;
        }

        public void SetSpeed(Speed speed)
        {
            try
            {
                Speed actualSpeed = speed > _train.MaxSpeed ? _train.MaxSpeed : speed;
                _train.CurrentSpeed = actualSpeed;

                // Send speed command to physical train via MQTT
                _mqttConnector.ChangeSpeed(_train, actualSpeed).Wait();

                Console.WriteLine($"Train {_train.Name} speed set to: {actualSpeed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting speed for train {_train.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Emergency stop the train
        /// </summary>
        public void EmergencyStop()
        {
            SetSpeed(Speed.STOP);
            _train.State = TrainState.Stopped;
            Console.WriteLine($"EMERGENCY STOP for train {_train.Name}");
        }
    }

    // ============================================================================
    // REPOSITORY
    // ============================================================================

    public class TimetableRepository
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public TimetableRepository(IServiceScopeFactory scopeFactory)
        {
            _scopeFactory = scopeFactory;
        }

        public List<TimetableEntries> GetTimetableEntries()
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                return db.TimetableEntries
                    .Where(e => e.EntryState != EntryState.Arrived)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToList();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public bool AddEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Add(entry);
                db.SaveChanges();
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding entry: {ex.Message}");
                return false;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void UpdateEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Update(entry);
                db.SaveChanges();
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public void DeleteEntry(TimetableEntries entry)
        {
            _semaphore.Wait();
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                db.TimetableEntries.Remove(entry);
                db.SaveChanges();
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }

    // ============================================================================
    // BACKGROUND SERVICES
    // ============================================================================

    public class TimetableSchedulerService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly SystemConfiguration _config;

        public TimetableSchedulerService(IServiceProvider serviceProvider, SystemConfiguration config)
        {
            _serviceProvider = serviceProvider;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("Timetable Scheduler Service started");

            // Wait until the next whole minute
            var now = DateTime.Now;
            var nextMinute = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
            var delay = nextMinute - now;
            await Task.Delay(delay, stoppingToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.Timetable.SchedulerIntervalSeconds));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();

                    timetableManager.LoadEntries();
                    await timetableManager.ProcessScheduledTrains();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in timetable scheduler: {ex.Message}");
                }
            }
        }
    }

    public class SystemMonitorService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public SystemMonitorService(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            Console.WriteLine("System Monitor Service started");

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));

            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();

                    Console.WriteLine("\n=== System Status ===");
                    Console.WriteLine($"Active Train: {trackManager.Trains.Count(t => t.State != TrainState.Waiting)}");
                    Console.WriteLine($"Occupied Sections: {trackManager.OccupiedSubSections.Count}");
                    Console.WriteLine($"Pending Entries: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Upcoming)}");
                    Console.WriteLine($"In Progress: {timetableManager.Entries.Count(e => e.EntryState == EntryState.InProgress)}");
                    Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                    Console.WriteLine("=====================\n");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in system monitor: {ex.Message}");
                }
            }
        }
    }

    // ============================================================================
    // MQTT MESSAGE HANDLER - Complete with HandleTrainSignalChanged
    // ============================================================================

    public class MQTTMessageHandler
    {
        private readonly TrackManager _trackManager;
        private readonly TimetableManager _timetableManager;
        private readonly TrainManagerService _trainManager;
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public MQTTMessageHandler(
            TrackManager trackManager,
            TimetableManager timetableManager,
            TrainManagerService trainManager,
            SystemConfiguration config)
        {
            _trackManager = trackManager;
            _timetableManager = timetableManager;
            _trainManager = trainManager;
            _config = config;

            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            InitializeMQTT().Wait();
        }

        private async Task InitializeMQTT()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                await HandleMessage(topic, payload);
            };

            await _mqttClient.ConnectAsync(options);

            // Subscribe to all relevant topics
            var topics = new[]
            {
            _config.MQTT.TrackSectionTopic,
            _config.MQTT.TrackPositionTopic,
            _config.MQTT.TrackRFIDTopic,
            _config.MQTT.TimetableStartRequestTopic,
            _config.MQTT.TrainSignalRequestTopic,
            _config.MQTT.TrainSignalChangedTopic
        };

            foreach (var topic in topics)
            {
                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .Build());
            }
        }

        private async Task HandleMessage(string topic, string payload)
        {
            try
            {
                if (topic == _config.MQTT.TrackSectionTopic)
                {
                    HandleTrackSectionMessage(payload);
                }
                else if (topic == _config.MQTT.TrackPositionTopic)
                {
                    HandleHallSensorMessage(payload);
                }
                else if (topic == _config.MQTT.TrackRFIDTopic)
                {
                    HandleRFIDMessage(payload);
                }
                else if (topic == _config.MQTT.TimetableStartRequestTopic)
                {
                    HandleTimetableStartRequest(payload);
                }
                else if (topic == _config.MQTT.TrainSignalRequestTopic)
                {
                    HandleTrainSignalRequest(payload);
                }
                else if (topic == _config.MQTT.TrainSignalChangedTopic)
                {
                    HandleTrainSignalChanged(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling MQTT message: {ex.Message}");
            }
        }

        public void HandleTrackSectionMessage(string payload)
        {
            try
            {
                if (payload.Contains("state=\"true\""))
                {
                    var sectionName = ExtractSectionName(payload);
                    _trackManager.TrainAppeared(sectionName);
                }
                else if (payload.Contains("state=\"false\""))
                {
                    var sectionName = ExtractSectionName(payload);
                    _trackManager.TrainLeft(sectionName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling track section message: {ex.Message}");
            }
        }

        public void HandleHallSensorMessage(string payload)
        {
            try
            {
                var hallData = JsonConvert.DeserializeObject<HallSensorData>(payload);
                if (hallData?.State == true)
                {
                    var train = _trackManager.Trains.FirstOrDefault(t => t.StopHall == hallData.ID);
                    if (train != null)
                    {
                        train.State = TrainState.Stopped;
                        Console.WriteLine($"Train stopped at hall sensor: {hallData.ID}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling hall sensor message: {ex.Message}");
            }
        }

        public void HandleRFIDMessage(string payload)
        {
            try
            {
                var rfidData = JsonConvert.DeserializeObject<RFIDSensorData>(payload);
                if (rfidData != null)
                {
                    var train = _trackManager.Trains.FirstOrDefault(t =>
                        t.State == TrainState.Moving && t.SubSection != null);

                    if (train != null)
                    {
                        train.CarriageIdentifiers.Add(rfidData.UID);
                        Console.WriteLine($"RFID detected: {rfidData.UID} for train {train.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling RFID message: {ex.Message}");
            }
        }

        public void HandleTimetableStartRequest(string payload)
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<TimetableEntries>(payload);
                if (entry != null)
                {
                    _trackManager.TryStartTrain(entry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling start request: {ex.Message}");
            }
        }

        public void HandleTrainSignalRequest(string payload)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<SignalRequest>(payload);
                if (request != null)
                {
                    var signal = _trackManager.GetSignalForTrain(request.Name);
                    if (signal != null)
                    {
                        Console.WriteLine($"Signal for {request.Name}: {signal.Speed}");
                        // In real implementation, publish response via MQTT
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling signal request: {ex.Message}");
            }
        }

        // HIÁNYZÓ METÓDUS - HandleTrainSignalChanged
        public void HandleTrainSignalChanged(string payload)
        {
            try
            {
                var signalChange = JsonConvert.DeserializeObject<SignalStateChangeResponse>(payload);
                if (signalChange != null)
                {
                    // When signal changes, request the current signal state for the train
                    var train = _trackManager.Trains.FirstOrDefault(t => t.Name == signalChange.ID);
                    if (train != null)
                    {
                        // Get the current signal for this train
                        var signal = _trackManager.GetSignalForTrain(train.Name);
                        if (signal != null)
                        {
                            // Update train speed based on signal
                            _trainManager.UpdateTrainSpeed(train.Name, signal.Speed);
                            Console.WriteLine($"Signal changed for {train.Name}, speed set to: {signal.Speed}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling train signal changed: {ex.Message}");
            }
        }

        private string ExtractSectionName(string xml)
        {
            var match = System.Text.RegularExpressions.Regex.Match(xml, @"id=""([^""]+)""");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }

    // ============================================================================
    // MQTT MESSAGE DATA CLASSES
    // ============================================================================

    public class HallSensorData
    {
        public string ID { get; set; }
        public bool State { get; set; }
    }

    public class RFIDSensorData
    {
        public string ID { get; set; }
        public string UID { get; set; }
    }

    public class SignalRequest
    {
        public string Name { get; set; }
    }

    public class SignalResponse
    {
        public string ID { get; set; }
        public Speed Speed { get; set; }
        public Speed? NextSpeed { get; set; }
    }

    // ============================================================================
    // UTILITY CLASSES
    // ============================================================================

    public static class Constants
    {
        public static class SafetyProviderStatus
        {
            public const string OK = "OK";
            public const string NOK = "NOK";
            public const string Arrived = "Arrived";
            public const string Delay = "Delay";
        }
    }

    // ============================================================================
    // CONSOLE INTERFACE - Enhanced Speed Command
    // ============================================================================

    public class ConsoleInterface : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public ConsoleInterface(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(2000, stoppingToken);

            Console.WriteLine("\n=== Train Control System Console Interface ===");
            Console.WriteLine("Commands:");
            Console.WriteLine("  status          - Show system status");
            Console.WriteLine("  trains          - List all trains with details");
            Console.WriteLine("  timetable       - Show timetable entries");
            Console.WriteLine("  sections        - Show track sections and occupancy");
            Console.WriteLine("  signals         - Show signal states");
            Console.WriteLine("  start <id>      - Start train with timetable entry ID");
            Console.WriteLine("  start-train <trainId> <stationId> - Manually start train to station");
            Console.WriteLine("  stop <name>     - Stop train by name");
            Console.WriteLine("  speed <name> <STOP|SLOW|MEDIUM|HIGH> - Set train speed");
            Console.WriteLine("  emergency-stop <name> - Emergency stop train");
            Console.WriteLine("  add-entry       - Add new timetable entry");
            Console.WriteLine("  delete-entry <id> - Delete timetable entry by ID");
            Console.WriteLine("  force-schedule  - Force process scheduled trains immediately");
            Console.WriteLine("  clear-arrived   - Clear arrived timetable entries");
            Console.WriteLine("  help            - Show this help");
            Console.WriteLine("  exit            - Exit application");
            Console.WriteLine("===============================================\n");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Console.Write("> ");
                    var input = await ReadLineAsync(stoppingToken);

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var command = parts[0].ToLower();

                    using var scope = _serviceProvider.CreateScope();
                    var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();
                    var trainManager = scope.ServiceProvider.GetRequiredService<TrainManagerService>();

                    switch (command)
                    {
                        case "status":
                            ShowStatus(trackManager, timetableManager);
                            break;

                        case "trains":
                            ShowTrains(trackManager);
                            break;

                        case "timetable":
                            ShowTimetable(timetableManager);
                            break;

                        case "sections":
                            ShowSections(trackManager);
                            break;

                        case "signals":
                            ShowSignals(trackManager);
                            break;

                        case "start":
                            if (parts.Length > 1)
                            {
                                await StartTrainByEntryId(timetableManager, trackManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: start <entryId>");
                            }
                            break;

                        case "start-train":
                            if (parts.Length > 2)
                            {
                                await StartTrainManual(trackManager, parts[1], parts[2]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: start-train <trainId> <stationId>");
                            }
                            break;

                        case "stop":
                            if (parts.Length > 1)
                            {
                                await StopTrain(trackManager, trainManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: stop <trainName>");
                            }
                            break;

                        case "speed":
                            if (parts.Length > 2)
                            {
                                await SetTrainSpeed(trainManager, parts[1], parts[2]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: speed <trainName> <STOP|SLOW|MEDIUM|HIGH>");
                                Console.WriteLine("Available trains:");
                                trainManager.ListTrains();
                            }
                            break;

                        case "emergency-stop":
                            if (parts.Length > 1)
                            {
                                await EmergencyStopTrain(trainManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: emergency-stop <trainName>");
                            }
                            break;

                        case "add-entry":
                            await AddTimetableEntry(timetableManager, trackManager, stoppingToken);
                            break;

                        case "delete-entry":
                            if (parts.Length > 1)
                            {
                                timetableManager.DeleteEntry(parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: delete-entry <entryId>");
                            }
                            break;

                        case "force-schedule":
                            await timetableManager.ProcessScheduledTrains();
                            Console.WriteLine("Scheduled trains processed");
                            break;

                        case "clear-arrived":
                            ClearArrivedEntries(timetableManager);
                            break;

                        case "help":
                            ShowHelp();
                            break;

                        case "exit":
                            Console.WriteLine("Shutting down...");
                            Environment.Exit(0);
                            break;

                        default:
                            Console.WriteLine($"Unknown command: {command}. Type 'help' for available commands.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing command: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }
        }

        private void ShowStatus(TrackManager trackManager, TimetableManager timetableManager)
        {
            Console.WriteLine("\n=== System Status ===");
            Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Train status
            Console.WriteLine($"\nTrains:");
            Console.WriteLine($"  Total: {trackManager.Trains.Count}");
            Console.WriteLine($"  Waiting: {trackManager.Trains.Count(t => t.State == TrainState.Waiting)}");
            Console.WriteLine($"  Moving: {trackManager.Trains.Count(t => t.State == TrainState.Moving)}");
            Console.WriteLine($"  Stopped: {trackManager.Trains.Count(t => t.State == TrainState.Stopped)}");
            Console.WriteLine($"  PrepareToStop: {trackManager.Trains.Count(t => t.State == TrainState.PrepareToStop)}");

            // Track status
            Console.WriteLine($"\nTrack:");
            Console.WriteLine($"  Sections: {trackManager.Sections.Count}");
            Console.WriteLine($"  SubSections: {trackManager.SubSections.Count}");
            Console.WriteLine($"  Occupied SubSections: {trackManager.OccupiedSubSections.Count}");
            Console.WriteLine($"  Platforms: {trackManager.Platforms.Count}");
            Console.WriteLine($"  Signals: {trackManager.Signals.Count}");

            // Timetable status
            Console.WriteLine($"\nTimetable:");
            Console.WriteLine($"  Total Entries: {timetableManager.Entries.Count}");
            Console.WriteLine($"  Upcoming: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Upcoming)}");
            Console.WriteLine($"  In Progress: {timetableManager.Entries.Count(e => e.EntryState == EntryState.InProgress)}");
            Console.WriteLine($"  Arrived: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Arrived)}");
            Console.WriteLine($"  On Time: {timetableManager.Entries.Count(e => e.RouteState == RouteState.InTime)}");
            Console.WriteLine($"  Delayed: {timetableManager.Entries.Count(e => e.RouteState == RouteState.Delay)}");

            // Next scheduled train
            var nextTrain = timetableManager.Entries
                .Where(e => e.EntryState == EntryState.Upcoming)
                .OrderBy(e => e.StartDate)
                .ThenBy(e => e.StartTime)
                .FirstOrDefault();

            if (nextTrain != null)
            {
                Console.WriteLine($"\nNext Scheduled Train:");
                Console.WriteLine($"  Entry ID: {nextTrain.EntryID}");
                Console.WriteLine($"  Train: #{nextTrain.Train_DB_ID}");
                Console.WriteLine($"  Start: {nextTrain.StartDate:yyyy-MM-dd} {nextTrain.StartTime:hh\\:mm}");
            }

            Console.WriteLine("=====================\n");
        }

        private void ShowTrains(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Trains ===");

            if (!trackManager.Trains.Any())
            {
                Console.WriteLine("No trains available.");
                Console.WriteLine("==============\n");
                return;
            }

            foreach (var train in trackManager.Trains.OrderBy(t => t.Name))
            {
                Console.WriteLine($"\n{train.Name} (ID: {train.DB_ID}):");
                Console.WriteLine($"  State: {train.State}");
                Console.WriteLine($"  Speed: {train.CurrentSpeed} (Max: {train.MaxSpeed})");
                Console.WriteLine($"  Direction: {(train.Direction ? "Forward" : "Backward")}");
                Console.WriteLine($"  Active: {(train.IsActive ? "Yes" : "No")}");

                // Location information
                if (train.SubSection != null)
                {
                    Console.WriteLine($"  Current Location: {train.SubSection.Name}");
                    if (train.Section != null)
                    {
                        Console.WriteLine($"  Section: {train.Section.Name}");
                    }
                }
                else
                {
                    Console.WriteLine($"  Current Location: Unknown");
                }

                // Source and destination
                if (train.Source != null)
                {
                    Console.WriteLine($"  Source: {train.Source.Name}");
                }
                if (train.Destination != null)
                {
                    Console.WriteLine($"  Destination: {train.Destination.Name}");
                }

                // Next movement
                if (train.NextSubsection_DB_ID.HasValue)
                {
                    var nextSub = trackManager.SubSections.FirstOrDefault(s => s.DB_ID == train.NextSubsection_DB_ID);
                    if (nextSub != null)
                    {
                        Console.WriteLine($"  Next Subsection: {nextSub.Name}");
                    }
                }

                // Entry information
                if (!string.IsNullOrEmpty(train.CurrentEntryID))
                {
                    Console.WriteLine($"  Current Entry: {train.CurrentEntryID}");
                }

                // Locked resources
                if (train.LockedSections.Any())
                {
                    Console.WriteLine($"  Locked Sections: {string.Join(", ", train.LockedSections)}");
                }
                if (train.LockedSwitches.Any())
                {
                    Console.WriteLine($"  Locked Switches: {string.Join(", ", train.LockedSwitches.Select(s => s.Name))}");
                }
            }
            Console.WriteLine("==============\n");
        }

        private void ShowTimetable(TimetableManager timetableManager)
        {
            Console.WriteLine("\n=== Timetable ===");

            if (!timetableManager.Entries.Any())
            {
                Console.WriteLine("No timetable entries available.");
                Console.WriteLine("=================\n");
                return;
            }

            var entriesByState = timetableManager.Entries
                .OrderBy(e => e.StartDate)
                .ThenBy(e => e.StartTime)
                .GroupBy(e => e.EntryState);

            foreach (var stateGroup in entriesByState)
            {
                Console.WriteLine($"\n{stateGroup.Key}:");
                foreach (var entry in stateGroup)
                {
                    Console.WriteLine($"  {entry.EntryID}:");
                    Console.WriteLine($"    Train: #{entry.Train_DB_ID}");
                    Console.WriteLine($"    From Station: #{entry.SourceStation_DB_ID}");
                    Console.WriteLine($"    To Station: #{entry.DestinationStation_DB_ID}");
                    Console.WriteLine($"    Start: {entry.StartDate:yyyy-MM-dd} {entry.StartTime:hh\\:mm}");
                    Console.WriteLine($"    State: {entry.EntryState}");
                    Console.WriteLine($"    Route: {entry.RouteState}");

                    if (entry.ArrivedTime.HasValue)
                    {
                        Console.WriteLine($"    Arrived: {entry.ArrivedTime.Value:yyyy-MM-dd HH:mm:ss}");
                    }

                    if (entry.EntryState == EntryState.InProgress)
                    {
                        Console.WriteLine($"    Status: Active - monitoring progress");
                    }
                }
            }
            Console.WriteLine("=================\n");
        }

        private void ShowSections(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Track Sections ===");

            foreach (var section in trackManager.Sections.OrderBy(s => s.Name))
            {
                Console.WriteLine($"\n{section.Name} (ID: {section.DB_ID}):");
                Console.WriteLine($"  Active: {(section.isActive ? "Yes" : "No")}");
                Console.WriteLine($"  SubSections: {section.SubSections.Count}");

                foreach (var subSection in section.SubSections.OrderBy(ss => ss.Name))
                {
                    var isOccupied = trackManager.OccupiedSubSections.Any(oss => oss.DB_ID == subSection.DB_ID);
                    var occupyingTrain = trackManager.Trains.FirstOrDefault(t => t.SubSection?.DB_ID == subSection.DB_ID);

                    Console.WriteLine($"    {subSection.Name} (ID: {subSection.DB_ID}):");
                    Console.WriteLine($"      Allowed Speed: {subSection.AllowedSpeed}");
                    Console.WriteLine($"      Occupied: {(isOccupied ? "Yes" : "No")}");

                    if (isOccupied && occupyingTrain != null)
                    {
                        Console.WriteLine($"      Occupied By: {occupyingTrain.Name}");
                    }
                }
            }
            Console.WriteLine("======================\n");
        }

        private void ShowSignals(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Signals ===");

            if (!trackManager.Signals.Any())
            {
                Console.WriteLine("No signals available.");
                Console.WriteLine("==================\n");
                return;
            }

            foreach (var signal in trackManager.Signals.OrderBy(s => s.ID))
            {
                Console.WriteLine($"  {signal.ID}:");
                Console.WriteLine($"    Current Speed: {signal.Speed}");
                Console.WriteLine($"    Next Speed: {signal.NextSpeed?.ToString() ?? "None"}");

                // Find which train this signal is for
                var relatedTrain = trackManager.Trains.FirstOrDefault(t =>
                    trackManager.Objects.Any(o =>
                        o.ObjectID == signal.ID &&
                        o.SubSection_DB_ID == t.SubSection?.DB_ID));

                if (relatedTrain != null)
                {
                    Console.WriteLine($"    Related Train: {relatedTrain.Name}");
                }
            }
            Console.WriteLine("==================\n");
        }

        private async Task StartTrainByEntryId(TimetableManager timetableManager, TrackManager trackManager, string entryId)
        {
            var entry = timetableManager.Entries.FirstOrDefault(e => e.EntryID == entryId);
            if (entry != null)
            {
                Console.WriteLine($"Starting train for entry: {entryId}");
                trackManager.TryStartTrain(entry);

                // Wait a moment and check result
                await Task.Delay(1000);

                var updatedEntry = timetableManager.Entries.FirstOrDefault(e => e.EntryID == entryId);
                if (updatedEntry?.EntryState == EntryState.InProgress)
                {
                    Console.WriteLine($"Train started successfully for entry: {entryId}");
                }
                else
                {
                    Console.WriteLine($"Failed to start train for entry: {entryId}");
                }
            }
            else
            {
                Console.WriteLine($"Entry not found: {entryId}");
            }
        }

        private async Task StartTrainManual(TrackManager trackManager, string trainIdStr, string stationIdStr)
        {
            if (int.TryParse(trainIdStr, out int trainId) && int.TryParse(stationIdStr, out int stationId))
            {
                Console.WriteLine($"Manually starting train {trainId} to station {stationId}");

                // Debug information before starting
                var train = trackManager.Trains.FirstOrDefault(t => t.DB_ID == trainId);
                if (train != null)
                {
                    Console.WriteLine($"Train found: {train.Name}, State: {train.State}, Current Location: {train.SubSection?.Name}");
                }
                else
                {
                    Console.WriteLine($"Train not found with ID: {trainId}");
                    return;
                }

                bool success = trackManager.ManualStartTrain(trainId, stationId);

                if (success)
                {
                    Console.WriteLine($"✓ Train {trainId} started successfully to station {stationId}");
                }
                else
                {
                    Console.WriteLine($"✗ Failed to start train {trainId} to station {stationId}");
                    Console.WriteLine("Possible solutions:");
                    Console.WriteLine("  1. Check if the train is in 'Waiting' state");
                    Console.WriteLine("  2. Check if destination station has available platforms");
                    Console.WriteLine("  3. Check if there's a valid route to the destination");
                    Console.WriteLine("  4. Use 'sections' command to see track layout");
                    Console.WriteLine("  5. Use 'trains' command to see current train states");
                }
            }
            else
            {
                Console.WriteLine("Invalid train ID or station ID. Please use numeric values.");
                Console.WriteLine("Available trains:");
                foreach (var train in trackManager.Trains)
                {
                    Console.WriteLine($"  ID: {train.DB_ID}, Name: {train.Name}, State: {train.State}");
                }
            }
        }

        private async Task StopTrain(TrackManager trackManager, TrainManagerService trainManager, string trainName)
        {
            var train = trackManager.Trains.FirstOrDefault(t => t.Name == trainName);
            if (train != null)
            {
                Console.WriteLine($"Stopping train: {trainName}");
                train.State = TrainState.Stopped;
                trainManager.UpdateTrainSpeed(trainName, Speed.STOP);

                // Also update signals
                var signal = trackManager.GetSignalForTrain(trainName);
                if (signal != null)
                {
                    signal.Speed = Speed.STOP;
                    signal.NextSpeed = null;
                }

                Console.WriteLine($"Train {trainName} stopped");
            }
            else
            {
                Console.WriteLine($"Train not found: {trainName}");
            }
        }

        private async Task SetTrainSpeed(TrainManagerService trainManager, string trainName, string speedStr)
        {
            try
            {
                // First check if train exists
                var train = trainManager.GetTrain(trainName);
                if (train == null)
                {
                    Console.WriteLine($"Train not found: {trainName}");
                    Console.WriteLine("Available trains:");
                    trainManager.ListTrains();
                    return;
                }

                if (Enum.TryParse<Speed>(speedStr, true, out Speed speed))
                {
                    Console.WriteLine($"Setting speed for {trainName} to {speed}");
                    trainManager.UpdateTrainSpeed(trainName, speed);

                    // Verify the change
                    await Task.Delay(100);
                    var updatedTrain = trainManager.GetTrain(trainName);
                    if (updatedTrain != null)
                    {
                        Console.WriteLine($"✓ Speed set to {updatedTrain.CurrentSpeed} for train {trainName}");
                    }
                }
                else
                {
                    Console.WriteLine($"Invalid speed: {speedStr}. Use STOP, SLOW, MEDIUM, or HIGH.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting speed for {trainName}: {ex.Message}");
            }
        }

        private async Task EmergencyStopTrain(TrainManagerService trainManager, string trainName)
        {
            try
            {
                var train = trainManager.GetTrain(trainName);
                if (train == null)
                {
                    Console.WriteLine($"Train not found: {trainName}");
                    return;
                }

                Console.WriteLine($"EMERGENCY STOP for {trainName}");

                // Use the train controller for emergency stop
                trainManager.UpdateTrainSpeed(trainName, Speed.STOP);

                // Also update train state
                train.State = TrainState.Stopped;

                Console.WriteLine($"✓ Emergency stop executed for {trainName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during emergency stop for {trainName}: {ex.Message}");
            }
        }

        private async Task AddTimetableEntry(TimetableManager timetableManager, TrackManager trackManager, CancellationToken ct)
        {
            try
            {
                Console.WriteLine("\n=== Add Timetable Entry ===");

                // Show available trains
                Console.WriteLine("\nAvailable Trains:");
                foreach (var train in trackManager.Trains.Where(t => t.IsActive && t.State == TrainState.Waiting))
                {
                    Console.WriteLine($"  ID: {train.DB_ID}, Name: {train.Name}, Current Location: {train.SubSection?.Name ?? "Unknown"}");
                }

                Console.Write("\nTrain ID: ");
                var trainIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(trainIdInput, out int trainId) || !trackManager.Trains.Any(t => t.DB_ID == trainId))
                {
                    Console.WriteLine("Invalid train ID.");
                    return;
                }

                // Show available stations
                Console.WriteLine("\nAvailable Stations (from database):");
                // You would typically load these from database
                Console.WriteLine("  (Note: Enter station IDs from your database)");

                Console.Write("Source Station ID: ");
                var sourceIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(sourceIdInput, out int sourceId))
                {
                    Console.WriteLine("Invalid source station ID.");
                    return;
                }

                Console.Write("Destination Station ID: ");
                var destIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(destIdInput, out int destId))
                {
                    Console.WriteLine("Invalid destination station ID.");
                    return;
                }

                // Date and time
                Console.Write("Start Date (yyyy-MM-dd) [or 'now' for current time]: ");
                var dateInput = await ReadLineAsync(ct);
                DateTime startDate;
                TimeSpan startTime;

                if (dateInput.Trim().ToLower() == "now")
                {
                    startDate = DateTime.Now.Date;
                    startTime = DateTime.Now.TimeOfDay;
                }
                else
                {
                    if (!DateTime.TryParse(dateInput, out startDate))
                    {
                        Console.WriteLine("Invalid date format. Using today's date.");
                        startDate = DateTime.Now.Date;
                    }

                    Console.Write("Start Time (HH:mm) [or 'now' for current time]: ");
                    var timeInput = await ReadLineAsync(ct);
                    if (timeInput.Trim().ToLower() == "now")
                    {
                        startTime = DateTime.Now.TimeOfDay;
                    }
                    else if (!TimeSpan.TryParse(timeInput, out startTime))
                    {
                        Console.WriteLine("Invalid time format. Using current time.");
                        startTime = DateTime.Now.TimeOfDay;
                    }
                }

                var entry = new TimetableEntries
                {
                    Train_DB_ID = trainId,
                    SourceStation_DB_ID = sourceId,
                    DestinationStation_DB_ID = destId,
                    StartDate = startDate,
                    StartTime = startTime,
                    EntryState = EntryState.Upcoming,
                    RouteState = RouteState.InTime
                };

                bool success = timetableManager.AddEntry(entry);
                if (success)
                {
                    Console.WriteLine($"\nEntry added successfully! Entry ID: {entry.EntryID}");

                    // Check if should start immediately
                    var now = DateTime.Now;
                    var scheduledTime = startDate.Add(startTime);
                    if (scheduledTime <= now.AddMinutes(1))
                    {
                        Console.WriteLine("Entry is scheduled to start now or in the past. Starting train immediately...");
                        trackManager.TryStartTrain(entry);
                    }
                    else
                    {
                        Console.WriteLine($"Train will start automatically at {scheduledTime:yyyy-MM-dd HH:mm}");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to add entry to database.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding entry: {ex.Message}");
            }
        }

        private void ClearArrivedEntries(TimetableManager timetableManager)
        {
            var arrivedEntries = timetableManager.Entries.Where(e => e.EntryState == EntryState.Arrived).ToList();

            if (!arrivedEntries.Any())
            {
                Console.WriteLine("No arrived entries to clear.");
                return;
            }

            Console.WriteLine($"Found {arrivedEntries.Count} arrived entries. Clearing...");

            foreach (var entry in arrivedEntries)
            {
                timetableManager.DeleteEntry(entry.EntryID);
            }

            Console.WriteLine("Arrived entries cleared.");
        }

        private void ShowHelp()
        {
            Console.WriteLine("\n=== Available Commands ===");
            Console.WriteLine("status          - Display system status including trains, track, and timetable");
            Console.WriteLine("trains          - List all trains with detailed information");
            Console.WriteLine("timetable       - Show all timetable entries grouped by state");
            Console.WriteLine("sections        - Display track sections and their occupancy");
            Console.WriteLine("signals         - Show current signal states");
            Console.WriteLine("start <id>      - Start a train using its timetable entry ID");
            Console.WriteLine("start-train <trainId> <stationId> - Manually start a train to a station");
            Console.WriteLine("stop <name>     - Stop a specific train by name");
            Console.WriteLine("speed <name> <speed> - Set train speed (STOP, SLOW, MEDIUM, HIGH)");
            Console.WriteLine("add-entry       - Add a new timetable entry (interactive)");
            Console.WriteLine("delete-entry <id> - Delete a timetable entry by ID");
            Console.WriteLine("force-schedule  - Process scheduled trains immediately");
            Console.WriteLine("clear-arrived   - Remove all arrived timetable entries");
            Console.WriteLine("help            - Show this help message");
            Console.WriteLine("exit            - Exit the application");
            Console.WriteLine("==========================\n");
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            return await Task.Run(() => Console.ReadLine(), ct) ?? string.Empty;
        }
    }
}

// ============================================================================
// CONFIGURATION FILE (appsettings.json)
// ============================================================================
/*
{
  "ConnectionString": "Server=localhost;Database=TrainControllerSystem;Trusted_Connection=True;MultipleActiveResultSets=true;TrustServerCertificate=True",
  "MQTT": {
    "Address": "172.22.2.2",
    "Port": 1883,
    "TrackSectionTopic": "rocrail/service/info/fb",
    "TrackPositionTopic": "track/info/hall",
    "TrackRFIDTopic": "track/info/rfid",
    "TrackCommandTopic": "rocrail/service/client",
    "TrackSignalTopic": "track/command/signal",
    "TrainSignalRequestTopic": "train/signal/request",
    "TrainSignalResponseTopic": "train/signal/response",
    "TrainSignalChangedTopic": "train/signal/changed",
    "TrainSpeedCommandTopic": "rocrail/service/client",
    "TimetableStartRequestTopic": "train/start/request",
    "TimetableStatusTopic": "train/status"
  },
  "Track": {
    "MaxOccupiedSections": 100
  },
  "Timetable": {
    "SchedulerIntervalSeconds": 30,
    "MaxArrivedEntriesToKeep": 3
  },
  "SignalChangedMessage": "SignalChanged"
}
*/
