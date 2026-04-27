using System.Collections.Generic;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Common
{
    /// <summary>
    /// Represents a pre-calculated route with static path and switch configurations
    /// This cache stores only the static track geometry (path and switches) - NOT dynamic state like locks or occupancy
    /// </summary>
    public class PreCalculatedRoute
    {
        /// <summary>
        /// The static path of section IDs from source to destination
        /// </summary>
        public List<int> RoutePath { get; set; }

        /// <summary>
        /// The RoutePlan object with edge information including switch constraints
        /// </summary>
        public RoutePlan RoutePlan { get; set; }

        /// <summary>
        /// Required switch configurations for this route (pre-calculated from path)
        /// </summary>
        public List<SwitchConfiguration> RequiredSwitches { get; set; }

        /// <summary>
        /// Whether this route was successfully found during pre-calculation
        /// </summary>
        public bool RouteExists { get; set; }

        /// <summary>
        /// Human-readable route description for debugging
        /// </summary>
        public string RouteDescription { get; set; }

        public PreCalculatedRoute()
        {
            RoutePath = new List<int>();
            RequiredSwitches = new List<SwitchConfiguration>();
            RouteExists = false;
            RouteDescription = string.Empty;
        }

        /// <summary>
        /// Creates a successful pre-calculated route
        /// </summary>
        public static PreCalculatedRoute CreateSuccessful(List<int> path, RoutePlan routePlan, List<SwitchConfiguration> switches, string description)
        {
            return new PreCalculatedRoute
            {
                RoutePath = path,
                RoutePlan = routePlan,
                RequiredSwitches = switches,
                RouteExists = true,
                RouteDescription = description
            };
        }

        /// <summary>
        /// Creates a failed pre-calculated route (no path exists)
        /// </summary>
        public static PreCalculatedRoute CreateFailed(string description)
        {
            return new PreCalculatedRoute
            {
                RouteExists = false,
                RouteDescription = description
            };
        }
    }
}
