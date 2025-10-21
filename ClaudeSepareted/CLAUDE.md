# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a .NET MAUI train control system that manages model train operations via MQTT communication and a SQL Server database. The application provides both a mobile interface and administrative console for managing train timetables, track layouts, and real-time train operations.

## Build and Development Commands

### Building the Application
```bash
# Build for all platforms
dotnet build

# Build for specific configuration
dotnet build -c Release

# Build for specific platform
dotnet build -f net8.0-windows10.0.19041.0
```

### Running the Application
```bash
# Run on Windows
dotnet run -f net8.0-windows10.0.19041.0

# Run on Android (requires Android emulator/device)
dotnet run -f net8.0-android

# Run on iOS/MacCatalyst (requires appropriate environment)
dotnet run -f net8.0-maccatalyst
```

### Database Operations
```bash
# Add database migration
dotnet ef migrations add MigrationName

# Update database
dotnet ef database update
```

## Architecture Overview

### Core Components

**Domain Layer** (`Domain/`):
- `Train.cs` - Train entity with state management
- `TrackLayout.cs` - Track sections, subsections, platforms, switches
- `Stations.cs` - Station management
- `Timetable.cs` - Train schedule entries
- `Enums.cs` - System enums (Speed, TrainState, EntryState, etc.)

**Services** (`Services/`):
- `TrackManager.cs` - Core track operations, route planning, train movement
- `TrainManagerService.cs` - Train speed control and emergency operations
- `TimetableManager.cs` - Schedule processing and train automation

**Messaging** (`Messaging/`):
- `MQTTMessageHandler.cs` - Central message routing and processing
- `TrainMQTTConnector.cs` - Train-specific MQTT communications
- `TrackMQTTConnector.cs` - Track signal and state communications
- `TimetableMQTTConnector.cs` - Schedule management communications

**Data Access** (`DataAccess/`):
- `ApplicationDbContext.cs` - Entity Framework database context
- `TimetableRepository.cs` - Timetable-specific data operations

**Configuration** (`Configuration/`):
- `SystemConfiguration.cs` - Main system settings
- `MQTTConfiguration.cs` - MQTT broker settings
- `TrackConfiguration.cs` - Track layout settings

### Key Patterns

**Train Movement Flow**:
1. Timetable entries trigger train starts via `TryStartTrain()`
2. `TrackManager` plans routes using BFS algorithm
3. `TrainManagerService` controls train speeds via MQTT
4. Track sensors (Hall, RFID) update positions via MQTT messages
5. Signals are automatically managed based on train positions

**MQTT Topics** (from appsettings.json):
- Track sensing: `rocrail/service/info/fb`, `track/info/hall`, `track/info/rfid`
- Commands: `rocrail/service/client`, `track/command/signal`
- Train operations: `train/signal/*`, `train/start/request`, `train/status`

**Thread Safety**:
- All track operations use `lock (_lockObject)` for thread safety
- Route planning uses caching to improve performance
- Database operations are properly synchronized

## Database Configuration

The application uses SQL Server with connection string configured in both `appsettings.json` and `MauiProgram.cs`. Default connection uses Windows Authentication with APPLOGIN user.

## Key Development Notes

- The system is designed for real-time train control, avoid blocking operations in MQTT handlers
- Route planning uses simplified topology - in production, this would use actual track geometry
- All train movements must be validated through `CheckIfCanMove()` before execution
- Signal states are automatically managed based on train positions and movement
- The system supports both automatic timetable-based operation and manual control

## Testing

Manual testing can be done through the console interface using `ConsoleInterface.cs` which provides commands for:
- Starting trains manually: `ManualStartTrain(trainId, destinationStationId)`
- Debugging routes: `DebugRoute(startSubSectionId, endSubSectionId)`
- Viewing track layout: `DebugTrackLayout()`
- Listing trains and their current states