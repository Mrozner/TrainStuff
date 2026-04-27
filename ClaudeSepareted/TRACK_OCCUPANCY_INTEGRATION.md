# Track Occupancy Integration Guide

## Overview

This guide explains how the new **TrackOccupancyService** integrates with the existing collision avoidance system to implement rolling-block (dynamic) train control instead of static reservation-based control.

## Architecture Changes

### Before (Static Reservation System)
```
1. Route planned → Switches reserved for ENTIRE journey
2. Train starts movement
3. Train reaches destination → ALL switches released
4. Next train can use the route
```

### After (Dynamic Rolling-Block System)
```
1. Route planned → Switches reserved for ENTIRE journey (initial)
2. Train position registered → Real-time tracking begins
3. Train moves section by section → Switches released as train passes
4. Signals update automatically based on position
5. Multiple trains can follow each other safely
```

## New Files Created

### 1. `TrackOccupancyService.cs`
**Purpose**: Central tracking service for real-time train positions

**Key Features**:
- `ConcurrentDictionary<string, string>` for thread-safe occupancy tracking
- MQTT subscription to Rocrail `<fb>` messages
- `OnTrainMoved` event fires on position changes
- Automatic train identification via adjacency queries
- Support for train registration and querying

### 2. `TrainMovedEventArgs.cs`
**Purpose**: Event arguments for train movement notifications

**Properties**:
- `TrainId` - Which train moved
- `PreviousSubSection` - Where it was (null for initial appearance)
- `CurrentSubSection` - Where it is now
- `Direction` - Movement direction

### 3. `SignalControllerService.cs`
**Purpose**: Traffic light control bridge between occupancy tracking and physical signals

**Key Features**:
- Subscribes to `TrackOccupancyService.OnTrainMoved`
- Calls `ObjectsLibrary.GetLightInfo()` for signal lookup
- Sets RED signals behind trains (rear protection)
- Sets GREEN signals ahead of trains (clearance)
- Look-ahead logic for predictive signal control

## Modified Files

### 1. `SwitchConfigurationService.cs`
**New Method**: `ReleaseSwitchesBehindTrainAsync(string trainName, string clearedSubSection)`

**Purpose**: Implements rolling lock release

**How It Works**:
```csharp
// Step 1: Query database for subsection
var subsection = await _dbContext.SubSections
    .FirstOrDefaultAsync(ss => ss.Name == clearedSubSection);

// Step 2: Find switch constraints for this subsection
var connections = await _dbContext.VLookupSectionNextSection
    .Where(c => c.Section_DB_ID == subsection.DB_ID)
    .ToListAsync();

// Step 3: Parse switch constraints and release if owned by train
foreach (var connection in connections)
{
    var switches = ParseSwitchConstraints(connection.SwitchConstraints);
    foreach (var switchName in switches)
    {
        if (_lockedSwitches.TryGetValue(switchName, out var owner) && owner == trainName)
        {
            _lockedSwitches.TryRemove(switchName, out _);
        }
    }
}
```

**Safety Mechanisms**:
1. **Database Validation**: Only releases switches tied to the cleared section
2. **Ownership Verification**: Checks if train actually owns the switch
3. **Atomic Operations**: Thread-safe removal from `_lockedSwitches`
4. **No Premature Release**: Switches held by other trains are never released

### 2. `TrainManagerService.cs`
**New Dependency**: `TrackOccupancyService trackOccupancyService`

**New Method**: `RegisterTrainPositionAsync()`

**Integration Point**: Called in `Run()` method before train starts moving:
```csharp
// Ensure MQTT is ready
if (!await _mqttService.InitializeAsync())
    return;

// CRITICAL: Register position BEFORE movement
await RegisterTrainPositionAsync();

// Now start the train
if (!await _speedController.StartTrainAsync())
    return;
```

**What It Does**:
```csharp
private async Task RegisterTrainPositionAsync()
{
    // Derive starting position from timetable entry
    var startingSubSection = _timetableEntry.SourcePlatform?.SubSection?.Name;

    // Register with occupancy service
    _trackOccupancyService.RegisterTrainPosition(
        _train.Name,
        startingSubSection,
        _train.Direction
    );
}
```

## Dependency Injection Registration

Add these services to `MauiProgram.cs`:

```csharp
// In ConfigureServices method

// 1. Track Occupancy Service (must be registered first)
services.AddSingleton<TrackOccupancyService>();

// 2. Signal Controller Service (depends on TrackOccupancyService)
services.AddSingleton<SignalControllerService>();

// 3. Switch Configuration Service (already registered, now with ILogger)
// Update registration to include logger if not already present:
services.AddSingleton<SwitchConfigurationService>();

// 4. TrainManagerService (already registered, now with optional TrackOccupancyService)
// No changes needed - TrackOccupancyService is optional in constructor
```

## How the Integration Works

### Complete Flow Example

```
1. TIMETABLE DISPATCH (TrackHandlerService)
   ↓
   Finds due train entry
   ↓
   Plans route: Platform A → Platform B
   ↓
   Tries to reserve switches
   ↓
   Switches locked successfully
   ↓

2. TRAIN MANAGER CREATION
   ↓
   TrainManagerService instantiated
   ↓
   Run() method starts
   ↓
   RegisterTrainPositionAsync() called
   ↓
   TrackOccupancyService.RegisterTrainPosition("Train1", "section_5", true)
   ↓
   OnTrainMoved event fired (initial registration)
   ↓

3. SIGNAL CONTROLLER ACTIVATION
   ↓
   SignalControllerService receives OnTrainMoved event
   ↓
   ObjectsLibrary.GetLightInfo("section_5", null, true)
   ↓
   Sets GREEN signal for section_5 (train can proceed)
   ↓

4. TRAIN MOVEMENT STARTS
   ↓
   TrainSpeedController.StartTrainAsync()
   ↓
   Train begins moving from section_5
   ↓

5. REAL-TIME TRACKING (TrackOccupancyService)
   ↓
   Rocrail sends: <fb id="section_6" state="true"/>
   ↓
   TrackOccupancyService.ProcessRocrailFeedbackAsync()
   ↓
   Identifies Train1 moved from section_5 → section_6
   ↓
   Updates occupancy map: {section_6: "Train1"}
   ↓
   Fires OnTrainMoved event
   ↓

6. DYNAMIC SIGNAL UPDATE
   ↓
   SignalControllerService receives event
   ↓
   Sets RED behind: LightsToRed(section_5, section_4, true)
   ↓
   Sets GREEN ahead: LightsToGreen(section_6, section_5, true)
   ↓

7. ROLLING SWITCH RELEASE
   ↓
   SwitchConfigurationService.ReleaseSwitchesBehindTrainAsync("Train1", "section_5")
   ↓
   Queries database for switches tied to section_5
   ↓
   Releases: switch1, switch2 (owned by Train1)
   ↓
   Keeps: switch3 (needed for remaining route)
   ↓

8. NEXT TRAIN CAN FOLLOW
   ↓
   Train2 can now reserve and enter section_5
   ↓
   Protected by RED signal at section_5
   ↓
   Train2 follows Train1 at safe distance
```

## Collision Prevention Layers

### Layer 1: Initial Reservation (Existing)
- Switches locked for entire route before movement
- Prevents route conflicts at dispatch time

### Layer 2: Real-Time Occupancy (New)
- TrackOccupancyService tracks actual positions
- Hardware feedback via Rocrail `<fb>` messages
- No train can occupy a section already occupied

### Layer 3: Dynamic Signaling (New)
- RED signals protect train rears
- GREEN signals indicate clear path ahead
- Automatic signal updates based on movement

### Layer 4: Rolling Lock Release (New)
- Switches released as train passes
- Frees resources for following trains
- Maintains safety with database validation

## Testing the Integration

### 1. Manual Testing via Admin Panel
```csharp
// Register a train manually
trackOccupancyService.RegisterTrainPosition("TestTrain", "section_1", true);

// Check occupancy
var isOccupied = trackOccupancyService.IsSectionOccupied("section_1"); // true
var trainName = trackOccupancyService.GetTrainInSection("section_1"); // "TestTrain"

// Simulate movement
// (Hardware sends <fb id="section_2" state="true"/>)
// Check again
var trainIn2 = trackOccupancyService.GetTrainInSection("section_2"); // "TestTrain"
var trainIn1 = trackOccupancyService.GetTrainInSection("section_1"); // null
```

### 2. Signal Control Testing
```csharp
// Manually trigger signal change
await signalController.SetManualSignalAsync("section_2", "section_1", true, false);

// Check console output for MQTT commands
// Should see: [LÁMPA] section_1 -> section_2 | Mega: 1, ID: 1 -> ZÖLD
```

### 3. Rolling Release Testing
```csharp
// Reserve switches for train
var switches = new List<string> {"switch1", "switch2", "switch3"};
switchConfigService.TryReserveSwitches(switches, "Train1");

// Train moves past section_5
await switchConfigService.ReleaseSwitchesBehindTrainAsync("Train1", "section_5");

// Check that only safe switches are released
// switch1 and switch2 released (tied to section_5)
// switch3 still locked (needed for remaining route)
```

## Troubleshooting

### Issue: Trains Not Being Tracked
**Solution**:
1. Ensure `TrackOccupancyService.InitializeAsync()` is called
2. Check MQTT subscription to "rocrail/service/info" topic
3. Verify Rocrail is sending `<fb>` messages
4. Check train registration happens before movement

### Issue: Signals Not Updating
**Solution**:
1. Ensure `SignalControllerService.Initialize()` is called
2. Check subscription to `TrackOccupancyService.OnTrainMoved`
3. Verify `ObjectsLibrary.GetLightInfo()` returns valid data (mega != -1)
4. Check MQTT client is connected in `Lights` class

### Issue: Switches Released Prematurely
**Solution**:
1. Verify database query in `ReleaseSwitchesBehindTrainAsync()` is correct
2. Check that subsection names match database exactly
3. Ensure ownership verification is working (train owns switch)
4. Review switch constraints in `V_Lookup_Section_NextSection` view

### Issue: Multiple Trains in Same Section
**Solution**:
1. Check hardware feedback timing (Rocrail should prevent this)
2. Verify adjacency logic in `IdentifyTrainForSectionAsync()`
3. Review direction validation in `ValidateConnectionDirectionAsync()`
4. Add section capacity checks if needed

## Future Enhancements

### 1. Advanced Look-Ahead Logic
Currently mocked in `GetNextAnticipatedSection()`. Implement:
```csharp
private async Task<string> GetNextAnticipatedSectionAsync(TrainMovedEventArgs args)
{
    // Query next sections in train's direction
    var connections = await _dbContext.VLookupSectionNextSection
        .Where(c => c.Section_DB_ID == currentSectionId && c.Direction == args.Direction)
        .ToListAsync();

    // Check if next section is occupied
    foreach (var connection in connections)
    {
        var nextSectionName = await GetSubSectionNameAsync(connection.NextSection_DB_ID);
        if (!_trackOccupancyService.IsSectionOccupied(nextSectionName))
        {
            return nextSectionName; // Safe to proceed
        }
    }

    return null; // Path blocked
}
```

### 2. Signal Integration with Rolling Release
```csharp
// In SignalControllerService, after setting signals:
if (args.PreviousSubSection != null)
{
    // Release switches for cleared section
    await _switchConfigService.ReleaseSwitchesBehindTrainAsync(
        args.TrainId,
        args.PreviousSubSection
    );
}
```

### 3. Emergency Stop Integration
```csharp
// In SignalControllerService, add:
public async Task SetAllSignalsToRedAsync()
{
    // Iterate through all configured signals
    // Set each to RED
    // Stop all trains immediately
}
```

### 4. Section Capacity Management
```csharp
// In TrackOccupancyService, add:
public bool CanTrainEnterSection(string trainName, string sectionName)
{
    // Check section capacity (some sections allow multiple trains)
    // Consider train length
    // Return true if safe to enter
}
```

## Migration Checklist

- [ ] Copy new files to project
- [ ] Update `MauiProgram.cs` with DI registrations
- [ ] Update existing `SwitchConfigurationService` with new method
- [ ] Update existing `TrainManagerService` with registration logic
- [ ] Initialize `TrackOccupancyService` on application startup
- [ ] Initialize `SignalControllerService` after occupancy service
- [ ] Test train registration
- [ ] Test signal updates with real hardware
- [ ] Test rolling switch release
- [ ] Monitor logs for collision prevention effectiveness
- [ ] Update documentation with any changes

## Summary

The integration transforms the system from static reservation to dynamic rolling-block control:

**Benefits**:
- Higher track capacity (trains can follow each other)
- Faster dispatch (switches released incrementally)
- Better collision prevention (real-time tracking)
- Automatic signal control (no manual intervention)
- Scalable to more trains on same route

**Safety Maintained**:
- Initial reservation still prevents route conflicts
- Hardware feedback prevents double occupancy
- Database validation ensures safe switch release
- Signal separation maintains following distance
