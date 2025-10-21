using Microsoft.EntityFrameworkCore;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ClaudeSepareted
{
    public partial class AdminPanelPage : ContentPage
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly AdminMQTTService _mqttService;
        private readonly StatusNotificationService _statusService;
        private readonly List<Label> _statusLabels = new List<Label>();
        private const int MaxStatusMessages = 50;

        public AdminPanelPage(ApplicationDbContext dbContext, AdminMQTTService mqttService, StatusNotificationService statusService)
        {
            InitializeComponent();
            _dbContext = dbContext;
            _mqttService = mqttService;
            _statusService = statusService;

            LoadTrains();
            LoadSpeeds();
            LoadDirections();

            InitializeStatusBar();
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

        private void LoadDirections()
        {
            DirectionPicker.ItemsSource = Enum.GetNames(typeof(Direction));
        }

        private void InitializeStatusBar()
        {
            _statusService.OnStatusUpdated += OnStatusUpdated;

            // Load recent status messages
            LoadRecentStatusMessages();

            _statusService.ShowInfo("Admin Panel ready");
        }

        private void LoadRecentStatusMessages()
        {
            var recentMessages = _statusService.GetRecentMessages(20);

            foreach (var message in recentMessages)
            {
                var statusText = $"[{message.Timestamp:HH:mm:ss}]";

                if (!string.IsNullOrEmpty(message.TrainName))
                    statusText += $" {message.TrainName}:";

                statusText += $" {message.Message}";

                var statusLabel = new Label
                {
                    Text = statusText,
                    TextColor = GetStatusColor(message.Type),
                    FontSize = 12,
                    Margin = new Thickness(0, 1, 0, 1)
                };

                _statusLabels.Add(statusLabel);
                StatusMessagesStack.Children.Add(statusLabel);
            }
        }

        private void OnStatusUpdated(StatusMessage status)
        {
            // Update UI on main thread
            MainThread.BeginInvokeOnMainThread(() =>
            {
                UpdateStatusBar(status);
            });
        }

        private void UpdateStatusBar(StatusMessage status)
        {
            var statusText = $"[{status.Timestamp:HH:mm:ss}]";

            if (!string.IsNullOrEmpty(status.TrainName))
                statusText += $" {status.TrainName}:";

            statusText += $" {status.Message}";

            // Create new status label
            var statusLabel = new Label
            {
                Text = statusText,
                TextColor = GetStatusColor(status.Type),
                FontSize = 12,
                Margin = new Thickness(0, 1, 0, 1)
            };

            // Add to our tracking list
            _statusLabels.Add(statusLabel);

            // Keep only the last MaxStatusMessages
            if (_statusLabels.Count > MaxStatusMessages)
            {
                var oldestLabel = _statusLabels[0];
                _statusLabels.RemoveAt(0);

                // Remove from UI if still available
                if (StatusMessagesStack.Children.Contains(oldestLabel))
                {
                    StatusMessagesStack.Children.Remove(oldestLabel);
                }
            }

            // Add new label to UI
            StatusMessagesStack.Children.Add(statusLabel);

            // Auto-scroll to bottom
            if (StatusMessagesStack.Parent is ScrollView scrollView)
            {
                scrollView.ScrollToAsync(statusLabel, ScrollToPosition.End, true);
            }
        }

        private Color GetStatusColor(StatusType type)
        {
            return type switch
            {
                StatusType.Success => Colors.Green,
                StatusType.Warning => Colors.Orange,
                StatusType.Error => Colors.Red,
                StatusType.TrainStatus => Colors.Blue,
                StatusType.Info => Colors.Gray,
                _ => Colors.Black
            };
        }

        private async void OnStartClicked(object sender, EventArgs e)
        {
            var selectedTrain = TrainPicker.SelectedItem as Train;
            var selectedSpeed = SpeedPicker.SelectedItem?.ToString();
            var selectedDirection = DirectionPicker.SelectedItem?.ToString();

            if (selectedTrain == null)
            {
                _statusService.ShowError("Kérjük, válasszon vonatot!");
                return;
            }

            if (string.IsNullOrEmpty(selectedSpeed))
            {
                _statusService.ShowError("Kérjük, válasszon sebességet!");
                return;
            }

            if (string.IsNullOrEmpty(selectedDirection))
            {
                _statusService.ShowError("Kérjük, válasszon irányt!");
                return;
            }

            // Disable button to prevent multiple clicks
            ((Button)sender).IsEnabled = false;

            try
            {
                // Parse speed and direction
                if (Enum.TryParse<Speed>(selectedSpeed, true, out Speed speed) &&
                    Enum.TryParse<Direction>(selectedDirection, true, out Direction direction))
                {
                    // Show start status
                    _statusService.ShowInfo($"Indítás: {selectedTrain.Name} -> {selectedSpeed} ({direction})", selectedTrain.Name);

                    // Send MQTT command asynchronously
                    bool success = await Task.Run(async () =>
                    {
                        return await _mqttService.SendTrainSpeedCommandAsync(selectedTrain.Name, speed, direction);
                    });

                    if (success)
                    {
                        _statusService.ShowSuccess($"Vonat elindítva: {selectedTrain.Name} ({speed})", selectedTrain.Name);
                    }
                    else
                    {
                        _statusService.ShowError($"MQTT hiba: {selectedTrain.Name} sebesség beállítása sikertelen", selectedTrain.Name);
                    }
                }
                else
                {
                    _statusService.ShowError($"Érvénytelen sebesség vagy irány: {selectedSpeed}/{selectedDirection}", selectedTrain.Name);
                }
            }
            catch (Exception ex)
            {
                _statusService.ShowError($"Hiba történt: {ex.Message}", selectedTrain.Name);
            }
            finally
            {
                // Re-enable button
                ((Button)sender).IsEnabled = true;
            }
        }

    }
}
