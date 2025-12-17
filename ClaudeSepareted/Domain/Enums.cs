namespace ClaudeSepareted
{
    public enum Speed
    {
        STOP = 0,
        SLOW = 30,
        MEDIUM = 60,
        HIGH = 90
    }

    public enum TrainState
    {
        Waiting,
        Moving,
        Stopped,
        PrepareToStop,
        Arrived
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
        RFID
    }
}