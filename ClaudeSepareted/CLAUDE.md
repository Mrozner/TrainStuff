# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a **.NET MAUI Train Control System** - a sophisticated multi-platform mobile application for managing train schedules, routes, and real-time railway operations using MQTT communication and SQL Server database integration.

### Technology Stack
- **Framework**: .NET 10.0 MAUI (Multi-platform App UI)
- **Database**: Microsoft SQL Server with Entity Framework Core 10.0
- **Real-time Communication**: MQTTnet for train control messaging
- **Architecture**: Clean Architecture with MVVM pattern
- **Target Platforms**: Android, iOS, macOS Catalyst, Windows, Tizen

## Development Commands

### Build and Run
```bash
# Build the entire solution for all platforms
dotnet build

# Build for specific platform (Windows)
dotnet build -f net10.0-windows10.0.19041.0

# Build for Android
dotnet build -f net10.0-android

# Build for iOS
dotnet build -f net10.0-ios

# Run the application
dotnet run
```

### Database Operations
```bash
# Add new Entity Framework migration
dotnet-ef migrations add <MigrationName>

# Update database with pending migrations
dotnet-ef database update

# List pending migrations
dotnet-ef migrations list
```

### Testing and Debugging
The project supports debugging on all target platforms through Visual Studio or VS Code with MAUI workloads.

## Architecture and Code Structure

### Core Architecture Patterns

**Clean Architecture Implementation:**
- **Domain Layer** (`/Domain`): Business entities and core logic
- **Data Access Layer** (`/DataAccess`): Entity Framework context and repositories
- **Service Layer** (`/Services`): Business services and MQTT handlers
- **Presentation Layer** (`/ViewModels`, XAML pages): MVVM implementation

**Key Directories:**
- `/Common` - Shared data models and communication objects (MQTT messages, sensor data)
- `/Configuration` - System configuration classes (MQTT, Track, Timetable settings)
- `/Domain` - Entity Framework models (Train, Platforms, TrackConnections, etc.)
- `/Services` - Business logic services (TrackHandler, TrainManager, VirtualClock)
- `/ViewModels` - MVVM ViewModels with data binding
- `/DataAccess` - EF Core DbContext and repository implementations
- `/TrackHandler` - Complex track topology and routing logic

### Database Integration

**Database:** SQL Server `TrainControllerSystem`
**Connection:** Uses APPLOGIN credentials (see appsettings.json)

**Key Entities:**
- `Trains` - Train information and current status
- `Platforms` - Station platforms and track sections
- `TimetableEntries` - Schedule entries with routes
- `TrackConnections` - Railway track topology and connections
- `Sections/SubSections` - Track segmentation for train control

**Database Views:**
- `V_PlatformEntry` - Complex view for platform entry routes
- `V_Lookup_Section_NextSection` - Track section navigation mapping

### MQTT Communication System

**MQTT Broker:** 172.22.2.2:1883

**Key Topics:**
- `rocrail/service/info/fb` - Track feedback and section status
- `track/info/hall` - Hall sensor data for train positioning
- `track/info/rfid` - RFID reader data for train identification
- `track/command/signal` - Signal control commands
- `train/signal/request` - Train signal operation requests
- `train/signal/response` - Train signal operation responses
- `train/signal/changed` - Signal state change notifications
- `train/start/request` - Timetable start requests
- `train/status` - Train status updates

### Key Services

**TrackHandlerService** - Automated train scheduling and routing system
- Manages train movements through track topology
- Handles switch and signal control
- Prevents collisions through section locking
- Integrates with VirtualClock for simulation timing

**TrainManagerService** - Individual train operation control
- Manages train speed and direction
- Handles arrival/departure logic
- Communicates with MQTT for hardware control

**VirtualClock** - Time simulation system
- Accelerates/pauses simulation time
- Triggers scheduled events and train movements
- Midnight reset functionality for daily schedules

**StatusNotificationService** - Real-time status updates
- Broadcasts train and track status changes
- Admin notifications for system events

### Admin Panel Features

The AdminPanelPage provides advanced control capabilities:
- Manual train speed control
- Direction management
- Direct MQTT command interface
- Real-time status monitoring
- Override controls for automated systems

### Configuration Management

**appsettings.json** contains:
- Database connection string
- MQTT broker settings and topic configurations
- Track configuration (MaxOccupiedSections: 100)
- Timetable settings (SchedulerIntervalSeconds: 30)
- Virtual clock parameters

**Hard-coded Configuration** in MauiProgram.cs:
- System configuration object with all settings
- Dependency injection setup for all services
- Database context registration with SQL Server

## Key Development Notes

### Null Reference Warnings
The build generates many CS8618 (non-nullable field) and CS8625 (null literal) warnings. These are expected due to the codebase using non-nullable reference types with Entity Framework and dependency injection patterns.

### Entity Framework Patterns
- Domain models use `[NotMapped]` for runtime properties
- Navigation properties are configured separately
- Database context uses SQL Server with TrustServerCertificate

### MQTT Integration
- Services use dependency injection for MQTT client
- Async/await patterns for message handling
- Error handling for network connectivity issues

### MAUI-Specific Considerations
- XAML pages use data binding to ViewModels
- Platform-specific implementations in `/Platforms` directory
- GraphicsView for custom timeline visualization
- Picker controls for train/platform selection

### Thread Safety
- TrackHandlerService uses locking mechanisms for concurrent operations
- Dictionary collections for managing active trains and commands
- CancellationToken for service cancellation

## Working with This Codebase

### When Adding New Features:
1. Follow the Clean Architecture pattern - keep business logic in Domain layer
2. Register new services in MauiProgram.cs dependency injection
3. Add database changes through Entity Framework migrations
4. Update configuration in both appsettings.json and MauiProgram.cs
5. Consider MQTT topic implications for new features

### When Debugging:
- Check VirtualClock time for timing-related issues
- Verify MQTT broker connectivity (172.22.2.2:1883)
- Monitor SQL Server connection and permissions
- Use the Admin Panel for manual control during testing
- Check StatusNotificationService logs for system events

### Database Schema Changes:
- Always use Entity Framework migrations
- Test migrations on development database first
- Consider impact on existing timetable entries
- Update related views if modifying track topology

This system is designed for real-time railway operations management and requires careful consideration of timing, concurrency, and hardware integration aspects.