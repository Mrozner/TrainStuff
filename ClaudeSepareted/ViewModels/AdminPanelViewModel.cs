using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using ClaudeSepareted.DataAccess;
using ClaudeSepareted.Domain;
using ClaudeSepareted.Services;
using Microsoft.Maui;

namespace ClaudeSepareted.ViewModels
{
    public class AdminPanelViewModel : INotifyPropertyChanged
    {
        private readonly ITimetableRepository _timetableRepository;
        private readonly RocrailCommandService _rocrailCommandService;
        private readonly StatusNotificationService _statusService;

        // Observable Collections
        public ObservableCollection<Train> Trains { get; set; } = new ObservableCollection<Train>();
        public ObservableCollection<string> Speeds { get; set; } = new ObservableCollection<string>();
        public ObservableCollection<string> Directions { get; set; } = new ObservableCollection<string>();

        // Selected Items Properties
        private Train _selectedTrain;
        public Train SelectedTrain
        {
            get => _selectedTrain;
            set { SetProperty(ref _selectedTrain, value); }
        }

        private string _selectedSpeed;
        public string SelectedSpeed
        {
            get => _selectedSpeed;
            set { SetProperty(ref _selectedSpeed, value); }
        }

        private string _selectedDirection;
        public string SelectedDirection
        {
            get => _selectedDirection;
            set { SetProperty(ref _selectedDirection, value); }
        }

        private bool _isStartEnabled = true;
        public bool IsStartEnabled
        {
            get => _isStartEnabled;
            set { SetProperty(ref _isStartEnabled, value); }
        }

        // Commands
        public ICommand LoadDataCommand { get; }
        public ICommand StartTrainCommand { get; }

        public AdminPanelViewModel(
            ITimetableRepository timetableRepository,
            RocrailCommandService rocrailCommandService,
            StatusNotificationService statusService)
        {
            _timetableRepository = timetableRepository;
            _rocrailCommandService = rocrailCommandService;
            _statusService = statusService;

            LoadDataCommand = new Command(async () => await LoadDataAsync());
            StartTrainCommand = new Command(async () => await StartTrainAsync());
        }

        public async Task LoadDataAsync()
        {
            await LoadTrainsAsync();
            LoadSpeeds();
            LoadDirections();
        }

        private async Task LoadTrainsAsync()
        {
            try
            {
                var trains = await _timetableRepository.GetActiveTrainsAsync();

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    Trains.Clear();
                    foreach (var train in trains)
                    {
                        Trains.Add(train);
                    }
                });
            }
            catch (Exception ex)
            {
                _statusService.ShowError($"Hiba a vonatok betöltésekor: {ex.Message} - [AdminPanelViewModel]");
            }
        }

        private void LoadSpeeds()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Speeds.Clear();
                foreach (var speedName in Enum.GetNames(typeof(Speed)))
                {
                    Speeds.Add(speedName);
                }
            });
        }

        private void LoadDirections()
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Directions.Clear();
                foreach (var directionName in Enum.GetNames(typeof(Direction)))
                {
                    Directions.Add(directionName);
                }
            });
        }

        private async Task StartTrainAsync()
        {
            if (SelectedTrain == null)
            {
                _statusService.ShowError("Kérjük, válasszon vonatot!");
                return;
            }

            if (string.IsNullOrEmpty(SelectedSpeed))
            {
                _statusService.ShowError("Kérjük, válasszon sebességet!");
                return;
            }

            if (string.IsNullOrEmpty(SelectedDirection))
            {
                _statusService.ShowError("Kérjük, válasszon irányt!");
                return;
            }

            IsStartEnabled = false;

            try
            {
                if (Enum.TryParse<Speed>(SelectedSpeed, true, out Speed speed) &&
                    Enum.TryParse<Direction>(SelectedDirection, true, out Direction direction))
                {
                    _statusService.ShowInfo($"Indítás: {SelectedTrain.Name} -> {SelectedSpeed} ({direction})", SelectedTrain.Name);

                    bool success = await _rocrailCommandService.SendTrainSpeedCommandAsync(SelectedTrain.Name, speed, direction);

                    if (success)
                    {
                        _statusService.ShowSuccess($"Vonat elindítva: {SelectedTrain.Name} ({speed})", SelectedTrain.Name);
                    }
                    else
                    {
                        _statusService.ShowError($"MQTT hiba: {SelectedTrain.Name} sebesség beállítása sikertelen - [AdminPanelViewModel]", SelectedTrain.Name);
                    }
                }
                else
                {
                    _statusService.ShowError($"Érvénytelen sebesség vagy irány: {SelectedSpeed}/{SelectedDirection} - [AdminPanelViewModel]", SelectedTrain.Name);
                }
            }
            catch (Exception ex)
            {
                _statusService.ShowError($"Hiba történt: {ex.Message} - [AdminPanelViewModel]", SelectedTrain.Name);
            }
            finally
            {
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    IsStartEnabled = true;
                });
            }
        }

        // Status message helper (for backward compatibility)
        public void AddStatusMessage(string message)
        {
            if (message.Contains("✅"))
                _statusService.ShowSuccess(message);
            else if (message.Contains("❌"))
                _statusService.ShowError($"{message} - [AdminPanelViewModel]");
            else
                _statusService.ShowInfo(message);
        }

        // INotifyPropertyChanged Implementation
        public event PropertyChangedEventHandler PropertyChanged;
        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(backingStore, value))
                return false;

            backingStore = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
