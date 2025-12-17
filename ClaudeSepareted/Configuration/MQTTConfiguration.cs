// Placeholder for MQTTConfiguration.cs
namespace ClaudeSepareted {
    public class MQTTConfiguration
    {
        public string Address { get; set; } = "localhost";
        public int Port { get; set; } = 1883;

        // Track Topics
        public string TrackSectionTopic { get; set; } = "rocrail/service/info";
        public string TrackPositionTopic { get; set; } = "track/info/hall";
        public string TrackRFIDTopic { get; set; } = "track/info/rfid";
        public string TrackCommandTopic { get; set; } = "rocrail/service/client";
        public string TrackSignalTopic { get; set; } = "track/command/signal";
        public string SwitchCommandTopic { get; set; } = "rocrail/service/client";

        // Train Topics
        public string TrainSignalRequestTopic { get; set; } = "train/signal/request";
        public string TrainSignalResponseTopic { get; set; } = "train/signal/response";
        public string TrainSignalChangedTopic { get; set; } = "train/signal/changed";
        public string TrainSpeedCommandTopic { get; set; } = "rocrail/service/client";

        // Timetable Topics
        public string TimetableStartRequestTopic { get; set; } = "train/start/request";
        public string TimetableStatusTopic { get; set; } = "train/status";
    }
}
