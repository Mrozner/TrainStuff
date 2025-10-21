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
        [ForeignKey(nameof(SourceStation_DB_ID))]
        public Stations SourceStation { get; set; }
        [ForeignKey(nameof(DestinationStation_DB_ID))]
        public Stations DestinationStation { get; set; }
        [ForeignKey(nameof(Train_DB_ID))]
        public Train Train { get; set; }
    }
}