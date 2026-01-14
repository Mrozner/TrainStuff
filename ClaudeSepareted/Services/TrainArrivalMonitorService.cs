using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Service for monitoring train arrivals by listening to shared MQTT feedback
    /// </summary>
    public class TrainArrivalMonitorService : IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService? _statusService;
        private readonly ILogger<TrainArrivalMonitorService>? _logger;

        // NEW: Shared Infrastructure Service
        private readonly MqttInfrastructureService _mqttService;

        private bool _isRunning = false;
        private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();
        private readonly object _lockObject = new object();

        // Dictionary to track active trains and their destination sections
        private readonly ConcurrentDictionary<string, TrainArrivalInfo> _activeTrains = new();

        // No need to track occupancy - Rocrail only sends fb when sections are occupied

        public struct TrainArrivalInfo
        {
            public int TrainId { get; set; }
            public string TrainName { get; set; }
            public string DestinationPlatformName { get; set; }
            public DateTime StartTime { get; set; }
            public bool HasArrived { get; set; }
        }

        public TrainArrivalMonitorService(
            IServiceScopeFactory scopeFactory,
            MQTTConfiguration mqttConfig,
            MqttInfrastructureService mqttService, // INJECTED HERE
            StatusNotificationService? statusService = null,
            ILogger<TrainArrivalMonitorService>? logger = null)
        {
            _scopeFactory = scopeFactory;
            _mqttConfig = mqttConfig;
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _statusService = statusService;
            _logger = logger;
        }

  
        /// <summary>
        /// Start monitoring train arrivals
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            if (_isRunning) return;

            // 1. Ensure shared infrastructure is ready
            // The TrackHandlerService usually initializes this, but we double-check here.
            if (!await _mqttService.InitializeAsync())
            {
                Console.WriteLine("[TrainArrivalMonitor] Failed to initialize via Shared Infrastructure");
                return;
            }

            lock (_lockObject)
            {
                if (_isRunning) return;
                _isRunning = true;
            }

            // 2. Subscribe to the shared event
            // This replaces the raw MQTT subscription and message processing loop
            _mqttService.OnRocrailFeedbackReceived += ProcessRocrailFeedbackAsync;

            // 3. Start periodic backup check
            _ = Task.Run(PeriodicCheckAsync, _cancellationTokenSource.Token);

            Console.WriteLine("[TrainArrivalMonitor] Started monitoring train arrivals via Shared Infrastructure");
            _statusService?.ShowInfo("Vonat érkezés figyelés aktív (Shared MQTT)");
        }

        /// <summary>
        /// Register a train for arrival monitoring
        /// </summary>
        public void RegisterTrainArrival(Train train, Platforms destinationPlatform)
        {
            if (train == null || destinationPlatform == null)
                return;

            var arrivalInfo = new TrainArrivalInfo
            {
                TrainId = train.DB_ID,
                TrainName = train.Name,
                DestinationPlatformName = destinationPlatform.Name,
                StartTime = DateTime.Now,
                HasArrived = false
            };

            _activeTrains[train.Name] = arrivalInfo;

            Console.WriteLine($"[TrainArrivalMonitor] Registered arrival monitoring: {train.Name} -> {destinationPlatform.Name} (section DB_ID: {destinationPlatform.SubSection_DB_ID})");
            _statusService?.ShowInfo($"{train.Name} érkezés figyelése: {destinationPlatform.Name}", train.Name);
        }

        /// <summary>
        /// Unregister a train from arrival monitoring
        /// </summary>
        public void UnregisterTrainArrival(string trainName)
        {
            if (_activeTrains.TryRemove(trainName, out var arrivalInfo))
            {
                Console.WriteLine($"[TrainArrivalMonitor] Unregistered arrival monitoring: {trainName}");
                _statusService?.ShowInfo($"{trainName} érkezés figyelése leállítva", trainName);
            }
        }

        /// <summary>
        /// Process Rocrail feedback messages received from the shared service
        /// </summary>
        private async Task ProcessRocrailFeedbackAsync(string feedbackMessage)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(feedbackMessage) || !feedbackMessage.Trim().StartsWith("<"))
                    return;

                // Parse the XML
                // Note: We create a new XElement here. Ideally, MqttInfrastructure could pass XElement,
                // but passing string ensures thread safety and decoupling.
                var xml = XElement.Parse(feedbackMessage.Trim());

                // Check for "fb" (feedback) events
                if (xml.Name == "fb")
                {
                    string sectionIdStr = xml.Attribute("id")?.Value;

                    if (!string.IsNullOrEmpty(sectionIdStr))
                    {
                        // Logic preserved from original file
                        Console.WriteLine($"[TrainArrivalMonitor] 🚂 Section {sectionIdStr} OCCUPIED (fb message received)");
                        await CheckTrainArrivals(sectionIdStr);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error processing feedback: {ex.Message}");
                _logger?.LogError(ex, "Error processing Rocrail feedback");
            }
        }

        /// <summary>
        /// Check if any train has arrived at its destination
        /// </summary>
        private async Task CheckTrainArrivals(string occupiedSectionName)
        {
            // Since Rocrail only sends fb when occupied, this is an immediate arrival event
            Console.WriteLine($"[TrainArrivalMonitor] 🔍 Checking if any train destination section: {occupiedSectionName}");

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // First, find the SubSection by its name to get its integer ID
                var occupiedSubSection = await dbContext.SubSections
                    .FirstOrDefaultAsync(ss => ss.Name == occupiedSectionName);

                if (occupiedSubSection == null)
                {
                    Console.WriteLine($"[TrainArrivalMonitor] ℹ️ Section {occupiedSectionName} not found in database");
                    return;
                }

                Console.WriteLine($"[TrainArrivalMonitor] 📍 Found section {occupiedSectionName} with DB_ID: {occupiedSubSection.DB_ID}");

                // First, get all timetable entries for platforms with this SubSection_DB_ID (database-side filtering)
                var candidateEntries = await dbContext.TimetableEntries
                    .Include(te => te.Train)
                    .Include(te => te.DestinationPlatform)
                    .Where(te => te.DestinationPlatform.SubSection_DB_ID == occupiedSubSection.DB_ID)
                    .ToListAsync();

                // Then filter in memory for trains that are in Moving state (client-side filtering)
                var activeEntries = candidateEntries.Where(te => te.Train.State == TrainState.Moving).ToList();

                foreach (var timetableEntry in activeEntries)
                {
                    var trainName = timetableEntry.Train.Name;
                    Console.WriteLine($"[TrainArrivalMonitor] 🎯 TRAIN {trainName} ARRIVED at section {occupiedSectionName} (ID: {occupiedSubSection.DB_ID}) (Platform: {timetableEntry.DestinationPlatform.Name})!");
                    await HandleTrainArrival(trainName);
                    break; // Only one train can occupy a section at a time
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error checking train arrivals: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle train arrival at destination
        /// </summary>
        private async Task HandleTrainArrival(string trainName)
        {
            if (!_activeTrains.TryGetValue(trainName, out var arrivalInfo))
                return;

            try
            {
                Console.WriteLine($"[TrainArrivalMonitor] 🚂 TRAIN ARRIVED: {trainName} at {arrivalInfo.DestinationPlatformName}!");
                _statusService?.ShowSuccess($"🚂 {trainName} ÉRKEZETT: {arrivalInfo.DestinationPlatformName}", trainName);

                // Mark as arrived
                arrivalInfo.HasArrived = true;
                _activeTrains[trainName] = arrivalInfo;

                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get the train from database
                var train = await dbContext.Trains
                    .FirstOrDefaultAsync(t => t.DB_ID == arrivalInfo.TrainId);

                if (train != null)
                {
                    // Stop the train by setting speed to 0
                    await StopTrainAsync(train);

                    // Create timetable arrived entry
                    await CreateTimetableArrivedEntryAsync(dbContext, arrivalInfo);

                    // Remove from active monitoring after arrival
                    UnregisterTrainArrival(trainName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error handling train arrival for {trainName}: {ex.Message}");
                _logger?.LogError(ex, "Error handling train arrival for {TrainName}", trainName);
            }
        }

        /// <summary>
        /// Stop a train by setting speed to 0
        /// </summary>
        private async Task StopTrainAsync(Train train)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var trainManager = scope.ServiceProvider.GetRequiredService<TrainManagerService>();

                Console.WriteLine($"[TrainArrivalMonitor] Stopping train {train.Name} - has arrived at destination");

                // Stop the train using the TrainManagerService
                trainManager.Stop();

                // Wait a moment for the command to be processed
                await Task.Delay(1000);

                Console.WriteLine($"[TrainArrivalMonitor] Train {train.Name} has been stopped");
                _statusService?.ShowInfo($"{train.Name} megállítva - célállomáson", train.Name);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error stopping train {train.Name}: {ex.Message}");
                _logger?.LogError(ex, "Error stopping train {TrainName}", train.Name);
            }
        }

        /// <summary>
        /// Create a TimetableEntriesArrived entry
        /// </summary>
        private async Task CreateTimetableArrivedEntryAsync(ApplicationDbContext dbContext, TrainArrivalInfo arrivalInfo)
        {
            try
            {
                // Find the corresponding timetable entry
                var timetableEntry = await dbContext.TimetableEntries
                    .Where(te => te.Train_DB_ID == arrivalInfo.TrainId)
                    .OrderByDescending(te => te.StartTime)
                    .FirstOrDefaultAsync();

                if (timetableEntry != null)
                {
                    var arrivedEntry = new TimetableEntriesArrived
                    {
                        Train_DB_ID = arrivalInfo.TrainId,
                        SourcePlatform_DB_ID = timetableEntry.SourcePlatform_DB_ID,
                        DestinationPlatform_DB_ID = timetableEntry.DestinationPlatform_DB_ID,
                        StartDate = timetableEntry.StartDate,
                        StartTime = timetableEntry.StartTime,
                        ArrivedTime = DateTime.Now,
                        RouteState = RouteState.InTime
                    };

                    dbContext.TimetableEntriesArrived.Add(arrivedEntry);
                    await dbContext.SaveChangesAsync();

                    Console.WriteLine($"[TrainArrivalMonitor] Created arrived entry for {arrivalInfo.TrainName}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error creating arrived entry: {ex.Message}");
                _logger?.LogError(ex, "Error creating arrived entry");
            }
        }

        /// <summary>
        /// Periodic check to handle any missed arrivals (backup mechanism)
        /// </summary>
        private async Task PeriodicCheckAsync()
        {
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try {
                    await Task.Delay(1000, _cancellationTokenSource.Token);
                    // Placeholder for any additional safety checks (previously CheckForMissedArrivals)
                } catch { break; }
            }
        }

        /// <summary>
        /// Get current status of all monitored trains
        /// </summary>
        public Dictionary<string, TrainArrivalInfo> GetMonitoredTrains()
        {
            return new Dictionary<string, TrainArrivalInfo>(_activeTrains);
        }

        // No need to track occupancy - Rocrail only sends fb when sections are occupied
        // If you receive an fb message for a section, that section is occupied at that moment

        /// <summary>
        /// Stop monitoring and cleanup resources
        /// </summary>
        public async Task StopAsync()
        {
            _isRunning = false;
            _cancellationTokenSource.Cancel();

            // CLEANUP: Unsubscribe from the shared service
            _mqttService.OnRocrailFeedbackReceived -= ProcessRocrailFeedbackAsync;

            _activeTrains.Clear();
            Console.WriteLine("[TrainArrivalMonitor] Arrival monitoring stopped");
            _statusService?.ShowInfo("Vonat érkezés figyelés leállítva");
        }

        public void Dispose()
        {
            if (_mqttService != null)
            {
                 _mqttService.OnRocrailFeedbackReceived -= ProcessRocrailFeedbackAsync;
            }
            _cancellationTokenSource?.Dispose();
        }
    }
}