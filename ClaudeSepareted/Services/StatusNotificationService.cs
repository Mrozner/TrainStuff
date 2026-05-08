using System.Collections.Concurrent;
using ClaudeSepareted.Services;
using Microsoft.Extensions.Logging;

namespace ClaudeSepareted
{
    public class StatusNotificationService
    {
        private readonly ConcurrentQueue<StatusMessage> _statusMessages = new();
        private readonly VirtualClock? _virtualClock;
        private readonly ILogger<StatusNotificationService>? _logger;
        private readonly Services.FileLoggingService? _fileLogger;

        public event Action<StatusMessage>? OnStatusUpdated;

        // Inject VirtualClock (allow null for testing/fallback)
        public StatusNotificationService(VirtualClock virtualClock = null, ILogger<StatusNotificationService> logger = null, Services.FileLoggingService fileLogger = null)
        {
            _virtualClock = virtualClock;
            _logger = logger;
            _fileLogger = fileLogger;
        }

        public void ShowInfo(string message, string? trainName = null)
            => DispatchMessage(StatusType.Info, message, trainName);

        public void ShowSuccess(string message, string? trainName = null)
            => DispatchMessage(StatusType.Success, message, trainName);

        public void ShowWarning(string message, string? trainName = null)
            => DispatchMessage(StatusType.Warning, message, trainName);

        public void ShowError(string message, string? trainName = null)
            => DispatchMessage(StatusType.Error, message, trainName);

        public void ShowTrainStatus(string trainName, string status, string? details = null)
            => DispatchMessage(StatusType.TrainStatus, status, trainName, details);

        /// <summary>
        /// Centralized dispatch method that creates and broadcasts status messages.
        /// Eliminates code duplication across all public notification methods.
        /// </summary>
        private void DispatchMessage(StatusType type, string message, string? trainName, string? details = null)
        {
            // Centralized logging - all status notifications are automatically logged
            if (_logger != null)
            {
                if (type == StatusType.Error) _logger.LogError("{Message} - Train: {Train} - [StatusNotificationService]", message, trainName ?? "System");
                else if (type == StatusType.Warning) _logger.LogWarning("{Message} - Train: {Train}", message, trainName ?? "System");
                else _logger.LogInformation("{Message} - Train: {Train}", message, trainName ?? "System");
            }

            // Log to file for persistent tracking
            _fileLogger?.Log($"[{type.ToString().ToUpper()}] {trainName ?? "SYSTEM"}: {message}");

            var status = new StatusMessage
            {
                Type = type,
                Message = message,
                TrainName = trainName,
                Details = details,
                Timestamp = DateTime.UtcNow,
                VirtualTimestamp = _virtualClock?.CurrentTime ?? DateTime.UtcNow
            };

            AddStatus(status);
        }

        private void AddStatus(StatusMessage status)
        {
            // Add to queue
            _statusMessages.Enqueue(status);

            // Keep only last 50 messages
            while (_statusMessages.Count > 50)
            {
                _statusMessages.TryDequeue(out _);
            }

            // Notify listeners
            OnStatusUpdated?.Invoke(status);
        }

        public List<StatusMessage> GetRecentMessages(int count = 10)
        {
            return _statusMessages.Reverse().Take(count).ToList();
        }

        public void ClearStatus()
        {
            // Clear() is natively thread-safe for ConcurrentQueue in .NET 10
            _statusMessages.Clear();

            // Use centralized dispatch method for consistency
            DispatchMessage(StatusType.Info, "Status cleared", null);
        }
    }

    public class StatusMessage
    {
        public StatusType Type { get; set; }
        public string Message { get; set; } = "";
        public string? TrainName { get; set; }
        public string? Details { get; set; }
        public DateTime Timestamp { get; set; }          // Real-world time (UTC)
        public DateTime VirtualTimestamp { get; set; }   // Simulated timetable time
    }

    public enum StatusType
    {
        Info,
        Success,
        Warning,
        Error,
        TrainStatus
    }
}