using System.Threading.Tasks;

namespace ClaudeSepareted.Services
{
    public interface ITrainLocationRegistry
    {
        /// <summary>Registers or updates a train's current parked platform AND physical block name.</summary>
        void SetParkedLocation(int trainId, int platformId, string blockName);

        /// <summary>Removes a train from the registry (e.g., when it starts moving).</summary>
        void ClearParkedLocation(int trainId);

        /// <summary>Quick check to see if a specific physical track block (e.g., "PB2") has a parked train sitting on it.</summary>
        bool IsBlockOccupiedByParkedTrain(string blockName);

        /// <summary>Rebuilds the physical state of the layout by querying the Timetable.</summary>
        Task SyncWithDatabaseAsync();
    }
}
