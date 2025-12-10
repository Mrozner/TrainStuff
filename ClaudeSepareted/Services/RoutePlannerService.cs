using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;
using MQTTnet;
using System.Text;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Advanced route planner service with BFS pathfinding and MQTT switch control
    /// Calculates optimal routes and configures switches before train movement
    /// </summary>
    public class RoutePlannerService
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly MQTTConfiguration _mqttConfig;
        private readonly StatusNotificationService _statusService;
        private readonly AdminMQTTService _adminMQTTService;
        private readonly DirectPlatformPathfinder _platformPathfinder;
        private bool _isInitialized = false;
        private readonly object _lockObject = new object();
        private readonly Dictionary<string, string> _lastSwitchStates = new Dictionary<string, string>();

        public RoutePlannerService(
            ApplicationDbContext dbContext,
            MQTTConfiguration mqttConfig,
            StatusNotificationService statusService,
            AdminMQTTService adminMQTTService)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _mqttConfig = mqttConfig ?? throw new ArgumentNullException(nameof(mqttConfig));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _adminMQTTService = adminMQTTService ?? throw new ArgumentNullException(nameof(adminMQTTService));
            _platformPathfinder = new DirectPlatformPathfinder(_dbContext);
        }

        /// <summary>
        /// Initializes the route planner service
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                _statusService?.ShowInfo("Route Planner initialization...");
                Console.WriteLine("[RoutePlanner] Initializing route planner service...");

                // DirectPlatformPathfinder doesn't need complex initialization
                // It queries the database directly when needed

                _isInitialized = true;
                Console.WriteLine("[RoutePlanner] Route planner service initialized successfully");
                _statusService?.ShowSuccess("Route Planner ready");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RoutePlanner] Initialization error: {ex.Message}");
                _statusService?.ShowError($"Route Planner init failed: {ex.Message}");
                return false;
            }
        }

  
        /// <summary>
        /// Plans route between specific platforms and configures switches
        /// </summary>
        public async Task<RoutePlanResult> PlanAndConfigureRouteAsync(
            Platforms sourcePlatform,
            Platforms destinationPlatform,
            string trainName = "Unknown")
        {
            if (!_isInitialized)
            {
                var initResult = await InitializeAsync();
                if (!initResult)
                {
                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = "Failed to initialize route planner"
                    };
                }
            }

            try
            {
                var sourceStation = sourcePlatform.Station?.Name ?? "Unknown";
                var destStation = destinationPlatform.Station?.Name ?? "Unknown";

                // Format platform names - if platform name already contains station name, don't duplicate
                var sourcePlatformName = FormatPlatformName(sourcePlatform.Name, sourceStation);
                var destPlatformName = FormatPlatformName(destinationPlatform.Name, destStation);

                Console.WriteLine($"[RoutePlanner] Platform route planning for {trainName}: {sourcePlatformName} -> {destPlatformName}");
                _statusService?.ShowInfo($"Platform route: {sourcePlatformName} → {destPlatformName}", trainName);

                // Find route using DirectPlatformPathfinder
                var routeSections = await _platformPathfinder.FindDirectRouteAsync(sourcePlatform, destinationPlatform);
                if (routeSections == null || !routeSections.Any())
                {
                    var errorMsg = $"No platform route found from {sourcePlatformName} to {destPlatformName}";
                    Console.WriteLine($"[RoutePlanner] {errorMsg}");
                    _statusService?.ShowError(errorMsg, trainName);

                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = errorMsg
                    };
                }

                // Convert section list to RoutePlan
                var edgePath = new List<EdgeInfo>();
                for (int i = 0; i < routeSections.Count - 1; i++)
                {
                    edgePath.Add(new EdgeInfo
                    {
                        SourceNodeId = routeSections[i],
                        TargetNodeId = routeSections[i + 1],
                        Length = 1.0
                    });
                }

                var routePlan = new RoutePlan
                {
                    Path = edgePath,
                    TotalLength = edgePath.Sum(e => e.Length)
                };

                Console.WriteLine($"[RoutePlanner] Platform route found with {edgePath.Count} segments: {string.Join(" -> ", routeSections)}");
                _statusService?.ShowInfo($"Platform route found: {edgePath.Count} segments", trainName);

                // Configure switches before train starts moving
                var switchConfigResult = await ConfigureSwitchesForRouteAsync(routePlan, trainName);
                if (!switchConfigResult.Success)
                {
                    return new RoutePlanResult
                    {
                        Success = false,
                        ErrorMessage = $"Switch configuration failed: {switchConfigResult.ErrorMessage}",
                        RoutePlan = routePlan
                    };
                }

                Console.WriteLine($"[RoutePlanner] Platform route planning and switch configuration completed for {trainName}");
                _statusService?.ShowSuccess($"Platform route configured", trainName);

                return new RoutePlanResult
                {
                    Success = true,
                    RoutePlan = routePlan,
                    ConfiguredSwitches = switchConfigResult.ConfiguredSwitches
                };
            }
            catch (Exception ex)
            {
                var errorMsg = $"Platform route planning error: {ex.Message}";
                Console.WriteLine($"[RoutePlanner] {errorMsg}");
                _statusService?.ShowError(errorMsg, trainName);
                return new RoutePlanResult
                {
                    Success = false,
                    ErrorMessage = errorMsg
                };
            }
        }

        /// <summary>
        /// Configures all switches required for a route via MQTT
        /// </summary>
        private async Task<SwitchConfigurationResult> ConfigureSwitchesForRouteAsync(RoutePlan routePlan, string trainName)
        {
            try
            {
                var switchConfigurations = routePlan.GetSwitchConfigurations();
                if (!switchConfigurations.Any())
                {
                    Console.WriteLine("[RoutePlanner] No switches to configure for this route");
                    return new SwitchConfigurationResult
                    {
                        Success = true,
                        ConfiguredSwitches = new List<SwitchConfiguration>()
                    };
                }

                Console.WriteLine($"[RoutePlanner] Configuring {switchConfigurations.Count} switches for {trainName}");
                _statusService?.ShowInfo($"Configuring {switchConfigurations.Count} switches", trainName);

                var configuredSwitches = new List<SwitchConfiguration>();
                var errors = new List<string>();

                foreach (var switchConfig in switchConfigurations)
                {
                    try
                    {
                        // Check if switch is already in desired position
                        if (IsSwitchAlreadyConfigured(switchConfig.SwitchName, switchConfig.Position))
                        {
                            Console.WriteLine($"[RoutePlanner] Switch {switchConfig.SwitchName} already in {switchConfig.Position} position");
                            configuredSwitches.Add(switchConfig);
                            continue;
                        }

                        // Send switch command via MQTT
                        var success = await SendSwitchCommandAsync(switchConfig.SwitchName, switchConfig.Position, trainName);
                        if (success)
                        {
                            configuredSwitches.Add(switchConfig);
                            Console.WriteLine($"[RoutePlanner] ✓ Switch {switchConfig.SwitchName} set to {switchConfig.Position}");

                            // Add delay between switch commands for stability
                            await Task.Delay(500);
                        }
                        else
                        {
                            var errorMsg = $"Failed to configure switch {switchConfig.SwitchName} to {switchConfig.Position}";
                            errors.Add(errorMsg);
                            Console.WriteLine($"[RoutePlanner] ✗ {errorMsg}");
                        }
                    }
                    catch (Exception ex)
                    {
                        var errorMsg = $"Error configuring switch {switchConfig.SwitchName}: {ex.Message}";
                        errors.Add(errorMsg);
                        Console.WriteLine($"[RoutePlanner] ✗ {errorMsg}");
                    }
                }

                var result = new SwitchConfigurationResult
                {
                    Success = !errors.Any(),
                    ConfiguredSwitches = configuredSwitches,
                    Errors = errors
                };

                if (result.Success)
                {
                    Console.WriteLine($"[RoutePlanner] All {configuredSwitches.Count} switches configured successfully for {trainName}");
                    _statusService?.ShowSuccess($"Switches configured: {configuredSwitches.Count}", trainName);
                }
                else
                {
                    Console.WriteLine($"[RoutePlanner] Switch configuration completed with {errors.Count} errors for {trainName}");
                    _statusService?.ShowWarning($"Switch config: {configuredSwitches.Count}/{switchConfigurations.Count} successful", trainName);
                }

                return result;
            }
            catch (Exception ex)
            {
                var errorMsg = $"Switch configuration error: {ex.Message}";
                Console.WriteLine($"[RoutePlanner] {errorMsg}");
                return new SwitchConfigurationResult
                {
                    Success = false,
                    ErrorMessage = errorMsg
                };
            }
        }

        /// <summary>
        /// Sends switch command via AdminMQTTService
        /// </summary>
        private async Task<bool> SendSwitchCommandAsync(string switchName, string position, string trainName)
        {
            try
            {
                Console.WriteLine($"[RoutePlanner] Sending switch command via AdminMQTTService: {switchName} -> {position}");

                // Use AdminMQTTService which has working MQTT connection
                var success = await _adminMQTTService.SendSwitchCommandAsync(switchName, position);

                if (success)
                {
                    // Track switch state to avoid redundant commands
                    lock (_lockObject)
                    {
                        _lastSwitchStates[switchName] = position;
                    }

                    Console.WriteLine($"[RoutePlanner] Switch command sent successfully: {switchName} -> {position}");
                    _statusService?.ShowInfo($"Switch configured: {switchName} -> {position}", trainName);
                }
                else
                {
                    Console.WriteLine($"[RoutePlanner] Failed to send switch command: {switchName} -> {position}");
                    _statusService?.ShowError($"Switch config failed: {switchName}", trainName);
                }

                return success;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[RoutePlanner] Error sending switch command: {ex.Message}");
                _statusService?.ShowError($"Switch command error: {ex.Message}", trainName);
                return false;
            }
        }

        /// <summary>
        /// Checks if switch is already in the desired position
        /// </summary>
        private bool IsSwitchAlreadyConfigured(string switchName, string position)
        {
            lock (_lockObject)
            {
                return _lastSwitchStates.TryGetValue(switchName, out var lastPosition) &&
                       string.Equals(lastPosition, position, StringComparison.OrdinalIgnoreCase);
            }
        }

        
    
        /// <summary>
        /// Formats platform name properly - if platform name already contains station name, don't duplicate
        /// Examples: "Also A" -> "Also A" (no change), "Platform 1" with station "Also" -> "Also-Platform 1"
        /// </summary>
        private string FormatPlatformName(string platformName, string stationName)
        {
            if (string.IsNullOrEmpty(platformName))
                return stationName ?? "Unknown";

            if (string.IsNullOrEmpty(stationName))
                return platformName;

            // Normalize both names for comparison
            var normalizedPlatform = platformName.ToLower().Trim();
            var normalizedStation = stationName.ToLower().Trim();

            // Check if platform name already starts with station name (case-insensitive)
            // This handles cases like "Also A", "Felso I.", etc.
            if (normalizedPlatform.StartsWith(normalizedStation) ||
                normalizedPlatform.Contains(normalizedStation))
            {
                return platformName; // Platform name already contains station name
            }

            // Check for common patterns like "A", "B", "I", "II", "III" that are platform designations
            var commonPlatformSuffixes = new[] { "a", "b", "c", "i", "ii", "iii", "iv", "v", "vi" };
            var lastWord = normalizedPlatform.Split(' ').LastOrDefault();

            if (lastWord != null && commonPlatformSuffixes.Contains(lastWord))
            {
                // Platform name looks like a designation, combine with station name
                return $"{stationName} {platformName}";
            }

            // Platform name doesn't contain station name, combine them
            return $"{stationName}-{platformName}";
        }
    }

    /// <summary>
    /// Result of route planning operation
    /// </summary>
    public class RoutePlanResult
    {
        public bool Success { get; set; }
        public RoutePlan RoutePlan { get; set; }
        public List<SwitchConfiguration> ConfiguredSwitches { get; set; } = new List<SwitchConfiguration>();
        public string ErrorMessage { get; set; }
        public List<string> Errors { get; set; } = new List<string>();
    }

    /// <summary>
    /// Result of switch configuration operation
    /// </summary>
    public class SwitchConfigurationResult
    {
        public bool Success { get; set; }
        public List<SwitchConfiguration> ConfiguredSwitches { get; set; } = new List<SwitchConfiguration>();
        public List<string> Errors { get; set; } = new List<string>();
        public string ErrorMessage { get; set; }
    }
}