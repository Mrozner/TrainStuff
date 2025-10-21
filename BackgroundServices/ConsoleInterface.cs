using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrainControlSystem.Configuration;
using TrainControlSystem.Models;
using TrainControlSystem.Services;
using TrainControlSystem.Repositories;

namespace TrainControlSystem.BackgroundServices
{
    /// <summary>
    /// Console interface for system interaction and monitoring
    /// </summary>
    public class ConsoleInterface : BackgroundService
    {
        private readonly TrainManagerService _trainManager;
        private readonly TimetableManager _timetableManager;
        private readonly TrackManager _trackManager;
        private readonly MachinistServiceFactory _machinistFactory;
        private readonly TimetableRepository _timetableRepository;
        private readonly ILogger<ConsoleInterface> _logger;

        public ConsoleInterface(
            TrainManagerService trainManager,
            TimetableManager timetableManager,
            TrackManager trackManager,
            MachinistServiceFactory machinistFactory,
            TimetableRepository timetableRepository,
            ILogger<ConsoleInterface> logger)
        {
            _trainManager = trainManager;
            _timetableManager = timetableManager;
            _trackManager = trackManager;
            _machinistFactory = machinistFactory;
            _timetableRepository = timetableRepository;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Console Interface started");

            // Start a separate task for handling console input
            var consoleTask = Task.Run(async () =>
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await ProcessConsoleInput(stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing console input");
                        await Task.Delay(1000, stoppingToken);
                    }
                }
            }, stoppingToken);

            await consoleTask;
        }

        private async Task ProcessConsoleInput(CancellationToken stoppingToken)
        {
            Console.Write("\n> ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                return;

            var parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return;

            var command = parts[0].ToLowerInvariant();

            try
            {
                switch (command)
                {
                    case "help":
                    case "h":
                        ShowHelp();
                        break;

                    case "trains":
                    case "t":
                        _trainManager.ListTrains();
                        break;

                    case "machinists":
                    case "m":
                        _trainManager.ListMachinistStatus();
                        break;

                    case "overview":
                    case "o":
                        _trainManager.GetSystemOverview();
                        break;

                    case "stop":
                        if (parts.Length >= 2)
                        {
                            var trainName = parts[1];
                            await _trainManager.EmergencyStopTrainAsync(trainName);
                        }
                        else
                        {
                            Console.WriteLine("Usage: stop <train_name>");
                        }
                        break;

                    case "add-timetable":
                    case "at":
                        await AddTimetableEntry(parts);
                        break;

                    case "list-timetable":
                    case "lt":
                        ListTimetableEntries();
                        break;

                    case "stats":
                    case "s":
                        ShowStatistics();
                        break;

                    case "exit":
                    case "quit":
                    case "q":
                        Environment.Exit(0);
                        break;

                    default:
                        Console.WriteLine($"Unknown command: {command}. Type 'help' for available commands.");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error executing command: {command}");
                Console.WriteLine($"Error: {ex.Message}");
            }
        }

        private void ShowHelp()
        {
            Console.WriteLine("\nAvailable Commands:");
            Console.WriteLine("  help (h)           - Show this help message");
            Console.WriteLine("  trains (t)         - List all trains with status");
            Console.WriteLine("  machinists (m)     - Show machinist service status");
            Console.WriteLine("  overview (o)       - Show system overview");
            Console.WriteLine("  stop <train>       - Emergency stop a train");
            Console.WriteLine("  add-timetable (at) - Add a timetable entry");
            Console.WriteLine("  list-timetable (lt)- List timetable entries");
            Console.WriteLine("  stats (s)          - Show system statistics");
            Console.WriteLine("  exit (q)           - Exit the application");
        }

        private async Task AddTimetableEntry(string[] parts)
        {
            Console.WriteLine("Add Timetable Entry:");
            Console.Write("Train ID (1-3): ");
            if (!int.TryParse(Console.ReadLine(), out var trainId) || trainId < 1 || trainId > 3)
            {
                Console.WriteLine("Invalid train ID. Must be 1-3.");
                return;
            }

            Console.Write("Source Station ID (1-5): ");
            if (!int.TryParse(Console.ReadLine(), out var sourceId) || sourceId < 1 || sourceId > 5)
            {
                Console.WriteLine("Invalid source station ID. Must be 1-5.");
                return;
            }

            Console.Write("Destination Station ID (1-5): ");
            if (!int.TryParse(Console.ReadLine(), out var destId) || destId < 1 || destId > 5)
            {
                Console.WriteLine("Invalid destination station ID. Must be 1-5.");
                return;
            }

            Console.Write("Date (YYYY-MM-DD): ");
            if (!DateTime.TryParse(Console.ReadLine(), out var date))
            {
                Console.WriteLine("Invalid date format.");
                return;
            }

            Console.Write("Time (HH:mm): ");
            if (!TimeSpan.TryParse(Console.ReadLine(), out var time))
            {
                Console.WriteLine("Invalid time format.");
                return;
            }

            var entry = new TimetableEntries
            {
                Train_DB_ID = trainId,
                SourceStation_DB_ID = sourceId,
                DestinationStation_DB_ID = destId,
                StartDate = date,
                StartTime = time,
                EntryState = EntryState.Scheduled,
                RouteState = RouteState.InTime
            };

            var success = _timetableManager.AddTimetableEntry(entry);
            if (success)
            {
                Console.WriteLine("Timetable entry added successfully.");
            }
            else
            {
                Console.WriteLine("Failed to add timetable entry.");
            }
        }

        private void ListTimetableEntries()
        {
            var entries = _timetableRepository.GetTimetableEntries();
            Console.WriteLine("\nTimetable Entries:");
            Console.WriteLine("ID | Train | Source -> Destination | Scheduled Time | Status");
            Console.WriteLine("---|-------|---------------------|----------------|--------");

            foreach (var entry in entries)
            {
                var scheduledTime = entry.StartDate.Add(entry.StartTime).ToString("yyyy-MM-dd HH:mm");
                Console.WriteLine($"{entry.DB_ID,-2} | {entry.Train?.Name,-5} | {entry.SourceStation?.Name} -> {entry.DestinationStation?.Name,-15} | {scheduledTime,-14} | {entry.EntryState}");
            }
        }

        private void ShowStatistics()
        {
            var stats = _timetableManager.GetStatistics();
            Console.WriteLine("\nSystem Statistics:");
            Console.WriteLine($"Total Trains: {_trackManager.Trains.Count}");
            Console.WriteLine($"Active Machinists: {_machinistFactory.ActiveMachinistCount}");
            Console.WriteLine($"Total Timetable Entries: {stats.TotalEntries}");
            Console.WriteLine($"Scheduled Entries: {stats.ScheduledEntries}");
            Console.WriteLine($"In Transit: {stats.InTransitEntries}");
            Console.WriteLine($"At Station: {stats.AtStationEntries}");
            Console.WriteLine($"Arrived: {stats.ArrivedEntries}");
            Console.WriteLine($"Active Journeys: {stats.ActiveTrains}");
        }
    }
}