# MAUI Windows Architecture Analysis

## Overview

The application uses **two separate windows** with distinct purposes:

1. **MainPage** - Primary user interface for timetable management and virtual clock control
2. **AdminPanelPage** - Secondary window for system monitoring and status logging

---

## Window 1: MainPage (Primary Interface)

### Purpose
Main interface for **train schedule management** and **virtual clock control**.

### Layout Structure

```
┌─────────────────────────────────────────────────────────────────┐
│                        MainPage                                  │
├──────────────────────────┬──────────────────────────────────────┤
│   Left Panel (Scrollable) │         Right Panel (Fixed)          │
│                           │                                      │
│  ┌─────────────────────┐ │  ┌─────────────────────────────────┐│
│  │ Train Selection     │ │  │      ⏰ Virtual Clock            ││
│  │ Start Station       │ │  │      12:45                       ││
│  │ End Station         │ │  │      60x Speed                   ││
│  │ Departure Time      │ │  └─────────────────────────────────┘│
│  └─────────────────────┘ │                                      │
│                           │  ┌─────────────────────────────────┐│
│  ┌─────────────────────┐ │  │      Set Time                    ││
│  │   Schedule List     │ │  │      [HH]:[MM]                   ││
│  │ (CollectionView)    │ │  │      [Set Time]                  ││
│  │                     │ │  └─────────────────────────────────┘│
│  │ ┌─────────────────┐│ │                                      │
│  │ │Train1  Timeline ││ │  ┌─────────────────────────────────┐│
│  │ │───────────────  ││ │  │      Speed Control               ││
│  │ │Train2  Timeline ││ │  │      [Speed Picker]              ││
│  │ │───────────────  ││ │  │      [⏸️] [▶️]                   ││
│  │ │Train3  Timeline ││ │  │      [🔄 Reset]                  ││
│  │ └─────────────────┘│ │  └─────────────────────────────────┘│
│  │                     │ │                                      │
│  └─────────────────────┘ │  ┌─────────────────────────────────┐│
│                           │  │         [➕ Add]                  ││
└──────────────────────────┴──────────────────────────────────────┘
```

### Key Components

#### 1. **Left Panel - Schedule Management**

**Purpose**: Manage train timetable entries

**Features**:
- **Train Selection** (`TrainPicker`)
  - Dropdown to select active trains from database
  - Bound to `MainPageViewModel.Trains` collection
  - Displays train names

- **Station Selection** (`StartPicker`, `EndPicker`)
  - Dropdowns to select source and destination stations
  - Bound to `MainPageViewModel.Stations` collection
  - Displays station names

- **Departure Time** (`StartTimePicker`)
  - Time picker for setting train departure time
  - Format: HH:mm (24-hour format)
  - Default: 00:00

- **Schedule List** (`ScheduleCollection`)
  - Displays all timetable entries
  - Each item shows:
    - **Train Name** (left)
    - **Visual Timeline** (center) - Custom `GraphicsView` with `ScheduleDrawable`
    - **Delete Button** (right) - 🗑️ emoji

**Data Binding**:
```csharp
// ViewModel Collections
public ObservableCollection<Train> Trains { get; set; }
public ObservableCollection<Stations> Stations { get; set; }
public ObservableCollection<ScheduleItem> ScheduleItems { get; set; }

// Selected Items
public Train SelectedTrain { get; set; }
public Stations SelectedStartStation { get; set; }
public Stations SelectedEndStation { get; set; }
```

**Operations**:
- **Add Schedule Entry** (`OnAddClicked`)
  1. Validates user selections (train, stations, time)
  2. Creates new `TimetableEntries` entity
  3. Saves to database via `ApplicationDbContext`
  4. Refreshes schedule list
  5. Shows success/error alerts

- **Delete Schedule Entry** (`OnDeleteClicked`)
  1. Confirms deletion with user
  2. Finds entry in database
  3. Removes from database
  4. Refreshes schedule list
  5. Shows confirmation/alerts

#### 2. **Right Panel - Virtual Clock Control**

**Purpose**: Control the virtual time simulation

**Features**:
- **Virtual Clock Display**
  - Shows current virtual time (e.g., "12:45")
  - Shows speed multiplier (e.g., "60x")
  - Updates in real-time via `VirtualClockViewModel`
  - Data binding: `{Binding VirtualClock.CurrentTimeString}`

- **Set Time Controls**
  - Hour/Minute input fields
  - "Set Time" button
  - Validates: 0-23 for hours, 0-59 for minutes
  - Updates virtual clock to specified time

- **Speed Controls**
  - Speed picker: 0.1x, 0.5x, 1x, 2x, 5x, 10x, 30x, 60x, 120x, 300x
  - Pause button (⏸️) - Pauses virtual clock
  - Resume button (▶️) - Resumes virtual clock
  - Reset button (🔄) - Resets to 00:00 with 60x speed

- **Add Button** (➕)
  - Fixed circular button
  - Adds new schedule entry with current selections

**Data Flow**:
```
User Input → MainPage Code-Behind → VirtualClock → TimeChanged Event → VirtualClockViewModel → UI Update
```

### Lifecycle

#### Initialization
```csharp
protected override async void OnAppearing()
{
    base.OnAppearing();

    if (!_automatedServicesInitialized)
    {
        await InitializeAutomatedServicesAsync();  // One-time setup
        _automatedServicesInitialized = true;
    }

    await _viewModel.LoadDataAsync();  // Always refresh data
}
```

#### Automated Services Initialization
```csharp
private async Task InitializeAutomatedServicesAsync()
{
    // Step 1: Initialize Pathfinding Service (builds routing cache)
    await _pathfindingService.InitializeAsync();

    // Step 2: Configure Virtual Clock
    _virtualClock.SetSpeed(60.0);  // 60x speed
    _virtualClock.SetTime(new DateTime(2024, 1, 1, 0, 0, 0));  // Midnight

    // Step 3: Start Track Handler Service
    _trackHandlerService.Start();

    // Step 4: Open Admin Panel Window
    var adminPage = _serviceProvider.GetRequiredService<AdminPanelPage>();
    var statusWindow = new Window(adminPage)
    {
        Title = "System Status Monitor",
        Width = 600,
        Height = 800
    };
    Application.Current?.OpenWindow(statusWindow);
}
```

### ViewModel Structure

**MainPageViewModel**:
- Manages data binding between UI and database
- Implements `INotifyPropertyChanged` for UI updates
- Implements `IDisposable` for cleanup
- Contains `VirtualClockViewModel` as nested component

**VirtualClockViewModel**:
- Wraps `VirtualClock` service
- Provides observable properties for time and speed
- Handles time change events from `VirtualClock`
- Provides time/speed control methods (SetTime, Pause, Resume, Reset)

---

## Window 2: AdminPanelPage (Secondary Window)

### Purpose
System monitoring and **real-time status logging** window.

### Layout Structure

```
┌─────────────────────────────────────┐
│     System Status Monitor           │
├─────────────────────────────────────┤
│  SYSTEM EVENT LOG                   │
│  ┌───────────────────────────────┐  │
│  │ Scrollable Status Log         │  │
│  │                               │  │
│  │ [12:45:23] ✅ Success message  │  │
│  │ [12:45:20] ℹ️  Info message    │  │
│  │ [12:45:15] ⚠️  Warning message │  │
│  │ [12:45:10] ❌ Error message    │  │
│  │ [12:45:05] Train1: Status msg  │  │
│  │ ...                            │  │
│  │                                 │  │
│  └───────────────────────────────┘  │
│                                     │
│  (Auto-scrolls to latest message)   │
└─────────────────────────────────────┘
```

### Key Components

#### 1. **Status Log Display**

**Purpose**: Display real-time system events

**Features**:
- **Dark theme** (#1E1E1E background)
- **Auto-scrolling** to latest message
- **Color-coded messages**:
  - 🟢 Green → Success
  - 🟠 Orange → Warning
  - 🔴 Red → Error
  - 🔵 Blue → Train status
  - ⚪ Light Gray → Info

- **Message Format**:
  ```
  [HH:mm:ss] [TrainName:] Message
  ```

- **Maximum messages**: 50 (auto-removes oldest)
- **Recent messages**: Loads last 20 on startup

#### 2. **Status Message Flow**

```
Service/Component → StatusNotificationService → OnStatusUpdated Event → AdminPanelPage.OnStatusUpdated → UpdateStatusBar → UI Update
```

**Example Flow**:
```csharp
// 1. Service logs status
_statusNotificationService.ShowSuccess("Automated system started");

// 2. StatusNotificationService fires event
OnStatusUpdated?.Invoke(new StatusMessage {
    Timestamp = DateTime.Now,
    Message = "Automated system started",
    Type = StatusType.Success,
    TrainName = null
});

// 3. AdminPanelPage receives event
private void OnStatusUpdated(StatusMessage status)
{
    MainThread.BeginInvokeOnMainThread(() =>
    {
        UpdateStatusBar(status);  // UI update
    });
}

// 4. UI shows colored message
[12:45:23] ✅ Automated system started
```

### Lifecycle

#### Initialization
```csharp
public AdminPanelPage(AdminPanelViewModel viewModel, StatusNotificationService statusService)
{
    InitializeComponent();

    // Subscribe to status updates
    _statusService.OnStatusUpdated += OnStatusUpdated;

    // Load recent messages (last 20)
    LoadRecentStatusMessages();

    _statusService.ShowInfo("Admin Panel ready");
}
```

#### Automatic Window Creation

The AdminPanelPage is **automatically opened** when MainPage appears:

```csharp
// In MainPage.OnAppearing()
private async Task InitializeAutomatedServicesAsync()
{
    // ... initialize services ...

    // Launch Admin Panel on Main UI Thread
    MainThread.BeginInvokeOnMainThread(() =>
    {
        var adminPage = _serviceProvider.GetRequiredService<AdminPanelPage>();
        var statusWindow = new Window(adminPage)
        {
            Title = "System Status Monitor",
            Width = 600,
            Height = 800
        };
        Application.Current?.OpenWindow(statusWindow);
    });
}
```

### ViewModel Structure

**AdminPanelViewModel**:
- Manages train control functionality
- Loads active trains from database
- Provides speed/direction controls
- Handles train start commands via `RocrailCommandService`

**Note**: The AdminPanelPage primarily uses `StatusNotificationService` for status updates rather than its ViewModel.

---

## Window Communication Architecture

### Service-Based Communication

Both windows communicate through **shared services**, not direct references:

```
┌─────────────────────────────────────────────────────────────┐
│                     Application Layer                        │
│                                                              │
│  ┌──────────────────┐         ┌──────────────────────────┐  │
│  │   MainPage       │         │   AdminPanelPage         │  │
│  │                  │         │                          │  │
│  │  - Schedule Mgmt │         │  - Status Log Display    │  │
│  │  - Clock Control │         │  - System Monitoring     │  │
│  └────────┬─────────┘         └──────────────┬───────────┘  │
│           │                                 │                 │
│           │  Uses                          │  Subscribes     │
│           ↓                                 ↓                 │
│  ┌────────────────────────────────────────────────────────┐  │
│                   Shared Services (Singleton)              │  │
│                                                            │  │
│  ┌──────────────────────────────────────────────────────┐ │  │
│  │ StatusNotificationService                            │ │  │
│  │ - Central event broadcaster                          │ │  │
│  │ - OnStatusUpdated event                              │ │  │
│  │ - ShowSuccess/ShowError/ShowInfo/ShowWarning         │ │  │
│  └──────────────────────────────────────────────────────┘ │  │
│                                                            │  │
│  ┌──────────────────────────────────────────────────────┐ │  │
│  │ VirtualClock                                         │ │  │
│  │ - Time simulation                                    │ │  │
│  │ - TimeChanged event                                  │ │  │
│  │ - SetSpeed/SetTime/Pause/Resume                     │ │  │
│  └──────────────────────────────────────────────────────┘ │  │
│                                                            │  │
│  ┌──────────────────────────────────────────────────────┐ │  │
│  │ TrackHandlerService                                  │ │  │
│  │ - Automated train scheduling                         │ │  │
│  │ - Pathfinding and routing                            │ │  │
│  └──────────────────────────────────────────────────────┘ │  │
│                                                            │  │
│  ┌──────────────────────────────────────────────────────┐ │  │
│  │ PathfindingService                                   │ │  │
│  │ - Route calculation                                  │ │  │
│  │ - Switch configuration                               │ │  │
│  └──────────────────────────────────────────────────────┘ │  │
│                                                            │  │
│  ┌──────────────────────────────────────────────────────┐ │  │
│  │ LightsFromDatabase                                   │ │  │
│  │ - Database-driven signal control                     │ │  │
│  │ - Initialize on startup                              │ │  │
│  └──────────────────────────────────────────────────────┘ │  │
└────────────────────────────────────────────────────────────┘
```

### Data Flow Examples

#### Example 1: User Adds Schedule Entry

```
1. User selects train, stations, time in MainPage
2. User clicks ➕ Add button
3. MainPage.OnAddClicked() executes
4. Creates TimetableEntries entity
5. Saves to ApplicationDbContext
6. Calls MainPageViewModel.LoadDataAsync()
7. UI refreshes with new entry
8. StatusNotificationService.ShowSuccess("Entry added")
9. AdminPanelPage receives OnStatusUpdated event
10. AdminPanelPage displays success message
```

#### Example 2: Virtual Clock Update

```
1. VirtualClock service ticks (every 100ms real-time * speed multiplier)
2. VirtualClock fires TimeChanged event
3. VirtualClockViewModel.OnVirtualTimeChanged() receives event
4. VirtualClockViewModel updates CurrentTime property
5. MainPage UI binding updates (shows new time)
6. TrackHandlerService checks for scheduled trains
7. If train due, TrackHandlerService initiates movement
8. StatusNotificationService logs train departure
9. AdminPanelPage displays departure message
```

#### Example 3: System Status Updates

```
1. Any service calls StatusNotificationService.ShowSuccess/Error/Info/Warning
2. StatusNotificationService creates StatusMessage object
3. StatusNotificationService fires OnStatusUpdated event
4. AdminPanelPage.OnStatusUpdated() receives event
5. AdminPanelPage.UpdateStatusBar() creates colored Label
6. AdminPanelPage adds Label to status log
7. Auto-scrolls to bottom
```

---

## Dependency Injection Architecture

### Service Registrations (MauiProgram.cs)

```csharp
// Singleton Services (shared across app)
builder.Services.AddSingleton<ApplicationDbContext>();
builder.Services.AddSingleton<IDbContextFactory<ApplicationDbContext>>();
builder.Services.AddSingleton<MainPageViewModel>();
builder.Services.AddSingleton<MainPage>();
builder.Services.AddSingleton<MqttInfrastructureService>();
builder.Services.AddSingleton<PathfindingService>();
builder.Services.AddSingleton<TrackOccupancyService>();
builder.Services.AddSingleton<TrackHandlerService>();
builder.Services.AddSingleton<BlockReservationService>();
builder.Services.AddSingleton<LightController>();
builder.Services.AddSingleton<LightsFromDatabase>();
builder.Services.AddSingleton<VirtualClock>();

// Transient Services (new instance each time)
builder.Services.AddTransient<AdminPanelViewModel>();
builder.Services.AddTransient<AdminPanelPage>();
```

### Service Resolution

**MainPage** (Singleton):
```csharp
public MainPage(
    MainPageViewModel viewModel,           // Singleton
    IDbContextFactory<ApplicationDbContext> dbContextFactory,
    TrackHandlerService trackHandlerService,  // Singleton
    VirtualClock virtualClock,               // Singleton
    StatusNotificationService statusNotificationService,  // Singleton
    PathfindingService pathfindingService,  // Singleton
    IServiceProvider serviceProvider)
{
    // Constructor injection
}
```

**AdminPanelPage** (Transient):
```csharp
public AdminPanelPage(
    AdminPanelViewModel viewModel,          // New instance each time
    StatusNotificationService statusService)  // Singleton
{
    // Constructor injection
}
```

---

## Window Lifecycle Flow

### Application Startup Sequence

```
1. App.xaml.cs constructor executes
2. LightsFromDatabase.InitializeSignalsAsync() starts in background
3. MainPage is set as application root (AppShell)
4. MainPage.OnAppearing() fires
5. MainPage.LoadDataAsync() loads trains, stations, schedule
6. MainPage.InitializeAutomatedServicesAsync() executes:
   a. PathfindingService.InitializeAsync() - Builds routing cache
   b. VirtualClock.SetSpeed(60.0) - Sets simulation speed
   c. TrackHandlerService.Start() - Starts automated scheduling
   d. AdminPanelPage window opens
7. AdminPanelPage.OnAppearing() fires
8. AdminPanelPage subscribes to StatusNotificationService
9. AdminPanelPage loads recent status messages
10. Application is fully initialized
```

### Runtime Operation

```
MainPage (User Interaction)
    ↓
User adds/edits schedule
    ↓
Database updated
    ↓
TrackHandlerService processes schedule
    ↓
VirtualClock provides time context
    ↓
PathfindingService calculates routes
    ↓
Train movements executed via MQTT
    ↓
StatusNotificationService logs events
    ↓
AdminPanelPage displays status updates
```

---

## Key Design Patterns

### 1. MVVM Pattern (Model-View-ViewModel)

**Benefits**:
- Separation of concerns
- Testable business logic
- Data binding for UI updates
- Clean architecture

**Implementation**:
- **View**: XAML files (MainPage.xaml, AdminPanelPage.xaml)
- **ViewModel**: C# classes (MainPageViewModel, AdminPanelViewModel, VirtualClockViewModel)
- **Model**: Entity classes (Train, Stations, TimetableEntries, etc.)

### 2. Service Pattern (Singleton Services)

**Benefits**:
- Shared state across application
- Centralized business logic
- Event-driven communication
- Dependency injection

**Examples**:
- `VirtualClock` - Single time source
- `TrackHandlerService` - Single scheduler
- `StatusNotificationService` - Central event broadcaster

### 3. Event-Driven Communication

**Benefits**:
- Loose coupling between components
- Real-time updates
- Multiple subscribers pattern
- No direct dependencies

**Examples**:
- `VirtualClock.TimeChanged` → VirtualClockViewModel updates
- `StatusNotificationService.OnStatusUpdated` → AdminPanelPage updates
- `TrackOccupancyService.OnTrainMoved` → TrainMovementMonitor updates

### 4. Repository Pattern

**Benefits**:
- Abstracted data access
- Centralized query logic
- Testable data layer

**Implementation**:
- `ITimetableRepository` interface
- `TimetableRepository` implementation
- Used by ViewModels for database operations

---

## User Interaction Scenarios

### Scenario 1: Adding a Train Schedule

```
1. User opens app → MainPage appears
2. AdminPanelPage automatically opens in separate window
3. User selects "Train1" from Train dropdown
4. User selects "Budapest" from Start Station dropdown
5. User selects "Debrecen" from End Station dropdown
6. User sets departure time to "08:30"
7. User clicks ➕ Add button
8. MainPage validates inputs
9. MainPage saves to database
10. MainPage refreshes schedule list
11. Status message: "✅ Schedule entry added"
12. AdminPanelPage displays status message
13. TrackHandlerService picks up new entry
14. When virtual clock reaches 08:30, train departs
15. Status messages logged in AdminPanelPage
```

### Scenario 2: Controlling Virtual Time

```
1. User sees current virtual time: "12:45"
2. User wants to jump to afternoon: "16:00"
3. User enters "16" in Hour field, "00" in Minute field
4. User clicks "Set Time" button
5. MainPage validates: 16 is valid (0-23), 00 is valid (0-59)
6. MainPage calls VirtualClock.SetTime(16:00)
7. VirtualClock fires TimeChanged event
8. VirtualClockViewModel updates CurrentTime property
9. MainPage UI shows new time: "16:00"
10. Status message: "ℹ️ Virtual time set to 16:00"
11. AdminPanelPage displays status message
12. TrackHandlerService checks for trains due at 16:00
13. Any due trains are automatically dispatched
```

### Scenario 3: Monitoring System Status

```
1. AdminPanelPage displays real-time status log
2. User sees various colored messages:
   - [12:45:23] ✅ Routing table built successfully
   - [12:45:25] ℹ️  Virtual clock started: 60x speed
   - [12:45:26] ℹ️  Track handler service started
   - [12:45:30] ℹ️  Train1: Departed from Budapest
   - [12:46:15] ⚠️  Train2: Waiting for clearance at signal
   - [12:47:00] ✅ Train3: Arrived at Debrecen
   - [12:47:30] ❌ MQTT connection lost
3. User can monitor system health and train movements
4. User can identify issues from colored messages
5. Auto-scrolling ensures latest messages visible
```

---

## Summary

### MainPage Window
- **Primary Interface**: Schedule management and clock control
- **User Actions**: Add/delete schedule entries, control virtual time
- **Data Sources**: Database tables (Trains, Stations, TimetableEntries)
- **Services Used**: TrackHandlerService, VirtualClock, PathfindingService
- **Lifecycle**: Singleton, created once at app startup

### AdminPanelPage Window
- **Secondary Interface**: System monitoring and status logging
- **Display Only**: Shows real-time system events
- **Data Source**: StatusNotificationService events
- **Services Used**: StatusNotificationService (read-only)
- **Lifecycle**: Transient, created automatically by MainPage, runs in separate window

### Communication
- **No Direct Communication**: Windows don't reference each other
- **Service-Based**: All communication through shared Singleton services
- **Event-Driven**: StatusNotificationService broadcasts events to all subscribers
- **Real-Time**: Immediate updates via event subscriptions

### Architecture Benefits
- ✅ **Loose Coupling**: Windows are independent
- ✅ **Scalability**: Easy to add new windows/services
- ✅ **Testability**: Services can be mocked
- ✅ **Maintainability**: Clear separation of concerns
- ✅ **Flexibility**: Services can be reused across windows
