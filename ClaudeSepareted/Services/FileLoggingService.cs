using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace ClaudeSepareted.Services
{
    /// <summary>
    /// Thread-safe file logging service that writes messages to a timestamped .txt file.
    /// Uses Channels to ensure non-blocking, asynchronous writes from multiple threads.
    /// </summary>
    public class FileLoggingService : IDisposable
    {
        private readonly Channel<string> _logChannel;
        private readonly Task _writerTask;
        private readonly CancellationTokenSource _cts;
        private readonly string _filePath;
        private bool _isDisposed;

        public FileLoggingService()
        {
            _logChannel = Channel.CreateUnbounded<string>();
            _cts = new CancellationTokenSource();

            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var logsDirectory = Path.Combine(documentsPath, "TrainControllerLogs");

            if (!Directory.Exists(logsDirectory))
            {
                Directory.CreateDirectory(logsDirectory);
            }

            string fileName = $"{DateTime.Now:yyyy_MM_dd_HH_mm_ss}.txt";
            _filePath = Path.Combine(logsDirectory, fileName);

            _writerTask = Task.Run(ProcessLogsAsync);

            LogSystemEvent("--- File Logging Service Started ---");
            LogSystemEvent($"Log file location: {_filePath}");
        }

        public void Log(string message)
        {
            if (_isDisposed) return;
            string formattedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
            _logChannel.Writer.TryWrite(formattedMessage);
        }

        private void LogSystemEvent(string message)
        {
            _logChannel.Writer.TryWrite(message);
        }

        private async Task ProcessLogsAsync()
        {
            try
            {
                using var writer = new StreamWriter(_filePath, append: true) { AutoFlush = true };
                await foreach (var message in _logChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    await writer.WriteLineAsync(message);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CRITICAL LOGGING ERROR: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            LogSystemEvent("--- File Logging Service Shutting Down ---");

            _logChannel.Writer.TryComplete();

            try { _writerTask.Wait(TimeSpan.FromSeconds(3)); }
            catch { }

            _cts.Cancel();
            _cts.Dispose();
        }
    }
}