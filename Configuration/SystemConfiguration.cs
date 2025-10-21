namespace TrainControlSystem.Configuration
{
    // ============================================================================
    // SYSTEM CONFIGURATION
    // ============================================================================

    public class SystemConfiguration
    {
        public string ConnectionString { get; set; } = null!;
        public MQTTConfiguration MQTT { get; set; } = null!;
        public TrackConfiguration Track { get; set; } = null!;
        public TimetableConfiguration Timetable { get; set; } = null!;
        public string SignalChangedMessage { get; set; } = "SignalChanged";
    }

    public class MQTTConfiguration
    {
        public string Address { get; set; } = "localhost";
        public int Port { get; set; } = 1883;

        // Track Topics
        public string TrackSectionTopic { get; set; } = "rocrail/service/info/fb";
        public string TrackPositionTopic { get; set; } = "track/info/hall";
        public string TrackRFIDTopic { get; set; } = "track/info/rfid";
        public string TrackCommandTopic { get; set; } = "rocrail/service/client";
        public string TrackSignalTopic { get; set; } = "track/command/signal";

        // Train Topics
        public string TrainSignalRequestTopic { get; set; } = "train/signal/request";
        public string TrainSignalResponseTopic { get; set; } = "train/signal/response";
        public string TrainSignalChangedTopic { get; set; } = "train/signal/changed";
        public string TrainSpeedCommandTopic { get; set; } = "rocrail/service/client";

        // Timetable Topics
        public string TimetableStartRequestTopic { get; set; } = "train/start/request";
        public string TimetableStatusTopic { get; set; } = "train/status";

        // Machinist Topics (train-specific)
        public MachinistTopicsConfiguration MachinistTopics { get; set; } = new MachinistTopicsConfiguration();
    }

    public class MachinistTopicsConfiguration
    {
        public string TrainCommandTemplate { get; set; } = "train/{trainName}/command";
        public string TrainResponseTemplate { get; set; } = "train/{trainName}/response";
        public string TrainFeedbackTemplate { get; set; } = "track/feedback/{trainName}";
        public string TrainStatusTemplate { get; set; } = "train/{trainName}/status";
        public string TrainLocationTemplate { get; set; } = "train/{trainName}/location";
        public string TrainSpeedTemplate { get; set; } = "train/{trainName}/speed";

        /// <summary>
        /// Gets the train-specific command topic for a given train name
        /// </summary>
        public string GetTrainCommandTopic(string trainName) =>
            TrainCommandTemplate.Replace("{trainName}", trainName);

        /// <summary>
        /// Gets the train-specific response topic for a given train name
        /// </summary>
        public string GetTrainResponseTopic(string trainName) =>
            TrainResponseTemplate.Replace("{trainName}", trainName);

        /// <summary>
        /// Gets the train-specific feedback topic for a given train name
        /// </summary>
        public string GetTrainFeedbackTopic(string trainName) =>
            TrainFeedbackTemplate.Replace("{trainName}", trainName);

        /// <summary>
        /// Gets the train-specific status topic for a given train name
        /// </summary>
        public string GetTrainStatusTopic(string trainName) =>
            TrainStatusTemplate.Replace("{trainName}", trainName);

        /// <summary>
        /// Gets the train-specific location topic for a given train name
        /// </summary>
        public string GetTrainLocationTopic(string trainName) =>
            TrainLocationTemplate.Replace("{trainName}", trainName);

        /// <summary>
        /// Gets the train-specific speed topic for a given train name
        /// </summary>
        public string GetTrainSpeedTopic(string trainName) =>
            TrainSpeedTemplate.Replace("{trainName}", trainName);
    }

    public class TrackConfiguration
    {
        public int MaxOccupiedSections { get; set; } = 100;
    }

    public class TimetableConfiguration
    {
        public int SchedulerIntervalSeconds { get; set; } = 30;
        public int MaxArrivedEntriesToKeep { get; set; } = 3;
    }
}