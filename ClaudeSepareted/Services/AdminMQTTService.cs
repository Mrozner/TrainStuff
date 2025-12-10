using ClaudeSepareted.Domain;
using MQTTnet;
using System.Text;

namespace ClaudeSepareted
{
    public class AdminMQTTService
    {
        private readonly MQTTConfiguration _config;
        private readonly StatusNotificationService? _statusService;
        private IMqttClient? _mqttClient;
        private bool _isInitialized = false;
        private readonly object _lockObject = new object();

        public AdminMQTTService(MQTTConfiguration config, StatusNotificationService statusService = null)
        {
            _config = config;
            _statusService = statusService;
        }

        public async Task<bool> InitializeAsync()
        {
            lock (_lockObject)
            {
                if (_isInitialized)
                    return true;
            }

            try
            {
                _statusService?.ShowInfo("MQTT kapcsolódik...");

                var factory = new MqttClientFactory();
                _mqttClient = factory.CreateMqttClient();

                var options = new MqttClientOptionsBuilder()
                    .WithTcpServer(_config.Address, _config.Port)
                    .WithCleanSession()
                    .Build();

                var result = await _mqttClient.ConnectAsync(options);

                if (result.ResultCode == MqttClientConnectResultCode.Success)
                {
                    lock (_lockObject)
                    {
                        _isInitialized = true;
                    }
                    Console.WriteLine($"Admin MQTT connected to {_config.Address}:{_config.Port}");
                    _statusService?.ShowSuccess($"MQTT csatlakoztatva ({_config.Address}:{_config.Port})");
                    return true;
                }
                else
                {
                    Console.WriteLine($"MQTT connection failed: {result.ResultCode}");
                    _statusService?.ShowError($"MQTT kapcsolat hiba: {result.ResultCode}");
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
            if (!_isInitialized)
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
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    Console.WriteLine("MQTT client not connected");
                    _statusService?.ShowError($"MQTT nincs csatlakozva", trainName);
                    return false;
                }

                // Ellenőrizzük és szükség esetén bekapcsoljuk a teljes terepaszt és a vonat power-ét
                if (!await EnsureSystemPowerOnAsync(trainName))
                {
                    _statusService?.ShowError($"Rendszer power bekapcsolása sikertelen", trainName);
                    return false;
                }

                // Rocrail speed command format: <lc id="TrainName" v="speed" dir="true/false"/>
                // Convert Speed enum to numeric value and Direction to boolean
                int speedValue = (int)speed;
                bool directionValue = direction == Direction.Forward;
                var rocrailCommand = $"<lc id=\"{trainName}\" v=\"{speedValue}\" dir=\"{directionValue.ToString().ToLower()}\"/>";

                _statusService?.ShowInfo($"Sebesség parancs küldése: {speed} ({direction})", trainName);
                Console.WriteLine($"Sending Rocrail command: {rocrailCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.TrainSpeedCommandTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(rocrailCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _mqttClient.PublishAsync(mqttMessage);

                if (result.IsSuccess)
                {
                    _statusService?.ShowSuccess($"Sebesség beállítva: {speed} ({direction})", trainName);
                    Console.WriteLine($"Speed command sent to {trainName}: {speed} ({direction}) (Rocrail format)");
                    return true;
                }
                else
                {
                    _statusService?.ShowError($"Sebesség küldése sikertelen", trainName);
                    Console.WriteLine($"Failed to send speed command to {trainName}");
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

                // Rocrail system power command format: <sys cmd="go"/>
                var systemPowerOnCommand = "<sys cmd=\"go\"/>";

                Console.WriteLine($"Sending system power-on command: {systemPowerOnCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.TrainSpeedCommandTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(systemPowerOnCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _mqttClient.PublishAsync(mqttMessage);

                if (result.IsSuccess)
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
                    Console.WriteLine($"Failed to send system power-on command");
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

                // Rocrail power command format: <lc id="TrainName" cmd="on"/>
                var powerOnCommand = $"<lc id=\"{trainName}\" cmd=\"on\"/>";

                Console.WriteLine($"Sending power-on command: {powerOnCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.TrainSpeedCommandTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(powerOnCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _mqttClient.PublishAsync(mqttMessage);

                if (result.IsSuccess)
                {
                    _statusService?.ShowInfo($"Vonat power bekapcsolva: {trainName}", trainName);
                    Console.WriteLine($"Power-on command sent to {trainName}");

                    // Rövid várakozás, hogy a Rocrail feldolgozza a parancsot
                    await Task.Delay(500);

                    return true;
                }
                else
                {
                    Console.WriteLine($"Failed to send power-on command to {trainName}");
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
            if (!_isInitialized)
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
            if (!_isInitialized)
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
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    Console.WriteLine("MQTT client not connected");
                    _statusService?.ShowError($"MQTT nincs csatlakozva", trainName);
                    return false;
                }

                // Rocrail power off command format: <lc id="TrainName" cmd="off"/>
                var powerOffCommand = $"<lc id=\"{trainName}\" cmd=\"off\"/>";

                _statusService?.ShowInfo($"Vonat power kikapcsolása: {trainName}", trainName);
                Console.WriteLine($"Sending power-off command: {powerOffCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.TrainSpeedCommandTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(powerOffCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _mqttClient.PublishAsync(mqttMessage);

                if (result.IsSuccess)
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
                    Console.WriteLine($"Failed to send power-off command to {trainName}");
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
            if (!_isInitialized)
            {
                var connected = await InitializeAsync();
                if (!connected)
                {
                    Console.WriteLine("MQTT not connected for switch command");
                    return false;
                }
            }

            try
            {
                if (_mqttClient == null || !_mqttClient.IsConnected)
                {
                    Console.WriteLine("MQTT client not connected for switch command");
                    return false;
                }

                // Rocrail switch command format: <sw id="SwitchName" cmd="straight/turnout"/>
                var rocrailCommand = $"<sw id=\"{switchName}\" cmd=\"{position}\"/>";

                Console.WriteLine($"[AdminMQTT] Sending switch command: {rocrailCommand}");

                var mqttMessage = new MqttApplicationMessageBuilder()
                    .WithTopic(_config.TrainSpeedCommandTopic)
                    .WithPayload(Encoding.UTF8.GetBytes(rocrailCommand))
                    .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
                    .Build();

                var result = await _mqttClient.PublishAsync(mqttMessage);

                if (result.IsSuccess)
                {
                    Console.WriteLine($"[AdminMQTT] Switch command sent successfully: {switchName} -> {position}");
                    return true;
                }
                else
                {
                    Console.WriteLine($"[AdminMQTT] Failed to send switch command: {switchName} -> {position}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AdminMQTT] Error sending switch command: {ex.Message}");
                return false;
            }
        }

        public async Task DisconnectAsync()
        {
            if (_mqttClient != null && _mqttClient.IsConnected)
            {
                await _mqttClient.DisconnectAsync();
                Console.WriteLine("Admin MQTT disconnected");
            }
        }
    }
}