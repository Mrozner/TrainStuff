using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ClaudeSepareted; // EZ HIÁNYZOTT: Itt található a StatusNotificationService
using ClaudeSepareted.DataAccess;
using ClaudeSepareted.Domain;
using ClaudeSepareted.Lights;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Automatic Train Protection (ATP) service.
    /// Monitors Hall sensor MQTT messages and immediately stops trains that enter
    /// sections with ZERO (STOP) speed limits, preventing red-light violations.
    ///
    /// This is a safety-critical service that operates independently of the main
    /// train control system to provide an additional layer of protection.
    /// </summary>
    public class HallStopTest : IDisposable
    {
        #region Dependencies

        private readonly MqttInfrastructureService _mqttService;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly RocrailCommandService _rocrailCommandService;
        private readonly TrackOccupancyService _trackOccupancyService;
        private readonly StatusNotificationService _statusService;
        private readonly ILogger<HallStopTest> _logger;
        private readonly FileLoggingService? _fileLogger;

        #endregion

        #region State Management

        private bool _isInitialized = false;
        private readonly SemaphoreSlim _initializationSemaphore = new SemaphoreSlim(1, 1);
        private volatile bool _disposed = false;

        // JSON serializer options to properly map camelCase JSON to PascalCase C# properties
        private readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        #endregion

        #region Hall Sensor Mapping

        /// <summary>
        /// Maps boardId and hardware sensor ID to Hall sensor names.
        /// Key: "boardId_sensorId" (e.g., "10_1" for board 10, Arduino ID 1)
        /// Value: Hall sensor name (e.g., "HALL_01")
        /// </summary>
        private readonly Dictionary<string, string> _hallSensorMap = new Dictionary<string, string>
        {
            // Board 10 mappings (update with your actual sensor configuration)
            { "10_1", "HALL_01" },
            { "10_2", "HALL_02" },
            { "10_3", "HALL_03" },
            // Add more sensors as needed
        };

        #endregion

        #region Constructor

        public HallStopTest(
            MqttInfrastructureService mqttService,
            IDbContextFactory<ApplicationDbContext> dbContextFactory,
            RocrailCommandService rocrailCommandService,
            TrackOccupancyService trackOccupancyService,
            StatusNotificationService statusService,
            ILogger<HallStopTest> logger,
            FileLoggingService fileLogger = null)
        {
            _mqttService = mqttService ?? throw new ArgumentNullException(nameof(mqttService));
            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
            _rocrailCommandService = rocrailCommandService ?? throw new ArgumentNullException(nameof(rocrailCommandService));
            _trackOccupancyService = trackOccupancyService ?? throw new ArgumentNullException(nameof(trackOccupancyService));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _fileLogger = fileLogger;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initialize the ATP service and subscribe to Hall sensor MQTT messages.
        /// Thread-safe - can be called multiple times without issues.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_isInitialized)
                return;

            await _initializationSemaphore.WaitAsync();
            try
            {
                if (_isInitialized)
                    return;

                if (!await _mqttService.InitializeAsync())
                {
                    _logger.LogError("Failed to initialize HallStopTest ATP service: MQTT service unavailable - [HallStopTest]");
                    return;
                }

                _mqttService.Subscribe("track/sensor/hall", ProcessHallSensorMessageAsync);

                _isInitialized = true;
                _logger.LogInformation("HallStopTest ATP service initialized successfully - monitoring for red-light violations");
                _fileLogger?.Log("[ATP] HallStopTest service initialized - automatic train protection active");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error initializing HallStopTest ATP service - [HallStopTest]");
            }
            finally
            {
                _initializationSemaphore.Release();
            }
        }

        #endregion

        #region MQTT Message Processing

        /// <summary>
        /// Process incoming Hall sensor messages and check for red-light violations.
        /// </summary>
        private async Task ProcessHallSensorMessageAsync(string sensorMessage)
        {
            if (string.IsNullOrWhiteSpace(sensorMessage))
                return;

            try
            {
                var sensorData = JsonSerializer.Deserialize<HallSensorMessage>(sensorMessage, _jsonOptions);
                if (sensorData == null || !sensorData.TrainDetected)
                {
                    return;
                }

                if (sensorData.ActiveSensorIds == null || sensorData.ActiveSensorIds.Length == 0)
                {
                    _logger?.LogDebug("[ATP] Received Hall sensor message with train detected but no active sensor IDs");
                    return;
                }

                foreach (int sensorId in sensorData.ActiveSensorIds)
                {
                    await HandleTriggeredSensorAsync(sensorData.BoardId, sensorId);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse Hall sensor JSON message - [HallStopTest]");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing Hall sensor message - [HallStopTest]");
            }
        }

        /// <summary>
        /// Handle a triggered Hall sensor and check if the train ran a red light.
        /// </summary>
        private async Task HandleTriggeredSensorAsync(int boardId, int sensorId)
        {
            try
            {
                // 1. Map boardId + sensorId to Hall sensor name
                string sensorKey = $"{boardId}_{sensorId}";
                if (!_hallSensorMap.TryGetValue(sensorKey, out string hallSensorName))
                {
                    _logger?.LogDebug("[ATP] Unmapped Hall sensor: Board {BoardId}, Sensor ID {SensorId}", boardId, sensorId);
                    return;
                }

                // LOGDEBUG HELYETT LOGINFORMATION (Hogy a konzolon is mindig látszódjon)
                _logger?.LogInformation("[ATP] Hall sensor triggered: {HallSensorName} (Board {BoardId}, Sensor ID {SensorId})",
                    hallSensorName, boardId, sensorId);

                // 2. Get section information from ObjectsLibrary
                var (sectionName, direction, _) = ObjectsLibrary.GetSection(hallSensorName);
                if (string.IsNullOrEmpty(sectionName))
                {
                    _logger?.LogWarning("[ATP] Hall sensor {HallSensorName} has no section mapping", hallSensorName);
                    return;
                }

                // ÚJ SOR: Értesítés küldése a Status Monitor UI-ra minden áthaladáskor!
                _statusService?.ShowInfo($"Szenzor áthaladás: {hallSensorName} a(z) {sectionName} szakaszon.");

                // 3. Query database to check AllowedSpeed for this section
                Speed allowedSpeed = await GetAllowedSpeedForSectionAsync(sectionName);
                if (allowedSpeed != Speed.ZERO)
                {
                    _logger?.LogInformation("[ATP] Section {SectionName} allows speed {Speed} - no violation", sectionName, allowedSpeed);
                    return;
                }

                // 4. RED LIGHT DETECTED!
                _logger?.LogWarning("[ATP] 🚨 RED LIGHT VIOLATION DETECTED 🚨 - Train entered section {SectionName} with ZERO speed limit!", sectionName);
                _fileLogger?.Log($"[ATP] RED LIGHT VIOLATION: Train detected in section {sectionName} (speed limit: ZERO)");

                // 5. Identify train
                string trainName = _trackOccupancyService.GetTrainInSection(sectionName);
                if (string.IsNullOrEmpty(trainName))
                {
                    _logger?.LogError("[ATP] ⚠️ RED LIGHT VIOLATION but NO TRAIN FOUND in section {SectionName}!", sectionName);
                    _fileLogger?.Log($"[ATP] WARNING: Red light violation in {sectionName} but could not identify the train!");

                    _statusService?.ShowError($"🚨 ISMERETLEN VONAT VÉSZFÉK: Jogosulatlan belépés a(z) {sectionName} szakaszra!");
                    return;
                }

                // 6. EMERGENCY STOP!
                _logger?.LogCritical("[ATP] 🛑 EMERGENCY STOP triggered for train {TrainName} - Red light violation in section {SectionName}", trainName, sectionName);
                _fileLogger?.Log($"[ATP] EMERGENCY STOP: Train {trainName} stopped for red light violation in section {sectionName}");

                bool stopped = await _rocrailCommandService.SendTrainSpeedCommandAsync(trainName, Speed.ZERO);
                if (stopped)
                {
                    _logger?.LogInformation("[ATP] ✅ Train {TrainName} successfully stopped - Red light protection activated", trainName);
                    _statusService?.ShowError($"🚨 VÉSZFÉK: A vonat meghaladta a vörös jelzést a(z) {sectionName} szakaszon!", trainName);
                }
                else
                {
                    _logger?.LogError("[ATP] ❌ FAILED to stop train {TrainName} - Communication error!", trainName);
                    _statusService?.ShowError($"⚠️ HIBA: Sikertelen vészfékezés a(z) {sectionName} szakaszon!", trainName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling triggered Hall sensor - [HallStopTest]");
            }
        }

        /// <summary>
        /// Query the database to get the AllowedSpeed for a specific section.
        /// </summary>
        /// <param name="sectionName">The section name to query (e.g., "P24.2")</param>
        /// <returns>The allowed speed for the section (defaults to ZERO if not found)</returns>
        private async Task<Speed> GetAllowedSpeedForSectionAsync(string sectionName)
        {
            try
            {
                using var dbContext = await _dbContextFactory.CreateDbContextAsync();

                var subSection = await dbContext.SubSections
                    .AsNoTracking()
                    .FirstOrDefaultAsync(ss => ss.Name == sectionName);

                if (subSection == null)
                {
                    _logger?.LogWarning("[ATP] No subsection found with name {SectionName}", sectionName);
                    return Speed.ZERO; // Default to safest option
                }

                return subSection.AllowedSpeed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error querying AllowedSpeed for section {SectionName} - [HallStopTest]", sectionName);
                return Speed.ZERO; // Default to safest option on error
            }
        }

        #endregion

        #region DTOs

        private class HallSensorMessage
        {
            public int BoardId { get; set; }
            public bool TrainDetected { get; set; }
            public int[] SensorStates { get; set; }
            public int[] ActiveSensorIds { get; set; }
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _initializationSemaphore.Dispose();

            _logger?.LogInformation("[ATP] HallStopTest service disposed");
        }

        #endregion
    }
}