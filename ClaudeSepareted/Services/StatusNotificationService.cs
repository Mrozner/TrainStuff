using System.Collections.Concurrent;

namespace ClaudeSepareted
{
    public class StatusNotificationService
    {
        private readonly ConcurrentQueue<StatusMessage> _statusMessages = new();
        private readonly object _lockObject = new object();

        public event Action<StatusMessage>? OnStatusUpdated;

        public StatusNotificationService()
        {
            Console.WriteLine("StatusNotificationService initialized");
        }

        public void ShowInfo(string message, string? trainName = null)
        {
            var status = new StatusMessage
            {
                Type = StatusType.Info,
                Message = message,
                TrainName = trainName,
                Timestamp = DateTime.UtcNow
            };

            AddStatus(status);
        }

        public void ShowSuccess(string message, string? trainName = null)
        {
            var status = new StatusMessage
            {
                Type = StatusType.Success,
                Message = message,
                TrainName = trainName,
                Timestamp = DateTime.UtcNow
            };

            AddStatus(status);
        }

        public void ShowWarning(string message, string? trainName = null)
        {
            var status = new StatusMessage
            {
                Type = StatusType.Warning,
                Message = message,
                TrainName = trainName,
                Timestamp = DateTime.UtcNow
            };

            AddStatus(status);
        }

        public void ShowError(string message, string? trainName = null)
        {
            var status = new StatusMessage
            {
                Type = StatusType.Error,
                Message = message,
                TrainName = trainName,
                Timestamp = DateTime.UtcNow
            };

            AddStatus(status);
        }

        public void ShowTrainStatus(string trainName, string status, string? details = null)
        {
            var message = new StatusMessage
            {
                Type = StatusType.TrainStatus,
                Message = status,
                TrainName = trainName,
                Details = details,
                Timestamp = DateTime.UtcNow
            };

            AddStatus(message);
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

            // Log to console for debugging
            Console.WriteLine($"[{status.Timestamp:HH:mm:ss}] [{status.Type}] {status.TrainName ?? "System"}: {status.Message}");
        }

        public List<StatusMessage> GetRecentMessages(int count = 10)
        {
            return _statusMessages.Reverse().Take(count).ToList();
        }

        public void ClearStatus()
        {
            lock (_lockObject)
            {
                while (_statusMessages.TryDequeue(out _)) { }
            }

            var clearStatus = new StatusMessage
            {
                Type = StatusType.Info,
                Message = "Status cleared",
                Timestamp = DateTime.UtcNow
            };

            OnStatusUpdated?.Invoke(clearStatus);
        }
    }

    public class StatusMessage
    {
        public StatusType Type { get; set; }
        public string Message { get; set; } = "";
        public string? TrainName { get; set; }
        public string? Details { get; set; }
        public DateTime Timestamp { get; set; }
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