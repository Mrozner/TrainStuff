using System;

namespace ClaudeSepareted
{
    public class VirtualClock
    {
        private DateTime _virtualTime;
        private DateTime _startTime;
        private double _speedMultiplier;
        private readonly Timer _updateTimer;

        public event EventHandler<DateTime>? TimeChanged;

        public DateTime CurrentTime => _virtualTime;
        public double SpeedMultiplier => _speedMultiplier;

        public VirtualClock()
        {
            _virtualTime = new DateTime(2024, 1, 1, 0, 0, 0); // Default 0:00 (midnight)
            _startTime = DateTime.Now;
            _speedMultiplier = 60.0; // Default 60x speed

            // Update every 100ms for smooth UI updates
            _updateTimer = new Timer(UpdateVirtualTime, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        }

        private void UpdateVirtualTime(object? state)
        {
            var elapsedRealTime = DateTime.Now - _startTime;
            var elapsedVirtualTime = TimeSpan.FromTicks((long)(elapsedRealTime.Ticks * _speedMultiplier));

            var newVirtualTime = new DateTime(2024, 1, 1, 0, 0, 0).Add(elapsedVirtualTime);

            if (newVirtualTime.Date != _virtualTime.Date)
            {
                // Reset to same day if we've passed midnight
                _startTime = DateTime.Now;
                newVirtualTime = new DateTime(2024, 1, 1, 0, 0, 0);
            }

            _virtualTime = newVirtualTime;
            TimeChanged?.Invoke(this, _virtualTime);
        }

        public void SetSpeed(double multiplier)
        {
            if (multiplier <= 0)
                throw new ArgumentException("Speed multiplier must be greater than 0", nameof(multiplier));

            // Adjust start time to maintain current virtual time when speed changes
            var elapsedRealTime = DateTime.Now - _startTime;
            var elapsedVirtualTime = TimeSpan.FromTicks((long)(elapsedRealTime.Ticks * _speedMultiplier));

            _speedMultiplier = multiplier;
            _startTime = DateTime.Now - TimeSpan.FromTicks((long)(elapsedVirtualTime.Ticks / _speedMultiplier));
        }

        public void SetTime(DateTime newTime)
        {
            _virtualTime = new DateTime(2024, 1, 1, newTime.Hour, newTime.Minute, newTime.Second);
            // Adjust start time so the timer calculates to the desired time
            var targetTimeOfDay = newTime.TimeOfDay;
            _startTime = DateTime.Now - TimeSpan.FromTicks((long)(targetTimeOfDay.Ticks / _speedMultiplier));
        }

        public void Reset()
        {
            SetTime(new DateTime(2024, 1, 1, 0, 0, 0));
            SetSpeed(60.0);
        }

        public void Pause()
        {
            SetSpeed(0.0001); // Nearly stopped but still running to avoid timer issues
        }

        public void Resume()
        {
            SetSpeed(60.0); // Back to default speed
        }

        public void Dispose()
        {
            _updateTimer?.Dispose();
        }

    }
}