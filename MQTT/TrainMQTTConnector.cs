using Microsoft.Extensions.Logging;
using MQTTnet;
using Newtonsoft.Json;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;

namespace TrainControlSystem.MQTT
{
    /// <summary>
    /// MQTT connector for train-related communications
    /// </summary>
    public class TrainMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private readonly ILogger<TrainMQTTConnector> _logger;
        private IMqttClient _mqttClient;

        public TrainMQTTConnector(SystemConfiguration config, ILogger<TrainMQTTConnector> logger)
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
                    .WithClientId($"train_controller_{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .Build();

                await _mqttClient.ConnectAsync(options);
                _logger.LogInformation($"Train MQTT connected to {_config.MQTT.Address}:{_config.MQTT.Port}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect train MQTT client");
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
                _logger.LogInformation("Train MQTT disconnected");
            }
        }

        /// <summary>
        /// Changes train speed via MQTT command
        /// </summary>
        public async Task ChangeSpeed(Trains train, Speed speed)
        {
            if (_mqttClient?.IsConnected != true) return;

            var command = new
            {
                command = "speed",
                train = train.Name,
                speed = speed.ToString(),
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrainSpeedCommandTopic, command);
            _logger.LogInformation($"Sent speed command to train {train.Name}: {speed}");
        }

        /// <summary>
        /// Sends emergency stop command
        /// </summary>
        public async Task EmergencyStop(Trains train)
        {
            if (_mqttClient?.IsConnected != true) return;

            var command = new
            {
                command = "emergency_stop",
                train = train.Name,
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrainSpeedCommandTopic, command);
            _logger.LogWarning($"Sent emergency stop to train {train.Name}");
        }

        /// <summary>
        /// Sends signal request for a train
        /// </summary>
        public async Task RequestSignal(Trains train, string requestedSignal)
        {
            if (_mqttClient?.IsConnected != true) return;

            var request = new
            {
                train = train.Name,
                requestedSignal = requestedSignal,
                timestamp = DateTime.UtcNow
            };

            await PublishMessage(_config.MQTT.TrainSignalRequestTopic, request);
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