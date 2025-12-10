using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClaudeSepareted.Domain;
using Microsoft.EntityFrameworkCore;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Integration service that demonstrates how to use the RailwayPathfinderService
    /// with the existing train management system
    /// </summary>
    public class TrainPathfinderIntegration
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly RailwayPathfinderService _pathfinder;
        private readonly ITrackGraphFactory _trackGraphFactory;
        private readonly StatusNotificationService _statusService;

        public TrainPathfinderIntegration(
            ApplicationDbContext dbContext,
            RailwayPathfinderService pathfinder,
            ITrackGraphFactory trackGraphFactory,
            StatusNotificationService statusService)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
            _pathfinder = pathfinder ?? throw new ArgumentNullException(nameof(pathfinder));
            _trackGraphFactory = trackGraphFactory ?? throw new ArgumentNullException(nameof(trackGraphFactory));
            _statusService = statusService ?? throw new ArgumentNullException(nameof(statusService));
        }

        /// <summary>
        /// Demonstrates complete train movement planning using pathfinding
        /// This shows how to integrate the pathfinder into train operations
        /// </summary>
        public async Task<bool> PlanTrainMovementAsync(int trainDbId)
        {
            try
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Planning movement for train {trainDbId}");

                // 1. Load train with all necessary data
                var train = await LoadTrainWithPathfindingDataAsync(trainDbId);
                if (train == null)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Train {trainDbId} not found");
                    return false;
                }

                // 2. Load all other trains for conflict detection
                var otherTrains = await LoadAllOtherTrainsAsync(trainDbId);

                // 3. Initialize pathfinder (if not already initialized)
                await _pathfinder.InitializeAsync();

                // 4. Calculate path from current position to destination
                var path = await _pathfinder.CalculatePathAsync(train, otherTrains);
                if (path == null)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] No path available for train {train.Name}");
                    _statusService?.ShowError($"No path available for train {train.Name}");
                    return false;
                }

                Console.WriteLine($"[TrainPathfinderIntegration] Path found for {train.Name}: {string.Join(" -> ", path)}");

                // 5. Optimize path (optional)
                var trackGraph = await _trackGraphFactory.CreateTrackGraphAsync();
                var optimizedPath = PathOptimizer.OptimizePath(path, trackGraph);
                Console.WriteLine($"[TrainPathfinderIntegration] Optimized path: {string.Join(" -> ", optimizedPath)}");

                // 6. Validate path viability
                bool isPathViable = await _pathfinder.IsPathViableAsync(optimizedPath, train, otherTrains);
                if (!isPathViable)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Path is no longer viable for train {train.Name}");
                    return false;
                }

                // 7. Get required switch configurations
                var requiredSwitches = await _pathfinder.GetRequiredSwitchesAsync(optimizedPath, train.Direction);
                if (requiredSwitches.Any())
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Required switches: {string.Join(", ", requiredSwitches.Select(s => $"{s.SwitchName}={s.RequiredPosition}"))}");

                    // Here you would configure the switches via MQTT
                    // This would be integrated with your existing MQTT services
                    foreach (var (switchName, position) in requiredSwitches)
                    {
                        Console.WriteLine($"[TrainPathfinderIntegration] Setting switch {switchName} to {position}");
                        // await SetSwitchStateAsync(switchName, position);
                    }
                }

                // 8. Lock the required sections for the train
                train.LockedSections = optimizedPath;
                Console.WriteLine($"[TrainPathfinderIntegration] Locked sections for train {train.Name}: {string.Join(", ", optimizedPath)}");

                // 9. Update train state to moving
                train.State = TrainState.Moving;

                // 10. Save the changes to database
                await UpdateTrainAsync(train);

                Console.WriteLine($"[TrainPathfinderIntegration] Successfully planned movement for train {train.Name}");
                _statusService?.ShowSuccess($"Path planned for train {train.Name}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error planning train movement: {ex.Message}");
                _statusService?.ShowError($"Path planning failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Find available platform for a train at destination station
        /// </summary>
        public async Task<int?> FindAvailablePlatformForTrainAsync(int trainDbId, int destinationStationId)
        {
            try
            {
                // Load all trains to see which platforms are occupied
                var allTrains = await LoadAllTrainsAsync();
                await _pathfinder.InitializeAsync();

                var platformId = await _pathfinder.FindAvailablePlatformAsync(destinationStationId, allTrains);
                if (platformId.HasValue)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Found available platform {platformId.Value} for train {trainDbId} at station {destinationStationId}");
                }
                else
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] No available platforms for train {trainDbId} at station {destinationStationId}");
                }

                return platformId;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error finding available platform: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get departure sections for a platform
        /// </summary>
        public async Task<List<int>> GetPlatformDepartureSectionsAsync(int platformDbId, bool direction)
        {
            try
            {
                await _pathfinder.InitializeAsync();
                var departureSections = await _pathfinder.GetPlatformDepartureSectionsAsync(platformDbId, direction);
                Console.WriteLine($"[TrainPathfinderIntegration] Platform {platformDbId} departure sections: {string.Join(", ", departureSections)}");
                return departureSections;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error getting platform departure sections: {ex.Message}");
                return new List<int>();
            }
        }

        /// <summary>
        /// Loads a train with all necessary pathfinding data
        /// </summary>
        private async Task<Train> LoadTrainWithPathfindingDataAsync(int trainDbId)
        {
            try
            {
                var train = await _dbContext.Trains
                    .Include(t => t.SubSection)
                    .Include(t => t.Source)
                        .ThenInclude(p => p.Station)
                    .Include(t => t.Destination)
                        .ThenInclude(p => p.Station)
                    .Include(t => t.Destination)
                        .ThenInclude(p => p.SubSection)
                    .FirstOrDefaultAsync(t => t.DB_ID == trainDbId);

                if (train != null)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Loaded train {train.Name}: Current={train.SubSection?.DB_ID}, Destination={train.Destination?.SubSection_DB_ID}, Direction={train.Direction}");
                }

                return train;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error loading train: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads all other trains for conflict detection
        /// </summary>
        private async Task<List<Train>> LoadAllOtherTrainsAsync(int excludeTrainDbId)
        {
            try
            {
                var otherTrains = await _dbContext.Trains
                    .Include(t => t.SubSection)
                    .Include(t => t.Destination)
                    .Where(t => t.DB_ID != excludeTrainDbId && t.IsActive)
                    .ToListAsync();

                Console.WriteLine($"[TrainPathfinderIntegration] Loaded {otherTrains.Count} other trains for conflict detection");
                return otherTrains;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error loading other trains: {ex.Message}");
                return new List<Train>();
            }
        }

        /// <summary>
        /// Loads all trains for platform availability checking
        /// </summary>
        private async Task<List<Train>> LoadAllTrainsAsync()
        {
            try
            {
                var allTrains = await _dbContext.Trains
                    .Include(t => t.SubSection)
                    .Include(t => t.Destination)
                    .Where(t => t.IsActive)
                    .ToListAsync();

                Console.WriteLine($"[TrainPathfinderIntegration] Loaded {allTrains.Count} trains");
                return allTrains;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error loading all trains: {ex.Message}");
                return new List<Train>();
            }
        }

        /// <summary>
        /// Updates train information in database
        /// </summary>
        private async Task UpdateTrainAsync(Train train)
        {
            try
            {
                _dbContext.Trains.Update(train);
                await _dbContext.SaveChangesAsync();
                Console.WriteLine($"[TrainPathfinderIntegration] Updated train {train.Name} in database");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error updating train: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Example method showing how to handle train arrival at platform
        /// </summary>
        public async Task<bool> HandleTrainArrivalAsync(int trainDbId, int platformDbId)
        {
            try
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Handling arrival of train {trainDbId} at platform {platformDbId}");

                var train = await LoadTrainWithPathfindingDataAsync(trainDbId);
                if (train == null)
                {
                    return false;
                }

                // Update train state
                train.State = TrainState.Waiting;
                train.Platform_DB_ID = platformDbId;

                // Clear locked sections since train has arrived
                train.LockedSections.Clear();

                await UpdateTrainAsync(train);

                Console.WriteLine($"[TrainPathfinderIntegration] Train {train.Name} arrived at platform {platformDbId}");
                _statusService?.ShowSuccess($"Train {train.Name} arrived at platform");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error handling train arrival: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Example method showing how to handle train departure from platform
        /// </summary>
        public async Task<bool> HandleTrainDepartureAsync(int trainDbId, int destinationPlatformDbId)
        {
            try
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Handling departure of train {trainDbId} to platform {destinationPlatformDbId}");

                // Load train and set destination
                var train = await LoadTrainWithPathfindingDataAsync(trainDbId);
                if (train == null)
                {
                    return false;
                }

                // Load destination platform
                var destinationPlatform = await _dbContext.Platforms
                    .Include(p => p.Station)
                    .Include(p => p.SubSection)
                    .FirstOrDefaultAsync(p => p.DB_ID == destinationPlatformDbId);

                if (destinationPlatform?.SubSection == null)
                {
                    Console.WriteLine($"[TrainPathfinderIntegration] Destination platform {destinationPlatformDbId} not found or has no subsection");
                    return false;
                }

                // Set destination
                train.Destination = destinationPlatform;

                // Plan the movement
                return await PlanTrainMovementAsync(trainDbId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TrainPathfinderIntegration] Error handling train departure: {ex.Message}");
                return false;
            }
        }
    }
}