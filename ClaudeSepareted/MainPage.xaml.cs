using Microsoft.EntityFrameworkCore;
using System;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ClaudeSepareted.Services;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClaudeSepareted
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;
        private readonly IDbContextFactory<ApplicationDbContext> _dbContextFactory;
        private readonly TrackHandlerService _trackHandlerService;
        private readonly VirtualClock _virtualClock;
        private readonly StatusNotificationService _statusNotificationService;
        private readonly PathfindingService _pathfindingService;
        private readonly IServiceProvider _serviceProvider;
        private bool _automatedServicesInitialized = false;

        public MainPage(MainPageViewModel viewModel, IDbContextFactory<ApplicationDbContext> dbContextFactory,
                       TrackHandlerService trackHandlerService, VirtualClock virtualClock,
                       StatusNotificationService statusNotificationService,
                       PathfindingService pathfindingService,
                       IServiceProvider serviceProvider)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _dbContextFactory = dbContextFactory;
            _trackHandlerService = trackHandlerService;
            _virtualClock = virtualClock;
            _statusNotificationService = statusNotificationService;
            _pathfindingService = pathfindingService;
            _serviceProvider = serviceProvider;
            BindingContext = _viewModel;
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();

            if (!_automatedServicesInitialized)
            {
                await InitializeAutomatedServicesAsync();
                _automatedServicesInitialized = true;
            }

            // Always reload data when the page appears
            try
            {
                await _viewModel.LoadDataAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading data: {ex.Message}");
                _statusNotificationService?.ShowError($"Hiba történt: {ex.Message}");
            }
        }

        private async System.Threading.Tasks.Task InitializeAutomatedServicesAsync()
        {
            try
            {
                // Step 1: Initialize Pathfinding Service and build routing table
                var pathfindingInitStart = DateTime.Now;
                var pathfindingSuccess = await _pathfindingService.InitializeAsync();
                var pathfindingDuration = (DateTime.Now - pathfindingInitStart).TotalSeconds;

                if (pathfindingSuccess)
                {
                    // Routing table built successfully
                }
                else
                {
                    _statusNotificationService?.ShowError("Pathfinding service initialization failed");
                    return; // Don't continue if pathfinding failed
                }

                // Step 2: Configure virtual clock
                _virtualClock.SetSpeed(60.0); // 60x speed (default from your config)
                _virtualClock.SetTime(new DateTime(2024, 1, 1, 0, 0, 0)); // Start at 00:00 (midnight)

                // Step 3: Start Track Handler Service
                _trackHandlerService.Start();

                var totalDuration = (DateTime.Now - pathfindingInitStart).TotalSeconds;
                _statusNotificationService?.ShowSuccess($"Automatizált rendszer elindítva ({totalDuration:F0}s)");

                // Launch the Status Monitor safely on the Main UI Thread
                // MAUI WinUI 3 Fix: We must wait for the main window to fully render
                // before asking the OS to spawn a second window handle.
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try
                    {
                        await Task.Delay(1500); // 1.5 second UI breather

                        var adminPage = _serviceProvider.GetRequiredService<AdminPanelPage>();
                        var statusWindow = new Window(adminPage)
                        {
                            Title = "System Status Monitor",
                            Width = 600,
                            Height = 800
                        };
                        Application.Current?.OpenWindow(statusWindow);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to open status window: {ex.Message}");
                        _statusNotificationService?.ShowError($"Nem sikerült megnyitni a státusz ablakot: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                _statusNotificationService?.ShowError($"Automatizált rendszer indítása sikertelen: {ex.Message}");
            }
        }

        private async void OnAddClicked(object sender, EventArgs e)
        {
            // 1. A bemeneti adatok kinyerése a felhasználói felületről (feltételezett UI elemek nevei)
            var selectedTrain = TrainPicker.SelectedItem as Train;
            var selectedStartPlatform = StartPicker.SelectedItem as Platforms;
            var selectedEndPlatform = EndPicker.SelectedItem as Platforms;

            // 2. Ellenőrzés, hogy a felhasználó választott-e ki valamit
            if (selectedTrain == null || selectedStartPlatform == null || selectedEndPlatform == null)
            {
                await DisplayAlert("Hiba", "Kérjük, válasszon vonatot, indulási peront és cél peront.", "OK");
                return;
            }

            var startTime = StartTimePicker.Time ?? TimeSpan.Zero;

            try
            {
                using var dbContext = await _dbContextFactory.CreateDbContextAsync();

                var newEntry = new TimetableEntries
                {
                    SourcePlatform_DB_ID = selectedStartPlatform.DB_ID,
                    DestinationPlatform_DB_ID = selectedEndPlatform.DB_ID,
                    Train_DB_ID = selectedTrain.DB_ID,

                    // NEW: Capture the UI direction state
                    Direction = DirectionSwitch.IsToggled,

                    StartDate = DateTime.Today,
                    StartTime = startTime,
                    EntryState = EntryState.Upcoming,
                    RouteState = RouteState.InTime
                };

                dbContext.TimetableEntries.Add(newEntry);
                int rowsAffected = await dbContext.SaveChangesAsync();
                await _viewModel.LoadDataAsync();

                if (rowsAffected > 0)
                {
                    await DisplayAlert("Siker", $"Az új menetrendi bejegyzés ({selectedTrain.Name}) sikeresen hozzáadva.", "OK");
                }
                else
                {
                    await DisplayAlert("Figyelem", "Az adatbázis nem módosult.", "OK");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Adatbázis hiba: {ex.Message}");
                await DisplayAlert("Hiba", $"Hiba történt a mentés során: {ex.Message}", "OK");
            }
        }

        private async void OnDeleteClicked(object sender, EventArgs e)
        {
            // Ellenőrizzük, hogy a gomb melyik elemhez tartozik
            if (sender is Button button && button.BindingContext is ScheduleItem scheduleItem)
            {
                bool confirm = await DisplayAlert("Törlés megerősítése",
                                                  $"Biztosan törölni szeretnéd a(z) {scheduleItem.TrainName} menetrendi bejegyzést?",
                                                  "Igen", "Nem");

                if (!confirm)
                    return;

                try
                {
                    using var dbContext = await _dbContextFactory.CreateDbContextAsync();

                    // 1. Fetch potential matches from SQL Server without using [NotMapped] properties
                    var potentialEntries = await dbContext.TimetableEntries
                        .Include(e => e.Train)
                        .Include(e => e.SourcePlatform)
                            .ThenInclude(p => p.Station)
                        .Include(e => e.DestinationPlatform)
                            .ThenInclude(p => p.Station)
                        .Where(e => e.Train.Name == scheduleItem.TrainName && e.StartTime == scheduleItem.Start)
                        .ToListAsync();

                    // 2. Filter in C# memory where DisplayName can be evaluated safely
                    var entryToDelete = potentialEntries.FirstOrDefault(e =>
                        e.SourcePlatform?.DisplayName == scheduleItem.From &&
                        e.DestinationPlatform?.DisplayName == scheduleItem.To);

                    if (entryToDelete == null)
                    {
                        await DisplayAlert("Hiba", "A bejegyzés nem található az adatbázisban.", "OK");
                        return;
                    }

                    dbContext.TimetableEntries.Remove(entryToDelete);
                    int rowsAffected = await dbContext.SaveChangesAsync();
                    await _viewModel.LoadDataAsync();

                    if (rowsAffected > 0)
                    {
                        await DisplayAlert("Siker", "A menetrendi bejegyzés sikeresen törölve.", "OK");
                    }
                    else
                    {
                        await DisplayAlert("Figyelem", "Nem történt módosítás az adatbázisban.", "OK");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Törlési hiba: {ex.Message}");
                    await DisplayAlert("Hiba", $"Hiba történt a törlés során: {ex.Message}", "OK");
                }
            }
            else
            {
                await DisplayAlert("Hiba", "Nem sikerült azonosítani a törlendő elemet.", "OK");
            }
        }



        private void OnSetVirtualTime(object sender, EventArgs e)
        {
            try
            {
                string hourText = HourEntry.Text?.Trim();
                string minuteText = MinuteEntry.Text?.Trim();

                if (int.TryParse(hourText, out int hours) && int.TryParse(minuteText, out int minutes))
                {
                    if (hours >= 0 && hours < 24 && minutes >= 0 && minutes < 60)
                    {
                        _viewModel.VirtualClock.SetTime(hours, minutes, 0);
                        DisplayAlert("Success", $"Virtual time set to {hours:00}:{minutes:00}", "OK");
                    }
                    else
                    {
                        DisplayAlert("Error", "Hours must be 0-23 and minutes must be 0-59", "OK");
                    }
                }
                else
                {
                    DisplayAlert("Error", "Please enter valid numbers for hours and minutes", "OK");
                }
            }
            catch (Exception ex)
            {
                DisplayAlert("Error", $"Failed to set time: {ex.Message}", "OK");
            }
        }

        private void OnPauseClock(object sender, EventArgs e)
        {
            try
            {
                _viewModel.VirtualClock.Pause();
                DisplayAlert("Clock Paused", "Virtual clock has been paused", "OK");
            }
            catch (Exception ex)
            {
                DisplayAlert("Error", $"Failed to pause clock: {ex.Message}", "OK");
            }
        }

        private void OnResumeClock(object sender, EventArgs e)
        {
            try
            {
                _viewModel.VirtualClock.Resume();
                DisplayAlert("Clock Resumed", "Virtual clock has been resumed", "OK");
            }
            catch (Exception ex)
            {
                DisplayAlert("Error", $"Failed to resume clock: {ex.Message}", "OK");
            }
        }

        private void OnResetClock(object sender, EventArgs e)
        {
            try
            {
                _viewModel.VirtualClock.Reset();
                HourEntry.Text = "";
                MinuteEntry.Text = "";
                DisplayAlert("Clock Reset", "Virtual clock has been reset to 00:00 with 60x speed", "OK");
            }
            catch (Exception ex)
            {
                DisplayAlert("Error", $"Failed to reset clock: {ex.Message}", "OK");
            }
        }

    }
}
