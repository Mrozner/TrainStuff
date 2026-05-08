// MauiProgram.cs

using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using ClaudeSepareted;
using ClaudeSepareted.Services;
using ClaudeSepareted.DataAccess;
using ClaudeSepareted.Domain;
using ClaudeSepareted.Lights;

public static class MauiProgram
{
    // Database connection string - centralized for security and maintainability
    // TODO: Move to user secrets or secure configuration in production
    private const string DefaultConnection = "Server=LAPTOP-ANHCTCLU\\SQLEXPRESS;Database=TrainControllerSystem;TrustServerCertificate=True;Trusted_Connection=True;User Id = APPLOGIN; Password=12345";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // 1. Regisztráljuk a DbContext-et a DI konténerben (keményen kódolt connection string)
        builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var fileLogger = sp.GetService<FileLoggingService>();
            options.UseSqlServer(DefaultConnection)
                   .EnableSensitiveDataLogging()
                   .LogTo(msg =>
                   {
                       if (msg.Contains("SELECT") || msg.Contains("UPDATE") || msg.Contains("INSERT") || msg.Contains("DELETE"))
                       {
                           fileLogger?.Log($"[EF CORE SQL] {msg.Replace(Environment.NewLine, " ")}");
                       }
                   }, Microsoft.Extensions.Logging.LogLevel.Information);
        });

        // Register IDbContextFactory for Singleton services that need DbContext
        builder.Services.AddDbContextFactory<ApplicationDbContext>((sp, options) =>
        {
            var fileLogger = sp.GetService<FileLoggingService>();
            options.UseSqlServer(DefaultConnection)
                   .EnableSensitiveDataLogging()
                   .LogTo(msg =>
                   {
                       if (msg.Contains("SELECT") || msg.Contains("UPDATE") || msg.Contains("INSERT") || msg.Contains("DELETE"))
                       {
                           fileLogger?.Log($"[EF CORE SQL] {msg.Replace(Environment.NewLine, " ")}");
                       }
                   }, Microsoft.Extensions.Logging.LogLevel.Information);
        });

        // 2. Konfiguráció manuális létrehozása
        var systemConfig = new ClaudeSepareted.SystemConfiguration
        {
            ConnectionString = DefaultConnection,
            MQTT = new MQTTConfiguration
            {
                Address = "172.22.2.2",
                Port = 1883,
                RocrailIngressTopic = "rocrail/service/client",
                TrackSectionTopic = "rocrail/service/info",
                TrackPositionTopic = "track/info/hall",
                TrackRFIDTopic = "track/info/rfid",
                TrackCommandTopic = "rocrail/service/client",
                TrackSignalTopic = "track/command/signal",
                TrainSignalRequestTopic = "train/signal/request",
                TrainSignalResponseTopic = "train/signal/response",
                TrainSignalChangedTopic = "train/signal/changed",
                TimetableStartRequestTopic = "train/start/request",
                TimetableStatusTopic = "train/status"
            },
            Track = new TrackConfiguration
            {
                MaxOccupiedSections = 100
            },
            Timetable = new TimetableConfiguration
            {
                SchedulerIntervalSeconds = 30,
                MaxArrivedEntriesToKeep = 3
            },
            SignalChangedMessage = "SignalChanged"
        };

        // Regisztráljuk a konfigurációt
        builder.Services.AddSingleton(systemConfig);

        // 2. Regisztráljuk a ViewModel-t
        builder.Services.AddSingleton<MainPageViewModel>();

        // 3. Regisztráljuk a View-t (MainPage) és injektáljuk be a ViewModel-t
        builder.Services.AddSingleton<MainPage>();

        // 4. A konfigurációs osztályok már a systemConfig-ból származnak
        builder.Services.AddSingleton(systemConfig.MQTT);
        builder.Services.AddSingleton(systemConfig.Track);
        builder.Services.AddSingleton(systemConfig.Timetable);

        builder.Services.AddSingleton<ITimetableRepository, TimetableRepository>();

        // Rocrail Command Service for sending Rocrail commands via MQTT
        builder.Services.AddSingleton<RocrailCommandService>();

        // Status notification service
        builder.Services.AddSingleton<StatusNotificationService>();

        // File logging service
        builder.Services.AddSingleton<FileLoggingService>();

        // Register shared dictionaries for pathfinding services (thread-safe)
        builder.Services.AddSingleton<ConcurrentDictionary<int, Platforms>>(new ConcurrentDictionary<int, Platforms>());
        builder.Services.AddSingleton<ConcurrentDictionary<int, Sections>>(new ConcurrentDictionary<int, Sections>());

        // Virtual clock service
        builder.Services.AddSingleton<VirtualClock>();

        // Centralized MQTT Infrastructure Service - SINGLE connection for entire app
        builder.Services.AddSingleton<MqttInfrastructureService>();

        // Unified Pathfinding Service - consolidates all pathfinding functionality
        // Uses in-memory TrackGraph for all routing operations
        builder.Services.AddSingleton<RoutingTableCacheService>();
        builder.Services.AddSingleton<SwitchConfigurationService>();
        builder.Services.AddSingleton<PathfindingService>();

        // Track Graph Factory for advanced pathfinding
        builder.Services.AddSingleton<ITrackGraphFactory, TrackGraphFactory>();

        // Track Occupancy Service - centralized real-time tracking of train positions
        // Must be registered as Singleton to be shared across all services
        builder.Services.AddSingleton<TrackOccupancyService>();

        // Track Handler Service for automated train scheduling
        builder.Services.AddSingleton<TrackHandlerService>();

        // Block Reservation Service for N-block look-ahead reservation system
        builder.Services.AddSingleton<BlockReservationService>();

        // Light Controller for signal control
        builder.Services.AddSingleton<LightController>();

        // Lights From Database - reads signal states from database and updates physical signals
        builder.Services.AddSingleton<LightsFromDatabase>();

        // Travel Time Measurement Service - records and estimates train journey durations
        builder.Services.AddSingleton<TravelTimeMeasurementService>();

        // Hall Sensor ATP (Automatic Train Protection) - stops trains that run red lights
        builder.Services.AddSingleton<HallStopTest>();

        // Train Location Registry - persistent tracking of parked trains across the layout
        builder.Services.AddSingleton<ITrainLocationRegistry, TrainLocationRegistry>();

        // 5. Regisztráljuk az AdminPanelPage-t is
        builder.Services.AddTransient<AdminPanelPage>();

        return builder.Build();
    }
}