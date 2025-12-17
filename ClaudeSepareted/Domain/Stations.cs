using System.ComponentModel.DataAnnotations;

namespace ClaudeSepareted
{
    public class Stations
    {
        [Key]
        public int DB_ID { get; set; }
        public string Name { get; set; }
        public bool IsActive { get; set; }
    }
}