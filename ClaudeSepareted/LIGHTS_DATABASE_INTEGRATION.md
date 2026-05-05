# LightsFromDatabase Integration Guide

## Overview
The `LightsFromDatabase` service has been created to control railway light signals based on MÁV F.1 signaling rules using data stored in the database.

## Changes Made

### 1. Updated Files

#### `Lights/LightsFromDatabase.cs`
- **Complete implementation** of the database-driven signal control system
- Queries `V_Lookup_Section_NextSection` view for track topology
- Maps `AllowedSpeed` values to hardware-expected ASCII codes
- Calls `LightController.SetLightSpeedAsync()` to update physical signals

#### `Lights/LightController.cs`
- **Modified constructor** to accept `MqttInfrastructureService` instead of `IMqttClient`
- Updated internal methods to get MQTT client via `_mqttService.GetMqttClient()`
- This aligns with the pattern used by other services (e.g., `RocrailCommandService`)

### 2. Dependency Injection Registration

Add the following registrations to `MauiProgram.cs` **after** the existing service registrations (around line 125):

```csharp
// Light Controller for signal control
builder.Services.AddSingleton<LightController>();

// Lights From Database - reads signal states from database and updates physical signals
// Must be registered after LightController and MqttInfrastructureService
builder.Services.AddSingleton<LightsFromDatabase>();
```

### 3. Initialization During Application Startup

The `LightsFromDatabase` service needs to be initialized during application startup to set all signals to their initial states.

Add the following code to `App.xaml.cs` in the constructor or during app initialization:

```csharp
public App(IServiceProvider serviceProvider)
{
    InitializeComponent();

    // Existing initialization code...

    // Initialize signals from database
    Task.Run(async () =>
    {
        try
        {
            var lightsFromDatabase = serviceProvider.GetRequiredService<LightsFromDatabase>();
            await lightsFromDatabase.InitializeSignalsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[App] Failed to initialize signals from database: {ex.Message}");
        }
    });
}
```

**Alternative**: If you prefer a simpler approach without modifying `App.xaml.cs`, you can call `InitializeSignalsAsync()` from your main view model or any other startup service:

```csharp
// In MainPageViewModel constructor or initialization method
public MainPageViewModel(LightsFromDatabase lightsFromDatabase)
{
    // Existing initialization...

    // Initialize signals (fire and forget)
    _ = Task.Run(async () =>
    {
        try
        {
            await lightsFromDatabase.InitializeSignalsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MainPageViewModel] Failed to initialize signals: {ex.Message}");
        }
    });
}
```

## Speed to ASCII Code Mapping

The system maps speed values to ASCII codes expected by the hardware:

| Speed Value | ASCII Code | Character | Description |
|-------------|------------|-----------|-------------|
| STOP        | 48         | '0'       | 0 km/h (Red signal) |
| SLOW        | 49         | '1'       | 40 km/h |
| MEDIUM      | 50         | '2'       | 80 km/h |
| HIGH        | 51         | '3'       | 120 km/h |
| MAX         | 52         | '4'       | Maximum speed |

## Usage

### Automatic Initialization
Signals are automatically initialized when the application starts (see initialization code above).

### Manual Updates
When the database changes (e.g., speed limits are updated), call:

```csharp
await lightsFromDatabase.UpdateSignalsFromDatabaseAsync();
```

### Example: Updating Signals After Database Changes

```csharp
public class YourService
{
    private readonly LightsFromDatabase _lightsFromDatabase;

    public YourService(LightsFromDatabase lightsFromDatabase)
    {
        _lightsFromDatabase = lightsFromDatabase;
    }

    public async Task UpdateSpeedLimitsAsync()
    {
        // 1. Update speed limits in the database
        // ... your database update logic ...

        // 2. Refresh all signals with new speed data
        await _lightsFromDatabase.UpdateSignalsFromDatabaseAsync();
    }
}
```

## Important Notes

### Separation of Concerns
- This service **does NOT** check for train occupancy
- This service **does NOT** perform pathfinding or block reservations
- It **only** sets signals based on `AllowedSpeed` values stored in the database
- Train occupancy and collision avoidance are handled by other services (`TrackOccupancyService`, `BlockReservationService`, `SignalControllerService`)

### Edge Cases Handled
- **Terminus tracks**: If a section has no `NextSection_DB_ID`, the next speed is automatically set to 48 ('0', STOP)
- **Missing subsections**: If a section has no associated subsections, the signal is skipped with a warning
- **Unknown before sections**: If the previous section cannot be determined, the signal is skipped with a warning

### Database Requirements
The service requires the following database entities to be properly configured:
- `V_Lookup_Section_NextSection` - Track topology and adjacency
- `SubSections` - Speed limits (AllowedSpeed field)
- `Sections` - Section names
- `LookupSectionsSubSections` - Section to SubSection mappings

### Logging
All operations are logged to console with `[LightsFromDatabase]` prefix for easy debugging:
- Initialization status
- Number of adjacency entries found
- Number of subsections and sections loaded
- Individual signal updates with speed codes
- Summary of signals set vs. skipped
- Warnings for missing data
- Errors for processing failures

## Troubleshooting

### Issue: No signals are being set
**Solution**: Check the console logs for warnings. Common causes:
- No data in `V_Lookup_Section_NextSection` view
- Sections have no associated subsections
- `beforeSection` cannot be determined for the signals

### Issue: Wrong speed codes are being sent
**Solution**: Verify the `AllowedSpeed` values in the `SubSections` table. The mapping is:
- "STOP" → 48
- "SLOW" → 49
- "MEDIUM" → 50
- "HIGH" → 51
- "MAX" → 52

### Issue: MQTT commands not reaching hardware
**Solution**:
1. Check `MqttInfrastructureService` is connected
2. Verify broker address in configuration (172.22.2.2:1883)
3. Check `LightController` logs for MQTT send status

## Architecture Alignment

This implementation follows the existing architecture patterns:
- ✅ Uses `IDbContextFactory<ApplicationDbContext>` for database access
- ✅ Registered as Singleton service
- ✅ Uses `MqttInfrastructureService` for MQTT communication
- ✅ Follows dependency injection patterns
- ✅ Comprehensive logging with service-specific prefix
- ✅ Async/await patterns throughout
- ✅ Cancellation token support for graceful shutdown
