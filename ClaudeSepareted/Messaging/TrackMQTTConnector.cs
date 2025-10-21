using MQTTnet;
using Newtonsoft.Json;

namespace ClaudeSepareted
{
    public class TrackMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public TrackMQTTConnector(SystemConfiguration config)
        {
            _config = config;

            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            ConnectAsync().Wait();
            Console.WriteLine("Track MQTT Connector initialized");
        }

        private async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Track MQTT Connected to broker");

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackSectionTopic)
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackPositionTopic)
                    .Build());

                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TrackRFIDTopic)
                    .Build());
            };

            await _mqttClient.ConnectAsync(options);
        }

        public async void SendSignalUpdate(Signal signal)
        {
            try
            {
                var signalMessage = new
                {
                    ID = signal.ID,
                    Speed = signal.Speed,
                    NextSpeed = signal.NextSpeed
                };

                var payload = JsonConvert.SerializeObject(signalMessage);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TrackSignalTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Signal Update: {signal.ID} -> {signal.Speed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending signal update: {ex.Message}");
            }
        }

        public async void SwitchControl(Switches sw)
        {
            try
            {
                var switchCommand = $"<sw id=\"{sw.Name}\" state=\"{(sw.State == "1" ? "1" : "0")}\"/>";

                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TrackCommandTopic)
                    .WithPayload(switchCommand)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Switch Control: {sw.Name} -> {sw.State}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending switch control: {ex.Message}");
            }
        }
    }
}