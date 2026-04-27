using MQTTnet;
using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using ClaudeSepareted;
using Microsoft.Extensions.Logging;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Centralized MQTT infrastructure service that manages a single connection
    /// for the entire application and broadcasts incoming messages to subscribers
    /// using a generic Pub/Sub pattern
    /// </summary>
    public class MqttInfrastructureService : IDisposable
    {
        private readonly MQTTConfiguration _config;
        private readonly ILogger<MqttInfrastructureService> _logger;
        private readonly FileLoggingService? _fileLogger;
        private IMqttClient? _mqttClient;
        private volatile bool _isInitialized = false;
        private readonly SemaphoreSlim _connectionSemaphore = new SemaphoreSlim(1, 1);
        private volatile bool _isDisposed = false;
        private int _isReconnecting = 0;

        /// <summary>
        /// Thread-safe dictionary mapping topics to their subscribed handlers.
        /// Key: MQTT topic (e.g., "rocrail/service/info")
        /// Value: ImmutableList of async handlers that receive messages for this topic
        /// Uses ImmutableList for lock-free thread-safe operations.
        /// </summary>
        private readonly ConcurrentDictionary<string, ImmutableList<Func<string, Task>>> _topicSubscribers = new();

        public bool IsConnected => _mqttClient?.IsConnected ?? false;

        /// <summary>
        /// Gets the underlying MQTT client instance for advanced operations
        /// Use with caution - direct access bypasses the service's abstractions
        /// </summary>
        public IMqttClient? GetMqttClient() => _mqttClient;

        public MqttInfrastructureService(MQTTConfiguration config, ILogger<MqttInfrastructureService> logger = null, FileLoggingService fileLogger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;
            _fileLogger = fileLogger;
        }

        /// <summary>
        /// Subscribe to an MQTT topic with a handler function.
        /// If the MQTT client is already connected, this method will also subscribe to the topic on the broker.
        /// Uses ImmutableList for lock-free thread-safe operations.
        /// </summary>
        /// <param name="topic">The MQTT topic to subscribe to</param>
        /// <param name="handler">Async function that handles incoming messages for this topic</param>
        public void Subscribe(string topic, Func<string, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Topic cannot be null or empty", nameof(topic));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Add handler to the topic's subscriber list using immutable list operations
            _topicSubscribers.AddOrUpdate(
                topic,
                _ => ImmutableList.Create(handler),
                (_, existingHandlers) => existingHandlers.Add(handler)
            );

            // If MQTT client is already connected, subscribe to the topic on the broker
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                var client = _mqttClient; // Capture to avoid race conditions
                Task.Run(async () =>
                {
                    try
                    {
                        // Check if service is disposed or client is null before subscribing
                        if (_isDisposed || client == null)
                            return;

                        await client.SubscribeAsync(topic, MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to subscribe to MQTT topic: {Topic} - [MqttInfrastructureService]", topic);
                    }
                });
            }
        }

        /// <summary>
        /// Unsubscribe a handler from an MQTT topic.
        /// If this was the last handler for the topic, the topic will also be unsubscribed from the broker.
        /// Uses retry logic to handle concurrent modifications to the dictionary.
        /// </summary>
        /// <param name="topic">The MQTT topic to unsubscribe from</param>
        /// <param name="handler">The handler function to remove</param>
        public void Unsubscribe(string topic, Func<string, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(topic))
                throw new ArgumentException("Topic cannot be null or empty", nameof(topic));

            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            // Retry loop to handle concurrent modifications
            while (true)
            {
                // Try to find and update the topic's subscriber list
                if (!_topicSubscribers.TryGetValue(topic, out var handlers))
                    return; // Topic doesn't exist, nothing to do

                var updatedHandlers = handlers.Remove(handler);

                if (updatedHandlers.IsEmpty)
                {
                    // No more handlers for this topic, remove from dictionary
                    if (_topicSubscribers.TryRemove(topic, out _))
                    {
                        // Successfully removed from dictionary, now unsubscribe from broker
                        if (_mqttClient != null && _mqttClient.IsConnected)
                        {
                            var client = _mqttClient; // Capture to avoid race conditions
                            Task.Run(async () =>
                            {
                                try
                                {
                                    if (_isDisposed || client == null)
                                        return;

                                    // Re-check if topic was re-added by another thread before unsubscribing
                                    if (_topicSubscribers.ContainsKey(topic))
                                        return; // Topic was re-subscribed, abort broker unsubscription

                                    await client.UnsubscribeAsync(topic);
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogError(ex, "Failed to unsubscribe from MQTT topic: {Topic} - [MqttInfrastructureService]", topic);
                                }
                            });
                        }
                        return; // Successfully removed and scheduled broker unsubscribe
                    }
                    // TryRemove failed due to concurrent modification, retry loop
                }
                else
                {
                    // Update with the new list (without the removed handler)
                    if (_topicSubscribers.TryUpdate(topic, updatedHandlers, handlers))
                        return; // Successfully updated

                    // TryUpdate failed due to concurrent modification, retry loop
                }
            }
        }

        /// <summary>
        /// Initialize the single MQTT connection and subscribe to all required topics
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                await _connectionSemaphore.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore is disposed, service is shutting down
                return false;
            }

            try
            {
                if (_isInitialized && _mqttClient != null && _mqttClient.IsConnected)
                    return true;

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
                    _fileLogger?.Log("[MQTT] Connected to broker successfully.");

                    // Subscribe to all required topics
                    await SubscribeToAllTopicsAsync();

                    _isInitialized = true;

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "MQTT initialization failed for broker {BrokerAddress}:{BrokerPort} - [MqttInfrastructureService]", _config.Address, _config.Port);
                return false;
            }
            finally
            {
                try
                {
                    if (!_isDisposed)
                    {
                        _connectionSemaphore.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Safely ignore: The application is shutting down and the semaphore is already disposed.
                }
            }
        }

        /// <summary>
        /// Subscribe to all MQTT topics that have been registered via Subscribe() calls.
        /// This is called automatically after the MQTT connection is established.
        /// </summary>
        private async Task SubscribeToAllTopicsAsync()
        {
            if (_mqttClient == null || !_mqttClient.IsConnected)
                return;

            var qos = MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce;

            foreach (var topic in _topicSubscribers.Keys)
            {
                try
                {
                    await _mqttClient.SubscribeAsync(topic, qos);
                    _logger?.LogDebug("Successfully subscribed to MQTT topic: {Topic}", topic);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to subscribe to MQTT topic: {Topic} - [MqttInfrastructureService]", topic);
                }
            }
        }

        /// <summary>
        /// Handle incoming MQTT messages and route them to registered subscribers.
        /// This method dynamically looks up handlers based on the topic.
        /// Uses ImmutableList for lock-free thread-safe iteration.
        /// </summary>
        private async Task HandleIncomingMessageAsync(MqttApplicationMessageReceivedEventArgs e)
        {
            try
            {
                // Modern payload handling using MQTTnet 4.3.0+ API
                var payload = e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
                var topic = e.ApplicationMessage.Topic;

                _fileLogger?.Log($"[MQTT IN] Topic: {topic} | Payload: {payload}");

                // Look up handlers for this topic
                if (_topicSubscribers.TryGetValue(topic, out var handlers))
                {
                    // Iterate directly over the ImmutableList (thread-safe, no lock needed)
                    foreach (var handler in handlers)
                    {
                        try
                        {
                            await handler.Invoke(payload);
                        }
                        catch (Exception handlerEx)
                        {
                            _logger?.LogError(handlerEx, "Error in MQTT message handler for topic {Topic} - [MqttInfrastructureService]", topic);
                        }
                    }
                }
                else
                {
                    _logger?.LogDebug("No handlers registered for MQTT topic: {Topic}", topic);
                }
            }
            catch (Exception ex)
            {
                var errorPayload = e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;
                _logger?.LogError(ex, "Error processing incoming MQTT message on topic {Topic}. Payload: {Payload} - [MqttInfrastructureService]", e.ApplicationMessage.Topic, errorPayload);
            }
        }

        /// <summary>
        /// Handle MQTT disconnection events with automatic reconnection
        /// </summary>
        private async Task HandleDisconnectedAsync(MqttClientDisconnectedEventArgs e)
        {
            _fileLogger?.Log("[MQTT] Disconnected from broker.");

            _isInitialized = false;

            // Ensure only one reconnection task is running using atomic compare-exchange
            // If _isReconnecting is 0, set it to 1 and start reconnection (returns 0 == 0 = true)
            // If _isReconnecting is already 1, skip starting another reconnection task (returns 1 == 0 = false)
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        while (!_isDisposed && !IsConnected)
                        {
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(5));

                                if (!_isDisposed)
                                {
                                    await InitializeAsync();
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogError(ex, "Error during MQTT reconnection attempt - [MqttInfrastructureService]");
                            }
                        }
                    }
                    finally
                    {
                        // Reset reconnection flag when loop exits (connected or disposed)
                        Interlocked.Exchange(ref _isReconnecting, 0);
                    }
                });
            }
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
            // Log MQTT traffic before transmission
            _fileLogger?.Log($"[MQTT OUT] Topic: {topic} | Payload: {payload}");

            if (_isDisposed)
                return false;

            if (!IsConnected)
            {
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
                    // Published successfully
                }

                return result.IsSuccess;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to publish MQTT message to topic {Topic} - [MqttInfrastructureService]", topic);
                return false;
            }
        }

        /// <summary>
        /// Disconnect from MQTT broker and clean up resources
        /// </summary>
        public async Task DisconnectAsync()
        {
            try
            {
                await _connectionSemaphore.WaitAsync();
            }
            catch (ObjectDisposedException)
            {
                // Semaphore is disposed, service is shutting down
                return;
            }

            try
            {
                if (_mqttClient != null)
                {
                    await _mqttClient.DisconnectAsync();
                    _mqttClient.Dispose();
                    _mqttClient = null;
                }
                _isInitialized = false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during MQTT disconnect - [MqttInfrastructureService]");
            }
            finally
            {
                try
                {
                    if (!_isDisposed)
                    {
                        _connectionSemaphore.Release();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Safely ignore
                }
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
            if (_isDisposed) return;
            _isDisposed = true;

            // Unregister event handlers to prevent ghost events
            if (_mqttClient != null)
            {
                _mqttClient.ApplicationMessageReceivedAsync -= HandleIncomingMessageAsync;
                _mqttClient.DisconnectedAsync -= HandleDisconnectedAsync;
            }

            _mqttClient?.Dispose();
            _connectionSemaphore.Dispose();
        }
    }
}