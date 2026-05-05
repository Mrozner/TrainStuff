using System.Collections.Generic;
using System.Linq;

namespace ClaudeSepareted.Domain
{
    /// <summary>
    /// Represents a planned route through the railway network
    /// Moved from Services layer to Domain to break circular dependency
    /// </summary>
    public class RoutePlan
    {
        public List<EdgeInfo> Path { get; set; } = new List<EdgeInfo>();
        public double TotalLength { get; set; }

        /// <summary>
        /// Gets the unique switches that need to be configured for this route
        /// </summary>
        public List<SwitchConfiguration> GetSwitchConfigurations()
        {
            var switchConfigs = new Dictionary<string, string>();

            foreach (var edge in Path)
            {
                if (edge.SwitchRequirements != null)
                {
                    foreach (var req in edge.SwitchRequirements)
                    {
                        if (!string.IsNullOrEmpty(req.SwitchName) && !string.IsNullOrEmpty(req.RequiredPosition))
                        {
                            switchConfigs[req.SwitchName] = req.RequiredPosition;
                        }
                    }
                }
            }

            return switchConfigs.Select(kvp => new SwitchConfiguration(kvp.Key, kvp.Value)).ToList();
        }

        public bool HasPath => Path.Any();
    }

    /// <summary>
    /// Represents a required switch configuration for a route
    /// Moved from Services layer to Domain to break circular dependency
    /// </summary>
    public class SwitchConfiguration
    {
        public string SwitchName { get; set; }
        public string Position { get; set; } // "straight" or "turnout"

        public SwitchConfiguration(string switchName, string position)
        {
            SwitchName = switchName;
            Position = position;
        }
    }

    /// <summary>
    /// Information about a track edge in a route
    /// Moved from Services layer to Domain to break circular dependency
    /// </summary>
    public class EdgeInfo
    {
        public int SourceNodeId { get; set; }
        public int TargetNodeId { get; set; }

        /// <summary>
        /// List of switch requirements for this edge.
        /// Each SwitchRequirement contains switch name and required position.
        /// Multiple switches can be required for a single edge.
        /// </summary>
        public List<SwitchRequirement> SwitchRequirements { get; set; } = new List<SwitchRequirement>();

        public double Length { get; set; }
    }
}
