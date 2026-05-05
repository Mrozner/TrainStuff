using System;
using System.Diagnostics;

namespace ClaudeSepareted
{
    public class VirtualClock : IDisposable
    {
        // Phase 1: Add lock object for thread safety
        private readonly object _lock = new();
        private DateTime _virtualTime;
        private long _lastUpdateTimestamp;
        private long _accumulatedVirtualTicks = 0;
        private double _speedMultiplier;
        private readonly Timer _updateTimer;
        private volatile bool _disposed = false;
        private bool _isPaused = true; // Default to true since timer starts suspended
        private double _previousSpeedMultiplier = 60.0; // Store for resume
        private readonly Services.FileLoggingService? _fileLogger;

        public event EventHandler<DateTime>? TimeChanged;
        public event EventHandler? MidnightReset;

        // Phase 1: Thread-safe property
        public DateTime CurrentTime { get { lock (_lock) return _virtualTime; } }
        public double SpeedMultiplier { get { lock (_lock) return _speedMultiplier; } }

        public VirtualClock(Services.FileLoggingService fileLogger = null)
        {
            _fileLogger = fileLogger;
            _virtualTime = new DateTime(2024, 1, 1, 0, 0, 0); // Default 0:00 (midnight)
            _lastUpdateTimestamp = Stopwatch.GetTimestamp();
            _speedMultiplier = 60.0; // Default 60x speed
            _previousSpeedMultiplier = 60.0;

            // Phase 4: Start as one-shot timer
            _updateTimer = new Timer(UpdateVirtualTime, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Accumulate elapsed real time into virtual ticks based on current speed multiplier.
        /// This must be called before any operation that affects time calculation.
        /// If paused, only updates the timestamp without accumulating time.
        /// Phase 2: Single timestamp capture to prevent drift.
        /// </summary>
        private void AccumulateTime()
        {
            // Phase 2: Single capture to prevent drift
            long now = Stopwatch.GetTimestamp();

            if (_isPaused)
            {
                _lastUpdateTimestamp = now;
                return;
            }

            var elapsedRealTime = Stopwatch.GetElapsedTime(_lastUpdateTimestamp, now);
            var virtualTicksToAdd = (long)(elapsedRealTime.Ticks * _speedMultiplier);
            _accumulatedVirtualTicks += virtualTicksToAdd;
            _lastUpdateTimestamp = now;
        }

        public void Start()
        {
            lock (_lock)
            {
                // Reset the last update timestamp to now and begin the timer
                _lastUpdateTimestamp = Stopwatch.GetTimestamp();
                _isPaused = false; // Mark as running
                // Phase 4: One-shot pattern
                _updateTimer.Change(0, Timeout.Infinite);
            }
        }

        private void UpdateVirtualTime(object? state)
        {
            if (_disposed) return;

            DateTime oldTime;
            DateTime newTime;
            bool triggerMidnight = false;

            // Phase 1: Lock for thread safety
            lock (_lock)
            {
                AccumulateTime();
                oldTime = _virtualTime;

                // Phase 3: 24-hour wrap logic using modulo
                const long ticksPerDay = TimeSpan.TicksPerDay;
                _accumulatedVirtualTicks %= ticksPerDay;

                newTime = new DateTime(2024, 1, 1).AddTicks(_accumulatedVirtualTicks);
                _virtualTime = newTime;

                // Phase 3: State-based midnight trigger (detect wraparound)
                if (newTime.TimeOfDay < oldTime.TimeOfDay)
                    triggerMidnight = true;
            }

            // Fire events outside the lock to prevent deadlocks
            if (triggerMidnight)
                MidnightReset?.Invoke(this, EventArgs.Empty);

            if (!_disposed)
                TimeChanged?.Invoke(this, newTime);

            // Phase 4: Reschedule next tick using one-shot pattern
            lock (_lock)
            {
                if (!_disposed && !_isPaused)
                    _updateTimer.Change(100, Timeout.Infinite);
            }
        }

        public void SetSpeed(double multiplier)
        {
            if (multiplier <= 0)
                throw new ArgumentException("Speed multiplier must be greater than 0", nameof(multiplier));

            lock (_lock)
            {
                // Lock in elapsed time at current speed before changing
                AccumulateTime();

                // Store current speed for potential resume
                _previousSpeedMultiplier = multiplier;

                // Update speed multiplier for future calculations
                _speedMultiplier = multiplier;

                _fileLogger?.Log($"[VIRTUAL CLOCK] Speed multiplier changed from {_previousSpeedMultiplier}x to {multiplier}x.");
            }
        }

        public void SetTime(DateTime newTime)
        {
            lock (_lock)
            {
                // Set accumulated ticks to the target time of day
                _accumulatedVirtualTicks = newTime.TimeOfDay.Ticks;
                _virtualTime = new DateTime(2024, 1, 1, newTime.Hour, newTime.Minute, newTime.Second);
                // Reset last update timestamp to establish a fresh baseline
                _lastUpdateTimestamp = Stopwatch.GetTimestamp();

                _fileLogger?.Log($"[VIRTUAL CLOCK] Time explicitly set to {newTime:HH:mm:ss}.");
            }
        }

        public void Reset()
        {
            SetTime(new DateTime(2024, 1, 1, 0, 0, 0));
            SetSpeed(60.0);
        }

        public void Pause()
        {
            lock (_lock)
            {
                // First, lock in elapsed time before pausing
                AccumulateTime();
                // Mark as paused to prevent time accumulation during timer callbacks
                _isPaused = true;
                // Phase 4: Suspend the timer completely
                _updateTimer.Change(Timeout.Infinite, Timeout.Infinite);

                _fileLogger?.Log("[VIRTUAL CLOCK] Clock paused.");
            }
        }

        public void Resume()
        {
            lock (_lock)
            {
                // Establish a fresh baseline timestamp to prevent time jumps
                _lastUpdateTimestamp = Stopwatch.GetTimestamp();

                // Restore the speed multiplier to previous value (or default)
                _speedMultiplier = _previousSpeedMultiplier;

                // Mark as running to allow time accumulation
                _isPaused = false;

                // Phase 4: Restart timer using one-shot pattern
                _updateTimer.Change(0, Timeout.Infinite);

                _fileLogger?.Log("[VIRTUAL CLOCK] Clock resumed.");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // Stop the timer and dispose it
                    _updateTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    _updateTimer?.Dispose();

                    // Unsubscribe all events to prevent memory leaks
                    TimeChanged = null;
                    MidnightReset = null;
                }

                _disposed = true;
            }
        }

    }
}