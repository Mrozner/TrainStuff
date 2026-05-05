// Placeholder for MQTTConfiguration.cs
namespace ClaudeSepareted {
    public class MQTTConfiguration
    {
        public string Address { get; set; } = "172.22.2.2";
        public int Port { get; set; } = 1883;

        // Unified Rocrail ingress topic - consolidates switch, train speed, and system commands
        public string RocrailIngressTopic { get; set; } = "rocrail/service/client";

        // Track Topics
        public string TrackSectionTopic { get; set; } = "rocrail/service/info";
        public string TrackPositionTopic { get; set; } = "track/info/hall";
        public string TrackRFIDTopic { get; set; } = "track/info/rfid";
        public string TrackCommandTopic { get; set; } = "rocrail/service/client";
        public string TrackSignalTopic { get; set; } = "track/command/signal";

        // Train Topics
        public string TrainSignalRequestTopic { get; set; } = "train/signal/request";
        public string TrainSignalResponseTopic { get; set; } = "train/signal/response";
        public string TrainSignalChangedTopic { get; set; } = "train/signal/changed";

        // Timetable Topics
        public string TimetableStartRequestTopic { get; set; } = "train/start/request";
        public string TimetableStatusTopic { get; set; } = "train/status";
    }
}
