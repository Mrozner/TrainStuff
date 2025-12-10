using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClaudeSepareted
{
    public class VirtualClockViewModel : INotifyPropertyChanged
    {
        private readonly VirtualClock _virtualClock;
        private DateTime _currentTime;
        private double _speedMultiplier;
        private string _currentTimeString;
        private string _speedDisplay;

        public DateTime CurrentTime
        {
            get => _currentTime;
            set
            {
                if (SetProperty(ref _currentTime, value))
                {
                    CurrentTimeString = value.ToString("HH:mm");
                    OnPropertyChanged(nameof(TimeDisplay));
                }
            }
        }

        public string CurrentTimeString
        {
            get => _currentTimeString;
            set => SetProperty(ref _currentTimeString, value);
        }

        public double SpeedMultiplier
        {
            get => _speedMultiplier;
            set
            {
                if (SetProperty(ref _speedMultiplier, value))
                {
                    SpeedDisplay = value == 0.0001 ? "Paused" : $"{value:F1}x";
                }
            }
        }

        public string SpeedDisplay
        {
            get => _speedDisplay;
            set => SetProperty(ref _speedDisplay, value);
        }

        public string TimeDisplay => $"Virtual Time: {CurrentTimeString}";

        public List<string> SpeedOptions { get; } = new List<string>
        {
            "0.1x", "0.5x", "1x", "2x", "5x", "10x", "30x", "60x", "120x", "300x"
        };

        private string _selectedSpeedOption;
        public string SelectedSpeedOption
        {
            get => _selectedSpeedOption;
            set
            {
                if (SetProperty(ref _selectedSpeedOption, value))
                {
                    if (!string.IsNullOrEmpty(value) && value.EndsWith("x"))
                    {
                        if (double.TryParse(value.Replace("x", ""), out double speed))
                        {
                            SetSpeed(speed);
                        }
                    }
                }
            }
        }

        public VirtualClockViewModel(VirtualClock virtualClock)
        {
            _virtualClock = virtualClock;
            if (virtualClock != null)
            {
                _currentTime = virtualClock.CurrentTime;
                _speedMultiplier = virtualClock.SpeedMultiplier;
                _currentTimeString = _currentTime.ToString("HH:mm");
                _speedDisplay = $"{_speedMultiplier:F1}x";
                _selectedSpeedOption = "60x";

                virtualClock.TimeChanged += OnVirtualTimeChanged;

                // Start the virtual clock if it hasn't been started yet
                virtualClock.Start();
            }
            else
            {
                _currentTime = new DateTime(2024, 1, 1, 0, 0, 0); // Midnight
                _speedMultiplier = 60.0;
                _currentTimeString = _currentTime.ToString("HH:mm");
                _speedDisplay = "60.0x";
                _selectedSpeedOption = "60x";
            }
        }

        private void OnVirtualTimeChanged(object? sender, DateTime newTime)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                CurrentTime = newTime;
            });
        }

        public void SetSpeed(double multiplier)
        {
            _virtualClock?.SetSpeed(multiplier);
            SpeedMultiplier = multiplier;
        }

        public void SetTime(DateTime newTime)
        {
            _virtualClock?.SetTime(newTime);
            CurrentTime = newTime;
        }

        public void SetTime(int hours, int minutes, int seconds = 0)
        {
            SetTime(new DateTime(2024, 1, 1, hours, minutes, seconds));
        }

        public void Pause()
        {
            _virtualClock?.Pause();
            SpeedMultiplier = 0.0001;
            SpeedDisplay = "Paused";
        }

        public void Resume()
        {
            _virtualClock?.Resume();
            SpeedMultiplier = 60.0;
            SpeedDisplay = "60.0x";
            SelectedSpeedOption = "60x";
        }

        public void Reset()
        {
            _virtualClock?.Reset();
            CurrentTime = _virtualClock?.CurrentTime ?? new DateTime(2024, 1, 1, 0, 0, 0); // Midnight
            SpeedMultiplier = _virtualClock?.SpeedMultiplier ?? 60.0;
            SpeedDisplay = "60.0x";
            SelectedSpeedOption = "60x";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string? propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}