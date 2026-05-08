using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ClaudeSepareted.Domain;

namespace ClaudeSepareted.Services
{
    public class TrainLocationRegistry : ITrainLocationRegistry
    {
        // Maps Train_DB_ID -> (Platform_DB_ID, PhysicalBlockName)
        private readonly ConcurrentDictionary<int, (int PlatformId, string BlockName)> _parkedTrains = new();

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<TrainLocationRegistry> _logger;

        public TrainLocationRegistry(
            IServiceScopeFactory scopeFactory,
            ILogger<TrainLocationRegistry> logger,
            VirtualClock virtualClock)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;

            // Automatically wipe and resync the layout state at midnight
            virtualClock.MidnightReset += async (sender, args) =>
            {
                _logger.LogInformation("Midnight reset triggered. Resyncing TrainLocationRegistry...");
                await SyncWithDatabaseAsync();
            };
        }

        public void SetParkedLocation(int trainId, int platformId, string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return;

            _parkedTrains[trainId] = (platformId, blockName);
            _logger.LogDebug("Train {TrainId} locked to Platform {PlatformId} (Block: {BlockName})", trainId, platformId, blockName);
        }

        public void ClearParkedLocation(int trainId)
        {
            if (_parkedTrains.TryRemove(trainId, out var locationInfo))
            {
                _logger.LogDebug("Train {TrainId} removed from Platform {PlatformId} (now moving)", trainId, locationInfo.PlatformId);
            }
        }

        public bool IsBlockOccupiedByParkedTrain(string blockName)
        {
            if (string.IsNullOrWhiteSpace(blockName)) return false;

            return _parkedTrains.Values.Any(v => string.Equals(v.BlockName, blockName, StringComparison.OrdinalIgnoreCase));
        }

        public async Task SyncWithDatabaseAsync()
        {
            _logger.LogInformation("Starting physical reality sync from database...");
            _parkedTrains.Clear();

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                // 1. Fetch today's schedule, ordered chronologically, including SubSections
                var todayEntries = await dbContext.TimetableEntries
                    .Include(e => e.SourcePlatform).ThenInclude(p => p.SubSection)
                    .Include(e => e.DestinationPlatform).ThenInclude(p => p.SubSection)
                    .OrderBy(e => e.StartDate)
                    .ThenBy(e => e.StartTime)
                    .ToListAsync();

                if (!todayEntries.Any())
                {
                    _logger.LogWarning("No timetable entries found during sync. Layout assumed empty.");
                    return;
                }

                // 2. Group by train to evaluate their true current position
                var entriesByTrain = todayEntries.GroupBy(e => e.Train_DB_ID).ToList();

                // 3. Rebuild reality based on the most recent state
                foreach (var trainGroup in entriesByTrain)
                {
                    var trainId = trainGroup.Key;

                    // Condition A: Was the train moving when the server died?
                    var inProgress = trainGroup.FirstOrDefault(e => e.EntryState == EntryState.InProgress);
                    if (inProgress != null)
                    {
                        _logger.LogWarning("[SYNC ALERT] Train {TrainId} is currently InProgress. It is lost on the tracks.", trainId);
                        continue;
                    }

                    // Condition B & C: Find the last completed trip and the next scheduled trip
                    var lastArrived = trainGroup.LastOrDefault(e => e.EntryState == EntryState.Arrived);
                    var firstUpcoming = trainGroup.FirstOrDefault(e => e.EntryState == EntryState.Upcoming);

                    if (lastArrived != null)
                    {
                        // The train has completed at least one trip today. It sits at the destination of its last trip.
                        var blockName = lastArrived.DestinationPlatform?.SubSection?.Name;
                        if (!string.IsNullOrEmpty(blockName))
                        {
                            SetParkedLocation(trainId, lastArrived.DestinationPlatform_DB_ID, blockName);
                            _logger.LogInformation("[SYNC] Train {TrainId} initialized parked at Destination {BlockName}", trainId, blockName);
                        }
                    }
                    else if (firstUpcoming != null)
                    {
                        // The train hasn't started any trips today. It sits at the source of its first trip.
                        var blockName = firstUpcoming.SourcePlatform?.SubSection?.Name;
                        if (!string.IsNullOrEmpty(blockName))
                        {
                            SetParkedLocation(trainId, firstUpcoming.SourcePlatform_DB_ID, blockName);
                            _logger.LogInformation("[SYNC] Train {TrainId} initialized parked at Source {BlockName}", trainId, blockName);
                        }
                    }
                }

                _logger.LogInformation("Physical reality sync complete. {Count} trains securely parked.", _parkedTrains.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CRITICAL ERROR during TrainLocationRegistry sync. Train locations are unknown.");
            }
        }
    }
}
