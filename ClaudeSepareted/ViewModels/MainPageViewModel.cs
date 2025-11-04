using ClaudeSepareted;
using Microsoft.EntityFrameworkCore;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

public class MainPageViewModel : INotifyPropertyChanged
{
    private readonly ApplicationDbContext _dbContext;

    // 1. Observable Properties (Listák)
    public ObservableCollection<Train> Trains { get; set; } = new ObservableCollection<Train>();
    public ObservableCollection<Stations> Stations { get; set; } = new ObservableCollection<Stations>();
    public ObservableCollection<ScheduleItem> ScheduleItems { get; set; } = new ObservableCollection<ScheduleItem>();

    // 2. Observable Properties (Kiválasztott elemek)
    private Train _selectedTrain;
    public Train SelectedTrain
    {
        get => _selectedTrain;
        set { SetProperty(ref _selectedTrain, value); }
    }

    private Stations _selectedStartStation;
    public Stations SelectedStartStation
    {
        get => _selectedStartStation;
        set { SetProperty(ref _selectedStartStation, value); }
    }

    private Stations _selectedEndStation;
    public Stations SelectedEndStation
    {
        get => _selectedEndStation;
        set { SetProperty(ref _selectedEndStation, value); }
    }

    // Virtual Clock ViewModel
    public VirtualClockViewModel VirtualClock { get; private set; }

    public MainPageViewModel(ApplicationDbContext dbContext, VirtualClock virtualClock)
    {
        _dbContext = dbContext;
        VirtualClock = new VirtualClockViewModel(virtualClock);
    }

    public async Task LoadDataAsync()
    {
        if (ScheduleItems != null)
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                ScheduleItems.Clear();
            });
        }

        try
        {
            var trains = await _dbContext.Trains
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .ToListAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Trains.Clear();
                foreach (var train in trains)
                {
                    Trains.Add(train);
                }
            });

            // Állomások betöltése
            var stations = await _dbContext.Stations
                .Where(s => s.IsActive)
                .OrderBy(s => s.Name)
                .ToListAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Stations.Clear();
                foreach (var station in stations)
                {
                    Stations.Add(station);
                }
            });

            var entries = await _dbContext.TimetableEntries
                .ToListAsync();
            foreach (var entry in entries.OrderBy(e => e.StartTime))
            {
                // Kiszámoljuk az érkezési időt TimeSpan formában
                TimeSpan endTime = entry.ArrivedTime.HasValue
                    ? entry.ArrivedTime.Value.TimeOfDay
                    : entry.StartTime.Add(new TimeSpan(2, 0, 0));
                ScheduleItems.Add(new ScheduleItem
                {
                    // A navigációs tulajdonságokat (Train, SourceStation, DestinationStation) használjuk.
                    TrainName = entry.Train?.Name ?? "Vonat neve hiányzik",
                    Start = entry.StartTime,
                    End = endTime,
                    From = entry.SourceStation?.Name ?? "Kiinduló állomás hiányzik",
                    To = entry.DestinationStation?.Name ?? "Célállomás hiányzik"
                });
            }



            Console.WriteLine($"Adatok betöltve: {Trains.Count} vonat, {Stations.Count} állomás.");
        }
        catch (Exception ex)
        {
            // Valamilyen hibakezelés
            Console.WriteLine($"Hiba az adatok betöltésekor: {ex.Message}");
            // Itt kellene egy szolgáltatás, ami kiírja a felhasználónak a hibaüzenetet (pl. IAlertService)
        }
    }


    // 4. INotifyPropertyChanged Helper
    public event PropertyChangedEventHandler PropertyChanged;
    protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    // Setter segédmetódus
    protected bool SetProperty<T>(ref T backingStore, T value, [CallerMemberName] string propertyName = "")
    {
        if (EqualityComparer<T>.Default.Equals(backingStore, value))
            return false;

        backingStore = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}