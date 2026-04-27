using ClaudeSepareted.Domain;
using ClaudeSepareted.Services;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Threading;
using System;
using Microsoft.Extensions.DependencyInjection;

namespace ClaudeSepareted
{
    /// <summary>
    /// Service for sending Rocrail commands via MQTT
    /// Handles train speed/power control and switch commands
    /// Implements state caching to eliminate redundant power commands
    /// </summary>
    public class RocrailCommandService
    {
        private readonly MQTTConfiguration _config;
        private readonly StatusNotificationService? _statusService;
        private readonly MqttInfrastructureService _mqttService;
        private readonly ILogger<RocrailCommandService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly FileLoggingService? _fileLogger;

        // State caching for power commands
        private volatile bool _isSystemPoweredOn = false;
        private readonly ConcurrentDictionary<string, bool> _poweredOnTrains = new(StringComparer.OrdinalIgnoreCase);

        // Lock for system power state to prevent race conditions
        private readonly object _systemPowerLock = new object();

        // Per-train command deduplication (prevents sending identical commands for same train)
        private readonly ConcurrentDictionary<string, string> _lastSpeedCommands = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _lastPowerCommands = new(StringComparer.OrdinalIgnoreCase);

        public RocrailCommandService(
            MQTTConfiguration config,
            MqttInfrastructureService mqttService,
            StatusNotificationService statusService = null,
            IServiceProvider serviceProvider = null,
            ILogger<RocrailCommandService> logger = null,
            FileLoggingService fileLogger = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _statusService = statusService;
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger;
            _fileLogger = fileLogger;
        }

        public async Task<bool> SendTrainSpeedCommandAsync(string trainName, Speed speed, Direction direction = Direction.Forward)
        {
            try
            {
                // Generate the XML string using RocrailCommandFactory.TrainVelocity
                bool directionValue = direction == Direction.Forward;
                var rocrailCommand = RocrailCommandFactory.TrainVelocity(trainName, (int)speed, directionValue);

                // Check 1 (Deduplication): Per-train deduplication check
                if (_lastSpeedCommands.TryGetValue(trainName, out var lastCommand) && lastCommand == rocrailCommand)
                {
                    _fileLogger?.Log($"[ROCRAIL CACHE] Suppressed duplicate speed command for {trainName}");
                    _logger?.LogDebug("Duplicate speed command skipped for train {TrainName}: {Command}", trainName, rocrailCommand);
                    return false;
                }

                // Check 2 (Track Handler): Global deduplication check via TrackHandlerService
                var trackHandlerService = _serviceProvider.GetService<TrackHandlerService>();
                if (trackHandlerService != null && !trackHandlerService.ShouldSendTrainCommand(trainName, rocrailCommand))
                {
                    _fileLogger?.Log($"[ROCRAIL CACHE] TrackHandler blocked speed command for {trainName}");
                    _logger?.LogDebug("TrackHandler blocked speed command for train {TrainName}", trainName);
                    return false;
                }

                // Check 3 (Power): Ensure system and train power are on
                if (!await EnsureSystemPowerOnAsync(trainName))
                {
                    return false;
                }

                // All checks passed - publish the MQTT message
                _logger?.LogDebug("Sending speed command: {Speed} ({Direction}) to train {TrainName}", speed, direction, trainName);
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, rocrailCommand);

                if (success)
                {
                    // Update per-train command cache on success
                    _lastSpeedCommands[trainName] = rocrailCommand;
                    _statusService?.ShowSuccess($"Sebesség beállítva: {speed} ({direction})", trainName);
                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Sebesség küldése sikertelen - [RocrailCommandService]", trainName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Sebesség parancs hiba: {ex.Message} - [RocrailCommandService]", trainName);
                return false;
            }
        }

        private async Task<bool> EnsureSystemPowerOnAsync(string trainName)
        {
            try
            {
                // Fast-exit check: system and train already powered (Step 1: Performance)
                if (_isSystemPoweredOn && _poweredOnTrains.ContainsKey(trainName))
                {
                    return true;
                }

                // Thread-safe system power-on check with lock to prevent cold-start race condition
                bool shouldPowerOn = false;
                lock (_systemPowerLock)
                {
                    if (!_isSystemPoweredOn)
                    {
                        shouldPowerOn = true;
                        _isSystemPoweredOn = true; // Set it immediately so other threads don't enter
                    }
                }

                if (shouldPowerOn)
                {
                    _logger?.LogDebug("Powering on layout for train {TrainName}", trainName);

                    // Small delay for stability
                    await Task.Delay(200);

                    // Use RocrailCommandFactory for XML generation
                    var systemPowerOnCommand = RocrailCommandFactory.SystemPower(true);

                    // Use centralized MQTT service with correct topic (Step 3)
                    var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, systemPowerOnCommand);

                    if (!success)
                    {
                        // Rollback the cache flag if power-on failed
                        lock (_systemPowerLock)
                        {
                            _isSystemPoweredOn = false;
                        }

                        // Standardized error handling (Step 4)
                        _statusService?.ShowError($"Terepaszt power bekapcsolása sikertelen - [RocrailCommandService]", trainName);
                        return false;
                    }

                    // Wait for Rocrail to process
                    await Task.Delay(1000);
                }

                // Now ensure train power
                return await PowerOnTrainAsync(trainName);
            }
            catch (Exception ex)
            {
                // Rollback the cache flag on exception
                lock (_systemPowerLock)
                {
                    _isSystemPoweredOn = false;
                }

                // Standardized error handling (Step 4)
                _statusService?.ShowError($"Rendszer power bekapcsolási hiba: {ex.Message} - [RocrailCommandService]", trainName);
                return false;
            }
        }

        public async Task<bool> PowerOnTrainAsync(string trainName)
        {
            try
            {
                // Fast-exit check: train already powered (Step 1: Performance)
                if (_poweredOnTrains.ContainsKey(trainName))
                {
                    return true;
                }

                _logger?.LogDebug("Powering on train {TrainName}", trainName);

                // Use RocrailCommandFactory for XML generation
                var powerOnCommand = RocrailCommandFactory.TrainPower(trainName, true);

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, powerOnCommand);

                if (success)
                {
                    // Update cache (Step 1)
                    _poweredOnTrains.TryAdd(trainName, true);

                    // Wait for Rocrail to process
                    await Task.Delay(500);

                    return true;
                }
                else
                {
                    // Standardized error handling (Step 4)
                    _statusService?.ShowError($"Vonat power bekapcsolása sikertelen: {trainName} - [RocrailCommandService]");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Standardized error handling (Step 4)
                _statusService?.ShowError($"Vonat power bekapcsolása sikertelen: {trainName} - [RocrailCommandService]");
                return false;
            }
        }

        public async Task<bool> PowerOffTrainAsync(string trainName)
        {
            // Removed redundant connection check
            try
            {
                var powerOffCommand = RocrailCommandFactory.TrainPower(trainName, false);
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, powerOffCommand);

                if (success)
                {
                    _poweredOnTrains.TryRemove(trainName, out _);
                    _logger?.LogDebug("Train {TrainName} powered off successfully", trainName);
                    await Task.Delay(500);
                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Vonat power kikapcsolása sikertelen - [RocrailCommandService]", trainName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Vonat power kikapcsolási hiba: {ex.Message} - [RocrailCommandService]", trainName);
                return false;
            }
        }

        public async Task<bool> SendSwitchCommandAsync(string switchName, string position)
        {
            // Reduced notification spam - Only show errors, not info
            _logger?.LogDebug("Attempting switch command: {SwitchName} -> {Position}", switchName, position);

            try
            {
                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.Switch(switchName, position);

                _logger?.LogDebug("Sending MQTT switch command to topic {Topic}", _config.TrackCommandTopic);

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrackCommandTopic, rocrailCommand);

                if (success)
                {
                    _logger?.LogDebug("Switch command sent successfully: {SwitchName} -> {Position}", switchName, position);
                    return true;
                }
                else
                {
                    // Standardized error handling (Step 4)
                    _statusService?.ShowError($"Váltó parancs küldése sikertelen: {switchName} - [RocrailCommandService]");
                    return false;
                }
            }
            catch (Exception ex)
            {
                // Standardized error handling (Step 4)
                _statusService?.ShowError($"Váltó parancs hiba: {ex.Message} - [RocrailCommandService]");
                return false;
            }
        }

        /// <summary>
        /// Invalidate the power cache when external state changes occur
        /// Call this method if the physical layout loses power or is turned off manually in Rocrail
        /// This ensures the internal state stays synchronized with the actual hardware state
        /// </summary>
        public void InvalidatePowerCache()
        {
            _isSystemPoweredOn = false;
            _poweredOnTrains.Clear();
            _logger?.LogInformation("Power cache invalidated due to external state change.");
        }

        public async Task DisconnectAsync()
        {
            // No need to disconnect from centralized MQTT service
            // The infrastructure service manages the connection lifecycle
        }

        #region Train Object Overloads

        /// <summary>
        /// Send a train command (speed/direction) via MQTT using Train object
        /// </summary>
        public async Task<bool> SendTrainCommandAsync(Train train, Speed speed = Speed.ZERO, bool direction = true)
        {
            try
            {
                int speedValue = (int)speed;

                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.TrainVelocity(train.Name, speedValue, direction);

                // Check if this speed command is different from the last one sent (per-train check)
                if (_lastSpeedCommands.TryGetValue(train.Name, out var lastCommand) && lastCommand == rocrailCommand)
                {
                    return false;
                }

                // Additional global check if TrackHandlerService is available
                var trackHandlerService = _serviceProvider.GetService<TrackHandlerService>();
                if (trackHandlerService != null && !trackHandlerService.ShouldSendTrainCommand(train.Name, rocrailCommand))
                {
                    return false;
                }

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, rocrailCommand);

                if (success)
                {
                    // Store the speed command that was sent for this train
                    _lastSpeedCommands[train.Name] = rocrailCommand;
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Error sending train command: {ex.Message} - [RocrailCommandService]", train.Name);
                return false;
            }
        }

        /// <summary>
        /// Send power on/off command to train using Train object
        /// </summary>
        public async Task<bool> SendTrainPowerCommandAsync(Train train, bool powerOn)
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.TrainPower(train.Name, powerOn);

                // Deduplication: skip if we've already sent this exact command for this train
                if (_lastPowerCommands.TryGetValue(train.Name, out var lastCommand) && lastCommand == powerCommand)
                {
                    _fileLogger?.Log($"[ROCRAIL CACHE] Suppressed duplicate power command for {train.Name}");
                    return false;
                }

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, powerCommand);

                if (success)
                {
                    _lastPowerCommands[train.Name] = powerCommand;
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Error sending power command: {ex.Message} - [RocrailCommandService]", train.Name);
                return false;
            }
        }

        /// <summary>
        /// Send system power command
        /// </summary>
        public async Task<bool> SendSystemPowerCommandAsync()
        {
            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerCommand = RocrailCommandFactory.SystemPower(true);

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.RocrailIngressTopic, powerCommand);

                if (success)
                {
                    await Task.Delay(1000); // Wait for power to come on
                    return true;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Error sending power command: {ex.Message} - [RocrailCommandService]");
                return false;
            }
        }

        /// <summary>
        /// Reset command history (useful when starting a new journey)
        /// Clears both per-train command caches and internal power caching logic
        /// </summary>
        public void ResetCommandHistory()
        {
            // Clear per-train command history
            _lastSpeedCommands.Clear();
            _lastPowerCommands.Clear();

            // Clear internal power caching logic
            _isSystemPoweredOn = false;
            _poweredOnTrains.Clear();

            _logger?.LogDebug("Command history and power cache reset");
        }

        /// <summary>
        /// Get the last speed command that was sent for a specific train
        /// </summary>
        public string? GetLastSpeedCommand(string trainName)
        {
            return _lastSpeedCommands.TryGetValue(trainName, out var lastCommand) ? lastCommand : null;
        }

        /// <summary>
        /// Get the last power command that was sent for a specific train
        /// </summary>
        public string? GetLastPowerCommand(string trainName)
        {
            return _lastPowerCommands.TryGetValue(trainName, out var lastCommand) ? lastCommand : null;
        }

        #endregion
    }
}