using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.MQTT;

namespace TrainControlSystem.Services
{
    /// <summary>
    /// Controller for individual train operations
    /// </summary>
    public class TrainController
    {
        private readonly Models.Trains _train;
        private readonly SystemConfiguration _config;
        private readonly TrainMQTTConnector _mqttConnector;
        private readonly ILogger<TrainController> _logger;

        public TrainController(
            Models.Trains train,
            SystemConfiguration config,
            TrainMQTTConnector mqttConnector,
            ILogger<TrainController> logger)
        {
            _train = train;
            _config = config;
            _mqttConnector = mqttConnector;
            _logger = logger;
        }

        /// <summary>
        /// Sets the train speed
        /// </summary>
        public async Task SetSpeed(Speed speed)
        {
            try
            {
                Speed actualSpeed = speed > _train.MaxSpeed ? _train.MaxSpeed : speed;
                _train.CurrentSpeed = actualSpeed;

                // Send speed command to physical train via MQTT
                await _mqttConnector.ChangeSpeed(_train, actualSpeed);

                _logger.LogInformation($"Train {_train.Name} speed set to: {actualSpeed}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error setting speed for train {_train.Name}");
            }
        }

        /// <summary>
        /// Emergency stop the train
        /// </summary>
        public async Task EmergencyStop()
        {
            try
            {
                await SetSpeed(Speed.STOP);
                _train.State = TrainState.Stopped;

                // Send emergency stop command via MQTT
                await _mqttConnector.EmergencyStop(_train);

                _logger.LogWarning($"EMERGENCY STOP for train {_train.Name}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error during emergency stop for train {_train.Name}");
            }
        }

        /// <summary>
        /// Gets train information
        /// </summary>
        public Models.Trains GetTrainInfo()
        {
            return _train;
        }

        /// <summary>
        /// Updates train state
        /// </summary>
        public void UpdateState(TrainState state)
        {
            _train.State = state;
            _logger.LogInformation($"Train {_train.Name} state updated to: {state}");
        }

        /// <summary>
        /// Requests a signal for the train
        /// </summary>
        public async Task RequestSignal(string signalRequest)
        {
            try
            {
                await _mqttConnector.RequestSignal(_train, signalRequest);
                _logger.LogInformation($"Train {_train.Name} requested signal: {signalRequest}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error requesting signal for train {_train.Name}");
            }
        }
    }
}