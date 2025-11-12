using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted
{
    public class Sections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool isActive { get; set; }
        [NotMapped]
        public List<SubSections> SubSections { get; set; } = new List<SubSections>();
    }

    public class SubSections
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        [NotMapped]
        public Sections Section { get; set; }
        public Speed AllowedSpeed { get; set; }
    }

    public class Platforms
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public int SubSection_DB_ID { get; set; }
        [NotMapped]
        public SubSections SubSection { get; set; }
        public int Station_DB_ID { get; set; }
        public bool isActive { get; set; }
    }

    public class Switches
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        [NotMapped]
        public string State { get; set; }
        [NotMapped]
        public int? TrainDB_ID { get; set; }
    }
}