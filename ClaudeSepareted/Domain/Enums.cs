namespace ClaudeSepareted
{
    public enum Speed
    {
        ZERO = 0,
        SLOW = 30,
        MEDIUM = 60,
        HIGH = 90,
        MAX = 100
    }

    public enum TrainState
    {
        Waiting,
        Moving,
        Stopped,
        PrepareToStop,
        Arrived,
        WaitingForClearance  // NEW: Train is stopped waiting for track ahead to clear
    }

    public enum EntryState
    {
        Upcoming,
        InProgress,
        Arrived
    }

    public enum RouteState
    {
        InTime,
        Delay
    }

    public enum ObjectType
    {
        Signal,
        Hall,
        RFID,
        Switch
    }

    /// <summary>
    /// Represents a switch requirement for track routing.
    /// Replaces fragile (string, string) tuples with proper JSON serialization.
    /// </summary>
    public class SwitchRequirement
    {
        public string SwitchName { get; set; }
        public string RequiredPosition { get; set; }
    }
}