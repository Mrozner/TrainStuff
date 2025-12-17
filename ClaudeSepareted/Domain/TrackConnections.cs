using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Represents connections between track sections (nodes in the track graph)
    /// </summary>
    public class TrackConnection
    {
        [Key]
        public int DB_ID { get; set; }

        /// <summary>
        /// Source section/subsection where the connection starts
        /// </summary>
        public int SourceSubSection_DB_ID { get; set; }

        /// <summary>
        /// Target section/subsection where the connection ends
        /// </summary>
        public int TargetSubSection_DB_ID { get; set; }

        /// <summary>
        /// Switch that controls this connection (null if no switch involved)
        /// </summary>
        public int? Switch_DB_ID { get; set; }

        /// <summary>
        /// Switch position required for this connection ("straight" or "turned")
        /// </summary>
        [StringLength(20)]
        public string RequiredSwitchPosition { get; set; }

        /// <summary>
        /// Direction of travel (true = forward, false = reverse)
        /// </summary>
        public bool Direction { get; set; }

        /// <summary>
        /// Is this connection currently active/usable
        /// </summary>
        public bool IsActive { get; set; } = true;

        /// <summary>
        /// Length of this connection in meters (for route optimization)
        /// </summary>
        public double Length { get; set; } = 1.0;

        // Navigation properties
        [ForeignKey("SourceSubSection_DB_ID")]
        public virtual SubSections SourceSubSection { get; set; }

        [ForeignKey("TargetSubSection_DB_ID")]
        public virtual SubSections TargetSubSection { get; set; }

        [ForeignKey("Switch_DB_ID")]
        public virtual Switches ControllingSwitch { get; set; }
    }
}