using ClaudeSepareted;
using ClaudeSepareted.Services;
using Microsoft.EntityFrameworkCore;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore.Storage;

public class MainPageViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
    private readonly TravelTimeMeasurementService _travelTimeService;

    // 1. Observable Properties (Listák)
    public ObservableCollection<Train> Trains { get; set; } = new ObservableCollection<Train>();
    public ObservableCollection<Platforms> Platforms { get; set; } = new ObservableCollection<Platforms>();
    public ObservableCollection<ScheduleItem> ScheduleItems { get; set; } = new ObservableCollection<ScheduleItem>();

    // 2. Observable Properties (Kiválasztott elemek)
    private Train _selectedTrain;
    public Train SelectedTrain
    {
        get => _selectedTrain;
        set { SetProperty(ref _selectedTrain, value); }
    }

    private Platforms _selectedStartPlatform;
    public Platforms SelectedStartPlatform
    {
        get => _selectedStartPlatform;
        set { SetProperty(ref _selectedStartPlatform, value); }
    }

    private Platforms _selectedEndPlatform;
    public Platforms SelectedEndPlatform
    {
        get => _selectedEndPlatform;
        set { SetProperty(ref _selectedEndPlatform, value); }
    }

    // Virtual Clock ViewModel
    public VirtualClockViewModel VirtualClock { get; private set; }

    public MainPageViewModel(IDbContextFactory<ApplicationDbContext> dbContextFactory, VirtualClock virtualClock, TravelTimeMeasurementService travelTimeService)
    {
        _dbContextFactory = dbContextFactory;
        _travelTimeService = travelTimeService;
        VirtualClock = new VirtualClockViewModel(virtualClock);
    }

    public async Task LoadDataAsync()
    {
        try
        {
            using var dbContext = await _dbContextFactory.CreateDbContextAsync();

            // 1. Load Trains safely
            var trains = await dbContext.Trains.Where(t => t.IsActive).OrderBy(t => t.Name).ToListAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Trains.Clear();
                foreach (var train in trains) Trains.Add(train);
            });

            // 2. Load Platforms safely
            var platforms = await dbContext.Platforms
                .Include(p => p.Station)
                .Where(p => p.IsActive)
                .OrderBy(p => p.Station.Name)
                .ThenBy(p => p.Name)
                .ToListAsync();
            MainThread.BeginInvokeOnMainThread(() =>
            {
                Platforms.Clear();
                foreach (var platform in platforms) Platforms.Add(platform);
            });

            // 3. Load Timetable
            var entries = await dbContext.TimetableEntries
                .Include(e => e.Train)
                .Include(e => e.SourcePlatform)
                .Include(e => e.DestinationPlatform)
                .ToListAsync();

            MainThread.BeginInvokeOnMainThread(() =>
            {
                ScheduleItems.Clear();
                foreach (var entry in entries.OrderBy(e => e.StartTime))
                {
                    TimeSpan endTime;
                    if (entry.ArrivedTime.HasValue)
                    {
                        endTime = entry.ArrivedTime.Value.TimeOfDay;
                    }
                    else
                    {
                        // Ask the service if we know how long this train takes on this route
                        var estimatedDuration = _travelTimeService.GetEstimatedTravelTime(
                            entry.Train_DB_ID,
                            entry.SourcePlatform_DB_ID,
                            entry.DestinationPlatform_DB_ID);

                        // If we have an estimate, add it. If not, use the 2-hour placeholder.
                        endTime = entry.StartTime.Add(estimatedDuration ?? new TimeSpan(2, 0, 0));
                    }

                    ScheduleItems.Add(new ScheduleItem
                    {
                        TrainName = entry.Train?.Name ?? "Vonat neve hiányzik",
                        Start = entry.StartTime,
                        End = endTime,
                        From = entry.SourcePlatform?.DisplayName ?? "Kiinduló peron hiányzik",
                        To = entry.DestinationPlatform?.DisplayName ?? "Cél peron hiányzik"
                    });
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error loading data: {ex.Message}");
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

    // Dispose pattern to properly clean up resources
    public void Dispose()
    {
        VirtualClock?.Dispose();
    }
}