using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;

namespace TrainControlSystem.MQTT
{
    /// <summary>
    /// MQTT connector for track-related communications
    /// </summary>
    public class TrackMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private readonly ILogger<TrackMQTTConnector> _logger;
        private IMqttClient _mqttClient;

        public TrackMQTTConnector(SystemConfiguration config, ILogger<TrackMQTTConnector> logger)
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
                    .WithClientId($"track_controller_{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .Build();

                await _mqttClient.ConnectAsync(options);
                _logger.LogInformation($"Track MQTT connected to {_config.MQTT.Address}:{_config.MQTT.Port}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect track MQTT client");
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
                _logger.LogInformation("Track MQTT disconnected");
            }
        }

        /// <summary>
        /// Publishes a track section update
        /// </summary>
        public async Task PublishTrackSectionUpdate(Sections section)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                sectionId = section.DB_ID,
                sectionName = section.Name,
                isOccupied = section.IsOccupied,
                trainId = section.TrainId,
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrackSectionTopic, message);
        }

        /// <summary>
        /// Publishes a signal change
        /// </summary>
        public async Task PublishSignalChange(Signals signal)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                signalId = signal.DB_ID,
                signalName = signal.Name,
                state = signal.State.ToString(),
                speed = signal.Speed.ToString(),
                subSectionId = signal.SubSectionId,
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrackSignalTopic, message);
        }

        /// <summary>
        /// Publishes a track position update
        /// </summary>
        public async Task PublishTrackPosition(SubSections subSection, Trains? train)
        {
            if (_mqttClient?.IsConnected != true) return;

            var message = new
            {
                subSectionId = subSection.DB_ID,
                subSectionName = subSection.Name,
                sectionId = subSection.SectionId,
                trainId = train?.DB_ID,
                trainName = train?.Name,
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrackPositionTopic, message);
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