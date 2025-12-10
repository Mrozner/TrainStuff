using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Entity for V_PlatformEntry database view
    /// Provides optimized platform-to-platform routing with section locking information
    /// </summary>
    [Table("V_PlatformEntry")]
    public class VPlatformEntry
    {
        /// <summary>
        /// Platform database ID
        /// </summary>
        [Key]
        [Column("Platform_DB_ID")]
        public int Platform_DB_ID { get; set; }

        /// <summary>
        /// Lookup table database ID
        /// </summary>
        [Required]
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
        [Required]
        [Column("NextSection_DB_ID")]
        public int NextSection_DB_ID { get; set; }

        /// <summary>
        /// Direction of travel (0 or 1)
        /// </summary>
        [Required]
        public int Direction { get; set; }

        /// <summary>
        /// End of platform section for this direction
        /// </summary>
        [Required]
        [Column("EndOfPlatform")]
        public int EndOfPlatform { get; set; }

        /// <summary>
        /// Route string with section IDs (format: "#1#2#3#")
        /// </summary>
        [Required]
        public string Route { get; set; } = string.Empty;

        /// <summary>
        /// Hierarchical level in the platform entry hierarchy
        /// </summary>
        [Required]
        public int Level { get; set; }

        /// <summary>
        /// Comma-separated sections that need to be locked for this route
        /// </summary>
        [Column("SectionsToLock")]
        public string? SectionsToLock { get; set; }

        // Navigation properties
        /// <summary>
        /// Navigation to the platform
        /// </summary>
        public virtual Platforms? Platform { get; set; }

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