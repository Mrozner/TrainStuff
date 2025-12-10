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
    public ObservableCollection<Platforms> Platforms { get; set; } = new ObservableCollection<Platforms>();
    public ObservableCollection<ScheduleItem> ScheduleItems { get; set; } = new ObservableCollection<ScheduleItem>();

    // Helper property for platform display in pickers
    public ObservableCollection<PlatformDisplayItem> PlatformDisplayItems { get; set; } = new ObservableCollection<PlatformDisplayItem>();

    // 2. Observable Properties (Kiválasztott elemek)
    private Train _selectedTrain;
    public Train SelectedTrain
    {
        get => _selectedTrain;
        set { SetProperty(ref _selectedTrain, value); }
    }

    private PlatformDisplayItem _selectedStartPlatform;
    public PlatformDisplayItem SelectedStartPlatform
    {
        get => _selectedStartPlatform;
        set { SetProperty(ref _selectedStartPlatform, value); }
    }

    private PlatformDisplayItem _selectedEndPlatform;
    public PlatformDisplayItem SelectedEndPlatform
    {
        get => _selectedEndPlatform;
        set { SetProperty(ref _selectedEndPlatform, value); }
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

            // Peronok betöltése
            var platforms = await _dbContext.Platforms
                .Include(p => p.Station)
                .Where(p => p.IsActive)
                .OrderBy(p => p.Station.Name)
                    .ThenBy(p => p.Name)
                .ToListAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                Platforms.Clear();
                PlatformDisplayItems.Clear();
                foreach (var platform in platforms)
                {
                    Platforms.Add(platform);
                    PlatformDisplayItems.Add(new PlatformDisplayItem(platform));
                }
            });

            var entries = await _dbContext.TimetableEntries
                .Include(e => e.Train)
                .Include(e => e.SourcePlatform)
                    .ThenInclude(sp => sp.Station)
                .Include(e => e.DestinationPlatform)
                    .ThenInclude(dp => dp.Station)
                .ToListAsync();
            foreach (var entry in entries.OrderBy(e => e.StartTime))
            {
                // Kiszámoljuk az érkezési időt TimeSpan formában
                TimeSpan endTime = entry.ArrivedTime.HasValue
                    ? entry.ArrivedTime.Value.TimeOfDay
                    : entry.StartTime.Add(new TimeSpan(2, 0, 0));
                ScheduleItems.Add(new ScheduleItem
                {
                    // A navigációs tulajdonságokat (Train, SourcePlatform, DestinationPlatform) használjuk.
                    TrainName = entry.Train?.Name ?? "Vonat neve hiányzik",
                    Start = entry.StartTime,
                    End = endTime,
                    From = entry.SourcePlatform?.Station?.Name ?? "Kiinduló állomás hiányzik",
                    To = entry.DestinationPlatform?.Station?.Name ?? "Célállomás hiányzik",
                    // Add platform-specific information for precise deletion
                    FromPlatform = entry.SourcePlatform?.Name ?? "Platform hiányzik",
                    ToPlatform = entry.DestinationPlatform?.Name ?? "Platform hiányzik"
                });
            }



            Console.WriteLine($"Adatok betöltve: {Trains.Count} vonat, {Platforms.Count} peron.");
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

// Helper class for displaying platforms in pickers
public class PlatformDisplayItem
{
    public Platforms Platform { get; set; }
    public string DisplayName { get; set; }

    public PlatformDisplayItem(Platforms platform)
    {
        Platform = platform;
        DisplayName = $"{platform.Station?.Name ?? "Unknown"} - {platform.Name}";
    }
}