using System.Diagnostics;

namespace ClaudeSepareted
{
    public class TrainManagerService
    {
        private readonly TrackManager _trackManager;
        private readonly SystemConfiguration _config;
        private readonly TrainMQTTConnector _trainMQTTConnector;
        private readonly Dictionary<string, TrainController> _trainControllers = new Dictionary<string, TrainController>();

        public TrainManagerService(TrackManager trackManager, SystemConfiguration config, TrainMQTTConnector trainMQTTConnector)
        {
            _trackManager = trackManager;
            _config = config;
            _trainMQTTConnector = trainMQTTConnector;

            InitializeTrainControllers();
        }

        private void InitializeTrainControllers()
        {
            foreach (var train in _trackManager.Trains)
            {
                var controller = new TrainController(train, _config, _trainMQTTConnector);
                _trainControllers[train.Name] = controller;
            }

            Console.WriteLine($"Initialized {_trainControllers.Count} train controllers");
        }

        public void UpdateTrainSpeed(string trainName, Speed speed)
        {
            if (_trainControllers.TryGetValue(trainName, out var controller))
            {
                controller.SetSpeed(speed);
            }
            else
            {
                Console.WriteLine($"Train controller not found for: {trainName}");
            }
        }

        /// <summary>
        /// Gets train by name
        /// </summary>
        public Train GetTrain(string trainName)
        {
            return _trackManager.Trains.FirstOrDefault(t => t.Name == trainName);
        }

        /// <summary>
        /// Lists all available trains
        /// </summary>
        public void ListTrains()
        {
            Console.WriteLine("\nAvailable Trains:");
            foreach (var train in _trackManager.Trains)
            {
                Console.WriteLine($"  {train.Name}: State={train.State}, Speed={train.CurrentSpeed}, Location={train.SubSection?.Name}");
            }
        }
    }

    public class TrainController
    {
        private readonly Train _train;
        private readonly SystemConfiguration _config;
        private readonly TrainMQTTConnector _mqttConnector;

        public TrainController(Train train, SystemConfiguration config, TrainMQTTConnector mqttConnector)
        {
            _train = train;
            _config = config;
            _mqttConnector = mqttConnector;
        }

        public void SetSpeed(Speed speed)
        {
            try
            {
                Speed actualSpeed = speed > _train.MaxSpeed ? _train.MaxSpeed : speed;
                _train.CurrentSpeed = actualSpeed;

                // Send speed command to physical train via MQTT
                _mqttConnector.ChangeSpeed(_train, actualSpeed).Wait();

                Console.WriteLine($"Train {_train.Name} speed set to: {actualSpeed}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting speed for train {_train.Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Emergency stop the train
        /// </summary>
        public void EmergencyStop()
        {
            SetSpeed(Speed.STOP);
            _train.State = TrainState.Stopped;
            Console.WriteLine($"EMERGENCY STOP for train {_train.Name}");
        }
    }
}