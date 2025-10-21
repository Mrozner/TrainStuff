using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TrainControlSystem.Models
{
    // ============================================================================
    // ENTITY MODELS
    // ============================================================================

    public class Stations
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class Sections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOccupied { get; set; }
        public int? TrainId { get; set; }

        [NotMapped]
        public List<Trains> TrainsInSection { get; set; } = new List<Trains>();
    }

    public class SubSections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public bool IsOccupied { get; set; }
        public int SectionId { get; set; }
        public int? TrainId { get; set; }

        // Navigation
        [NotMapped]
        [ForeignKey(nameof(SectionId))]
        public Sections Section { get; set; } = null!;

        [NotMapped]
        public List<Trains> TrainsInSubSection { get; set; } = new List<Trains>();
    }

    public class Trains
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public TrainState State { get; set; }
        public Speed CurrentSpeed { get; set; }
        public Speed MaxSpeed { get; set; }
        public int? SubSectionId { get; set; }

        // Navigation
        [NotMapped]
        [ForeignKey(nameof(SubSectionId))]
        public SubSections? SubSection { get; set; }
    }

    public class Signals
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; } = string.Empty;
        public SignalState State { get; set; }
        public Speed Speed { get; set; }
        public int SubSectionId { get; set; }

        // Navigation
        [NotMapped]
        [ForeignKey(nameof(SubSectionId))]
        public SubSections SubSection { get; set; } = null!;
    }

    public class TimetableEntries
    {
        [Key]
        public int DB_ID { get; set; }
        public string EntryID { get; set; } = Guid.NewGuid().ToString();
        public int SourceStation_DB_ID { get; set; }
        public int DestinationStation_DB_ID { get; set; }
        public int Train_DB_ID { get; set; }
        public DateTime StartDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public EntryState EntryState { get; set; }
        public RouteState RouteState { get; set; }
        [NotMapped]
        public DateTime? ArrivedTime { get; set; }

        // Navigation
        [NotMapped]
        [ForeignKey(nameof(SourceStation_DB_ID))]
        public Stations SourceStation { get; set; } = null!;

        [NotMapped]
        [ForeignKey(nameof(DestinationStation_DB_ID))]
        public Stations DestinationStation { get; set; } = null!;

        [NotMapped]
        [ForeignKey(nameof(Train_DB_ID))]
        public Trains Train { get; set; } = null!;
    }

    // ============================================================================
    // ENUMS
    // ============================================================================

    public enum Speed
    {
        STOP,
        SLOW,
        MEDIUM,
        FAST,
        MAX
    }

    public enum TrainState
    {
        Stopped,
        Moving,
        PrepareToStop
    }

    public enum SignalState
    {
        RED,
        YELLOW,
        GREEN
    }

    public enum EntryState
    {
        Scheduled,    // New entry scheduled for future departure
        InTransit,    // Train currently traveling between stations
        AtStation,    // Train currently at a station
        Upcoming,     // Entry is coming up (legacy)
        InProgress,   // Entry is in progress (legacy)
        Arrived       // Train has arrived at destination
    }

    public enum RouteState
    {
        InTime,
        Delayed,
        Early
    }
}