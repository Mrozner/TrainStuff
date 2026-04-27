using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted
{
    public class Train
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool Direction { get; set; }
        public bool IsActive { get; set; }
        public int Platform_DB_ID { get; set; }
        public Speed MaxSpeed { get; set; }
        [NotMapped]
        public Speed CurrentSpeed { get; set; } = Speed.ZERO;
        [NotMapped]
        public TrainState State { get; set; } = TrainState.Waiting;

        // Navigation
        [NotMapped]
        public Platforms Source { get; set; }
        [NotMapped]
        public Platforms Destination { get; set; }
        [NotMapped]
        public SubSections SubSection { get; set; }
        [NotMapped]
        public Sections Section { get; set; }
        [NotMapped]
        public Sections NextSection { get; set; }
        [NotMapped]
        public int? NextSubsection_DB_ID { get; set; }

        // Runtime data
        [NotMapped]
        public List<int> LockedSections { get; set; } = new List<int>();
        [NotMapped]
        public List<Switches> LockedSwitches { get; set; } = new List<Switches>();
        [NotMapped]
        public List<string> CarriageIdentifiers { get; set; } = new List<string>();
        [NotMapped]
        public string StopHall { get; set; }
        [NotMapped]
        public string CurrentEntryID { get; set; }

        public bool Arrived()
        {
            return SubSection?.DB_ID == Destination?.SubSection_DB_ID ||
                   SubSection?.DB_ID == Source?.SubSection_DB_ID;
        }
    }
}