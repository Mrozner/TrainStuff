using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClaudeSepareted.Domain;
using Microsoft.Extensions.Logging;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Service for handling railway switch configuration and management
    /// Manages switch states, reservations, and MQTT commands
    /// </summary>
    public class SwitchConfigurationService
    {
        private readonly RocrailCommandService _rocrailCommandService;
        private readonly StatusNotificationService _statusService;
        private readonly ILogger<SwitchConfigurationService> _logger;
        private readonly FileLoggingService? _fileLogger;

        // Switch state tracking
        private readonly Dictionary<string, string> _lastSwitchStates = new Dictionary<string, string>();
        private readonly object _lockObject = new object();

        public SwitchConfigurationService(
            RocrailCommandService rocrailCommandService,
            StatusNotificationService statusService,
            ILogger<SwitchConfigurationService> logger = null,
            FileLoggingService fileLogger = null)
        {
            _rocrailCommandService = rocrailCommandService ?? throw new ArgumentNullException(nameof(rocrailCommandService));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _logger = logger; // Optional dependency
            _fileLogger = fileLogger;
        }

        /// <summary>
        /// Configures switches for a SINGLE block using just-in-time approach.
        ///
        /// This method implements N-block look-ahead by only configuring the switches
        /// needed for the immediate next section, not the entire route.
        ///
        /// Algorithm:
        /// 1. Find the edge in the route plan for the next section ID
        /// 2. Extract switch requirements from the edge
        /// 3. Send MQTT commands to configure those switches
        /// 4. Return success/failure status
        ///
        /// This replaces the global route locking approach with dynamic, real-time
        /// switch configuration as the train progresses.
        /// </summary>
        /// <param name="nextSectionId">The section ID the train is about to enter</param>
        /// <param name="nextSectionName">The section name for logging purposes</param>
        /// <param name="routePlan">The full route plan for the journey</param>
        /// <param name="trainName">The train requesting switch configuration</param>
        /// <returns>True if all switches configured successfully, false otherwise</returns>
        public async Task<bool> ConfigureSwitchesForBlockAsync(int currentSectionId, int nextSectionId, string nextSectionName, RoutePlan routePlan, string trainName)
        {
            if (nextSectionId <= 0)
            {
                _logger?.LogWarning("ConfigureSwitchesForBlockAsync called with invalid section ID {SectionId} for train {TrainName}", nextSectionId, trainName);
                return false;
            }

            if (currentSectionId <= 0)
            {
                _logger?.LogWarning("ConfigureSwitchesForBlockAsync called with invalid current section ID {SectionId} for train {TrainName}", currentSectionId, trainName);
                return false;
            }

            if (routePlan == null || !routePlan.HasPath)
            {
                _logger?.LogWarning("ConfigureSwitchesForBlockAsync called with invalid route plan for train {TrainName}", trainName);
                return false;
            }

            try
            {
                // Step 1: Find the edge in the route plan that leads from current section to next section
                // PHASE 3.2: Fixed directional routing flaw
                // Changed from blanket TargetNodeId check to strict (SourceNodeId + TargetNodeId) matching
                // This ensures we target the exact chronological edge the train is currently on
                // Prevents wrong switch configuration when a node appears multiple times in bidirectional routes
                var edgeForNextSection = routePlan.Path
                    .FirstOrDefault(e => e.SourceNodeId == currentSectionId && e.TargetNodeId == nextSectionId);

                if (edgeForNextSection == null)
                {
                    _logger?.LogDebug("No edge found for section {Section} in route plan for train {TrainName}", nextSectionName, trainName);
                    // Not necessarily an error - the section might not require switch configuration
                    return true;
                }

                // Step 2: Extract switch requirements from the edge
                // PHASE 3.3: Multiple Switch Support - RESOLVED
                // EdgeInfo now supports multiple switches via SwitchRequirements list
                // This correctly handles edges with multiple switch requirements (e.g., "switch1=straight,switch2=turned")
                var requiredSwitches = new List<SwitchRequirement>();

                if (edgeForNextSection.SwitchRequirements != null && edgeForNextSection.SwitchRequirements.Any())
                {
                    requiredSwitches.AddRange(edgeForNextSection.SwitchRequirements);
                }

                if (!requiredSwitches.Any())
                {
                    _logger?.LogDebug("No switches required for section {Section} for train {TrainName}", nextSectionName, trainName);
                    return true;
                }

                // Step 3: Send MQTT commands for all switches concurrently with a limit of 3
                using var semaphore = new SemaphoreSlim(3);
                var switchTasks = requiredSwitches.Select(async (req) =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var success = await SendSwitchCommandAsync(req.SwitchName, req.RequiredPosition, trainName);
                        return (Success: success, SwitchName: req.SwitchName);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                // Execute all switch commands simultaneously
                var results = await Task.WhenAll(switchTasks);
                var successCount = results.Count(r => r.Success);

                var allSuccessful = successCount == requiredSwitches.Count;

                if (allSuccessful)
                {
                    _fileLogger?.Log($"[SWITCH CONFIG] JIT configuration successful: {successCount} switches set for {trainName ?? "train"} entering {nextSectionName}.");
                    _logger?.LogInformation(
                        "✅ SWITCHES CONFIGURED: {SwitchCount} switches configured for train {TrainName} entering {Section}",
                        successCount, trainName, nextSectionName);
                }
                else
                {
                    _fileLogger?.Log($"[SWITCH CONFIG] JIT configuration WARNING: Only {successCount}/{requiredSwitches.Count} switches set for {trainName ?? "train"} entering {nextSectionName}.");
                    _logger?.LogWarning(
                        "⚠️ PARTIAL SWITCH CONFIG: {SuccessCount}/{TotalCount} switches configured for train {TrainName} entering {Section}",
                        successCount, requiredSwitches.Count, trainName, nextSectionName);
                }

                return allSuccessful;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error configuring switches for train {TrainName} entering section {Section} - [SwitchConfigurationService]",
                    trainName, nextSectionName);
                return false;
            }
        }

        /// <summary>
        /// Send switch command via MQTT
        /// </summary>
        private async Task<bool> SendSwitchCommandAsync(string switchName, string position, string trainName)
        {
            try
            {
                var success = await _rocrailCommandService.SendSwitchCommandAsync(switchName, position);

                if (success)
                {
                    lock (_lockObject)
                    {
                        _lastSwitchStates[switchName] = position;
                    }

                    _statusService?.ShowInfo($"Switch configured: {switchName} -> {position}", trainName);
                }
                else
                {
                    _statusService?.ShowError($"Switch config failed: {switchName} - [SwitchConfigurationService]", trainName);
                }

                return success;
            }
            catch (Exception ex)
            {
                _statusService?.ShowError($"Switch command error: {ex.Message} - [SwitchConfigurationService]", trainName);
                return false;
            }
        }

        /// <summary>
        /// Helper method to execute switch command with error handling for parallel execution.
        /// PHASE 2.1: Added to remove Task.Run anti-pattern in ConfigureSwitchesForRouteAsync.
        /// </summary>
        private async Task<(bool Success, string SwitchName, string Position, Exception Error)> ExecuteSwitchCommandAsync(string switchName, string position, string trainName)
        {
            try
            {
                var success = await SendSwitchCommandAsync(switchName, position, trainName);
                return (success, switchName, position, null);
            }
            catch (Exception ex)
            {
                return (false, switchName, position, ex);
            }
        }

        /// <summary>
        /// Check if switch is already configured
        /// </summary>
        private bool IsSwitchAlreadyConfigured(string switchName, string position)
        {
            lock (_lockObject)
            {
                return _lastSwitchStates.TryGetValue(switchName, out var lastPosition) &&
                       string.Equals(lastPosition, position, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
