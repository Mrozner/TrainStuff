using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ClaudeSepareted
{
    public class ConsoleInterface : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;

        public ConsoleInterface(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(2000, stoppingToken);

            Console.WriteLine("\n=== Train Control System Console Interface ===");
            Console.WriteLine("Commands:");
            Console.WriteLine("  status          - Show system status");
            Console.WriteLine("  trains          - List all trains with details");
            Console.WriteLine("  timetable       - Show timetable entries");
            Console.WriteLine("  sections        - Show track sections and occupancy");
            Console.WriteLine("  signals         - Show signal states");
            Console.WriteLine("  start <id>      - Start train with timetable entry ID");
            Console.WriteLine("  start-train <trainId> <stationId> - Manually start train to station");
            Console.WriteLine("  stop <name>     - Stop train by name");
            Console.WriteLine("  speed <name> <STOP|SLOW|MEDIUM|HIGH> - Set train speed");
            Console.WriteLine("  emergency-stop <name> - Emergency stop train");
            Console.WriteLine("  add-entry       - Add new timetable entry");
            Console.WriteLine("  delete-entry <id> - Delete timetable entry by ID");
            Console.WriteLine("  force-schedule  - Force process scheduled trains immediately");
            Console.WriteLine("  clear-arrived   - Clear arrived timetable entries");
            Console.WriteLine("  help            - Show this help");
            Console.WriteLine("  exit            - Exit application");
            Console.WriteLine("===============================================\n");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    Console.Write("> ");
                    var input = await ReadLineAsync(stoppingToken);

                    if (string.IsNullOrWhiteSpace(input))
                        continue;

                    var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var command = parts[0].ToLower();

                    using var scope = _serviceProvider.CreateScope();
                    var trackManager = scope.ServiceProvider.GetRequiredService<TrackManager>();
                    var timetableManager = scope.ServiceProvider.GetRequiredService<TimetableManager>();
                    var trainManager = scope.ServiceProvider.GetRequiredService<TrainManagerService>();

                    switch (command)
                    {
                        case "status":
                            ShowStatus(trackManager, timetableManager);
                            break;

                        case "trains":
                            ShowTrains(trackManager);
                            break;

                        case "timetable":
                            ShowTimetable(timetableManager);
                            break;

                        case "sections":
                            ShowSections(trackManager);
                            break;

                        case "signals":
                            ShowSignals(trackManager);
                            break;

                        case "start":
                            if (parts.Length > 1)
                            {
                                await StartTrainByEntryId(timetableManager, trackManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: start <entryId>");
                            }
                            break;

                        case "start-train":
                            if (parts.Length > 2)
                            {
                                await StartTrainManual(trackManager, parts[1], parts[2]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: start-train <trainId> <stationId>");
                            }
                            break;

                        case "stop":
                            if (parts.Length > 1)
                            {
                                await StopTrain(trackManager, trainManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: stop <trainName>");
                            }
                            break;

                        case "speed":
                            if (parts.Length > 2)
                            {
                                await SetTrainSpeed(trainManager, parts[1], parts[2]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: speed <trainName> <STOP|SLOW|MEDIUM|HIGH>");
                                Console.WriteLine("Available trains:");
                                trainManager.ListTrains();
                            }
                            break;

                        case "emergency-stop":
                            if (parts.Length > 1)
                            {
                                await EmergencyStopTrain(trainManager, parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: emergency-stop <trainName>");
                            }
                            break;

                        case "add-entry":
                            await AddTimetableEntry(timetableManager, trackManager, stoppingToken);
                            break;

                        case "delete-entry":
                            if (parts.Length > 1)
                            {
                                timetableManager.DeleteEntry(parts[1]);
                            }
                            else
                            {
                                Console.WriteLine("Usage: delete-entry <entryId>");
                            }
                            break;

                        case "force-schedule":
                            await timetableManager.ProcessScheduledTrains();
                            Console.WriteLine("Scheduled trains processed");
                            break;

                        case "clear-arrived":
                            ClearArrivedEntries(timetableManager);
                            break;

                        case "help":
                            ShowHelp();
                            break;

                        case "exit":
                            Console.WriteLine("Shutting down...");
                            Environment.Exit(0);
                            break;

                        default:
                            Console.WriteLine($"Unknown command: {command}. Type 'help' for available commands.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error executing command: {ex.Message}");
                    Console.WriteLine($"Stack trace: {ex.StackTrace}");
                }
            }
        }

        private void ShowStatus(TrackManager trackManager, TimetableManager timetableManager)
        {
            Console.WriteLine("\n=== System Status ===");
            Console.WriteLine($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            // Train status
            Console.WriteLine($"\nTrains:");
            Console.WriteLine($"  Total: {trackManager.Trains.Count}");
            Console.WriteLine($"  Waiting: {trackManager.Trains.Count(t => t.State == TrainState.Waiting)}");
            Console.WriteLine($"  Moving: {trackManager.Trains.Count(t => t.State == TrainState.Moving)}");
            Console.WriteLine($"  Stopped: {trackManager.Trains.Count(t => t.State == TrainState.Stopped)}");
            Console.WriteLine($"  PrepareToStop: {trackManager.Trains.Count(t => t.State == TrainState.PrepareToStop)}");

            // Track status
            Console.WriteLine($"\nTrack:");
            Console.WriteLine($"  Sections: {trackManager.Sections.Count}");
            Console.WriteLine($"  SubSections: {trackManager.SubSections.Count}");
            Console.WriteLine($"  Occupied SubSections: {trackManager.OccupiedSubSections.Count}");
            Console.WriteLine($"  Platforms: {trackManager.Platforms.Count}");
            Console.WriteLine($"  Signals: {trackManager.Signals.Count}");

            // Timetable status
            Console.WriteLine($"\nTimetable:");
            Console.WriteLine($"  Total Entries: {timetableManager.Entries.Count}");
            Console.WriteLine($"  Upcoming: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Upcoming)}");
            Console.WriteLine($"  In Progress: {timetableManager.Entries.Count(e => e.EntryState == EntryState.InProgress)}");
            Console.WriteLine($"  Arrived: {timetableManager.Entries.Count(e => e.EntryState == EntryState.Arrived)}");
            Console.WriteLine($"  On Time: {timetableManager.Entries.Count(e => e.RouteState == RouteState.InTime)}");
            Console.WriteLine($"  Delayed: {timetableManager.Entries.Count(e => e.RouteState == RouteState.Delay)}");

            // Next scheduled train
            var nextTrain = timetableManager.Entries
                .Where(e => e.EntryState == EntryState.Upcoming)
                .OrderBy(e => e.StartDate)
                .ThenBy(e => e.StartTime)
                .FirstOrDefault();

            if (nextTrain != null)
            {
                Console.WriteLine($"\nNext Scheduled Train:");
                Console.WriteLine($"  Entry ID: {nextTrain.EntryID}");
                Console.WriteLine($"  Train: #{nextTrain.Train_DB_ID}");
                Console.WriteLine($"  Start: {nextTrain.StartDate:yyyy-MM-dd} {nextTrain.StartTime:hh\\:mm}");
            }

            Console.WriteLine("=====================\n");
        }

        private void ShowTrains(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Trains ===");

            if (!trackManager.Trains.Any())
            {
                Console.WriteLine("No trains available.");
                Console.WriteLine("==============\n");
                return;
            }

            foreach (var train in trackManager.Trains.OrderBy(t => t.Name))
            {
                Console.WriteLine($"\n{train.Name} (ID: {train.DB_ID}):");
                Console.WriteLine($"  State: {train.State}");
                Console.WriteLine($"  Speed: {train.CurrentSpeed} (Max: {train.MaxSpeed})");
                Console.WriteLine($"  Direction: {(train.Direction ? "Forward" : "Backward")}");
                Console.WriteLine($"  Active: {(train.IsActive ? "Yes" : "No")}");

                // Location information
                if (train.SubSection != null)
                {
                    Console.WriteLine($"  Current Location: {train.SubSection.Name}");
                    if (train.Section != null)
                    {
                        Console.WriteLine($"  Section: {train.Section.Name}");
                    }
                }
                else
                {
                    Console.WriteLine($"  Current Location: Unknown");
                }

                // Source and destination
                if (train.Source != null)
                {
                    Console.WriteLine($"  Source: {train.Source.Name}");
                }
                if (train.Destination != null)
                {
                    Console.WriteLine($"  Destination: {train.Destination.Name}");
                }

                // Next movement
                if (train.NextSubsection_DB_ID.HasValue)
                {
                    var nextSub = trackManager.SubSections.FirstOrDefault(s => s.DB_ID == train.NextSubsection_DB_ID);
                    if (nextSub != null)
                    {
                        Console.WriteLine($"  Next Subsection: {nextSub.Name}");
                    }
                }

                // Entry information
                if (!string.IsNullOrEmpty(train.CurrentEntryID))
                {
                    Console.WriteLine($"  Current Entry: {train.CurrentEntryID}");
                }

                // Locked resources
                if (train.LockedSections.Any())
                {
                    Console.WriteLine($"  Locked Sections: {string.Join(", ", train.LockedSections)}");
                }
                if (train.LockedSwitches.Any())
                {
                    Console.WriteLine($"  Locked Switches: {string.Join(", ", train.LockedSwitches.Select(s => s.Name))}");
                }
            }
            Console.WriteLine("==============\n");
        }

        private void ShowTimetable(TimetableManager timetableManager)
        {
            Console.WriteLine("\n=== Timetable ===");

            if (!timetableManager.Entries.Any())
            {
                Console.WriteLine("No timetable entries available.");
                Console.WriteLine("=================\n");
                return;
            }

            var entriesByState = timetableManager.Entries
                .OrderBy(e => e.StartDate)
                .ThenBy(e => e.StartTime)
                .GroupBy(e => e.EntryState);

            foreach (var stateGroup in entriesByState)
            {
                Console.WriteLine($"\n{stateGroup.Key}:");
                foreach (var entry in stateGroup)
                {
                    Console.WriteLine($"  {entry.EntryID}:");
                    Console.WriteLine($"    Train: #{entry.Train_DB_ID}");
                    Console.WriteLine($"    From Station: #{entry.SourceStation_DB_ID}");
                    Console.WriteLine($"    To Station: #{entry.DestinationStation_DB_ID}");
                    Console.WriteLine($"    Start: {entry.StartDate:yyyy-MM-dd} {entry.StartTime:hh\\:mm}");
                    Console.WriteLine($"    State: {entry.EntryState}");
                    Console.WriteLine($"    Route: {entry.RouteState}");

                    if (entry.ArrivedTime.HasValue)
                    {
                        Console.WriteLine($"    Arrived: {entry.ArrivedTime.Value:yyyy-MM-dd HH:mm:ss}");
                    }

                    if (entry.EntryState == EntryState.InProgress)
                    {
                        Console.WriteLine($"    Status: Active - monitoring progress");
                    }
                }
            }
            Console.WriteLine("=================\n");
        }

        private void ShowSections(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Track Sections ===");

            foreach (var section in trackManager.Sections.OrderBy(s => s.Name))
            {
                Console.WriteLine($"\n{section.Name} (ID: {section.DB_ID}):");
                Console.WriteLine($"  Active: {(section.isActive ? "Yes" : "No")}");
                Console.WriteLine($"  SubSections: {section.SubSections.Count}");

                foreach (var subSection in section.SubSections.OrderBy(ss => ss.Name))
                {
                    var isOccupied = trackManager.OccupiedSubSections.Any(oss => oss.DB_ID == subSection.DB_ID);
                    var occupyingTrain = trackManager.Trains.FirstOrDefault(t => t.SubSection?.DB_ID == subSection.DB_ID);

                    Console.WriteLine($"    {subSection.Name} (ID: {subSection.DB_ID}):");
                    Console.WriteLine($"      Allowed Speed: {subSection.AllowedSpeed}");
                    Console.WriteLine($"      Occupied: {(isOccupied ? "Yes" : "No")}");

                    if (isOccupied && occupyingTrain != null)
                    {
                        Console.WriteLine($"      Occupied By: {occupyingTrain.Name}");
                    }
                }
            }
            Console.WriteLine("======================\n");
        }

        private void ShowSignals(TrackManager trackManager)
        {
            Console.WriteLine("\n=== Signals ===");

            if (!trackManager.Signals.Any())
            {
                Console.WriteLine("No signals available.");
                Console.WriteLine("==================\n");
                return;
            }

            foreach (var signal in trackManager.Signals.OrderBy(s => s.ID))
            {
                Console.WriteLine($"  {signal.ID}:");
                Console.WriteLine($"    Current Speed: {signal.Speed}");
                Console.WriteLine($"    Next Speed: {signal.NextSpeed?.ToString() ?? "None"}");

                // Find which train this signal is for
                var relatedTrain = trackManager.Trains.FirstOrDefault(t =>
                    trackManager.Objects.Any(o =>
                        o.ObjectID == signal.ID &&
                        o.SubSection_DB_ID == t.SubSection?.DB_ID));

                if (relatedTrain != null)
                {
                    Console.WriteLine($"    Related Train: {relatedTrain.Name}");
                }
            }
            Console.WriteLine("==================\n");
        }

        private async Task StartTrainByEntryId(TimetableManager timetableManager, TrackManager trackManager, string entryId)
        {
            var entry = timetableManager.Entries.FirstOrDefault(e => e.EntryID == entryId);
            if (entry != null)
            {
                Console.WriteLine($"Starting train for entry: {entryId}");
                trackManager.TryStartTrain(entry);

                // Wait a moment and check result
                await Task.Delay(1000);

                var updatedEntry = timetableManager.Entries.FirstOrDefault(e => e.EntryID == entryId);
                if (updatedEntry?.EntryState == EntryState.InProgress)
                {
                    Console.WriteLine($"Train started successfully for entry: {entryId}");
                }
                else
                {
                    Console.WriteLine($"Failed to start train for entry: {entryId}");
                }
            }
            else
            {
                Console.WriteLine($"Entry not found: {entryId}");
            }
        }

        private async Task StartTrainManual(TrackManager trackManager, string trainIdStr, string stationIdStr)
        {
            if (int.TryParse(trainIdStr, out int trainId) && int.TryParse(stationIdStr, out int stationId))
            {
                Console.WriteLine($"Manually starting train {trainId} to station {stationId}");

                // Debug information before starting
                var train = trackManager.Trains.FirstOrDefault(t => t.DB_ID == trainId);
                if (train != null)
                {
                    Console.WriteLine($"Train found: {train.Name}, State: {train.State}, Current Location: {train.SubSection?.Name}");
                }
                else
                {
                    Console.WriteLine($"Train not found with ID: {trainId}");
                    return;
                }

                bool success = trackManager.ManualStartTrain(trainId, stationId);

                if (success)
                {
                    Console.WriteLine($"✓ Train {trainId} started successfully to station {stationId}");
                }
                else
                {
                    Console.WriteLine($"✗ Failed to start train {trainId} to station {stationId}");
                    Console.WriteLine("Possible solutions:");
                    Console.WriteLine("  1. Check if the train is in 'Waiting' state");
                    Console.WriteLine("  2. Check if destination station has available platforms");
                    Console.WriteLine("  3. Check if there's a valid route to the destination");
                    Console.WriteLine("  4. Use 'sections' command to see track layout");
                    Console.WriteLine("  5. Use 'trains' command to see current train states");
                }
            }
            else
            {
                Console.WriteLine("Invalid train ID or station ID. Please use numeric values.");
                Console.WriteLine("Available trains:");
                foreach (var train in trackManager.Trains)
                {
                    Console.WriteLine($"  ID: {train.DB_ID}, Name: {train.Name}, State: {train.State}");
                }
            }
        }

        private async Task StopTrain(TrackManager trackManager, TrainManagerService trainManager, string trainName)
        {
            var train = trackManager.Trains.FirstOrDefault(t => t.Name == trainName);
            if (train != null)
            {
                Console.WriteLine($"Stopping train: {trainName}");
                train.State = TrainState.Stopped;
                trainManager.UpdateTrainSpeed(trainName, Speed.STOP);

                // Also update signals
                var signal = trackManager.GetSignalForTrain(trainName);
                if (signal != null)
                {
                    signal.Speed = Speed.STOP;
                    signal.NextSpeed = null;
                }

                Console.WriteLine($"Train {trainName} stopped");
            }
            else
            {
                Console.WriteLine($"Train not found: {trainName}");
            }
        }

        private async Task SetTrainSpeed(TrainManagerService trainManager, string trainName, string speedStr)
        {
            try
            {
                // First check if train exists
                var train = trainManager.GetTrain(trainName);
                if (train == null)
                {
                    Console.WriteLine($"Train not found: {trainName}");
                    Console.WriteLine("Available trains:");
                    trainManager.ListTrains();
                    return;
                }

                if (Enum.TryParse<Speed>(speedStr, true, out Speed speed))
                {
                    Console.WriteLine($"Setting speed for {trainName} to {speed}");
                    trainManager.UpdateTrainSpeed(trainName, speed);

                    // Verify the change
                    await Task.Delay(100);
                    var updatedTrain = trainManager.GetTrain(trainName);
                    if (updatedTrain != null)
                    {
                        Console.WriteLine($"✓ Speed set to {updatedTrain.CurrentSpeed} for train {trainName}");
                    }
                }
                else
                {
                    Console.WriteLine($"Invalid speed: {speedStr}. Use STOP, SLOW, MEDIUM, or HIGH.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting speed for {trainName}: {ex.Message}");
            }
        }

        private async Task EmergencyStopTrain(TrainManagerService trainManager, string trainName)
        {
            try
            {
                var train = trainManager.GetTrain(trainName);
                if (train == null)
                {
                    Console.WriteLine($"Train not found: {trainName}");
                    return;
                }

                Console.WriteLine($"EMERGENCY STOP for {trainName}");

                // Use the train controller for emergency stop
                trainManager.UpdateTrainSpeed(trainName, Speed.STOP);

                // Also update train state
                train.State = TrainState.Stopped;

                Console.WriteLine($"✓ Emergency stop executed for {trainName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during emergency stop for {trainName}: {ex.Message}");
            }
        }

        private async Task AddTimetableEntry(TimetableManager timetableManager, TrackManager trackManager, CancellationToken ct)
        {
            try
            {
                Console.WriteLine("\n=== Add Timetable Entry ===");

                // Show available trains
                Console.WriteLine("\nAvailable Trains:");
                foreach (var train in trackManager.Trains.Where(t => t.IsActive && t.State == TrainState.Waiting))
                {
                    Console.WriteLine($"  ID: {train.DB_ID}, Name: {train.Name}, Current Location: {train.SubSection?.Name ?? "Unknown"}");
                }

                Console.Write("\nTrain ID: ");
                var trainIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(trainIdInput, out int trainId) || !trackManager.Trains.Any(t => t.DB_ID == trainId))
                {
                    Console.WriteLine("Invalid train ID.");
                    return;
                }

                // Show available stations
                Console.WriteLine("\nAvailable Stations (from database):");
                // You would typically load these from database
                Console.WriteLine("  (Note: Enter station IDs from your database)");

                Console.Write("Source Station ID: ");
                var sourceIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(sourceIdInput, out int sourceId))
                {
                    Console.WriteLine("Invalid source station ID.");
                    return;
                }

                Console.Write("Destination Station ID: ");
                var destIdInput = await ReadLineAsync(ct);
                if (!int.TryParse(destIdInput, out int destId))
                {
                    Console.WriteLine("Invalid destination station ID.");
                    return;
                }

                // Date and time
                Console.Write("Start Date (yyyy-MM-dd) [or 'now' for current time]: ");
                var dateInput = await ReadLineAsync(ct);
                DateTime startDate;
                TimeSpan startTime;

                if (dateInput.Trim().ToLower() == "now")
                {
                    startDate = DateTime.Now.Date;
                    startTime = DateTime.Now.TimeOfDay;
                }
                else
                {
                    if (!DateTime.TryParse(dateInput, out startDate))
                    {
                        Console.WriteLine("Invalid date format. Using today's date.");
                        startDate = DateTime.Now.Date;
                    }

                    Console.Write("Start Time (HH:mm) [or 'now' for current time]: ");
                    var timeInput = await ReadLineAsync(ct);
                    if (timeInput.Trim().ToLower() == "now")
                    {
                        startTime = DateTime.Now.TimeOfDay;
                    }
                    else if (!TimeSpan.TryParse(timeInput, out startTime))
                    {
                        Console.WriteLine("Invalid time format. Using current time.");
                        startTime = DateTime.Now.TimeOfDay;
                    }
                }

                var entry = new TimetableEntries
                {
                    Train_DB_ID = trainId,
                    SourceStation_DB_ID = sourceId,
                    DestinationStation_DB_ID = destId,
                    StartDate = startDate,
                    StartTime = startTime,
                    EntryState = EntryState.Upcoming,
                    RouteState = RouteState.InTime
                };

                bool success = timetableManager.AddEntry(entry);
                if (success)
                {
                    Console.WriteLine($"\nEntry added successfully! Entry ID: {entry.EntryID}");

                    // Check if should start immediately
                    var now = DateTime.Now;
                    var scheduledTime = startDate.Add(startTime);
                    if (scheduledTime <= now.AddMinutes(1))
                    {
                        Console.WriteLine("Entry is scheduled to start now or in the past. Starting train immediately...");
                        trackManager.TryStartTrain(entry);
                    }
                    else
                    {
                        Console.WriteLine($"Train will start automatically at {scheduledTime:yyyy-MM-dd HH:mm}");
                    }
                }
                else
                {
                    Console.WriteLine("Failed to add entry to database.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error adding entry: {ex.Message}");
            }
        }

        private void ClearArrivedEntries(TimetableManager timetableManager)
        {
            var arrivedEntries = timetableManager.Entries.Where(e => e.EntryState == EntryState.Arrived).ToList();

            if (!arrivedEntries.Any())
            {
                Console.WriteLine("No arrived entries to clear.");
                return;
            }

            Console.WriteLine($"Found {arrivedEntries.Count} arrived entries. Clearing...");

            foreach (var entry in arrivedEntries)
            {
                timetableManager.DeleteEntry(entry.EntryID);
            }

            Console.WriteLine("Arrived entries cleared.");
        }

        private void ShowHelp()
        {
            Console.WriteLine("\n=== Available Commands ===");
            Console.WriteLine("status          - Display system status including trains, track, and timetable");
            Console.WriteLine("trains          - List all trains with detailed information");
            Console.WriteLine("timetable       - Show all timetable entries grouped by state");
            Console.WriteLine("sections        - Display track sections and their occupancy");
            Console.WriteLine("signals         - Show current signal states");
            Console.WriteLine("start <id>      - Start a train using its timetable entry ID");
            Console.WriteLine("start-train <trainId> <stationId> - Manually start a train to a station");
            Console.WriteLine("stop <name>     - Stop a specific train by name");
            Console.WriteLine("speed <name> <speed> - Set train speed (STOP, SLOW, MEDIUM, HIGH)");
            Console.WriteLine("add-entry       - Add a new timetable entry (interactive)");
            Console.WriteLine("delete-entry <id> - Delete a timetable entry by ID");
            Console.WriteLine("force-schedule  - Process scheduled trains immediately");
            Console.WriteLine("clear-arrived   - Remove all arrived timetable entries");
            Console.WriteLine("help            - Show this help message");
            Console.WriteLine("exit            - Exit the application");
            Console.WriteLine("==========================\n");
        }

        private async Task<string> ReadLineAsync(CancellationToken ct)
        {
            return await Task.Run(() => Console.ReadLine(), ct) ?? string.Empty;
        }
    }
}