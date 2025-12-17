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
using MQTTnet;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Service for monitoring train arrivals by processing Rocrail feedback messages
    /// </summary>
    public class TrainArrivalMonitorService : IDisposable
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService? _statusService;
        private readonly ILogger<TrainArrivalMonitorService>? _logger;

        private IMqttClient? _mqttClient;
        private bool _isInitialized = false;
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
            StatusNotificationService? statusService = null,
            ILogger<TrainArrivalMonitorService>? logger = null)
        {
            _scopeFactory = scopeFactory;
            _mqttConfig = mqttConfig;
            _statusService = statusService;
            _logger = logger;
        }

        /// <summary>
        /// Initialize the arrival monitor service
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            lock (_lockObject)
            {
                if (_isInitialized)
                    return true;
            }

            try
            {
                Console.WriteLine("[TrainArrivalMonitor] Initializing arrival monitor service...");
                _statusService?.ShowInfo("Vonat érkezés monitor indítása...");

                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
                    .WithCleanSession()
                    .Build();

                var result = await _mqttClient.ConnectAsync(options);

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    // Subscribe to Rocrail feedback topic (rocrail/service/info)
                    await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                        .WithTopic("rocrail/service/info")
                        .Build());

                    lock (_lockObject)
                    {
                        _isInitialized = true;
                    }

                    Console.WriteLine($"[TrainArrivalMonitor] Connected to MQTT broker {_mqttConfig.Address}:{_mqttConfig.Port}");
                    Console.WriteLine($"[TrainArrivalMonitor] Subscribed to topic: rocrail/service/info");
                    _statusService?.ShowSuccess("Vonat érkezés monitor csatlakoztatva");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[TrainArrivalMonitor] MQTT connection failed: {result.ResultCode}");
                    _statusService?.ShowError($"MQTT kapcsolat hiba: {result.ResultCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Initialization error: {ex.Message}");
                _logger?.LogError(ex, "Failed to initialize TrainArrivalMonitorService");
                _statusService?.ShowError($"Monitor inicializációs hiba: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Start monitoring train arrivals
        /// </summary>
        public async Task StartMonitoringAsync()
        {
            if (!_isInitialized)
            {
                if (!await InitializeAsync())
                    return;
            }

            if (_isRunning)
                return;

            _isRunning = true;

            // Start message processing loop
            _ = Task.Run(ProcessMessagesAsync, _cancellationTokenSource.Token);

            // Start periodic checking loop (1 second interval)
            _ = Task.Run(PeriodicCheckAsync, _cancellationTokenSource.Token);

            Console.WriteLine("[TrainArrivalMonitor] Started monitoring train arrivals");
            _statusService?.ShowInfo("Vonat érkezés figyelés aktív");
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
        /// Process incoming MQTT messages
        /// </summary>
        private async Task ProcessMessagesAsync()
        {
            if (_mqttClient == null)
                return;

            _mqttClient.ApplicationMessageReceivedAsync += async (e) =>
            {
                try
                {
                    var message = e.ApplicationMessage;
                    if (message.Topic == "rocrail/service/info")
                    {
                        // Convert ReadOnlySequence<byte> to string (handle single segment case)
                        var payload = System.Text.Encoding.UTF8.GetString(message.Payload.First.Span);
                        await ProcessRocrailFeedbackAsync(payload);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrainArrivalMonitor] Error processing MQTT message: {ex.Message}");
                    _logger?.LogError(ex, "Error processing MQTT message");
                }
            };

            // Keep the message processing alive
            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                await Task.Delay(1000, _cancellationTokenSource.Token);
            }
        }

        /// <summary>
        /// Process Rocrail feedback XML messages
        /// </summary>
        private async Task ProcessRocrailFeedbackAsync(string feedbackMessage)
        {
            try
            {
                // Check if the message is actually an XML fragment
                if (string.IsNullOrWhiteSpace(feedbackMessage) || !feedbackMessage.Trim().StartsWith("<"))
                    return;

                // Parse the XML safely
                var xml = XElement.Parse(feedbackMessage.Trim());

                // Check if this is an "fb" (sensor/feedback) event
                if (xml.Name == "fb")
                {
                    // Safely retrieve attributes regardless of order
                    string sectionIdStr = xml.Attribute("id")?.Value;

                    if (!string.IsNullOrEmpty(sectionIdStr))
                    {
                        // Rocrail only sends fb when sections become occupied - no need to store state
                        Console.WriteLine($"[TrainArrivalMonitor] 🚂 Section {sectionIdStr} OCCUPIED (fb message received)");

                        // Immediately check if any train has arrived at this section
                        await CheckTrainArrivals(sectionIdStr);
                    }
                }
            }
            catch (System.Xml.XmlException ex)
            {
                // Ignore non-XML messages or malformed data
                Console.WriteLine($"[TrainArrivalMonitor] Ignoring malformed XML: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainArrivalMonitor] Error processing Rocrail feedback: {ex.Message}");
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
                try
                {
                    await Task.Delay(1000, _cancellationTokenSource.Token); // 1 second interval

                    // Check for trains that might have arrived but weren't detected
                    await CheckForMissedArrivalsAsync();
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrainArrivalMonitor] Error in periodic check: {ex.Message}");
                    _logger?.LogError(ex, "Error in periodic check");
                }
            }
        }

        /// <summary>
        /// Check for trains that might have arrived but weren't detected by feedback
        /// </summary>
        private async Task CheckForMissedArrivalsAsync()
        {
            var currentTime = DateTime.Now;
            var trainsToCheck = new List<string>();

            foreach (var kvp in _activeTrains)
            {
                var trainName = kvp.Key;
                var arrivalInfo = kvp.Value;

                // Skip if train has already arrived
                if (arrivalInfo.HasArrived)
                    continue;

                // Check if it's been too long since departure (more than 30 seconds)
                var travelTime = currentTime - arrivalInfo.StartTime;
                if (travelTime.TotalSeconds > 30)
                {
                    trainsToCheck.Add(trainName);
                }
            }

            // Since Rocrail only sends fb when occupied, there's no "missed arrival" concept
            // If we don't receive an fb message, the train hasn't arrived yet
            Console.WriteLine($"[TrainArrivalMonitor] No missed arrivals detected - {trainsToCheck.Count} trains still traveling");
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

            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
            }

            _activeTrains.Clear();
            // No occupancy tracking to clear

            Console.WriteLine("[TrainArrivalMonitor] Arrival monitoring stopped");
            _statusService?.ShowInfo("Vonat érkezés figyelés leállítva");
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Cancel();
            _cancellationTokenSource?.Dispose();
            _mqttClient?.Dispose();
        }
    }
}