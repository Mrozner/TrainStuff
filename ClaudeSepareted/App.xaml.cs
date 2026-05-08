using Microsoft.Extensions.DependencyInjection;
using ClaudeSepareted.Services;
using ClaudeSepareted.Lights;

namespace ClaudeSepareted
{
    public partial class App : Application
    {
        private readonly IServiceProvider _serviceProvider;

        public App(IServiceProvider serviceProvider)
        {
            InitializeComponent();

            _serviceProvider = serviceProvider;
            MainPage = new AppShell();

            // Initialize signals from database and ATP service (fire and forget)
            _ = Task.Run(async () =>
            {
                try
                {
                    // Initialize LightsFromDatabase
                    var lightsFromDatabase = _serviceProvider.GetService<LightsFromDatabase>();
                    if (lightsFromDatabase != null)
                    {
                        await lightsFromDatabase.InitializeSignalsAsync();
                    }
                    else
                    {
                        Console.WriteLine("[App] ERROR: LightsFromDatabase service not found in DI container.");
                    }

                    // Initialize HallStopTest ATP service
                    var hallStopTest = _serviceProvider.GetService<HallStopTest>();
                    if (hallStopTest != null)
                    {
                        await hallStopTest.InitializeAsync();
                        Console.WriteLine("[App] HallStopTest ATP service initialized successfully.");
                    }
                    else
                    {
                        Console.WriteLine("[App] ERROR: HallStopTest service not found in DI container.");
                    }

                    // Initialize TrainLocationRegistry - sync with database to rebuild physical reality
                    var trainLocationRegistry = _serviceProvider.GetService<ITrainLocationRegistry>();
                    if (trainLocationRegistry != null)
                    {
                        await trainLocationRegistry.SyncWithDatabaseAsync();
                        Console.WriteLine("[App] TrainLocationRegistry synced successfully.");
                    }
                    else
                    {
                        Console.WriteLine("[App] ERROR: TrainLocationRegistry service not found in DI container.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[App] Failed to initialize background services: {ex.Message}");
                }
            });
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            Window window = base.CreateWindow(activationState);

            // Handle window destruction to properly dispose services
            window.Destroying += (s, e) =>
            {
                // Dispose MainPageViewModel first (which will dispose VirtualClockViewModel)
                var mainPageViewModel = _serviceProvider.GetService<MainPageViewModel>();
                if (mainPageViewModel != null)
                {
                    mainPageViewModel.Dispose();
                }

                // Stop and dispose services with background operations
                var virtualClock = _serviceProvider.GetService<VirtualClock>();
                if (virtualClock != null)
                {
                    virtualClock.Dispose();
                }

                var mqttService = _serviceProvider.GetService<MqttInfrastructureService>();
                if (mqttService != null && mqttService is IDisposable disposableMqtt)
                {
                    disposableMqtt.Dispose();
                }

                var trackHandler = _serviceProvider.GetService<TrackHandlerService>();
                if (trackHandler != null && trackHandler is IDisposable disposableTrackHandler)
                {
                    disposableTrackHandler.Dispose();
                }
            };

            return window;
        }
    }
}
