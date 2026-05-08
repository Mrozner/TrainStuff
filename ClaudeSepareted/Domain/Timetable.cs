using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics;

namespace ClaudeSepareted
{
    public class TimetableEntries
    {
        [Key]
        public int DB_ID { get; set; }
        public string EntryID { get; set; } = Guid.NewGuid().ToString();
        public int SourcePlatform_DB_ID { get; set; }
        public int DestinationPlatform_DB_ID { get; set; }
        public int Train_DB_ID { get; set; }
        public DateTime StartDate { get; set; }
        public TimeSpan StartTime { get; set; }
        public EntryState EntryState { get; set; }
        public RouteState RouteState { get; set; }

        // NEW: Stores the physical travel direction for this specific journey (True = Forward, False = Reverse)
        public bool Direction { get; set; } = true;

        [NotMapped]
        public DateTime? ArrivedTime { get; set; }

        // Navigation properties
        [ForeignKey(nameof(SourcePlatform_DB_ID))]
        public Platforms SourcePlatform { get; set; }
        [ForeignKey(nameof(DestinationPlatform_DB_ID))]
        public Platforms DestinationPlatform { get; set; }
        [ForeignKey(nameof(Train_DB_ID))]
        public Train Train { get; set; }
    }
}