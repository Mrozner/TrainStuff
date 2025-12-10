# Railway Pathfinder Usage Example

This document demonstrates how to use the implemented pathfinding system in your train control application.

## Overview

The pathfinding system consists of several key components:
- `RailwayPathfinderService` - Core pathfinding algorithm
- `TrackGraph` - Graph representation of the railway network
- `TrainPathfinderIntegration` - High-level integration service
- `PathOptimizer` - Path optimization utilities

## Basic Usage

### 1. Planning a Train Movement

```csharp
// Inject the TrainPathfinderIntegration service into your class
public class YourTrainController
{
    private readonly TrainPathfinderIntegration _pathfinderIntegration;

    public YourTrainController(TrainPathfinderIntegration pathfinderIntegration)
    {
        _pathfinderIntegration = pathfinderIntegration;
    }

    // Plan movement for a specific train
    public async Task<bool> MoveTrainToDestination(int trainDbId)
    {
        try
        {
            // This will:
            // 1. Load the train and its current/destination positions
            // 2. Calculate the optimal path avoiding conflicts
            // 3. Configure required switches
            // 4. Lock the necessary sections
            // 5. Update the train state to Moving
            var success = await _pathfinderIntegration.PlanTrainMovementAsync(trainDbId);

            if (success)
            {
                Console.WriteLine("Train movement planned successfully");
            }
            else
            {
                Console.WriteLine("Failed to plan train movement - no path available or conflicts detected");
            }

            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error planning train movement: {ex.Message}");
            return false;
        }
    }
}
```

### 2. Finding Available Platform

```csharp
// Find an available platform at a destination station
public async Task<int?> FindPlatformForTrain(int trainDbId, int destinationStationId)
{
    var platformId = await _pathfinderIntegration.FindAvailablePlatformForTrainAsync(trainDbId, destinationStationId);

    if (platformId.HasValue)
    {
        Console.WriteLine($"Found available platform: {platformId.Value}");
        return platformId.Value;
    }
    else
    {
        Console.WriteLine("No available platforms at destination");
        return null;
    }
}
```

### 3. Handling Train Arrival

```csharp
// Handle train arrival at a platform
public async Task<bool> HandleTrainArrival(int trainDbId, int platformDbId)
{
    var success = await _pathfinderIntegration.HandleTrainArrivalAsync(trainDbId, platformDbId);

    if (success)
    {
        Console.WriteLine($"Train {trainDbId} successfully arrived at platform {platformDbId}");
    }

    return success;
}
```

### 4. Handling Train Departure

```csharp
// Handle train departure from a platform to a destination
public async Task<bool> HandleTrainDeparture(int trainDbId, int destinationPlatformDbId)
{
    var success = await _pathfinderIntegration.HandleTrainDepartureAsync(trainDbId, destinationPlatformDbId);

    if (success)
    {
        Console.WriteLine($"Train {trainDbId} departure planned to platform {destinationPlatformDbId}");
    }

    return success;
}
```

## Advanced Usage

### Direct Pathfinding Service Access

For more control, you can use the `RailwayPathfinderService` directly:

```csharp
public class AdvancedTrainController
{
    private readonly RailwayPathfinderService _pathfinder;
    private readonly ApplicationDbContext _dbContext;

    public AdvancedTrainController(RailwayPathfinderService pathfinder, ApplicationDbContext dbContext)
    {
        _pathfinder = pathfinder;
        _dbContext = dbContext;
    }

    public async Task<List<int>> CalculateCustomPath(int trainDbId, List<int> otherTrainIds)
    {
        // Load the train
        var train = await _dbContext.Trains
            .Include(t => t.SubSection)
            .Include(t => t.Destination)
            .FirstOrDefaultAsync(t => t.DB_ID == trainDbId);

        if (train == null) return null;

        // Load other trains for conflict detection
        var otherTrains = await _dbContext.Trains
            .Include(t => t.SubSection)
            .Include(t => t.Destination)
            .Where(t => otherTrainIds.Contains(t.DB_ID))
            .ToListAsync();

        // Initialize pathfinder
        await _pathfinder.InitializeAsync();

        // Calculate path
        var path = await _pathfinder.CalculatePathAsync(train, otherTrains);

        return path;
    }

    public async Task<List<(string SwitchName, string RequiredPosition)>> GetRequiredSwitches(List<int> path, bool direction)
    {
        return await _pathfinder.GetRequiredSwitchesAsync(path, direction);
    }

    public async Task<bool> ValidatePath(List<int> path, Train train, List<Train> otherTrains)
    {
        return await _pathfinder.IsPathViableAsync(path, train, otherTrains);
    }
}
```

## Integration with Existing Services

### Integration with TrackHandlerService

The pathfinding service is already integrated into the `TrackHandlerService`. You can access it through dependency injection:

```csharp
public class CustomTrackHandler
{
    private readonly TrackHandlerService _trackHandler;
    private readonly RailwayPathfinderService _pathfinder;

    public CustomTrackHandler(TrackHandlerService trackHandler, RailwayPathfinderService pathfinder)
    {
        _trackHandler = trackHandler;
        _pathfinder = pathfinder;
    }

    // Your custom logic here
}
```

### Integration with MQTT Services

The pathfinding system provides the required switch configurations that you can then apply using your MQTT services:

```csharp
public async Task ConfigureSwitchesForPath(List<(string SwitchName, string RequiredPosition)> requiredSwitches)
{
    foreach (var (switchName, position) in requiredSwitches)
    {
        // Use your existing MQTT service to configure switches
        // await _mqttService.SetSwitchState(switchName, position);
        Console.WriteLine($"Configuring switch {switchName} to {position}");
    }
}
```

## Path Optimization

The system includes path optimization to remove unnecessary detours:

```csharp
// Get optimized path
var originalPath = new List<int> { 1, 2, 3, 4, 5 };
var optimizedPath = PathOptimizer.OptimizePath(originalPath, trackGraph);

Console.WriteLine($"Original path length: {PathOptimizer.CalculatePathLength(originalPath)}");
Console.WriteLine($"Optimized path length: {PathOptimizer.CalculatePathLength(optimizedPath)}");
```

## Database Requirements

The pathfinding system uses the existing database structure:

- **V_Lookup_Section_NextSection** view for track connections
- **Sections** table for track sections
- **SubSections** table for track subsections
- **Platforms** table for station platforms
- **Trains** table for train information

Make sure these tables and views are properly populated with your railway network data.

## Error Handling

The pathfinding system includes comprehensive error handling:

```csharp
try
{
    var success = await _pathfinderIntegration.PlanTrainMovementAsync(trainDbId);
    // Handle success
}
catch (Exception ex)
{
    Console.WriteLine($"Pathfinding error: {ex.Message}");
    // Handle error - maybe train has no current subsection or destination
}
```

## Performance Considerations

- The pathfinding service is initialized once and cached for performance
- Path calculations use efficient graph algorithms (BFS-based)
- Switch configurations are cached to avoid repeated database queries
- The system handles concurrent train movements safely with conflict detection

## Logging

The pathfinding system provides detailed console logging for debugging:

- Path calculation progress
- Conflict detection results
- Switch configuration requirements
- Error messages and warnings

Monitor the console output to understand the pathfinding process and troubleshoot issues.