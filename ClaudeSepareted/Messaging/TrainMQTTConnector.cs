using MQTTnet;
using Newtonsoft.Json;
using System.Text;

namespace ClaudeSepareted
{
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
}