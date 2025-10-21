// MauiProgram.cs

using ClaudeSepareted;
using Microsoft.EntityFrameworkCore;
using ClaudeSepareted; // Ahova a DbContext-et tetted

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

        // 1. Regisztráljuk a DbContext-et a DI konténerben
        builder.Services.AddDbContext<ApplicationDbContext>(options =>
            options.UseSqlServer("Server=LAPTOP-ANHCTCLU\\SQLEXPRESS;Database=TrainControllerSystem;TrustServerCertificate=True;Trusted_Connection=True;User Id = APPLOGIN; Password=12345")
        );

        // 2. Regisztráljuk a ViewModel-t
        builder.Services.AddSingleton<MainPageViewModel>();

        // 3. Regisztráljuk a View-t (MainPage) és injektáljuk be a ViewModel-t
        builder.Services.AddSingleton<MainPage>();

        return builder.Build();
    }
}