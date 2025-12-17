using System.Threading.Tasks;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Factory interface for creating TrackGraph instances
    /// </summary>
    public interface ITrackGraphFactory
    {
        /// <summary>
        /// Creates a TrackGraph instance asynchronously
        /// </summary>
        Task<TrackGraph> CreateTrackGraphAsync();
    }

    /// <summary>
    /// Implementation of TrackGraph factory
    /// </summary>
    public class TrackGraphFactory : ITrackGraphFactory
    {
        private readonly ApplicationDbContext _dbContext;
        private TrackGraph _cachedTrackGraph;

        public TrackGraphFactory(ApplicationDbContext dbContext)
        {
            _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        }

        public async Task<TrackGraph> CreateTrackGraphAsync()
        {
            if (_cachedTrackGraph == null)
            {
                _cachedTrackGraph = await TrackGraph.LoadFromDatabaseAsync(_dbContext);
            }
            return _cachedTrackGraph;
        }
    }
}