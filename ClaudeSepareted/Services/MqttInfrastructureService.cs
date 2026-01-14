using MQTTnet;
using System;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Centralized MQTT infrastructure service that manages a single connection
    /// for the entire application and broadcasts incoming messages to subscribers
    /// </summary>
    public class MqttInfrastructureService : IDisposable
    {
        private readonly MQTTConfiguration _config;
        private IMqttClient? _mqttClient;
        private readonly object _lockObject = new object();
        private bool _isInitialized = false;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);

        // Events that services can subscribe to for different message types
        public event Func<string, Task>? OnRocrailFeedbackReceived;
        public event Func<string, Task>? OnHallSensorReceived;
        public event Func<string, Task>? OnRfidReceived;
        public event Func<string, Task>? OnTrainSignalRequestReceived;
        public event Func<string, Task>? OnTrainSignalResponseReceived;
        public event Func<string, Task>? OnTrainSignalChangedReceived;
        public event Func<string, Task>? OnTrainStatusReceived;
        public event Func<string, Task>? OnTimetableStartRequestReceived;

        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        public MqttInfrastructureService(MQTTConfiguration config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Initialize the single MQTT connection and subscribe to all required topics
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                lock (_lockObject)
                {
                    if (_isInitialized && _mqttClient != null && _mqttClient.IsConnected)
                        return true;
                }

                Console.WriteLine("[MqttInfrastructure] Initializing MQTT connection...");

                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                // Single Client ID for the whole app prevents connection thrashing
                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_config.Address, _config.Port)
                    .WithClientId($"TrainController_{Guid.NewGuid():N}")
                    .WithCleanSession()
                    .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
                    .Build();

                _mqttClient.ApplicationMessageReceivedAsync += HandleIncomingMessageAsync;
                _mqttClient.DisconnectedAsync += HandleDisconnectedAsync;

                var result = await _mqttClient.ConnectAsync(options);

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    // Subscribe to all required topics
                    await SubscribeToAllTopicsAsync();

                    lock (_lockObject)
                    {
                        _isInitialized = true;
                    }
                    Console.WriteLine($"[MqttInfrastructure] Connected to {_config.Address}:{_config.Port}");
                    return true;
                }

                Console.WriteLine($"[MqttInfrastructure] Connection failed: {result.ResultCode}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MqttInfrastructure] Init error: {ex.Message}");
                return false;
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        /// <summary>
        /// Subscribe to all MQTT topics needed by the application
        /// </summary>
        private async Task SubscribeToAllTopicsAsync()
        {
            if (_mqttClient == null || !_mqttClient.IsConnected)
                return;

            var topics = new[]
            {
                ("rocrail/service/info", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("track/info/hall", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("track/info/rfid", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("train/signal/request", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("train/signal/response", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("train/signal/changed", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("train/status", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce),
                ("train/start/request", MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            };

            foreach (var (topic, qos) in topics)
            {
                try
                {
                    await _mqttClient.SubscribeAsync(topic, qos);
                    Console.WriteLine($"[MqttInfrastructure] Subscribed to: {topic}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[MqttInfrastructure] Failed to subscribe to {topic}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Handle incoming MQTT messages and route them to appropriate event handlers
        /// </summary>
        private async Task HandleIncomingMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                var payload = Encoding.UTF8.GetString(e.ApplicationMessage.Payload);
                var topic = e.ApplicationMessage.Topic;

                Console.WriteLine($"[MqttInfrastructure] Message received on {topic}: {payload}");

                // Route message to appropriate handler based on topic
                switch (topic)
                {
                    case "rocrail/service/info":
                        if (OnRocrailFeedbackReceived != null)
                            await OnRocrailFeedbackReceived.Invoke(payload);
                        break;

                    case "track/info/hall":
                        if (OnHallSensorReceived != null)
                            await OnHallSensorReceived.Invoke(payload);
                        break;

                    case "track/info/rfid":
                        if (OnRfidReceived != null)
                            await OnRfidReceived.Invoke(payload);
                        break;

                    case "train/signal/request":
                        if (OnTrainSignalRequestReceived != null)
                            await OnTrainSignalRequestReceived.Invoke(payload);
                        break;

                    case "train/signal/response":
                        if (OnTrainSignalResponseReceived != null)
                            await OnTrainSignalResponseReceived.Invoke(payload);
                        break;

                    case "train/signal/changed":
                        if (OnTrainSignalChangedReceived != null)
                            await OnTrainSignalChangedReceived.Invoke(payload);
                        break;

                    case "train/status":
                        if (OnTrainStatusReceived != null)
                            await OnTrainStatusReceived.Invoke(payload);
                        break;

                    case "train/start/request":
                        if (OnTimetableStartRequestReceived != null)
                            await OnTimetableStartRequestReceived.Invoke(payload);
                        break;

                    default:
                        Console.WriteLine($"[MqttInfrastructure] No handler for topic: {topic}");
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MqttInfrastructure] Error handling message: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle MQTT disconnection events
        /// </summary>
        private async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            Console.WriteLine($"[MqttInfrastructure] Disconnected: {e.Reason}");
            lock (_lockObject)
            {
                _isInitialized = false;
            }

            // Optional: Implement automatic reconnection logic
            // await Task.Delay(TimeSpan.FromSeconds(5));
            // await InitializeAsync();
        }

        /// <summary>
        /// Publish a message to a specific MQTT topic
        /// </summary>
        /// <param name="topic">Target topic</param>
        /// <param name="payload">Message payload</param>
        /// <param name="qos">Quality of Service level (default: AtLeastOnce)</param>
        /// <returns>True if successful, false otherwise</returns>
        public async Task<bool> PublishAsync(string topic, string payload, MQTTnet.Protocol.MqttQualityOfServiceLevel qos = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
        {
            if (!IsConnected)
            {
                Console.WriteLine("[MqttInfrastructure] Not connected, attempting to initialize...");
                if (!await InitializeAsync())
                    return false;
            }

            try
            {
                var message = new MqttApplicationMessageBuilder()
                    .WithTopic(topic)
                    .WithPayload(Encoding.UTF8.GetBytes(payload))
                    .WithQualityOfServiceLevel(qos)
                    .Build();

                var result = await _mqttClient!.PublishAsync(message);

                if (result.IsSuccess)
                {
                    Console.WriteLine($"[MqttInfrastructure] Published to {topic}: {payload}");
                }
                else
                {
                    Console.WriteLine($"[MqttInfrastructure] Publish failed to {topic}: {result.ReasonCode}");
                }

                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MqttInfrastructure] Publish error to {topic}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Disconnect from MQTT broker and clean up resources
        /// </summary>
        public async Task DisconnectAsync()
        {
            await _connectionSemaphore.WaitAsync();
            try
            {
                if (_mqttClient != null)
                {
                    await _mqttClient.DisconnectAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                }
                lock (_lockObject)
                {
                    _isInitialized = false;
                }
                Console.WriteLine("[MqttInfrastructure] Disconnected and cleaned up");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MqttInfrastructure] Disconnect error: {ex.Message}");
            }
            finally
            {
                _connectionSemaphore.Release();
            }
        }

        /// <summary>
        /// Get current connection status and statistics
        /// </summary>
        public string GetStatusInfo()
        {
            return $"Connected: {IsConnected}, Initialized: {_isInitialized}, Server: {_config.Address}:{_config.Port}";
        }

        public void Dispose()
        {
            _connectionSemaphore.Dispose();
            _mqttClient?.Dispose();
        }
    }
}