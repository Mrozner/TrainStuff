//using System;
//using System.Collections.Concurrent;
//using System.Linq;
//using System.Threading;
//using System.Threading.Channels;
//using System.Threading.Tasks;
//using Microsoft.EntityFrameworkCore;
//using Microsoft.Extensions.Logging;
//using ClaudeSepareted.Lights;
//using ClaudeSepareted.DataAccess;
//using ClaudeSepareted.Domain;

//namespace ClaudeSepareted.Services
//{
//    /// <summary>
//    /// Traffic light bridge that translates train movement events into signal changes.
//    ///
//    /// This service subscribes to TrackOccupancyService's OnTrainMoved event and
//    /// automatically controls signals to implement dynamic, rolling-block traffic control.
//    ///
//    /// Signal Logic:
//    /// - RED behind trains (rear protection) - prevents following trains from colliding
//    /// - GREEN ahead of trains (clearance) - allows trains to proceed safely
//    /// - YELLOW for limited clearance (future enhancement)
//    ///
//    /// This replaces static signaling with event-driven, position-aware signal control
//    /// that adapts to train movements in real-time.
//    /// </summary>
//    public class SignalControllerService : IAsyncDisposable, IDisposable
//    {
//        #region Dependencies

//        private readonly TrackOccupancyService _trackOccupancyService;
//        private readonly LightController _lights;
//        private readonly ILogger<SignalControllerService> _logger;
//        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;

//        #endregion

//        #region State

//        private volatile bool _isInitialized = false;
//        private readonly SemaphoreSlim _initializationLock = new SemaphoreSlim(1, 1);
//        private CancellationTokenSource _processingCancellationTokenSource;
//        private IDisposable _trackSubscription;
//        private Task _processingTask;

//        // Topology cache for performance optimization
//        // Key: (SectionId, Direction) -> Value: List of NextSectionId(s) - supports switches (1-to-many)
//        private readonly ConcurrentDictionary<(int SectionId, bool Direction), List<int>> _topologyCache = new();
//        // Reverse topology cache for O(1) lookups - Key: (NextSectionId, Direction) -> Value: List of SectionId(s)
//        private readonly ConcurrentDictionary<(int NextSectionId, bool Direction), List<int>> _reverseTopologyCache = new();
//        // Section name to ID mapping for quick lookups
//        private readonly ConcurrentDictionary<string, int> _sectionNameToIdCache = new();
//        // Section ID to name mapping for reverse lookups
//        private readonly ConcurrentDictionary<int, string> _sectionIdToNameCache = new();
//        private volatile bool _topologyCacheLoaded = false;

//        #endregion

//        #region Constructor

//        /// <summary>
//        /// Creates a new SignalControllerService instance
//        /// </summary>
//        /// <param name="trackOccupancyService">Service providing real-time train position updates</param>
//        /// <param name="lights">Hardware interface for controlling physical signals</param>
//        /// <param name="dbContextFactory">Factory for creating database contexts (allows Singleton service to use DbContext)</param>
//        /// <param name="logger">Logger for diagnostic messages</param>
//        public SignalControllerService(
//            TrackOccupancyService trackOccupancyService,
//            LightController lights,
//            IDbContextFactory<ApplicationDbContext> dbContextFactory,
//            ILogger<SignalControllerService> logger)
//        {
//            _trackOccupancyService = trackOccupancyService ?? throw new ArgumentNullException(nameof(trackOccupancyService));
//            _lights = lights ?? throw new ArgumentNullException(nameof(lights));
//            _dbContextFactory = dbContextFactory ?? throw new ArgumentNullException(nameof(dbContextFactory));
//            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
//        }

//        #endregion

//        #region Initialization

//        /// <summary>
//        /// Initialize the service and subscribe to train movement events via Channel.
//        /// Thread-safe - can be called multiple times without issues.
//        /// </summary>
//        public async Task InitializeAsync()
//        {
//            await _initializationLock.WaitAsync();
//            try
//            {
//                // Double-check pattern for thread safety
//                if (_isInitialized)
//                    return;

//                // Load topology cache BEFORE subscribing to train movements
//                // This ensures cache is available when processing starts
//                await LoadTopologyCacheAsync();

//                // Subscribe to train movement notifications via Channel
//                var (reader, subscription) = _trackOccupancyService.Subscribe();
//                _trackSubscription = subscription;

//                // Start background processing task and store reference for proper disposal
//                _processingCancellationTokenSource = new CancellationTokenSource();
//                _processingTask = Task.Run(() => ProcessMovementsAsync(reader, _processingCancellationTokenSource.Token));

//                // Set initialized flag ONLY after everything is ready
//                // This prevents race conditions where other threads bypass the lock before cache is loaded
//                _isInitialized = true;

//                _logger.LogInformation("SignalControllerService initialized and subscribed to train movement events via Channel");
//            }
//            finally
//            {
//                _initializationLock.Release();
//            }
//        }

//        #endregion

//        #region Event Handler

//        /// <summary>
//        /// Background task that processes train movements from the Channel reader.
//        /// Replaces async void event handler with proper async/await pattern.
//        ///
//        /// This method processes train movements sequentially on a background thread,
//        /// implementing the rolling traffic light logic that protects trains from
//        /// behind while clearing the path ahead.
//        ///
//        /// Signal Update Logic:
//        /// 1. Set RED light behind the train (previous section) for rear protection
//        /// 2. Set GREEN light ahead of the train (current section) for clearance
//        ///
//        /// Note: Just because there's no signal at the train's exact current transition
//        /// doesn't mean we shouldn't update signals behind and ahead of it. The signal
//        /// lookup happens independently for each target section.
//        /// </summary>
//        /// <param name="reader">Channel reader for train movement events</param>
//        /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
//        private async Task ProcessMovementsAsync(ChannelReader<TrainMovedEventArgs> reader, CancellationToken cancellationToken)
//        {
//            try
//            {
//                _logger.LogInformation("SignalControllerService background processor started");

//                await foreach (var args in reader.ReadAllAsync(cancellationToken))
//                {
//                    try
//                    {
//                        _logger.LogDebug("Processing train movement: {EventDetails}", args.ToString());

//                        // Step 1: Set RED light behind the train (rear protection)
//                        // This prevents following trains from entering the section the train just left
//                        if (args.PreviousSubSection != null)
//                        {
//                            await SetRedSignalBehindTrainAsync(args, cancellationToken);
//                        }

//                        // Step 2: Set GREEN light ahead of the train (clearance)
//                        // This allows the train to proceed safely into the next section
//                        await SetGreenSignalAheadOfTrainAsync(args, cancellationToken);

//                        _logger.LogInformation(
//                            "Signals updated for train {TrainId}: RED behind, GREEN ahead (Section: {CurrentSection})",
//                            args.TrainId, args.CurrentSubSection
//                        );
//                    }
//                    catch (Exception ex)
//                    {
//                        _logger.LogError(ex,
//                            "Error processing train movement event for train {TrainId}: {PreviousSection} -> {CurrentSection}",
//                            args.TrainId, args.PreviousSubSection, args.CurrentSubSection
//                        );
//                    }
//                }
//            }
//            catch (OperationCanceledException)
//            {
//                _logger.LogInformation("SignalControllerService background processor cancelled");
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "SignalControllerService background processor failed");
//            }
//        }

//        #endregion

//        #region Signal Control Methods

//        /// <summary>
//        /// Sets RED signal behind the train to provide rear protection.
//        ///
//        /// The RED signal protects the train from behind by indicating to following
//        /// trains that the section is occupied or the track ahead is not clear.
//        /// This is the foundation of collision prevention in the rolling-block system.
//        ///
//        /// The signal is set on the transition FROM the section the train just left
//        /// TO the section it was in before that (two sections back).
//        /// </summary>
//        /// <param name="args">Train movement event arguments</param>
//        /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
//        private async Task SetRedSignalBehindTrainAsync(TrainMovedEventArgs args, CancellationToken cancellationToken)
//        {
//            try
//            {
//                // Query all sections before the previous section (handles converging switches)
//                var sectionsBeforePrevious = GetSectionBeforePreviousFromCache(args);

//                if (sectionsBeforePrevious.Count == 0)
//                {
//                    _logger.LogWarning(
//                        "Cannot set RED signal behind train {TrainId}: section before previous not found (PreviousSection: {PreviousSection})",
//                        args.TrainId, args.PreviousSubSection
//                    );
//                    return;
//                }

//                var directionBool = args.Direction == MovementDirection.Forward;
//                int signalsSet = 0;

//                // Set RED signals for ALL converging paths to prevent rear-end collisions
//                foreach (var sectionBeforePrevious in sectionsBeforePrevious)
//                {
                    
                    
//                }

//                if (signalsSet > 0)
//                {
//                    _logger.LogDebug(
//                        "RED signals set behind train {TrainId} at section {PreviousSection} for {Count} path(s)",
//                        args.TrainId, args.PreviousSubSection, signalsSet
//                    );
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex,
//                    "Error setting RED signal behind train {TrainId} at section {Section}",
//                    args.TrainId, args.PreviousSubSection
//                );
//            }
//        }

//        /// <summary>
//        /// Sets GREEN signal ahead of the train to provide clearance.
//        ///
//        /// The GREEN signal indicates to the train driver (or automation system)
//        /// that the track ahead is clear and it's safe to proceed.
//        ///
//        /// This method looks ahead to the next section and sets GREEN for the
//        /// transition from the current section to the next anticipated section.
//        ///
//        /// NOTE: Look-ahead logic uses cached topology queries and collision detection.
//        /// </summary>
//        /// <param name="args">Train movement event arguments</param>
//        /// <param name="cancellationToken">Cancellation token for graceful shutdown</param>
//        private async Task SetGreenSignalAheadOfTrainAsync(TrainMovedEventArgs args, CancellationToken cancellationToken)
//        {
//            try
//            {
//                // Query the cache for the next section in the train's direction
//                var nextSection = GetNextAnticipatedSectionFromCache(args);
//                var directionBool = args.Direction == MovementDirection.Forward;

//                if (nextSection != null)
//                {
//                    // Query ObjectsLibrary for light information about this specific transition
//                    var (mega, lightId) = ObjectsLibrary.GetLightInfo(
//                        nextSection,
//                        args.CurrentSubSection,
//                        directionBool
//                    );

//                    if (mega == -1)
//                    {
//                        _logger.LogDebug(
//                            "No signal configured for setting GREEN ahead of train {TrainId} at transition {Current} -> {Next}",
//                            args.TrainId, args.CurrentSubSection, nextSection
//                        );
//                        return;
//                    }

//                    // Set GREEN signal for the transition from current to next section
//                    await _lights.LightsToGreen(
//                        nextSection,
//                        args.CurrentSubSection,
//                        directionBool,
//                        mega,
//                        lightId,
//                        cancellationToken
//                    );

//                    _logger.LogDebug(
//                        "GREEN signal set ahead of train {TrainId} for section {CurrentSection} -> {NextSection} (Mega={Mega}, LightId={LightId})",
//                        args.TrainId, args.CurrentSubSection, nextSection, mega, lightId
//                    );
//                }
//                else
//                {
//                    // No next section found - default to safety, do NOT set GREEN signal
//                    // Setting a GREEN signal for an occupied section would risk rear-end collision
//                    _logger.LogWarning(
//                        "No next section found for train {TrainId} at {CurrentSection} in direction {Direction}. GREEN signal NOT set for safety.",
//                        args.TrainId, args.CurrentSubSection, args.Direction.ToString().ToLower()
//                    );
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex,
//                    "Error setting GREEN signal ahead of train {TrainId} at section {Section}",
//                    args.TrainId, args.CurrentSubSection
//                );
//            }
//        }

//        #endregion

//        #region Topology Cache Management

//        /// <summary>
//        /// Loads the track topology into memory caches at startup.
//        /// This dramatically improves performance by eliminating repeated database queries
//        /// during signal operations.
//        ///
//        /// The cache stores:
//        /// 1. Section name to ID mappings
//        /// 2. Section ID to name mappings (reverse lookup)
//        /// 3. Topology connections: (SectionId, Direction) -> NextSectionId
//        ///
//        /// This method is called once during Initialize() and the cache is used for
//        /// all subsequent topology queries.
//        /// </summary>
//        private async Task LoadTopologyCacheAsync()
//        {
//            if (_topologyCacheLoaded)
//            {
//                _logger.LogDebug("Topology cache already loaded, skipping");
//                return;
//            }

//            _logger.LogInformation("Loading topology cache for SignalControllerService...");

//            using var dbContext = await _dbContextFactory.CreateDbContextAsync();
//            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//            // Load all subsections for name/ID mapping
//            var allSubSections = await dbContext.SubSections.ToListAsync();
//            foreach (var section in allSubSections)
//            {
//                _sectionNameToIdCache.TryAdd(section.Name, section.DB_ID);
//                _sectionIdToNameCache.TryAdd(section.DB_ID, section.Name);
//            }

//            _logger.LogInformation("Loaded {Count} sections into name/ID cache", allSubSections.Count);

//            // Load all topology connections
//            var allConnections = await dbContext.VLookupSectionNextSection
//                .FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection")
//                .ToListAsync();

//            int connectionsCached = 0;
//            foreach (var connection in allConnections)
//            {
//                if (!connection.NextSection_DB_ID.HasValue)
//                    continue;

//                // Populate forward topology cache (Section -> NextSection(s))
//                var forwardKey = (connection.Section_DB_ID, connection.Direction);
//                _topologyCache.AddOrUpdate(
//                    forwardKey,
//                    _ => new List<int> { connection.NextSection_DB_ID.Value },
//                    (_, existingList) =>
//                    {
//                        lock (existingList)
//                        {
//                            if (!existingList.Contains(connection.NextSection_DB_ID.Value))
//                            {
//                                existingList.Add(connection.NextSection_DB_ID.Value);
//                            }
//                        }
//                        return existingList;
//                    }
//                );

//                // Populate reverse topology cache (NextSection -> Section(s)) for O(1) reverse lookups
//                var reverseKey = (connection.NextSection_DB_ID.Value, connection.Direction);
//                _reverseTopologyCache.AddOrUpdate(
//                    reverseKey,
//                    _ => new List<int> { connection.Section_DB_ID },
//                    (_, existingList) =>
//                    {
//                        lock (existingList)
//                        {
//                            if (!existingList.Contains(connection.Section_DB_ID))
//                            {
//                                existingList.Add(connection.Section_DB_ID);
//                            }
//                        }
//                        return existingList;
//                    }
//                );

//                connectionsCached++;
//            }

//            stopwatch.Stop();
//            _topologyCacheLoaded = true;

//            _logger.LogInformation(
//                "Topology cache loaded successfully: {Connections} connections cached in {ElapsedMs}ms",
//                connectionsCached,
//                stopwatch.ElapsedMilliseconds
//            );
//        }

//        #endregion

//        #region Look-Ahead Logic

//        /// <summary>
//        /// Gets all sections before previous using cached topology (fast path).
//        /// Uses reverse topology cache for O(1) lookup.
//        /// Returns a list of all converging paths to protect against rear-end collisions.
//        /// </summary>
//        private List<string> GetSectionBeforePreviousFromCache(TrainMovedEventArgs args)
//        {
//            var result = new List<string>();

//            if (string.IsNullOrEmpty(args.PreviousSubSection))
//            {
//                return result;
//            }

//            // Get the previous section ID from cache
//            if (!_sectionNameToIdCache.TryGetValue(args.PreviousSubSection, out var previousSectionId))
//            {
//                _logger.LogWarning(
//                    "Previous section {SectionName} not found in cache",
//                    args.PreviousSubSection
//                );
//                return result;
//            }

//            // Use reverse topology cache for O(1) lookup
//            var directionBool = args.Direction == MovementDirection.Forward;
//            var reverseKey = (previousSectionId, directionBool);

//            if (_reverseTopologyCache.TryGetValue(reverseKey, out var sectionList))
//            {
//                // Return ALL converging paths to protect against rear-end collisions
//                foreach (var sectionId in sectionList)
//                {
//                    if (sectionId != 0 && _sectionIdToNameCache.TryGetValue(sectionId, out var sectionName))
//                    {
//                        result.Add(sectionName);
//                    }
//                }

//                if (result.Count > 0)
//                {
//                    // Log a warning if there are multiple converging paths (switch detected)
//                    if (result.Count > 1)
//                    {
//                        _logger.LogWarning(
//                            "Converging switch detected: {Count} paths to section {PreviousSection} for train {TrainId}. Setting RED signals for all paths: {Paths}",
//                            result.Count, args.PreviousSubSection, args.TrainId, string.Join(", ", result)
//                        );
//                    }
//                    else
//                    {
//                        _logger.LogDebug(
//                            "Found section before previous using reverse cache: {BeforePrevious} -> {Previous} for train {TrainId}",
//                            result[0], args.PreviousSubSection, args.TrainId
//                        );
//                    }
//                }
//            }

//            if (result.Count == 0)
//            {
//                _logger.LogDebug(
//                    "No section found before {PreviousSection} for train {TrainId} in direction {Direction} (cache lookup)",
//                    args.PreviousSubSection, args.TrainId, args.Direction.ToString().ToLower()
//                );
//            }

//            return result;
//        }

//        /// <summary>
//        /// Gets next anticipated section using cached topology (fast path).
//        /// Uses list-based forward cache to support switches (1-to-many connections).
//        /// </summary>
//        private string GetNextAnticipatedSectionFromCache(TrainMovedEventArgs args)
//        {
//            if (string.IsNullOrEmpty(args.CurrentSubSection))
//            {
//                return null;
//            }

//            // Get the current section ID from cache
//            if (!_sectionNameToIdCache.TryGetValue(args.CurrentSubSection, out var currentSectionId))
//            {
//                _logger.LogWarning(
//                    "Current section {SectionName} not found in cache",
//                    args.CurrentSubSection
//                );
//                return null;
//            }

//            // Look up the next section ID list in the topology cache
//            var directionBool = args.Direction == MovementDirection.Forward;
//            var cacheKey = (currentSectionId, directionBool);

//            if (_topologyCache.TryGetValue(cacheKey, out var nextSectionIds) && nextSectionIds.Count > 0)
//            {
//                // FIX 3: Blind Switch Hazard - Don't blindly pick first path when multiple exist
//                // If Count > 1, we don't know which way the switch is aligned, so default to safety
//                if (nextSectionIds.Count > 1)
//                {
//                    _logger.LogWarning(
//                        "Switch routing unknown for train {TrainId} at section {CurrentSection}: {Count} possible paths. Defaulting to safety (no GREEN signal).",
//                        args.TrainId, args.CurrentSubSection, nextSectionIds.Count
//                    );
//                    return null;
//                }

//                // Single path - safe to proceed
//                var nextSectionId = nextSectionIds.FirstOrDefault();

//                if (nextSectionId != 0 && _sectionIdToNameCache.TryGetValue(nextSectionId, out var nextSectionName))
//                {
//                    _logger.LogDebug(
//                        "Next section found for train {TrainId} using cache: {CurrentSection} -> {NextSection}",
//                        args.TrainId, args.CurrentSubSection, nextSectionName
//                    );
//                    return nextSectionName;
//                }
//            }

//            _logger.LogDebug(
//                "No next section found for train {TrainId} at {CurrentSection} in direction {Direction} (cache lookup)",
//                args.TrainId, args.CurrentSubSection, args.Direction.ToString().ToLower()
//            );

//            return null;
//        }

//        #endregion

//        #region Manual Signal Control (Advanced Operations)

//        /// <summary>
//        /// Manually set all signals to RED (emergency stop condition).
//        /// Use this for system-wide emergency stops or maintenance.
//        ///
//        /// This method iterates over all valid track transitions (edges) in the topology
//        /// and sets their corresponding signals to RED concurrently for fast execution.
//        /// </summary>
//        public async Task SetAllSignalsToRedAsync(CancellationToken cancellationToken = default)
//        {
//            _logger.LogWarning("Setting all signals to RED (emergency stop)");

//            try
//            {
//                var signalTasks = new List<Task>();
//                int signalsSet = 0;

//                // Use cached topology (guaranteed to be loaded after InitializeAsync)
//                if (_topologyCacheLoaded)
//                {
//                    // Fast path: use cached topology
//                    foreach (var (key, nextSectionIds) in _topologyCache)
//                    {
//                        if (nextSectionIds.Count == 0)
//                            continue;

//                        // Get section names from cache - use first path for switches
//                        if (!_sectionIdToNameCache.TryGetValue(key.SectionId, out var currentSection))
//                            continue;

//                        // Process all paths for switches
//                        foreach (var nextSectionId in nextSectionIds)
//                        {
//                            if (!_sectionIdToNameCache.TryGetValue(nextSectionId, out var nextSection))
//                                continue;

//                            // Query ObjectsLibrary for light information for this transition
//                            var (mega, lightId) = ObjectsLibrary.GetLightInfo(
//                                nextSection,
//                                currentSection,
//                                key.Direction
//                            );

//                            if (mega != -1)
//                            {
//                                // Add the task directly without Task.Run - no thread pool waste
//                                signalTasks.Add(_lights.LightsToRed(
//                                    nextSection,
//                                    currentSection,
//                                    key.Direction,
//                                    mega,
//                                    lightId,
//                                    cancellationToken
//                                ).ContinueWith(t =>
//                                {
//                                    if (t.IsFaulted)
//                                    {
//                                        _logger.LogError(t.Exception,
//                                            "Error setting emergency RED for transition {Current} -> {Next}",
//                                            currentSection, nextSection
//                                        );
//                                    }
//                                    else
//                                    {
//                                        Interlocked.Increment(ref signalsSet);
//                                    }
//                                }));
//                            }
//                        }
//                    }
//                }
//                else
//                {
//                    // Emergency stop must never fail silently
//                    throw new InvalidOperationException("Topology cache not loaded. Cannot execute emergency stop. Ensure SignalControllerService is properly initialized.");
//                }

//                // Execute all signal operations concurrently
//                if (signalTasks.Count > 0)
//                {
//                    await Task.WhenAll(signalTasks);
//                }

//                _logger.LogWarning(
//                    "Emergency stop completed: {SignalsSet} signals set to RED",
//                    signalsSet
//                );
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError(ex, "Error executing emergency stop (all signals to RED)");
//                throw; // Rethrow to allow caller to handle the failure
//            }
//        }

//        /// <summary>
//        /// Manually set signals for a specific section.
//        /// Useful for maintenance or special routing scenarios.
//        /// </summary>
//        /// <param name="section">The section to control signals for</param>
//        /// <param name="previousSection">The section before</param>
//        /// <param name="direction">Train direction</param>
//        /// <param name="toRed">True to set RED, false to set GREEN</param>
//        /// <param name="cancellationToken">Cancellation token for async operation</param>
//        public async Task SetManualSignalAsync(string section, string previousSection, bool direction, bool toRed, CancellationToken cancellationToken = default)
//        {
//            // Guard clause - validate required parameters
//            if (string.IsNullOrEmpty(section))
//                throw new ArgumentNullException(nameof(section), "Section cannot be null or empty");

//            if (string.IsNullOrEmpty(previousSection))
//                throw new ArgumentNullException(nameof(previousSection), "PreviousSection cannot be null or empty");
//        }

//        #endregion

//        #region Cleanup

//        /// <summary>
//        /// Disposes of resources synchronously.
//        /// This provides best-effort cleanup with proper thread safety.
//        /// </summary>
//        public void Dispose()
//        {
//            if (!_initializationLock.Wait(TimeSpan.FromSeconds(5)))
//            {
//                // Failed to acquire lock within timeout (likely deadlocked)
//                // Log warning and proceed with best-effort cleanup
//                _logger?.LogWarning("Dispose: Failed to acquire initialization lock within 5 seconds - potential deadlock detected");
//                return;
//            }

//            try
//            {
//                if (!_isInitialized)
//                    return;

//                // Mark as not initialized inside the lock to prevent race conditions
//                _isInitialized = false;

//                // Cancel background processing
//                _processingCancellationTokenSource?.Cancel();
//                _processingCancellationTokenSource?.Dispose();
//                _processingCancellationTokenSource = null;

//                // Dispose the track subscription to prevent memory leaks
//                _trackSubscription?.Dispose();
//                _trackSubscription = null;
//            }
//            finally
//            {
//                _initializationLock.Release();
//            }

//            _initializationLock?.Dispose();
//            GC.SuppressFinalize(this);
//        }

//        /// <summary>
//        /// Disposes of resources and stops background processing asynchronously.
//        /// This is the preferred disposal method as it ensures graceful shutdown.
//        ///
//        /// Thread-safe: Uses locking to prevent race conditions with Initialize().
//        /// </summary>
//        public async ValueTask DisposeAsync()
//        {
//            Task backgroundTask = null;
//            bool shouldDispose = false;

//            await _initializationLock.WaitAsync();
//            try
//            {
//                if (!_isInitialized)
//                    return;

//                // Mark as not initialized inside the lock to prevent race conditions
//                _isInitialized = false;
//                shouldDispose = true;

//                // Cancel background processing
//                _processingCancellationTokenSource?.Cancel();

//                // Capture the background task reference to await it outside the lock
//                backgroundTask = _processingTask;
//            }
//            finally
//            {
//                _initializationLock.Release();
//            }

//            // Wait for the background task to complete gracefully (outside lock to prevent deadlocks)
//            if (backgroundTask != null)
//            {
//                try
//                {
//                    await backgroundTask;
//                }
//                catch (TaskCanceledException)
//                {
//                    // Expected during cancellation - ignore
//                }
//            }

//            // Dispose resources outside the lock
//            if (shouldDispose)
//            {
//                try
//                {
//                    // Dispose the cancellation token source
//                    _processingCancellationTokenSource?.Dispose();

//                    // Dispose the track subscription to prevent memory leaks
//                    _trackSubscription?.Dispose();
//                    _trackSubscription = null;

//                    _logger.LogInformation("SignalControllerService disposed");
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError(ex, "Error disposing SignalControllerService");
//                }
//            }

//            _initializationLock?.Dispose();
//            GC.SuppressFinalize(this);
//        }

//        #endregion
//    }
//}
