// Placeholder for SystemConfiguration.cs
namespace ClaudeSepareted {
    public class SystemConfiguration
    {
        public string ConnectionString { get; set; }
        public MQTTConfiguration MQTT { get; set; }
        public TrackConfiguration Track { get; set; }
        public TimetableConfiguration Timetable { get; set; }
        public string SignalChangedMessage { get; set; } = "SignalChanged";
    }
}
