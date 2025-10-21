using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.MQTT;

namespace TrainControlSystem.Services
{
    /// <summary>
    /// Manages track state, sections, and signals
    /// </summary>
    public class TrackManager
    {
        private readonly ILogger<TrackManager> _logger;
        private readonly SystemConfiguration _config;
        private readonly object _lockObject = new object();

        // Track data
        public List<Sections> Sections { get; private set; } = new List<Sections>();
        public List<SubSections> SubSections { get; private set; } = new List<SubSections>();
        public List<Signals> Signals { get; private set; } = new List<Signals>();
        public List<Trains> Trains { get; private set; } = new List<Trains>();
        public List<Stations> Stations { get; private set; } = new List<Stations>();

        // MQTT connectors (set after initialization)
        private TrackMQTTConnector? _trackConnector;
        private TimetableMQTTConnector? _timetableConnector;

        public TrackManager(ILogger<TrackManager> logger, SystemConfiguration config)
        {
            _logger = logger;
            _config = config;
            InitializeTrackData();
        }

        /// <summary>
        /// Sets MQTT connectors after they are created
        /// </summary>
        public void SetMqttConnectors(TrackMQTTConnector trackConnector, TimetableMQTTConnector timetableConnector)
        {
            _trackConnector = trackConnector;
            _timetableConnector = timetableConnector;
            _logger.LogInformation("MQTT connectors set for TrackManager");
        }

        /// <summary>
        /// Initialize default track data
        /// </summary>
        private void InitializeTrackData()
        {
            // Initialize stations
            Stations = new List<Stations>
            {
                new Stations { DB_ID = 1, Name = "Central Station", Description = "Main central station" },
                new Stations { DB_ID = 2, Name = "North Station", Description = "Northern terminus" },
                new Stations { DB_ID = 3, Name = "South Station", Description = "Southern terminus" },
                new Stations { DB_ID = 4, Name = "East Station", Description = "Eastern branch" },
                new Stations { DB_ID = 5, Name = "West Station", Description = "Western branch" }
            };

            // Initialize sections
            Sections = new List<Sections>
            {
                new Sections { DB_ID = 1, Name = "Main Line", IsOccupied = false },
                new Sections { DB_ID = 2, Name = "North Branch", IsOccupied = false },
                new Sections { DB_ID = 3, Name = "South Branch", IsOccupied = false },
                new Sections { DB_ID = 4, Name = "East Branch", IsOccupied = false },
                new Sections { DB_ID = 5, Name = "West Branch", IsOccupied = false }
            };

            // Initialize subsections
            SubSections = new List<SubSections>();
            int subSectionId = 1;
            foreach (var section in Sections)
            {
                for (int i = 1; i <= 3; i++)
                {
                    SubSections.Add(new SubSections
                    {
                        DB_ID = subSectionId++,
                        Name = $"{section.Name} - Block {i}",
                        IsOccupied = false,
                        SectionId = section.DB_ID
                    });
                }
            }

            // Initialize trains
            Trains = new List<Trains>
            {
                new Trains { DB_ID = 1, Name = "Express 101", State = TrainState.Stopped, CurrentSpeed = Speed.STOP, MaxSpeed = Speed.FAST },
                new Trains { DB_ID = 2, Name = "Local 202", State = TrainState.Stopped, CurrentSpeed = Speed.STOP, MaxSpeed = Speed.MEDIUM },
                new Trains { DB_ID = 3, Name = "Freight 303", State = TrainState.Stopped, CurrentSpeed = Speed.STOP, MaxSpeed = Speed.SLOW }
            };

            // Initialize signals
            Signals = new List<Signals>();
            int signalId = 1;
            foreach (var subSection in SubSections)
            {
                Signals.Add(new Signals
                {
                    DB_ID = signalId++,
                    Name = $"Signal {subSection.Name}",
                    State = SignalState.RED,
                    Speed = Speed.STOP,
                    SubSectionId = subSection.DB_ID
                });
            }

            _logger.LogInformation($"Track data initialized: {Stations.Count} stations, {Sections.Count} sections, {SubSections.Count} subsections, {Trains.Count} trains, {Signals.Count} signals");
        }

        /// <summary>
        /// Updates train position and notifies via MQTT
        /// </summary>
        public void UpdateTrainPosition(Trains train, SubSections newSubSection)
        {
            lock (_lockObject)
            {
                // Clear previous position
                if (train.SubSectionId.HasValue)
                {
                    var oldSubSection = SubSections.FirstOrDefault(s => s.DB_ID == train.SubSectionId.Value);
                    if (oldSubSection != null)
                    {
                        oldSubSection.IsOccupied = false;
                        oldSubSection.TrainId = null;
                    }
                }

                // Set new position
                train.SubSectionId = newSubSection.DB_ID;
                train.SubSection = newSubSection;
                newSubSection.IsOccupied = true;
                newSubSection.TrainId = train.DB_ID;

                _logger.LogInformation($"Train {train.Name} moved to {newSubSection.Name}");

                // Publish via MQTT
                _trackConnector?.PublishTrackPosition(newSubSection, train);
            }
        }

        /// <summary>
        /// Gets signal for a specific train
        /// </summary>
        public Signals? GetSignalForTrain(string trainName)
        {
            lock (_lockObject)
            {
                var train = Trains.FirstOrDefault(t => t.Name == trainName);
                if (train?.SubSectionId == null) return null;

                return Signals.FirstOrDefault(s => s.SubSectionId == train.SubSectionId.Value);
            }
        }

        /// <summary>
        /// Updates signal state
        /// </summary>
        public void UpdateSignal(int signalId, SignalState state, Speed speed)
        {
            lock (_lockObject)
            {
                var signal = Signals.FirstOrDefault(s => s.DB_ID == signalId);
                if (signal != null)
                {
                    signal.State = state;
                    signal.Speed = speed;
                    _logger.LogInformation($"Signal {signal.Name} updated to {state} with speed {speed}");

                    // Publish via MQTT
                    _trackConnector?.PublishSignalChange(signal);
                }
            }
        }

        /// <summary>
        /// Plans route from start to destination
        /// </summary>
        public List<SubSections> PlanRoute(SubSections start, SubSections destination)
        {
            // Simplified route planning - in real implementation would use pathfinding algorithms
            lock (_lockObject)
            {
                var route = new List<SubSections>();
                var currentSection = start.Section;
                var destinationSection = destination.Section;

                if (currentSection.DB_ID == destinationSection.DB_ID)
                {
                    // Same section - simple route
                    var currentIndex = SubSections.IndexOf(start);
                    var destIndex = SubSections.IndexOf(destination);
                    var step = currentIndex < destIndex ? 1 : -1;

                    for (int i = currentIndex; i != destIndex; i += step)
                    {
                        route.Add(SubSections[i]);
                    }
                    route.Add(destination);
                }
                else
                {
                    // Different sections - simplified routing
                    route.Add(start);
                    route.Add(destination);
                }

                return route;
            }
        }

        /// <summary>
        /// Gets available subsections for train placement
        /// </summary>
        public List<SubSections> GetAvailableSubSections()
        {
            lock (_lockObject)
            {
                return SubSections.Where(s => !s.IsOccupied).ToList();
            }
        }

        /// <summary>
        /// Gets train by name
        /// </summary>
        public Trains? GetTrain(string trainName)
        {
            lock (_lockObject)
            {
                return Trains.FirstOrDefault(t => t.Name == trainName);
            }
        }

        /// <summary>
        /// Gets subsection by ID
        /// </summary>
        public SubSections? GetSubSection(int id)
        {
            lock (_lockObject)
            {
                return SubSections.FirstOrDefault(s => s.DB_ID == id);
            }
        }

        /// <summary>
        /// Gets station by ID
        /// </summary>
        public Stations? GetStation(int id)
        {
            lock (_lockObject)
            {
                return Stations.FirstOrDefault(s => s.DB_ID == id);
            }
        }
    }
}