using MQTTnet;
using Newtonsoft.Json;
using System.Text;

namespace ClaudeSepareted
{
    public class TimetableMQTTConnector
    {
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public TimetableMQTTConnector(SystemConfiguration config)
        {
            _config = config;
            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            ConnectAsync().Wait();
            Console.WriteLine("Timetable MQTT Connector initialized");
        }

        private async Task ConnectAsync()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ConnectedAsync += async e =>
            {
                Console.WriteLine("Timetable MQTT Connected to broker");

                // Subscribe to timetable topics
                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(_config.MQTT.TimetableStartRequestTopic)
                    .Build());
            };

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);

                if (topic == _config.MQTT.TimetableStartRequestTopic)
                {
                    await HandleStartRequest(payload);
                }
            };

            await _mqttClient.ConnectAsync(options);
        }

        private async Task HandleStartRequest(string payload)
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<TimetableEntries>(payload);
                if (entry != null)
                {
                    Console.WriteLine($"Received start request for entry: {entry.EntryID}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling start request: {ex.Message}");
            }
        }

        public async Task SendStartResponse(string entryID, bool canStart)
        {
            try
            {
                var response = new
                {
                    EntryID = entryID,
                    Status = canStart ? "OK" : "NOK",
                    Timestamp = DateTime.Now
                };

                var payload = JsonConvert.SerializeObject(response);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TimetableStatusTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Start Response: {entryID} -> {(canStart ? "OK" : "NOK")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending start response: {ex.Message}");
            }
        }

        public async Task SendTrackStatus(string entryID, TrainState state)
        {
            try
            {
                string status = state == TrainState.Waiting ? "Arrived" : "Delay";

                var statusMessage = new
                {
                    EntryID = entryID,
                    Status = status,
                    Timestamp = DateTime.Now
                };

                var payload = JsonConvert.SerializeObject(statusMessage);
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.MQTT.TimetableStatusTopic)
                    .WithPayload(payload)
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                await _mqttClient.PublishAsync(message);
                Console.WriteLine($"Track Status: {entryID} -> {status}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending track status: {ex.Message}");
            }
        }
    }
}