using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;

namespace TrainControlSystem.MQTT
{
    /// <summary>
    /// MQTT connector for timetable-related communications
    /// </summary>
    public class TimetableMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private readonly ILogger<TimetableMQTTConnector> _logger;
        private IMqttClient _mqttClient;

        public TimetableMQTTConnector(SystemConfiguration config, ILogger<TimetableMQTTConnector> logger)
        {
            _config = config;
            _logger = logger;
        }

        /// <summary>
        /// Connects to the MQTT broker
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            try
            {
                var mqttClientFactory = new MqttClientFactory();
                _mqttClient = mqttClientFactory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                    .WithClientId($"timetable_{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .Build();

                await _mqttClient.ConnectAsync(options);
                _logger.LogInformation($"Timetable MQTT connected to {_config.MQTT.Address}:{_config.MQTT.Port}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect timetable MQTT client");
                return false;
            }
        }

        /// <summary>
        /// Disconnects from the MQTT broker
        /// </summary>
        public async Task DisconnectAsync()
        {
            if (_mqttClient?.IsConnected == true)
            {
                await _mqttClient.DisconnectAsync();
                _logger.LogInformation("Timetable MQTT disconnected");
            }
        }

        /// <summary>
        /// Publishes timetable status update
        /// </summary>
        public async Task PublishTimetableStatus(List<TimetableEntries> entries)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                totalEntries = entries.Count,
                activeEntries = entries.Count(e => e.EntryState == EntryState.InTransit || e.EntryState == EntryState.AtStation),
                scheduledEntries = entries.Count(e => e.EntryState == EntryState.Scheduled),
                timestamp = DateTime.UtcNow,
                entries = entries.Select(e => new
                {
                    entryId = e.EntryID,
                    trainId = e.Train_DB_ID,
                    sourceStation = e.SourceStation?.Name,
                    destinationStation = e.DestinationStation?.Name,
                    state = e.EntryState.ToString(),
                    scheduledTime = e.StartDate.Add(e.StartTime)
                })
            };

            await PublishMessage(_config.MQTT.TimetableStatusTopic, message);
        }

        /// <summary>
        /// Publishes train start request
        /// </summary>
        public async Task PublishTrainStartRequest(TimetableEntries entry)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                entryId = entry.EntryID,
                trainId = entry.Train_DB_ID,
                sourceStation = entry.SourceStation?.Name,
                destinationStation = entry.DestinationStation?.Name,
                scheduledTime = entry.StartDate.Add(entry.StartTime),
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TimetableStartRequestTopic, message);
        }

        private async Task PublishMessage(string topic, object message)
        {
            try
            {
                var json = JsonConvert.SerializeObject(message);
                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(json)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(mqttMessage);
                _logger.LogDebug($"Published message to {topic}: {json}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to publish message to {topic}");
            }
        }
    }
}