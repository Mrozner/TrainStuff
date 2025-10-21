using Microsoft.EntityFrameworkCore;
using System;
using System.Diagnostics;

namespace ClaudeSepareted
{
    public partial class MainPage : ContentPage
    {
        private readonly MainPageViewModel _viewModel;
        private readonly ApplicationDbContext _dbContext;

        public MainPage(MainPageViewModel viewModel, ApplicationDbContext dbContext)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _dbContext = dbContext;
            BindingContext = _viewModel;
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            _ = _viewModel.LoadDataAsync();
        }

        private async void OnAddClicked(object sender, EventArgs e)
        {
            // 1. A bemeneti adatok kinyerése a felhasználói felületről (feltételezett UI elemek nevei)
            var selectedTrain = TrainPicker.SelectedItem as Train;
            var selectedSourceStation = StartPicker.SelectedItem as Stations;
            var selectedDestinationStation = EndPicker.SelectedItem as Stations;

            // 2. Ellenőrzés, hogy a felhasználó választott-e ki valamit
            if (selectedTrain == null || selectedSourceStation == null || selectedDestinationStation == null)
            {
                await DisplayAlert("Hiba", "Kérjük, válasszon vonatot, indulási helyet és célállomást.", "OK");
                return;
            }
            var startTime = StartTimePicker.Time;

            using (_dbContext)
            {
                try
                {
                    var newEntry = new TimetableEntries
                    {
                        SourceStation_DB_ID = selectedSourceStation.DB_ID,
                        DestinationStation_DB_ID = selectedDestinationStation.DB_ID,
                        Train_DB_ID = selectedTrain.DB_ID,

                        StartDate = DateTime.Today,
                        StartTime = startTime,
                        EntryState = EntryState.Upcoming,
                        RouteState = RouteState.InTime
                    };

                    _dbContext.TimetableEntries.Add(newEntry);
                    int rowsAffected = await _dbContext.SaveChangesAsync();
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
                    // Megkeressük az adatbázisban a megfelelő bejegyzést
                    var entryToDelete = await _dbContext.TimetableEntries
                        .Include(e => e.Train)
                        .Include(e => e.SourceStation)
                        .Include(e => e.DestinationStation)
                        .FirstOrDefaultAsync(e =>
                            e.Train.Name == scheduleItem.TrainName &&
                            e.SourceStation.Name == scheduleItem.From &&
                            e.DestinationStation.Name == scheduleItem.To &&
                            e.StartTime == scheduleItem.Start);

                    if (entryToDelete == null)
                    {
                        await DisplayAlert("Hiba", "A bejegyzés nem található az adatbázisban.", "OK");
                        return;
                    }

                    _dbContext.TimetableEntries.Remove(entryToDelete);
                    int rowsAffected = await _dbContext.SaveChangesAsync();
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



        private async void AdminPanel(object sender, EventArgs e)
        {
            await Navigation.PushAsync(new AdminPanelPage(_dbContext));
        }


    }
}
