using ClaudeSepareted.Domain;
using ClaudeSepareted.Services;
using Microsoft.Maui.Controls;
using System;
using System.Collections.Generic;

namespace ClaudeSepareted
{
    public partial class AdminPanelPage : ContentPage
    {
        private readonly StatusNotificationService _statusService;
        private readonly TrackHandlerService _trackHandlerService;
        private readonly PathfindingService _pathfinder;
        private readonly List<Label> _statusLabels = new List<Label>();
        private const int MaxStatusMessages = 50;

        public AdminPanelPage(StatusNotificationService statusService, TrackHandlerService trackHandlerService, PathfindingService pathfinder)
        {
            InitializeComponent();
            _statusService = statusService;
            _trackHandlerService = trackHandlerService;
            _pathfinder = pathfinder;

            InitializeStatusBar();
        }

        protected override void OnAppearing()
        {
            base.OnAppearing();
            // Ensure we don't double-subscribe
            _statusService.OnStatusUpdated -= OnStatusUpdated;
            _statusService.OnStatusUpdated += OnStatusUpdated;
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();
            // Detach the event to prevent memory leaks and ghost UI updates
            _statusService.OnStatusUpdated -= OnStatusUpdated;
        }

        private void InitializeStatusBar()
        {
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                // Safety check: if the page is no longer attached to a window, do not update UI
                if (this.Window == null) return;

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
                StatusType.Info => Colors.LightGray,
                _ => Colors.White
            };
        }

        private async void OnForceMidnightResetClicked(object sender, EventArgs e)
        {
            bool confirm = await DisplayAlert("Megerősítés", "Biztosan alaphelyzetbe állítod a rendszert (Éjféli Reset)?", "Igen", "Nem");
            if (confirm)
            {
                await _trackHandlerService.ForceSystemResetAsync();
                await DisplayAlert("Siker", "Rendszer alaphelyzetbe állítva.", "OK");
            }
        }

        private async void OnRebuildCacheClicked(object sender, EventArgs e)
        {
            bool confirm = await DisplayAlert("Megerősítés", "Újraépíted az útvonal gyorsítótárat? Ez eltarthat néhány másodpercig.", "Igen", "Nem");
            if (confirm)
            {
                _statusService.ShowInfo("Útvonal gyorsítótár újraépítése folyamatban...");
                await _pathfinder.BuildRoutingTableAsync(forceRebuild: true);
                await DisplayAlert("Siker", "Útvonal gyorsítótár újraépítve és elmentve.", "OK");
            }
        }

        // Route planner functionality has been removed

    }
}
