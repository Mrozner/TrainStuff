using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

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

        /// <summary>
        /// Clears the currently cached track graph, forcing it to be rebuilt on the next request.
        /// Use this when the physical track layout or switch database is updated during runtime.
        /// </summary>
        Task InvalidateCacheAsync();
    }

    /// <summary>
    /// Implementation of TrackGraph factory with thread-safe caching
    /// </summary>
    public class TrackGraphFactory : ITrackGraphFactory
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);
        private TrackGraph _cachedTrackGraph;
        private readonly FileLoggingService? _fileLogger;

        public TrackGraphFactory(IServiceScopeFactory scopeFactory, FileLoggingService fileLogger = null)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _fileLogger = fileLogger;
        }

        public async Task<TrackGraph> CreateTrackGraphAsync()
        {
            // First check: fast path without locking
            if (_cachedTrackGraph != null)
            {
                return _cachedTrackGraph;
            }

            await _semaphore.WaitAsync();
            try
            {
                // Second check: inside the lock to prevent race conditions
                if (_cachedTrackGraph == null)
                {
                    // Create a short-lived scope for the database context
                    using var scope = _scopeFactory.CreateScope();
                    var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

                    _cachedTrackGraph = await TrackGraph.LoadFromDatabaseAsync(dbContext);
                    _fileLogger?.Log("[TRACK GRAPH] New track topology successfully loaded into memory cache.");
                }
                return _cachedTrackGraph;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        public async Task InvalidateCacheAsync()
        {
            await _semaphore.WaitAsync();
            try
            {
                _cachedTrackGraph = null;
                _fileLogger?.Log("[TRACK GRAPH] Topology cache invalidated. Will rebuild on next request.");
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
