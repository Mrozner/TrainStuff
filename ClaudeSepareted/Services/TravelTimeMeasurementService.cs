using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClaudeSepareted.Services
{
    public class TravelTimeMeasurementService
    {
        private readonly string _filePath;
        private ConcurrentDictionary<string, TimeSpan> _travelTimes;
        private readonly FileLoggingService? _fileLogger;

        public TravelTimeMeasurementService(FileLoggingService fileLogger = null)
        {
            _fileLogger = fileLogger;
            _filePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TrainControllerLogs", "travel_times.json");
            _travelTimes = new ConcurrentDictionary<string, TimeSpan>();
            LoadData();
        }

        private void LoadData()
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var records = JsonSerializer.Deserialize<List<TravelTimeRecord>>(json);
                    if (records != null)
                    {
                        foreach (var r in records)
                        {
                            var key = GenerateKey(r.TrainId, r.SourcePlatformId, r.DestinationPlatformId);
                            _travelTimes[key] = TimeSpan.FromTicks(r.DurationTicks);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _fileLogger?.Log($"[TRAVEL TIME] Error loading data: {ex.Message}");
                }
            }
        }

        public TimeSpan? GetEstimatedTravelTime(int trainId, int sourceId, int destId)
        {
            var key = GenerateKey(trainId, sourceId, destId);
            if (_travelTimes.TryGetValue(key, out var time))
            {
                return time;
            }
            return null; // Return null so the caller can use a fallback (e.g. 2 hours)
        }

        public async Task RecordTravelTimeAsync(int trainId, int sourceId, int destId, TimeSpan duration)
        {
            var key = GenerateKey(trainId, sourceId, destId);

            // Requirement: If there is already a value in place, do not save/overwrite it.
            if (_travelTimes.ContainsKey(key))
                return;

            _travelTimes[key] = duration;
            _fileLogger?.Log($"[TRAVEL TIME] Recorded new route time: Train {trainId} from {sourceId} to {destId} took {duration.TotalMinutes:F1} virtual minutes.");

            await SaveDataAsync();
        }

        private async Task SaveDataAsync()
        {
            try
            {
                var records = _travelTimes.Select(kvp =>
                {
                    var parts = kvp.Key.Split('_');
                    return new TravelTimeRecord
                    {
                        TrainId = int.Parse(parts[0]),
                        SourcePlatformId = int.Parse(parts[1]),
                        DestinationPlatformId = int.Parse(parts[2]),
                        DurationTicks = kvp.Value.Ticks
                    };
                }).ToList();

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(records, options);
                await File.WriteAllTextAsync(_filePath, json);
            }
            catch (Exception ex)
            {
                _fileLogger?.Log($"[TRAVEL TIME] Error saving data: {ex.Message}");
            }
        }

        private string GenerateKey(int trainId, int sourceId, int destId) => $"{trainId}_{sourceId}_{destId}";
    }

    public class TravelTimeRecord
    {
        public int TrainId { get; set; }
        public int SourcePlatformId { get; set; }
        public int DestinationPlatformId { get; set; }
        public long DurationTicks { get; set; }
    }
}
