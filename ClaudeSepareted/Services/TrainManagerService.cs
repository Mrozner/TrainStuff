using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using MQTTnet;

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
        private readonly CancellationTokenSource _cancellationTokenSource;
        private volatile bool _isCompleted;
        private IMqttClient? _mqttClient;
        private bool _isInitialized = false;
        private string? _lastSpeedCommandSent = null;
        private string? _lastPowerCommandSent = null;

        public bool IsCompleted => _isCompleted;

        public TrainManagerService(
            VirtualClock virtualClock,
            Train train,
            TimetableEntries timetableEntry,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            IServiceProvider serviceProvider,
            TrackHandlerService trackHandlerService = null)
        {
            _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
            _train = train ?? throw new ArgumentNullException(nameof(train));
            _timetableEntry = timetableEntry ?? throw new ArgumentNullException(nameof(timetableEntry));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _trackHandlerService = trackHandlerService;
            _cancellationTokenSource = new CancellationTokenSource();
            _isCompleted = false;
            _lastSpeedCommandSent = null; // Reset speed command history for new journey
            _lastPowerCommandSent = null; // Reset power command history for new journey
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
            _cancellationTokenSource.Cancel();
        }

        private async Task<bool> InitializeMqttAsync()
        {
            if (_isInitialized)
                return true;

            try
            {
                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_mqttConfig.Address, _mqttConfig.Port)
                    .WithCleanSession()
                    .Build();

                var result = await _mqttClient.ConnectAsync(options);

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    _isInitialized = true;
                    Console.WriteLine($"[TrainManager-{_train.Name}] MQTT connected to {_mqttConfig.Address}:{_mqttConfig.Port}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] MQTT connection failed: {result.ResultCode}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] MQTT initialization error: {ex.Message}");
                return false;
            }
        }

        private async Task SendTrainCommandAsync(string command, Speed speed = Speed.STOP, bool direction = true)
        {
            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                if (!await InitializeMqttAsync())
                    return;
            }

            try
            {
                // Rocrail locomotive command format: <lc id="TrainName" v="speed" dir="true/false"/>
                int speedValue = (int)speed;
                var rocrailCommand = $"<lc id=\"{_train.Name}\" v=\"{speedValue}\" dir=\"{direction.ToString().ToLower()}\"/>";

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

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_mqttConfig.TrainSpeedCommandTopic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(rocrailCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);

                // Store the speed command that was sent
                _lastSpeedCommandSent = rocrailCommand;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending train command: {ex.Message}");
            }
        }

        private async Task SendSystemPowerCommandAsync()
        {
            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                if (!await InitializeMqttAsync())
                    return;
            }

            try
            {
                // Rocrail system power command: <sys cmd="go"/>
                var powerCommand = "<sys cmd=\"go\"/>";

                Console.WriteLine($"[TrainManager-{_train.Name}] Sending system power command: {powerCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_mqttConfig.TrainSpeedCommandTopic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(powerCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);
                await Task.Delay(1000); // Wait for power to come on
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending power command: {ex.Message}");
            }
        }

        private async Task SendTrainPowerCommandAsync(bool powerOn)
        {
            if (!_isInitialized || _mqttClient == null || !_mqttClient.IsConnected)
            {
                if (!await InitializeMqttAsync())
                    return;
            }

            try
            {
                // Rocrail train power command: <lc id="TrainName" cmd="on"/> or <lc id="TrainName" cmd="off"/>
                var powerCommand = $"<lc id=\"{_train.Name}\" cmd=\"{(powerOn ? "on" : "off")}\"/>";

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

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_mqttConfig.TrainSpeedCommandTopic)
                    .WithPayload(System.Text.Encoding.UTF8.GetBytes(powerCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);

                // Store the power command that was sent
                _lastPowerCommandSent = powerCommand;

                await Task.Delay(500); // Wait for train power to change
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Error sending train power command: {ex.Message}");
            }
        }

        private async void Run(CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine($"[TrainManager-{_train.Name}] Starting journey management");

                // Initialize MQTT
                if (!await InitializeMqttAsync())
                {
                    Console.WriteLine($"[TrainManager-{_train.Name}] MQTT initialization failed");
                    _isCompleted = true;
                    return;
                }

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
                _isCompleted = true; // Only mark as completed when truly shutting down
            }
        }
    }
}