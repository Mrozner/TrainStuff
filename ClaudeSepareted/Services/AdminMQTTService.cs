using ClaudeSepareted.Domain;
using ClaudeSepareted.Services;
using MQTTnet;
using System.Text;

namespace ClaudeSepareted
{
    public class AdminMQTTService
    {
        private readonly MQTTConfiguration _config;
        private readonly StatusNotificationService? _statusService;
        private readonly MqttInfrastructureService _mqttService;

        public AdminMQTTService(MQTTConfiguration config, MqttInfrastructureService mqttService, StatusNotificationService statusService = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _statusService = statusService;
        }

        public async Task<bool> InitializeAsync()
        {
            try
            {
                _statusService?.ShowInfo("MQTT kapcsolódik...");

                var success = await _mqttService.InitializeAsync();

                if (success)
                {
                    Console.WriteLine($"Admin MQTT connected via centralized infrastructure to {_config.Address}:{_config.Port}");
                    _statusService?.ShowSuccess($"MQTT csatlakoztatva ({_config.Address}:{_config.Port})");
                    return true;
                }
                else
                {
                    Console.WriteLine("Centralized MQTT connection failed");
                    _statusService?.ShowError("Központi MQTT kapcsolat hiba");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MQTT initialization error: {ex.Message}");
                _statusService?.ShowError($"MQTT inicializációs hiba: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendTrainSpeedCommandAsync(string trainName, Speed speed, Direction direction = Direction.Forward)
        {
            try
            {
                // Ensure centralized MQTT is connected
                if (!_mqttService.IsConnected)
                {
                    _statusService?.ShowInfo($"MQTT csatlakozás...", trainName);
                    var connected = await InitializeAsync();
                    if (!connected)
                    {
                        _statusService?.ShowError($"MQTT kapcsolat sikertelen", trainName);
                        return false;
                    }
                }

                // Check system power and train power
                if (!await EnsureSystemPowerOnAsync(trainName))
                {
                    _statusService?.ShowError($"Rendszer power bekapcsolása sikertelen", trainName);
                    return false;
                }

                // Use RocrailCommandFactory for XML generation
                bool directionValue = direction == Direction.Forward;
                var rocrailCommand = RocrailCommandFactory.TrainVelocity(trainName, speed, directionValue);

                _statusService?.ShowInfo($"Sebesség parancs küldése: {speed} ({direction})", trainName);
                Console.WriteLine($"Sending Rocrail command: {rocrailCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrainSpeedCommandTopic, rocrailCommand);

                if (success)
                {
                    _statusService?.ShowSuccess($"Sebesség beállítva: {speed} ({direction})", trainName);
                    Console.WriteLine($"Speed command sent to {trainName}: {speed} ({direction}) (Rocrail format)");
                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Sebesség küldése sikertelen", trainName);
                    Console.WriteLine($"Failed to send speed command to {trainName} via MQTT infrastructure");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Hiba: {ex.Message}", trainName);
                Console.WriteLine($"Error sending speed command: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> EnsureSystemPowerOnAsync(string trainName)
        {
            try
            {
                _statusService?.ShowInfo($"Rendszer power ellenőrzése: {trainName}", trainName);

                // Először a terepaszt/layout power-jét kapcsoljuk be
                await Task.Delay(200); // Rövid várakozás a stabilitáshoz

                // Use RocrailCommandFactory for XML generation
                var systemPowerOnCommand = RocrailCommandFactory.SystemPower(true);

                Console.WriteLine($"Sending system power-on command: {systemPowerOnCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrainSpeedCommandTopic, systemPowerOnCommand);

                if (success)
                {
                    _statusService?.ShowInfo($"Terepaszt power bekapcsolva", trainName);
                    Console.WriteLine($"System power-on command sent successfully");

                    // Rövid várakozás, hogy a Rocrail feldolgozza a parancsot
                    await Task.Delay(1000);

                    // Majd a vonat power-jét is bekapcsoljuk
                    return await EnsureTrainPowerOnAsync(trainName);
                }
                else
                {
                    Console.WriteLine($"Failed to send system power-on command via MQTT infrastructure");
                    _statusService?.ShowError($"Terepaszt power bekapcsolása sikertelen", trainName);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending system power-on command: {ex.Message}");
                return false;
            }
        }

        private async Task<bool> EnsureTrainPowerOnAsync(string trainName)
        {
            try
            {
                _statusService?.ShowInfo($"Vonat power ellenőrzése: {trainName}", trainName);

                // Use RocrailCommandFactory for XML generation
                var powerOnCommand = RocrailCommandFactory.TrainPower(trainName, true);

                Console.WriteLine($"Sending power-on command: {powerOnCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrainSpeedCommandTopic, powerOnCommand);

                if (success)
                {
                    _statusService?.ShowInfo($"Vonat power bekapcsolva: {trainName}", trainName);
                    Console.WriteLine($"Power-on command sent to {trainName}");

                    // Rövid várakozás, hogy a Rocrail feldolgozza a parancsot
                    await Task.Delay(500);

                    return true;
                }
                else
                {
                    Console.WriteLine($"Failed to send power-on command to {trainName} via MQTT infrastructure");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending power-on command: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> PowerOnTrainAsync(string trainName)
        {
            if (!_mqttService.IsConnected)
            {
                _statusService?.ShowInfo($"MQTT csatlakozás...", trainName);
                var connected = await InitializeAsync();
                if (!connected)
                {
                    _statusService?.ShowError($"MQTT kapcsolat sikertelen", trainName);
                    return false;
                }
            }

            return await EnsureTrainPowerOnAsync(trainName);
        }

        public async Task<bool> PowerOffTrainAsync(string trainName)
        {
            if (!_mqttService.IsConnected)
            {
                _statusService?.ShowInfo($"MQTT csatlakozás...", trainName);
                var connected = await InitializeAsync();
                if (!connected)
                {
                    _statusService?.ShowError($"MQTT kapcsolat sikertelen", trainName);
                    return false;
                }
            }

            try
            {
                // Use RocrailCommandFactory for XML generation
                var powerOffCommand = RocrailCommandFactory.TrainPower(trainName, false);

                _statusService?.ShowInfo($"Vonat power kikapcsolása: {trainName}", trainName);
                Console.WriteLine($"Sending power-off command: {powerOffCommand}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrainSpeedCommandTopic, powerOffCommand);

                if (success)
                {
                    _statusService?.ShowInfo($"Vonat power kikapcsolva: {trainName}", trainName);
                    Console.WriteLine($"Power-off command sent to {trainName}");

                    // Rövid várakozás, hogy a Rocrail feldolgozza a parancsot
                    await Task.Delay(500);

                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Vonat power kikapcsolása sikertelen", trainName);
                    Console.WriteLine($"Failed to send power-off command to {trainName} via MQTT infrastructure");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Hiba: {ex.Message}", trainName);
                Console.WriteLine($"Error sending power-off command: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> SendSwitchCommandAsync(string switchName, string position)
        {
            _statusService?.ShowInfo($"Attempting switch command: {switchName} -> {position}");

            if (!_mqttService.IsConnected)
            {
                var connected = await InitializeAsync();
                if (!connected)
                {
                    _statusService?.ShowError("MQTT not connected for switch command");
                    return false;
                }
            }

            try
            {
                // Use RocrailCommandFactory for XML generation
                var rocrailCommand = RocrailCommandFactory.Switch(switchName, position);

                _statusService?.ShowInfo($"Sending MQTT: {rocrailCommand} to topic: {_config.TrackCommandTopic}");

                // Use centralized MQTT service
                var success = await _mqttService.PublishAsync(_config.TrackCommandTopic, rocrailCommand);

                if (success)
                {
                    _statusService?.ShowSuccess($"Switch command sent: {switchName} -> {position}");
                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Failed to send switch: {switchName} -> {position}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Switch command error: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            // No need to disconnect from centralized MQTT service
            // The infrastructure service manages the connection lifecycle
            Console.WriteLine("Admin MQTT service disconnect request - managed by centralized infrastructure");
        }
    }
}