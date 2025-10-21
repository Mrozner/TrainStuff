using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted
{
    public class Objects
    {
        [Key]
        public int DB_ID { get; set; }
        public int SubSection_DB_ID { get; set; }
        public bool Direction { get; set; }
        public ObjectType ObjectType { get; set; }
        public string ObjectID { get; set; }
    }

    public class Signal
    {
        [Key]
        public string ID { get; set; }
        public Speed Speed { get; set; }
        public Speed? NextSpeed { get; set; }
    }
}