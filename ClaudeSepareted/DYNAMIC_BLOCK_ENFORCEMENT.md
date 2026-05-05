# Dynamic Block Enforcement - Implementation Guide

## Overview

The **Dynamic Block Enforcement** system transforms the train control from a "start once, stop at destination" model to a **rolling-block operation** where trains automatically stop at red signals and resume when the track clears. This enables multiple trains to follow each other at safe distances.

## Architecture Components

### 1. **New State: `TrainState.WaitingForClearance`**

Added to `Domain/Enums.cs`:

```csharp
public enum TrainState
{
    Waiting,
    Moving,
    Stopped,
    PrepareToStop,
    Arrived,
    WaitingForClearance  // NEW: Train stopped at red signal
}
```

**Purpose**: Distinguishes between a train that's stopped for other reasons (arrival, manual stop) vs. waiting for track ahead to clear.

### 2. **Enhanced `TrainMovementMonitor.cs`**

The core of the dynamic block enforcement system.

**New Dependencies**:
- `TrackOccupancyService` - Real-time position tracking
- `ILogger<TrainMovementMonitor>` - Diagnostic logging
- `RoutePlan` - Route navigation for look-ahead logic

**New Fields**:
```csharp
private RoutePlan _routePlan;                      // Full journey route
private int _currentRouteIndex = 0;                // Position in route
private Dictionary<int, string> _sectionIdToNameMap;    // ID → Name mapping
private Dictionary<string, int> _sectionNameToIdMap;    // Name → ID mapping
private string _waitingForSectionToClear;          // Which section blocks us
private bool _isSubscribedToOccupancyService = false;
```

**Key Methods**:

#### `SetRoutePlan(RoutePlan routePlan)`
```csharp
// Called by TrainManagerService before movement starts
_movementMonitor.SetRoutePlan(routeResult.RoutePlan);
```
- Stores the complete route for navigation
- Initializes section ID ↔ name mappings
- Enables look-ahead calculations

#### `GetNextExpectedSubSection(string currentSubSection)`
```csharp
// Calculates which section comes next on the route
var nextSection = _movementMonitor.GetNextExpectedSubSection("section_5");
// Returns: "section_6"
```

**Algorithm**:
1. Convert current subsection name to section ID
2. Find edge in RoutePlan where current section is source
3. Return target node of that edge (next expected section)
4. Update route index as train progresses

#### `CheckBlockAheadAsync(string currentSubSection)`
```csharp
// Called automatically when Rocrail feedback received
// Checks if next section is occupied
if (_trackOccupancyService.IsSectionOccupied(nextExpectedSection))
{
    await StopTrainForRedSignalAsync(nextExpectedSection, blockingTrain);
}
```

#### `StopTrainForRedSignalAsync(string occupiedSection, string blockingTrain)`
```csharp
// Immediate emergency stop
await _commandSender.SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);

// Update state
_train.State = TrainState.WaitingForClearance;
_waitingForSectionToClear = occupiedSection;

// Subscribe to occupancy service for auto-resume
_trackOccupancyService.OnTrainMoved += OnTrainMovedForBlockClearance;
```

#### `OnTrainMovedForBlockClearance(object sender, TrainMovedEventArgs args)`
```csharp
// Event handler for auto-resume
if (_train.State == TrainState.WaitingForClearance &&
    args.PreviousSubSection == _waitingForSectionToClear)
{
    // Track cleared!
    await ResumeTrainForGreenSignalAsync();
}
```

### 3. **Integration with `TrainSpeedController.cs`**

The `TrainSpeedController` remains **unchanged** but now works in coordination with `TrainMovementMonitor`:

**Interaction Model**:
```
┌─────────────────────────────────────────────────────────────┐
│                    TrainManagerService                         │
│  ┌──────────────────────┐         ┌──────────────────────┐  │
│  │ TrainSpeedController │         │ TrainMovementMonitor  │  │
│  │                      │         │                       │  │
│  │ • StartTrainAsync()  │         │ • SetRoutePlan()      │  │
│  │ • StopTrainNowAsync()│         │ • CheckBlockAhead()   │  │
│  │ • Speed management   │◄────────┤ • StopForRedSignal()  │  │
│  │                      │  Sends  │ • ResumeForGreen()    │  │
│  └──────────────────────┘ Commands └──────────────────────┘  │
│                                   ▲                           │
│                                   │                           │
│                          TrackOccupancyService               │
│                          (Real-time positions)              │
└─────────────────────────────────────────────────────────────┘
```

**How They Work Together**:

1. **Initial Dispatch**:
```csharp
// TrainSpeedController starts the train
await _speedController.StartTrainAsync();  // Train begins moving
```

2. **During Movement**:
```csharp
// TrainMovementMonitor monitors Rocrail feedback
await ProcessRocrailFeedbackAsync(feedback);

// Calls CheckBlockAheadAsync()
await CheckBlockAheadAsync(currentSection);

// If block detected → StopTrainForRedSignalAsync()
// This calls _commandSender.SendTrainCommandAsync("stop", Speed.STOP, direction)
// Which uses the SAME command sender as TrainSpeedController!
```

3. **Auto-Resume**:
```csharp
// When track clears, OnTrainMovedForBlockClearance() fires
await ResumeTrainForGreenSignalAsync();

// Sends start command
await _commandSender.SendTrainCommandAsync("start", resumeSpeed, direction);
// Train resumes, TrainSpeedController sees train moving again
```

**Key Point**: Both `TrainSpeedController` and `TrainMovementMonitor` use the **same** `TrainCommandSender` instance, so commands are coordinated.

### 4. **Updated `TrainManagerService.cs`**

**New Constructor Parameter**:
```csharp
public TrainManagerService(
    // ... existing parameters ...
    TrackOccupancyService trackOccupancyService = null,
    RoutePlan routePlan = null,  // NEW!
    ILogger<TrainManagerService> logger = null)
```

**Route Plan Propagation**:
```csharp
// In constructor
_routePlan = routePlan;

// Pass to movement monitor for look-ahead logic
if (_routePlan != null)
{
    _movementMonitor.SetRoutePlan(_routePlan);
}
```

### 5. **Updated `TrackHandlerService.cs`**

**Route Planning Before Manager Creation**:
```csharp
// Step 3: Get route plan for TrainMovementMonitor
var routeResultForMonitor = await _pathfinder.PlanAndConfigureRouteAsync(
    timetableEntry.SourcePlatform,
    timetableEntry.DestinationPlatform,
    train.Name,
    train.Direction,
    configureSwitches: false);  // Don't configure yet

// Step 4: Create TrainManagerService with route plan
trainManager = new TrainManagerService(
    _virtualClock, train, timetableEntry, _mqttConfig, _statusService,
    _serviceProvider, _mqttService, this, requiredSwitchNames, _pathfinder,
    null, null, null, _trackOccupancyService,
    routeResultForMonitor.Success ? routeResultForMonitor.RoutePlan : null);
```

**Why Two Route Planning Calls?**:
- **First call** (line 507): Get route for `TrainMovementMonitor` look-ahead logic
- **Second call** (line 601): Actually configure switches with hardware commands

This separation ensures:
1. Monitor has route information from the start
2. Switches are configured AFTER train manager is created
3. No race conditions in resource allocation

## Complete Flow Example

### Scenario: Two Trains, Same Route

```
Route: section_1 → section_2 → section_3 → section_4 (destination)

Train1 starts at section_1
Train2 starts at section_1 (2 minutes later)
```

#### T+0:00 - Train1 Dispatch
```
1. TrackHandlerService plans route
2. Switches reserved and configured
3. TrainManagerService created with RoutePlan
4. MovementMonitor.SetRoutePlan(routePlan)
5. TrainSpeedController.StartTrainAsync()
6. Train1 begins moving: section_1 → section_2
```

#### T+0:30 - Train1 Enters Section 2
```
1. Rocrail sends: <fb id="section_2" state="true"/>
2. TrackOccupancyService receives feedback
3. Train1 position updated: section_1 → section_2
4. SignalControllerService sets RED behind, GREEN ahead
5. TrainMovementMonitor.ProcessRocrailFeedbackAsync()
   → CheckBlockAheadAsync("section_2")
   → GetNextExpectedSubSection("section_2") = "section_3"
   → IsSectionOccupied("section_3")? NO
   → Train1 continues
```

#### T+1:00 - Train2 Dispatch
```
1. TrackHandlerService plans route
2. Switches already reserved by Train1 → RESERVATION FAILS
3. Train2 marked as "Upcoming" for retry
4. Waiting for Train1 to clear switches
```

#### T+1:30 - Train1 Enters Section 3
```
1. Rocrail sends: <fb id="section_3" state="true"/>
2. TrackOccupancyService updates position
3. SignalControllerService updates signals
4. TrainMovementMonitor checks ahead
   → GetNextExpectedSubSection("section_3") = "section_4" (destination)
   → Section 4 clear, continue
5. SwitchConfigurationService.ReleaseSwitchesBehindTrainAsync("Train1", "section_2")
   → Switches for section_2 released
```

#### T+2:00 - Train2 Retry
```
1. TrackHandlerService reprocesses timetable entry
2. Plan route again
3. TryReserveSwitches() → SUCCESS! (Train1 released section_2 switches)
4. TrainManagerService created for Train2
5. Train2 begins moving: section_1 → section_2
```

#### T+2:15 - Train2 Approaches Section 3
```
1. Rocrail sends: <fb id="section_2" state="true"/>
2. Train2 enters section_2
3. TrainMovementMonitor checks ahead
   → GetNextExpectedSubSection("section_2") = "section_3"
   → IsSectionOccupied("section_3")? YES! (Train1 is there)
4. StopTrainForRedSignalAsync("section_3", "Train1")
   → Send stop command
   → Train2.State = WaitingForClearance
   → _waitingForSectionToClear = "section_3"
   → Subscribe to OnTrainMoved events
```

#### T+2:30 - Train1 Arrives at Destination
```
1. Rocrail sends: <fb id="section_4" state="true"/>
2. TrainMovementMonitor detects arrival
3. StopTrainOnArrivalAsync()
   → Stop Train1
   → Release all remaining switches
   → Unsubscribe from events
4. Train1.State = Arrived
```

#### T+2:31 - Train2 Auto-Resume!
```
1. TrackOccupancyService detects Train1 left section_3
2. Fires OnTrainMoved event:
   TrainMovedEventArgs{
       TrainId: "Train1",
       PreviousSubSection: "section_3",
       CurrentSubSection: "section_4",
       Direction: true
   }
3. Train2's OnTrainMovedForBlockClearance() handler fires
   → Check: Is Train2.State == WaitingForClearance? YES
   → Check: Is args.PreviousSubSection == "section_3"? YES
   → IsSectionOccupied("section_3")? NO (Train1 left)
   → ResumeTrainForGreenSignalAsync()
       → Send start command
       → Train2.State = Moving
       → Train2 resumes journey
```

## Safety Features

### 1. **Immediate Stop on Detection**
```csharp
// No delay - as soon as occupancy detected
await _commandSender.SendTrainCommandAsync("stop", Speed.STOP, _train.Direction);
```

### 2. **Double-Check Before Resume**
```csharp
// Even though event fired, verify section is actually clear
if (!_trackOccupancyService.IsSectionOccupied(_waitingForSectionToClear))
{
    await ResumeTrainForGreenSignalAsync();
}
```

### 3. **State Protection**
```csharp
// Only auto-resume if we're actually waiting
if (_train.State != TrainState.WaitingForClearance)
    return; // Don't interfere with other stop reasons
```

### 4. **Section Mapping Validation**
```csharp
// Convert between database IDs and subsection names
// Prevents confusion between internal IDs and hardware names
if (_sectionIdToNameMap.TryGetValue(sectionId, out var sectionName))
{
    return sectionName;
}
```

## Interaction with Other Systems

### **SignalControllerService**
- **Independent but coordinated**: Signals update based on position, not based on train commands
- **Block enforcement honors signals**: Stops when next section occupied (effectively a red signal)
- **No direct coupling**: Each system uses TrackOccupancyService independently

### **SwitchConfigurationService**
- **Rolling release continues**: Switches released as train passes sections
- **No coordination needed**: Block enforcement and switch release operate independently
- **Both use TrackOccupancyService**: For position information

### **TrackOccupancyService**
- **Central integration point**: All position-based systems subscribe here
- **Event-driven**: Both signals and block enforcement use OnTrainMoved events
- **Thread-safe**: Concurrent operations on occupancy map

## Configuration and Tuning

### **Stop Distance**
Currently uses immediate stop. To add stopping distance:

```csharp
// In GetNextExpectedSubSection(), look TWO sections ahead
var nextSection = GetNextExpectedSubSection(current);
var sectionAfterNext = GetNextExpectedSubSection(nextSection);

// Stop if sectionAfterNext is occupied (gives braking room)
if (_trackOccupancyService.IsSectionOccupied(sectionAfterNext))
{
    await StopTrainForRedSignalAsync(sectionAfterNext, blockingTrain);
}
```

### **Resume Delay**
To add delay between green signal and movement:

```csharp
// In ResumeTrainForGreenSignalAsync()
await Task.Delay(2000); // Wait 2 seconds after green
await _commandSender.SendTrainCommandAsync("start", resumeSpeed, _train.Direction);
```

### **Speed Adjustment**
To use slower speed when resuming:

```csharp
// In ResumeTrainForGreenSignalAsync()
var resumeSpeed = Speed.SLOW; // Resume slowly instead of full speed
await _commandSender.SendTrainCommandAsync("start", resumeSpeed, _train.Direction);
```

## Troubleshooting

### Issue: Train Stops Too Early
**Cause**: Looking ahead too many sections
**Solution**: Verify `GetNextExpectedSubSection()` returns correct next section
```csharp
_logger.LogDebug("Next section from {Current}: {Next}",
    currentSubSection, nextExpectedSection);
```

### Issue: Train Doesn't Resume
**Cause**: Event handler not subscribed or section name mismatch
**Solution**: Check subscription and section name matching
```csharp
// Verify subscription
_logger.LogDebug("Subscribed to occupancy: {Subscribed}", _isSubscribedToOccupancyService);

// Verify section name comparison
_logger.LogDebug("Waiting for: {Waiting}, Got: {Previous}, Match: {Match}",
    _waitingForSectionToClear,
    args.PreviousSubSection,
    string.Equals(_waitingForSectionToClear, args.PreviousSubSection,
        StringComparison.OrdinalIgnoreCase));
```

### Issue: Train Resumes Too Early
**Cause**: Event fires before section is actually clear
**Solution**: Add double-check in event handler (already implemented)
```csharp
if (!_trackOccupancyService.IsSectionOccupied(_waitingForSectionToClear))
{
    await ResumeTrainForGreenSignalAsync();
}
```

### Issue: Route Navigation Fails
**Cause**: Section mappings not initialized or route plan not set
**Solution**: Verify initialization order
```csharp
// In TrainManagerService constructor
if (_routePlan != null)
{
    _movementMonitor.SetRoutePlan(_routePlan); // This triggers async mapping init
    _logger.LogDebug("Route plan set with {EdgeCount} edges", _routePlan.Path.Count);
}
```

## Performance Considerations

### **Database Queries**
- Section mappings cached at initialization
- No repeated queries during operation
- Async initialization doesn't block startup

### **Event Handling**
- Lightweight event subscriptions
- No polling - purely event-driven
- Minimal thread synchronization

### **Memory Usage**
- Route plan stored per train (one copy per active train)
- Section mappings small (typically <100 entries)
- Auto-cleanup on train arrival

## Migration Checklist

- [ ] Add `WaitingForClearance` to `TrainState` enum
- [ ] Update `TrainMovementMonitor.cs` with new code
- [ ] Update `TrainManagerService.cs` to pass RoutePlan
- [ ] Update `TrackHandlerService.cs` to plan route for monitor
- [ ] Ensure `TrackOccupancyService` is injected into `TrainManagerService`
- [ ] Test with single train (verify no regression)
- [ ] Test with two trains on same route
- [ ] Verify auto-resume functionality
- [ ] Monitor logs for block enforcement messages
- [ ] Tune stop/resume timing if needed

## Summary

The Dynamic Block Enforcement system enables:

✅ **Automatic Collision Avoidance**: Trains stop before occupied sections
✅ **Rolling-Block Operation**: Multiple trains on same route
✅ **Event-Driven Response**: Immediate stop/resume based on position
✅ **Safe Following Distance**: Maintained by block enforcement
✅ **No Manual Intervention**: Fully automatic operation
✅ **Integration with Signals**: Coordinates with traffic lights
✅ **Rolling Lock Release**: Switches freed as train progresses

The system transforms from static reservation to **dynamic, real-time traffic control** while maintaining full collision safety!
