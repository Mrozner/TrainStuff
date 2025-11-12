# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET MAUI train control system application that manages train operations, track infrastructure, and scheduling through MQTT communication and a SQL Server database. The system controls model trains, monitors track sensors, and manages timetables with a virtual clock system.

## Development Commands

### Building the Application
```bash
# Build the entire solution
dotnet build ClaudeSepareted.sln

# Build specific project
dotnet build ClaudeSepareted.csproj

# Build for specific platform
dotnet build -f net8.0-windows10.0.19041.0
```

### Running the Application
```bash
# Run on Windows (requires Visual Studio or Windows SDK)
dotnet run

# Run with specific configuration
dotnet run -c Release
```

### Database Operations
```bash
# Add new migration
dotnet ef migrations add MigrationName

# Update database
dotnet ef database update

# List migrations
dotnet ef migrations list
```

### Package Management
```bash
# Restore packages
dotnet restore

# Add new package
dotnet add package PackageName
```

## Architecture Overview

### Core Components

**Database Layer:**
- `ApplicationDbContext` - Entity Framework Core context with SQL Server
- Key entities: `Train`, `Sections`, `SubSections`, `Platforms`, `Signals`, `TimetableEntries`
- Connection string configured in both `MauiProgram.cs` and `appsettings.json`

**Service Layer:**
- `TrackManager` - Central track management, route planning (BFS), train positioning
- `TrainManagerService` - Train speed control with individual `TrainController` instances
- `TimetableManager` - Schedule processing with thread-safe operations
- `TimetableRepository` - Thread-safe database access using `IServiceScopeFactory`

**MQTT Integration:**
- `TrackMQTTConnector` - Track sensor data (hall sensors, RFID) and control commands
- `TrainMQTTConnector` - Train speed commands and signal status updates
- `TimetableMQTTConnector` - Timetable request/response handling
- `MQTTMessageHandler` - Centralized message routing to appropriate managers
- `AdminMQTTService` - Administrative speed control commands

**Additional Services:**
- `StatusNotificationService` - Status message broadcasting and logging
- `VirtualClock` - Time acceleration system (60x speed default)
- `SystemConfiguration` - Centralized configuration management

### UI Layer (MVVM Pattern)
- `MainPage` - Main interface with timetable management and virtual clock controls
- `MainPageViewModel` - Data binding and business logic for main view
- `AdminPanelPage` - Administrative interface for manual train control
- Uses INotifyPropertyChanged and ObservableCollection for reactive UI

### Configuration System

**Dual Configuration Approach:**
1. Hard-coded `SystemConfiguration` in `MauiProgram.cs` (primary)
2. JSON configuration in `appsettings.json` (backup/external)

**MQTT Topics Structure:**
- Track sensors: `rocrail/service/info/fb`, `track/info/hall`, `track/info/rfid`
- Train control: `rocrail/service/client`, `train/signal/request/response/changed`
- Timetable: `train/start/request`, `train/status`

### Key Domain Enumerations
- `Speed`: STOP(0), SLOW(30), MEDIUM(60), HIGH(90)
- `TrainState`: Waiting, Moving, Stopped, PrepareToStop
- `EntryState`: Upcoming, InProgress, Arrived
- `RouteState`: InTime, Delay
- `Direction`: Forward, Backward
- `ObjectType`: Signal, Hall, RFID

## Development Guidelines

### Database Connection
The application uses SQL Server with this connection format:
```
Server=LAPTOP-ANHCTCLU\SQLEXPRESS;Database=TrainControllerSystem;TrustServerCertificate=True;Trusted_Connection=True;User Id=APPLOGIN;Password=12345
```

### MQTT Broker Configuration
- Address: 172.22.2.2
- Port: 1883
- Quality of Service: AtLeastOnce

### Thread Safety
- All manager services use lock objects for thread safety
- Repository uses `IServiceScopeFactory` for proper DbContext lifetime
- MQTT operations run on background threads with UI synchronization

### Virtual Clock System
- Default speed: 60x real-time
- Default start time: 08:00
- Controls train scheduling and timetable processing

### Error Handling
- Comprehensive exception handling throughout the service layer
- Status notification system for real-time feedback
- UI displays user-friendly error messages

## Common Development Tasks

### Adding New MQTT Topics
1. Update `MQTTConfiguration` class with new topic properties
2. Modify appropriate MQTT connector to handle the new topics
3. Update `SystemConfiguration` in `MauiProgram.cs`
4. Add topic definitions to `appsettings.json`

### Adding New Train Operations
1. Extend `TrainManagerService` with new methods
2. Update `TrainController` if needed
3. Add corresponding MQTT commands in `TrainMQTTConnector`
4. Update UI models and ViewModels accordingly

### Database Schema Changes
1. Modify entity classes in the Models folder
2. Create and apply Entity Framework migrations
3. Update `ApplicationDbContext` if adding new DbSets
4. Test migration on development database first

### Testing MQTT Communication
Use the admin panel to manually send train speed commands and monitor status messages in real-time. The status bar shows all MQTT communications with timestamps.

## Project Structure
- `MauiProgram.cs` - Dependency injection setup and configuration
- `MainPage.xaml/cs` - Main application interface
- `AdminPanelPage.xaml/cs` - Administrative control interface
- `appsettings.json` - External configuration file
- `ClaudeSepareted.csproj` - Project configuration and NuGet packages