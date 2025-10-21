using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.Repositories;
using TrainControlSystem.Services;

namespace TrainControlSystem.BackgroundServices
{
    /// <summary>
    /// MachinistService - A background service that controls exactly one train
    /// Each machinist polls the timetable for its train's schedule and controls the train via MQTT
    /// </summary>
    public class MachinistService : BackgroundService
    {
        private readonly Models.Trains _train;
        private readonly SystemConfiguration _config;
        private readonly TimetableRepository _timetableRepository;
        private readonly TrackManager _trackManager;
        private readonly ILogger<MachinistService> _logger;
        private IMqttClient? _mqttClient;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        // Machinist state
        private TimetableEntries? _currentEntry;
        private DateTime _lastPollTime = DateTime.MinValue;
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(10);

        public MachinistService(
            Models.Trains train,
            SystemConfiguration config,
            TimetableRepository timetableRepository,
            TrackManager trackManager,
            ILogger<MachinistService> logger)
        {
            _train = train;
            _config = config;
            _timetableRepository = timetableRepository;
            _trackManager = trackManager;
            _logger = logger;
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Machinist service starting for train: {_train.Name} (ID: {_train.DB_ID})");

            // Initialize dedicated MQTT connection for this train
            await InitializeMqttConnection();

            await base.StartAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"Machinist service started for train: {_train.Name}");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await PollTimetableAndExecuteActions(stoppingToken);
                    await Task.Delay(_pollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation($"Machinist service for train {_train.Name} stopping");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Error in machinist service for train {_train.Name}");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); // Wait before retry
                }
            }
        }

        private async Task InitializeMqttConnection()
        {
            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .WithClientId($"machinist_{_train.Name}_{Guid.NewGuid():N}")
                .WithCleanSession()
                .Build();

            await _mqttClient.ConnectAsync(options);

            // Subscribe to train-specific topics using the new configuration
            var responseTopic = _config.MQTT.MachinistTopics.GetTrainResponseTopic(_train.Name);
            var feedbackTopic = _config.MQTT.MachinistTopics.GetTrainFeedbackTopic(_train.Name);
            var statusTopic = _config.MQTT.MachinistTopics.GetTrainStatusTopic(_train.Name);
            var locationTopic = _config.MQTT.MachinistTopics.GetTrainLocationTopic(_train.Name);

            await _mqttClient.SubscribeAsync(responseTopic);
            await _mqttClient.SubscribeAsync(feedbackTopic);
            await _mqttClient.SubscribeAsync(statusTopic);
            await _mqttClient.SubscribeAsync(locationTopic);

            _logger.LogInformation($"MQTT connection established for train {_train.Name} with topics: {responseTopic}, {feedbackTopic}, {statusTopic}, {locationTopic}");
        }

        private async Task PollTimetableAndExecuteActions(CancellationToken stoppingToken)
        {
            if (DateTime.UtcNow - _lastPollTime < _pollInterval)
                return;

            await _semaphore.WaitAsync(stoppingToken);
            try
            {
                // Get next scheduled entry for this train
                var nextEntry = _timetableRepository.GetNextScheduledEntryForTrain(_train.DB_ID);

                if (nextEntry != null)
                {
                    // Check if we need to start this journey
                    if (ShouldStartJourney(nextEntry))
                    {
                        _logger.LogInformation($"Starting journey for train {_train.Name}: {nextEntry.SourceStation?.Name} -> {nextEntry.DestinationStation?.Name}");
                        await StartJourney(nextEntry);
                    }
                }

                // Check if current journey needs updates
                if (_currentEntry != null)
                {
                    await UpdateCurrentJourney();
                }

                _lastPollTime = DateTime.UtcNow;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        private bool ShouldStartJourney(TimetableEntries entry)
        {
            var scheduledTime = entry.StartDate.Date.Add(entry.StartTime);
            var now = DateTime.Now;

            // Start if we're within 2 minutes of scheduled time and haven't started yet
            return now >= scheduledTime.AddMinutes(-2) &&
                   now <= scheduledTime.AddMinutes(30) && // Don't start if too late
                   entry.EntryState == EntryState.Scheduled;
        }

        private async Task StartJourney(TimetableEntries entry)
        {
            try
            {
                // Update entry state
                entry.EntryState = EntryState.InTransit;
                _timetableRepository.UpdateEntry(entry);
                _currentEntry = entry;

                // Start the train moving at low speed
                await SendSpeedCommand(Speed.SLOW);
                _train.State = TrainState.Moving;

                _logger.LogInformation($"Train {_train.Name} departed from {entry.SourceStation?.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start journey for train {_train.Name}");
            }
        }

        private async Task UpdateCurrentJourney()
        {
            if (_currentEntry == null) return;

            // Check if train has reached destination (simplified - would use track sensors)
            if (HasReachedDestination())
            {
                _currentEntry.EntryState = EntryState.Arrived;
                _currentEntry.ArrivedTime = DateTime.Now;
                _timetableRepository.UpdateEntry(_currentEntry);

                await SendSpeedCommand(Speed.STOP);
                _train.State = TrainState.Stopped;

                _logger.LogInformation($"Train {_train.Name} arrived at {_currentEntry.DestinationStation?.Name}");
                _currentEntry = null;
            }
        }

        private bool HasReachedDestination()
        {
            // Simplified logic - in real implementation would check track sensors
            // For now, assume arrival after some time based on journey duration
            if (_currentEntry?.EntryState == EntryState.InTransit)
            {
                var journeyDuration = TimeSpan.FromMinutes(5); // Simplified
                return DateTime.Now - _currentEntry.StartDate.Date.Add(_currentEntry.StartTime) > journeyDuration;
            }
            return false;
        }

        private async Task SendSpeedCommand(Speed speed)
        {
            if (_mqttClient?.IsConnected == true)
            {
                var command = new
                {
                    train = _train.Name,
                    trainId = _train.DB_ID,
                    speed = speed.ToString(),
                    timestamp = DateTime.UtcNow,
                    machinistId = $"machinist_{_train.Name}"
                };

                var message = JsonConvert.SerializeObject(command);
                var commandTopic = _config.MQTT.MachinistTopics.GetTrainCommandTopic(_train.Name);

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(commandTopic)
                    .WithPayload(message)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);
                _train.CurrentSpeed = speed;

                // Also publish to speed-specific topic for monitoring
                var speedTopic = _config.MQTT.MachinistTopics.GetTrainSpeedTopic(_train.Name);
                var speedMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(speedTopic)
                    .WithPayload(JsonConvert.SerializeObject(new {
                        train = _train.Name,
                        speed = speed.ToString(),
                        timestamp = DateTime.UtcNow
                    }))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(speedMessage);

                _logger.LogDebug($"Sent speed command {speed} to train {_train.Name} on topic {commandTopic}");
            }
            else
            {
                _logger.LogWarning($"MQTT not connected for train {_train.Name}, cannot send speed command");
            }
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation($"Machinist service stopping for train: {_train.Name}");

            // Emergency stop the train
            await SendSpeedCommand(Speed.STOP);

            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync();
            }

            await base.StopAsync(cancellationToken);
        }

        public new void Dispose()
        {
            _mqttClient?.Dispose();
            _semaphore?.Dispose();
        }
    }
}