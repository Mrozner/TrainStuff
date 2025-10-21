using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Controls;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeSepareted
{
    public partial class AdminPanelPage : ContentPage
    {
        private readonly ApplicationDbContext _dbContext;

        public AdminPanelPage(ApplicationDbContext dbContext)
        {
            InitializeComponent();
            _dbContext = dbContext;

            LoadTrains();
            LoadSpeeds();
        }

        private async void LoadTrains()
        {
            var trains = await _dbContext.Trains
                .Where(t => t.IsActive)
                .OrderBy(t => t.Name)
                .ToListAsync();

            TrainPicker.ItemsSource = trains;
        }

        private void LoadSpeeds()
        {
            SpeedPicker.ItemsSource = Enum.GetNames(typeof(Speed));
        }

        private async void OnStartClicked(object sender, EventArgs e)
        {

        }

        private async Task SetTrainSpeed(TrainManagerService trainManager, string trainName, string speedStr)
        {
            try
            {
                var train = trainManager.GetTrain(trainName);
                if (train == null)
                {
                    await DisplayAlert("Hiba", $"A vonat nem található: {trainName}", "OK");
                    return;
                }

                if (Enum.TryParse<Speed>(speedStr, true, out Speed speed))
                {
                    trainManager.UpdateTrainSpeed(trainName, speed);
                    await Task.Delay(100);

                    var updatedTrain = trainManager.GetTrain(trainName);
                    if (updatedTrain != null)
                    {
                        await DisplayAlert("Siker", $"✓ A(z) {trainName} sebessége beállítva: {updatedTrain.CurrentSpeed}", "OK");
                    }
                }
                else
                {
                    await DisplayAlert("Hiba", $"Érvénytelen sebesség: {speedStr}.", "OK");
                }
            }
            catch (Exception ex)
            {
                await DisplayAlert("Hiba", $"Hiba történt a sebesség beállításakor: {ex.Message}", "OK");
            }
        }
    }

}
