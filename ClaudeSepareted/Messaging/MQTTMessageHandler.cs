using MQTTnet;
using Newtonsoft.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeSepareted
{
    public class MQTTMessageHandler
    {
        private readonly TrackManager _trackManager;
        private readonly TimetableManager _timetableManager;
        private readonly TrainManagerService _trainManager;
        private readonly SystemConfiguration _config;
        private IMqttClient _mqttClient;

        public MQTTMessageHandler(
            TrackManager trackManager,
            TimetableManager timetableManager,
            TrainManagerService trainManager,
            SystemConfiguration config)
        {
            _trackManager = trackManager;
            _timetableManager = timetableManager;
            _trainManager = trainManager;
            _config = config;

            var mqttClientFactory = new MqttClientFactory();
            _mqttClient = mqttClientFactory.CreateMqttClient();

            InitializeMQTT().Wait();
        }

        private async Task InitializeMQTT()
        {
            var options = new MqttClientOptionsBuilder()
                .WithTcpServer(_config.MQTT.Address, _config.MQTT.Port)
                .Build();

            _mqttClient.ApplicationMessageReceivedAsync += async e =>
            {
                var topic = e.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                await HandleMessage(topic, payload);
            };

            await _mqttClient.ConnectAsync(options);

            // Subscribe to all relevant topics
            var topics = new[]
            {
                _config.MQTT.TrackSectionTopic,
                _config.MQTT.TrackPositionTopic,
                _config.MQTT.TrackRFIDTopic,
                _config.MQTT.TimetableStartRequestTopic,
                _config.MQTT.TrainSignalRequestTopic,
                _config.MQTT.TrainSignalChangedTopic
            };

            foreach (var topic in topics)
            {
                await _mqttClient.SubscribeAsync(new MqttTopicFilterBuilder()
                    .WithTopic(topic)
                    .Build());
            }
        }

        private async Task HandleMessage(string topic, string payload)
        {
            try
            {
                if (topic == _config.MQTT.TrackSectionTopic)
                {
                    HandleTrackSectionMessage(payload);
                }
                else if (topic == _config.MQTT.TrackPositionTopic)
                {
                    HandleHallSensorMessage(payload);
                }
                else if (topic == _config.MQTT.TrackRFIDTopic)
                {
                    HandleRFIDMessage(payload);
                }
                else if (topic == _config.MQTT.TimetableStartRequestTopic)
                {
                    HandleTimetableStartRequest(payload);
                }
                else if (topic == _config.MQTT.TrainSignalRequestTopic)
                {
                    HandleTrainSignalRequest(payload);
                }
                else if (topic == _config.MQTT.TrainSignalChangedTopic)
                {
                    HandleTrainSignalChanged(payload);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling MQTT message: {ex.Message}");
            }
        }

        public void HandleTrackSectionMessage(string payload)
        {
            try
            {
                if (payload.Contains("state=\"true\""))
                {
                    var sectionName = ExtractSectionName(payload);
                    _trackManager.TrainAppeared(sectionName);
                }
                else if (payload.Contains("state=\"false\""))
                {
                    var sectionName = ExtractSectionName(payload);
                    _trackManager.TrainLeft(sectionName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling track section message: {ex.Message}");
            }
        }

        public void HandleHallSensorMessage(string payload)
        {
            try
            {
                var hallData = JsonConvert.DeserializeObject<HallSensorData>(payload);
                if (hallData?.State == true)
                {
                    var train = _trackManager.Trains.FirstOrDefault(t => t.StopHall == hallData.ID);
                    if (train != null)
                    {
                        train.State = TrainState.Stopped;
                        Console.WriteLine($"Train stopped at hall sensor: {hallData.ID}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling hall sensor message: {ex.Message}");
            }
        }

        public void HandleRFIDMessage(string payload)
        {
            try
            {
                var rfidData = JsonConvert.DeserializeObject<RFIDSensorData>(payload);
                if (rfidData != null)
                {
                    var train = _trackManager.Trains.FirstOrDefault(t =>
                        t.State == TrainState.Moving && t.SubSection != null);

                    if (train != null)
                    {
                        train.CarriageIdentifiers.Add(rfidData.UID);
                        Console.WriteLine($"RFID detected: {rfidData.UID} for train {train.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling RFID message: {ex.Message}");
            }
        }

        public void HandleTimetableStartRequest(string payload)
        {
            try
            {
                var entry = JsonConvert.DeserializeObject<TimetableEntries>(payload);
                if (entry != null)
                {
                    _trackManager.TryStartTrain(entry);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling start request: {ex.Message}");
            }
        }

        public void HandleTrainSignalRequest(string payload)
        {
            try
            {
                var request = JsonConvert.DeserializeObject<SignalRequest>(payload);
                if (request != null)
                {
                    var signal = _trackManager.GetSignalForTrain(request.Name);
                    if (signal != null)
                    {
                        Console.WriteLine($"Signal for {request.Name}: {signal.Speed}");
                        // In real implementation, publish response via MQTT
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling signal request: {ex.Message}");
            }
        }

        public void HandleTrainSignalChanged(string payload)
        {
            try
            {
                var signalChange = JsonConvert.DeserializeObject<SignalStateChangeResponse>(payload);
                if (signalChange != null)
                {
                    // When signal changes, request the current signal state for the train
                    var train = _trackManager.Trains.FirstOrDefault(t => t.Name == signalChange.ID);
                    if (train != null)
                    {
                        // Get the current signal for this train
                        var signal = _trackManager.GetSignalForTrain(train.Name);
                        if (signal != null)
                        {
                            // Update train speed based on signal
                            _trainManager.UpdateTrainSpeed(train.Name, signal.Speed);
                            Console.WriteLine($"Signal changed for {train.Name}, speed set to: {signal.Speed}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling train signal changed: {ex.Message}");
            }
        }

        private string ExtractSectionName(string xml)
        {
            var match = Regex.Match(xml, @"id=""([^""]+)""");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }
    }
}