// MauiProgram.cs

using Microsoft.EntityFrameworkCore;
using ClaudeSepareted;
using ClaudeSepareted.Services;
using ClaudeSepareted.DataAccess;

public static class MauiProgram
{
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
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer("Server=LAPTOP-ANHCTCLU\\SQLEXPRESS;Database=TrainControllerSystem;TrustServerCertificate=True;Trusted_Connection=True;User Id = APPLOGIN; Password=12345")
        );

        // 2. Konfiguráció manuális létrehozása
        var systemConfig = new ClaudeSepareted.SystemConfiguration
        {
            ConnectionString = "Server=LAPTOP-ANHCTCLU\\SQLEXPRESS;Database=TrainControllerSystem;TrustServerCertificate=True;Trusted_Connection=True;User Id = APPLOGIN; Password=12345",
            MQTT = new MQTTConfiguration
            {
                Address = "172.22.2.2",
                Port = 1883,
                TrackSectionTopic = "rocrail/service/info/fb",
                TrackPositionTopic = "track/info/hall",
                TrackRFIDTopic = "track/info/rfid",
                TrackCommandTopic = "rocrail/service/client",
                TrackSignalTopic = "track/command/signal",
                SwitchCommandTopic = "rocrail/service/client",
                TrainSignalRequestTopic = "train/signal/request",
                TrainSignalResponseTopic = "train/signal/response",
                TrainSignalChangedTopic = "train/signal/changed",
                TrainSpeedCommandTopic = "rocrail/service/client",
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

        // Admin MQTT service for speed control
        builder.Services.AddSingleton<AdminMQTTService>();

        // Status notification service
        builder.Services.AddSingleton<StatusNotificationService>();

        // Virtual clock service
        builder.Services.AddSingleton<VirtualClock>();

        // Admin MQTT service for speed control
        builder.Services.AddSingleton<AdminMQTTService>();

        // Unified Pathfinding Service - consolidates all pathfinding functionality
        builder.Services.AddSingleton<UnifiedPathfindingService>();

        // Track Graph Factory for advanced pathfinding
        builder.Services.AddSingleton<ITrackGraphFactory, TrackGraphFactory>();

        // Track Handler Service for automated train scheduling
        builder.Services.AddSingleton<TrackHandlerService>();

        // 5. Regisztráljuk az AdminPanelPage-t is
        builder.Services.AddTransient<AdminPanelPage>();

        return builder.Build();
    }
}