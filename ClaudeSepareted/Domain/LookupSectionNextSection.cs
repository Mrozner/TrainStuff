using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Represents the basic route connections between track sections
    /// </summary>
    [Table("Lookup_Section_NextSection")]
    public class LookupSectionNextSection
    {
        [Key]
        public int DB_ID { get; set; }

        [Column("Section_DB_ID")]
        public int Section_DB_ID { get; set; }

        [Column("NextSection_DB_ID")]
        public int? NextSection_DB_ID { get; set; }

        public bool Direction { get; set; }

        public bool IsActive { get; set; }
    }

    /// <summary>
    /// Represents switch constraints for route sections
    /// </summary>
    [Table("Lookup_SectionNextSection_Switches")]
    public class LookupSectionNextSectionSwitches
    {
        [Key]
        public int DB_ID { get; set; }

        public int Lookup_DB_ID { get; set; }

        public int Switch_DB_ID { get; set; }

        [ForeignKey("Switch_DB_ID")]
        public virtual Switches Switch { get; set; }

        [ForeignKey("Lookup_DB_ID")]
        public virtual LookupSectionNextSection Lookup { get; set; }

        [ForeignKey("Lookup_DB_ID")]
        public virtual LookupSectionNextSectionNextSection NextSection { get; set; }
    }

    /// <summary>
    /// Represents destinations for route sections
    /// </summary>
    [Table("Lookup_SectionNextSection_Destinations")]
    public class LookupSectionNextSectionDestinations
    {
        [Key]
        public int DB_ID { get; set; }

        public int Lookup_DB_ID { get; set; }

        public int Platform_DB_ID { get; set; }

        [ForeignKey("Lookup_DB_ID")]
        public virtual LookupSectionNextSection Lookup { get; set; }

        [ForeignKey("Platform_DB_ID")]
        public virtual Platforms Platform { get; set; }
    }

    /// <summary>
    /// Represents the next section connection for route sections
    /// </summary>
    [Table("Lookup_Sections_SubSections")]
    public class LookupSectionsSubSections
    {
        [Key]
        public int DB_ID { get; set; }

        public int Section_DB_ID { get; set; }

        public int SubSection_DB_ID { get; set; }
    }

    /// <summary>
    /// Represents next section connections for route sections
    /// </summary>
    [Table("Lookup_SectionNextSectionNextSection")]
    public class LookupSectionNextSectionNextSection
    {
        [Key]
        public int DB_ID { get; set; }

        public int Lookup_DB_ID { get; set; }

        public int NextSection_DB_ID { get; set; }
    }
}