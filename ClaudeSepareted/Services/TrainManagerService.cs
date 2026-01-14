using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Services
{
    public class TrainManagerService
    {
        private readonly VirtualClock _virtualClock;
        private readonly Train _train;
        private readonly TimetableEntries _timetableEntry;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService _statusService;
        private readonly IServiceProvider _serviceProvider;
        private readonly TrackHandlerService _trackHandlerService;
        private readonly MqttInfrastructureService _mqttService;
        private readonly CancellationTokenSource _cancellationTokenSource;
        private volatile bool _isCompleted;
        private string? _lastSpeedCommandSent = null;
        private string? _lastPowerCommandSent = null;

        // Destination monitoring fields
        private string? _destinationSectionName;
        private bool _destinationMonitoringActive = false;
        // No occupancy tracking needed - Rocrail only sends fb when sections are occupied

        // Switch reservation fields
        private readonly List<string> _reservedSwitches;
        private readonly UnifiedPathfindingService _unifiedPathfinder;

        public bool IsCompleted => _isCompleted;

        public TrainManagerService(
            VirtualClock virtualClock,
            Train train,
            TimetableEntries timetableEntry,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            MqttInfrastructureService mqttService,
            TrackHandlerService trackHandlerService = null,
            List<string> reservedSwitches = null,
            UnifiedPathfindingService unifiedPathfinder = null)
        {
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _train = train ?? throw new ArgumentNullException(nameof(train));
            _timetableEntry = timetableEntry ?? throw new ArgumentNullException(nameof(timetableEntry));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _trackHandlerService = trackHandlerService;
            _unifiedPathfinder = unifiedPathfinder;
            _reservedSwitches = reservedSwitches ?? new List<string>();
            _cancellationTokenSource = new CancellationTokenSource();
            _isCompleted = false;
            _lastSpeedCommandSent = null; // Reset speed command history for new journey
            _lastPowerCommandSent = null; // Reset power command history for new journey

            // Initialize destination section name for arrival monitoring
            _ = Task.Run(InitializeDestinationSectionNameAsync);
        }

        public void Start()
        {
            var thread = new Thread(() => Run(_cancellationTokenSource.Token))
            {
                IsBackground = true,
                Name = $"TrainManager-{_train.Name}"
            };
            thread.Start();
        }

        public void Stop()
        {
            Console.WriteLine($"[TrainManager-{_train.Name}] 🛑 Stop requested - stopping train and terminating manager");
            _destinationMonitoringActive = false; // Stop destination monitoring
            StopTrainNow();
            _cancellationTokenSource.Cancel();
        }

        /// <summary>
        /// Immediately stop the train by sending stop command
        /// </summary>
        private async void StopTrainNow()
        {
            try
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] 🚦 Sending immediate STOP command to train {_train.Name}");
                await SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);

                // Also send power off command to fully stop the train
                await SendTrainPowerCommandAsync(false);

                _train.CurrentSpeed = Speed.STOP;
                _train.State = TrainState.Stopped;

                Console.WriteLine($"[TrainManager-{_train.Name}] ✅ Train {_train.Name} stopped successfully");

                // Release switches held by this train
                if (_unifiedPathfinder != null)
                {
                    _unifiedPathfinder.ReleaseSwitches(_train.Name);
                    Console.WriteLine($"[TrainManager-{_train.Name}] Released switches for {_train.Name}");
                }

                // Notify TrackHandlerService to reprocess waiting trains
                if (_trackHandlerService != null)
                {
                    _trackHandlerService.OnTrainCompleted(_train.Name);
                    Console.WriteLine($"[TrainManager-{_train.Name}] Notified TrackHandler that {_train.Name} completed");
                }
                _statusService?.ShowSuccess($"Megállt: {_train.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] ❌ Error stopping train {_train.Name}: {ex.Message}");
                _statusService?.ShowError($"Vonat megállási hiba: {_train.Name} - {ex.Message}");
            }
        }

        
        private async Task SendTrainCommandAsync(string command, Speed speed = Speed.STOP, bool direction = true)
        {
            try
            {
                int speedValue = (int)speed;

                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.TrainVelocity(_train.Name, speedValue, direction);

                // Check if this speed command is different from the last one sent (per-train check)
                Console.WriteLine($"[TrainManager-{_train.Name}] Speed command check - Last: '{_lastSpeedCommandSent}', Current: '{rocrailCommand}'");
                if (_lastSpeedCommandSent == rocrailCommand)
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Skipping duplicate speed command: {rocrailCommand}");
                    return;
                }

                // Additional global check if TrackHandlerService is available
                if (_trackHandlerService != null && !_trackHandlerService.ShouldSendTrainCommand(_train.Name, rocrailCommand))
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Global duplicate check blocked command: {rocrailCommand}");
                    return;
                }

                Console.WriteLine($"[TrainManager-{_train.Name}] Sending train speed command: {rocrailCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrainSpeedCommandTopic, rocrailCommand);

                if (success)
                {
                    // Store the speed command that was sent
                    _lastSpeedCommandSent = rocrailCommand;
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Failed to publish speed command via MQTT infrastructure");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending train command: {ex.Message}");
            }
        }

        private async Task SendSystemPowerCommandAsync()
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.SystemPower(true);

                Console.WriteLine($"[TrainManager-{_train.Name}] Sending system power command: {powerCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrainSpeedCommandTopic, powerCommand);

                if (success)
                {
                    await Task.Delay(1000); // Wait for power to come on
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Failed to publish power command via MQTT infrastructure");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending power command: {ex.Message}");
            }
        }

        private async Task InitializeDestinationSectionNameAsync()
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // Get the destination platform with its SubSection
                var destinationPlatform = await dbContext.Platforms
                    .Include(p => p.SubSection)
                    .FirstOrDefaultAsync(p => p.DB_ID == _timetableEntry.DestinationPlatform_DB_ID);

                if (destinationPlatform?.SubSection != null)
                {
                    _destinationSectionName = destinationPlatform.SubSection.Name;
                    Console.WriteLine($"[TrainManager-{_train.Name}] Destination section set to: {_destinationSectionName} (SubSection name)");
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Could not determine destination section name");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error initializing destination section name: {ex.Message}");
            }
        }

        
        private async Task ProcessRocrailFeedbackAsync(string feedbackMessage)
        {
            try
            {
                // Check if the message is actually an XML fragment
                if (string.IsNullOrWhiteSpace(feedbackMessage) || !feedbackMessage.Trim().StartsWith("<"))
                    return;

                var processedCount = 0;
                var payload = feedbackMessage.Trim();

                Console.WriteLine($"[TrainManager-{_train.Name}] Processing Rocrail feedback payload: {payload}");

                // Handle multiple XML elements in one message
                // Rocrail can send multiple fb elements in one message like: <fb id="1" state="true"/><fb id="2" state="false"/>
                if (payload.Contains("<fb"))
                {
                    // Try to parse as document first (if there's a root element)
                    try
                    {
                        var doc = XDocument.Parse(payload);
                        foreach (var element in doc.Descendants())
                        {
                            if (await ProcessFeedbackElement(element))
                            {
                                processedCount++;
                            }
                        }
                    }
                    catch (System.Xml.XmlException)
                    {
                        // If document parsing fails, treat as multiple root elements
                        processedCount += await ProcessMultipleRootElements(payload);
                    }
                }

                if (processedCount > 0)
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Processed {processedCount} feedback elements from Rocrail");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error processing Rocrail feedback: {ex.Message}");
            }
        }

        private async Task<bool> ProcessFeedbackElement(XElement element)
        {
            try
            {
                // Check if this is an "fb" (sensor/feedback) event
                if (element.Name == "fb")
                {
                    // Safely retrieve the section id attribute
                    string sectionId = element.Attribute("id")?.Value;

                    if (!string.IsNullOrEmpty(sectionId))
                    {
                        // Rocrail only sends fb when sections become occupied - immediate arrival event
                        Console.WriteLine($"[TrainManager-{_train.Name}] 🚂 Section {sectionId} OCCUPIED (fb message received)");

                        // Check if this is our destination section and train is moving
                        if (!string.IsNullOrEmpty(_destinationSectionName) &&
                            _train.State == TrainState.Moving &&
                            sectionId == _destinationSectionName)
                        {
                            Console.WriteLine($"[TrainManager-{_train.Name}] 🎯 DESTINATION REACHED! Section {_destinationSectionName} - stopping train NOW!");
                            _statusService?.ShowSuccess($"🚂 {_train.Name} ÉRKEZETT: {_destinationSectionName}", _train.Name);

                            // Stop the train immediately in the feedback handler
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    Console.WriteLine($"[TrainManager-{_train.Name}] 🛑 IMMEDIATE STOP COMMANDS");
                                    await SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);
                                    await SendTrainPowerCommandAsync(false);

                                    // Update train state immediately
                                    _train.CurrentSpeed = Speed.STOP;
                                    _train.State = TrainState.Arrived;
                                    Console.WriteLine($"[TrainManager-{_train.Name}] ✅ Train stopped successfully at destination");

                                    // Release switches held by this train
                                    if (_unifiedPathfinder != null)
                                    {
                                        _unifiedPathfinder.ReleaseSwitches(_train.Name);
                                        Console.WriteLine($"[TrainManager-{_train.Name}] Released switches for {_train.Name}");
                                    }

                                    // Notify TrackHandlerService to reprocess waiting trains
                                    if (_trackHandlerService != null)
                                    {
                                        _trackHandlerService.OnTrainCompleted(_train.Name);
                                        Console.WriteLine($"[TrainManager-{_train.Name}] Notified TrackHandler that {_train.Name} completed");
                                    }

                                    // Save arrival record to database
                                    await SaveArrivalRecordAsync();

                                    // Stop monitoring
                                    _destinationMonitoringActive = false;
                                    Console.WriteLine($"[TrainManager-{_train.Name}] 🎯 ARRIVAL COMPLETE!");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[TrainManager-{_train.Name}] Error stopping train on arrival: {ex.Message}");
                                }
                            });

                            // Stop the destination monitoring loop
                            _destinationMonitoringActive = false;
                        }

                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] fb element missing id attribute: {element}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error processing feedback element: {ex.Message}");
            }

            return false;
        }

        private async Task<int> ProcessMultipleRootElements(string payload)
        {
            var processedCount = 0;
            try
            {
                // Split by XML element boundaries and process each element
                // This handles cases like: <fb id="1" state="true"/><fb id="2" state="false"/>
                var elementStartIndex = 0;
                var elementEndIndex = 0;

                while (elementStartIndex < payload.Length)
                {
                    // Find next element start
                    elementStartIndex = payload.IndexOf('<', elementStartIndex);
                    if (elementStartIndex == -1) break;

                    // Find matching end
                    var elementDepth = 0;
                    var currentPos = elementStartIndex;

                    while (currentPos < payload.Length)
                    {
                        if (payload[currentPos] == '<')
                        {
                            if (currentPos + 1 < payload.Length && payload[currentPos + 1] == '/')
                            {
                                // Closing tag
                                elementDepth--;
                            }
                            else if (currentPos + 1 < payload.Length && payload[currentPos + 1] != '?' && payload[currentPos + 1] != '!')
                            {
                                // Opening tag
                                elementDepth++;
                            }
                        }

                        if (elementDepth == 0 && payload[currentPos] == '>')
                        {
                            elementEndIndex = currentPos + 1;
                            break;
                        }

                        currentPos++;
                    }

                    if (elementEndIndex > elementStartIndex)
                    {
                        var elementXml = payload.Substring(elementStartIndex, elementEndIndex - elementStartIndex);

                        try
                        {
                            var element = XElement.Parse(elementXml);
                            if (await ProcessFeedbackElement(element))
                            {
                                processedCount++;
                            }
                        }
                        catch (System.Xml.XmlException)
                        {
                            // Skip malformed elements
                            Console.WriteLine($"[TrainManager-{_train.Name}] Skipping malformed XML element: {elementXml}");
                        }

                        elementStartIndex = elementEndIndex;
                    }
                    else
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error processing multiple root elements: {ex.Message}");
            }

            return processedCount;
        }

        private async Task SaveArrivalRecordAsync()
        {
            try
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] 💾 Saving arrival record to database");

                // Mark timetable entry as completed
                _timetableEntry.EntryState = EntryState.Arrived;
                _timetableEntry.ArrivedTime = _virtualClock.CurrentTime;

                // Save to database
                using (var scope = _serviceProvider.CreateScope())
                {
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var trackedEntry = dbContext.TimetableEntries.Find(_timetableEntry.DB_ID);
                    if (trackedEntry != null)
                    {
                        trackedEntry.EntryState = EntryState.Arrived;
                        trackedEntry.ArrivedTime = _virtualClock.CurrentTime;
                        await dbContext.SaveChangesAsync();
                        Console.WriteLine($"[TrainManager-{_train.Name}] ✅ Arrival record saved successfully");
                    }
                    else
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] ❌ Could not find timetable entry to update");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] 💥 Error saving arrival record: {ex.Message}");
            }
        }

        private async Task MonitorDestinationSectionAsync()
        {
            Console.WriteLine($"[TrainManager-{_train.Name}] Starting destination section monitoring (0.2s intervals) - listening for fb messages on rocrail/service/info");
            _destinationMonitoringActive = true;

            while (_destinationMonitoringActive && !_cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    // Check every 200ms as requested
                    await Task.Delay(200, _cancellationTokenSource.Token);

                    if (!_destinationMonitoringActive)
                        break;

                    // Arrival is handled directly in ProcessFeedbackElementAsync - no need for polling check
                    // If destinationMonitoringActive becomes false, it means train has arrived and stopped
                    if (!_destinationMonitoringActive)
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] ✅ Arrival detected - stopping destination monitoring");
                        return; // Exit Run method when train has arrived
                    }
                }
                catch (OperationCanceledException)
                {
                    // Expected when shutting down
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Error in destination monitoring: {ex.Message}");
                }
            }

            Console.WriteLine($"[TrainManager-{_train.Name}] Destination section monitoring stopped");
        }

        private async Task StopTrainAtDestinationAsync()
        {
            try
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] 🛑 Stopping train at destination section {_destinationSectionName}");

                // Send stop command
                await SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);

                // Also send power off command
                await SendTrainPowerCommandAsync(false);

                // Update train state
                _train.CurrentSpeed = Speed.STOP;
                _train.State = TrainState.Arrived;

                // Mark timetable entry as completed and save to database
                _timetableEntry.EntryState = EntryState.Arrived;
                _timetableEntry.ArrivedTime = _virtualClock.CurrentTime;

                // Save the Arrived state to database
                try
                {
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        var trackedEntry = dbContext.TimetableEntries.Find(_timetableEntry.DB_ID);
                        if (trackedEntry != null)
                        {
                            trackedEntry.EntryState = EntryState.Arrived;
                            trackedEntry.ArrivedTime = _virtualClock.CurrentTime;
                            dbContext.SaveChanges();
                            Console.WriteLine($"[TrainManager-{_train.Name}] Arrived state saved to database");
                        }
                    }
                }
                catch (Exception dbEx)
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Error saving arrived state to database: {dbEx.Message}");
                }

                Console.WriteLine($"[TrainManager-{_train.Name}] ✅ Train {_train.Name} successfully stopped at destination");
                _statusService?.ShowSuccess($"Megállt: {_train.Name} ( célállomáson)");

                // Release switches held by this train
                if (_unifiedPathfinder != null)
                {
                    _unifiedPathfinder.ReleaseSwitches(_train.Name);
                    Console.WriteLine($"[TrainManager-{_train.Name}] Released switches for {_train.Name}");
                }

                // Notify TrackHandlerService to reprocess waiting trains
                if (_trackHandlerService != null)
                {
                    _trackHandlerService.OnTrainCompleted(_train.Name);
                    Console.WriteLine($"[TrainManager-{_train.Name}] Notified TrackHandler that {_train.Name} completed");
                }

                // Stop the train manager
                _destinationMonitoringActive = false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error stopping train at destination: {ex.Message}");
                _statusService?.ShowError($"Hiba a megállás során: {_train.Name} - {ex.Message}");
            }
        }

        private async Task SendTrainPowerCommandAsync(bool powerOn)
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.TrainPower(_train.Name, powerOn);

                // Check if this power command is different from the last power command sent
                Console.WriteLine($"[TrainManager-{_train.Name}] Power command check - Last: '{_lastPowerCommandSent}', Current: '{powerCommand}'");
                if (_lastPowerCommandSent == powerCommand)
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Skipping duplicate power command: {powerCommand}");
                    await Task.Delay(500); // Still wait to maintain timing
                    return;
                }

                // Additional global check if TrackHandlerService is available
                if (_trackHandlerService != null && !_trackHandlerService.ShouldSendTrainPowerCommand(_train.Name, powerOn))
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Global power check blocked command: {powerCommand}");
                    await Task.Delay(500); // Still wait to maintain timing
                    return;
                }

                Console.WriteLine($"[TrainManager-{_train.Name}] Sending train power command: {powerCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_mqttConfig.TrainSpeedCommandTopic, powerCommand);

                if (success)
                {
                    // Store the power command that was sent
                    _lastPowerCommandSent = powerCommand;
                    await Task.Delay(500); // Wait for train power to change
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Failed to publish power command via MQTT infrastructure");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending train power command: {ex.Message}");
            }
        }

        private async void Run(CancellationToken cancellationToken)
        {
            // Subscribe to the centralized feedback stream
            Func<string, Task> feedbackHandler = async (payload) => await ProcessRocrailFeedbackAsync(payload);

            try
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Starting journey management");

                _mqttService.OnRocrailFeedbackReceived += feedbackHandler;

                // Ensure the centralized connection is ready
                if (!await _mqttService.InitializeAsync())
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] MQTT initialization failed via Infrastructure Service");
                    _mqttService.OnRocrailFeedbackReceived -= feedbackHandler;
                    _isCompleted = true;
                    return;
                }

                Console.WriteLine($"[TrainManager-{_train.Name}] Connected to centralized MQTT infrastructure");

                // Calculate estimated journey duration (in virtual time)
                // Extended duration to avoid frequent train completions and reduce database updates
                var estimatedJourneyDuration = TimeSpan.FromMinutes(60); // Longer journey to reduce completion frequency

                // Turn on train power only if needed (avoid unnecessary power commands)
                var needsPowerOn = _lastPowerCommandSent?.Contains("on") != true;
                if (needsPowerOn)
                {
                    await SendTrainPowerCommandAsync(true);
                }

                // Start the train with appropriate speed
                var departureSpeed = _train.MaxSpeed == Speed.STOP ? Speed.MEDIUM : _train.MaxSpeed;
                await SendTrainCommandAsync("start", departureSpeed, _train.Direction);

                _train.CurrentSpeed = departureSpeed;
                _train.State = TrainState.Moving;

                Console.WriteLine($"[TrainManager-{_train.Name}] Train {_train.Name} started (Speed: {departureSpeed}, Direction: {(_train.Direction ? "Forward" : "Backward")})");
                _statusService?.ShowInfo($"Vonat indul: {_train.Name} - Sebesség: {departureSpeed}");

                // Start destination section monitoring in background
                if (!string.IsNullOrEmpty(_destinationSectionName))
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Starting destination monitoring for section: {_destinationSectionName}");
                    _ = Task.Run(MonitorDestinationSectionAsync, cancellationToken);
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] No destination section set - skipping destination monitoring");
                }

                // Calculate when the train should stop (based on virtual clock)
                var startTime = _virtualClock.CurrentTime;
                var stopTime = startTime + estimatedJourneyDuration;

                Console.WriteLine($"[TrainManager-{_train.Name}] Journey started at {startTime:HH:mm:ss}, estimated stop at {stopTime:HH:mm:ss} (Duration: {estimatedJourneyDuration.TotalMinutes:F1} virtual minutes)");

                // Monitor virtual clock until stop time is reached
                while (!cancellationToken.IsCancellationRequested && _virtualClock.CurrentTime < stopTime)
                {
                    // Check progress every 100ms real-time
                    Thread.Sleep(100);

                    var elapsedTime = _virtualClock.CurrentTime - startTime;
                    var progress = elapsedTime.TotalMinutes / estimatedJourneyDuration.TotalMinutes * 100;

                    // Log progress every 10% of journey
                    if (progress % 10 < 1) // This will trigger roughly every 10%
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] Journey progress: {progress:F0}% - Virtual time: {_virtualClock.CurrentTime:HH:mm:ss}");
                    }

                    // Check if we should start preparing to stop (20% before destination)
                    if (progress >= 80 && _train.State == TrainState.Moving)
                    {
                        _train.State = TrainState.PrepareToStop;
                        Console.WriteLine($"[TrainManager-{_train.Name}] Preparing to stop - Journey progress: {progress:F0}%");
                    }
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    // Note: Train stopping disabled - trains continue running
                    // Journey completed but manager remains active for future commands
                    Console.WriteLine($"[TrainManager-{_train.Name}] Journey completed - train continues running, manager remains active");
                    _statusService?.ShowInfo($"Útvonal befejezve: {_train.Name} (vonat tovább halad)");

                    // Mark timetable entry as completed and save to database
                    // Note: Manager is NOT marked as completed - it stays active for continued train control
                    _timetableEntry.EntryState = EntryState.Arrived;
                    _timetableEntry.ArrivedTime = _virtualClock.CurrentTime;

                    // Save the Arrived state to database
                    try
                    {
                        using (var scope = _serviceProvider.CreateScope())
                        {
                            var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                            var trackedEntry = dbContext.TimetableEntries.Find(_timetableEntry.DB_ID);
                            if (trackedEntry != null)
                            {
                                trackedEntry.EntryState = EntryState.Arrived;
                                trackedEntry.ArrivedTime = _virtualClock.CurrentTime;
                                dbContext.SaveChanges();
                                Console.WriteLine($"[TrainManager-{_train.Name}] Saved Arrived state for timetable entry ID {trackedEntry.DB_ID} at {_virtualClock.CurrentTime:HH:mm:ss}");
                            }
                            else
                            {
                                Console.WriteLine($"[TrainManager-{_train.Name}] ERROR: Could not find timetable entry with ID {_timetableEntry.DB_ID}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] Database error saving arrived state: {ex.GetType().Name}: {ex.Message}");
                        if (ex.InnerException != null)
                        {
                            Console.WriteLine($"[TrainManager-{_train.Name}] Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
                        }
                    }

                    Console.WriteLine($"[TrainManager-{_train.Name}] Journey completed successfully");
                    _statusService?.ShowSuccess($"Útvonal befejezve: {_train.Name} ({_timetableEntry.SourceStation?.Name} -> {_timetableEntry.DestinationStation?.Name})");

                    // Check if train has already arrived at destination
                    if (_train.State == TrainState.Arrived)
                    {
                        Console.WriteLine($"[TrainManager-{_train.Name}] Train has arrived - manager will terminate gracefully");
                        _statusService?.ShowSuccess($"{_train.Name} megérkezett - a menedzser leáll");
                        return; // Exit gracefully without marking as completed
                    }

                    // Keep running - wait for new commands or next journey
                    Console.WriteLine($"[TrainManager-{_train.Name}] Manager staying active for continued train control");

                    // Infinite loop to keep manager alive
                    while (!cancellationToken.IsCancellationRequested)
                    {
                        // Manager stays alive, waiting for new commands
                        await Task.Delay(5000, cancellationToken); // Check every 5 seconds
                    }
                }
                else
                {
                    // Emergency stop if cancelled
                    await SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);
                    _train.CurrentSpeed = Speed.STOP;
                    _train.State = TrainState.Stopped;

                    Console.WriteLine($"[TrainManager-{_train.Name}] Journey cancelled - Emergency stop executed");
                    _statusService?.ShowWarning($"Vonat leállítva: {_train.Name} (Megszakítás)");
                }

                // Note: Train power left on to avoid unnecessary MQTT commands
                Console.WriteLine($"[TrainManager-{_train.Name}] Manager shutting down - train power left on");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] ERROR: {ex.Message}");
                _statusService?.ShowError($"Vonat kezelés hiba ({_train.Name}): {ex.Message}");

                // Emergency stop on error
                try
                {
                    await SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);
                }
                catch
                {
                    // Ignore errors during emergency stop
                }
            }
            finally
            {
                // Clean up MQTT subscription
                _mqttService.OnRocrailFeedbackReceived -= feedbackHandler;

                // Only mark as completed if train hasn't arrived at destination
                // This prevents destroying the manager after successful arrival
                if (_train.State != TrainState.Arrived)
                {
                    _isCompleted = true;
                    Console.WriteLine($"[TrainManager-{_train.Name}] Manager marked as completed (train state: {_train.State})");
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] Manager shutting down gracefully after train arrival");
                }
            }
        }
    }
}