using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Entity for V_Lookup_Section_NextSection database view
    /// Provides optimized lookup for section-to-section routing with switch constraints
    /// </summary>
    [Table("V_Lookup_Section_NextSection")]
    public class VLookupSectionNextSection
    {
        /// <summary>
        /// Primary key for the lookup table
        /// </summary>
        [Key]
        [Column("DB_ID")]
        public int DB_ID { get; set; }

        /// <summary>
        /// Current section database ID
        /// </summary>
        [Required]
        [Column("Section_DB_ID")]
        public int Section_DB_ID { get; set; }

        /// <summary>
        /// Next section database ID
        /// </summary>
        [Column("NextSection_DB_ID")]
        public int? NextSection_DB_ID { get; set; }

        /// <summary>
        /// Direction of travel (0 or 1)
        /// </summary>
        [Required]
        [Column("Direction")]
        public int Direction { get; set; }

        /// <summary>
        /// Aggregated destination platforms for this route
        /// </summary>
        [Required]
        [Column("Destinations")]
        public string Destinations { get; set; } = string.Empty;

        /// <summary>
        /// Comma-separated switch constraints for this route segment
        /// Format: "switchName=position,switchName=position,..."
        /// </summary>
        [Required]
        [Column("SwitchConstraints")]
        public string SwitchConstraints { get; set; } = string.Empty;

        // Navigation properties (if needed)
        /// <summary>
        /// Navigation to the current section
        /// </summary>
        public virtual Sections? Section { get; set; }

        /// <summary>
        /// Navigation to the next section
        /// </summary>
        public virtual Sections? NextSection { get; set; }
    }
}