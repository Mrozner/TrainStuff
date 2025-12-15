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
# Build the entire solution for all platforms (expects warnings)
dotnet build

# Build for specific platform (Windows)
dotnet build -f net10.0-windows10.0.19041.0

# Build for Android
dotnet build -f net10.0-android

# Build for iOS
dotnet build -f net10.0-ios

# Build for Mac Catalyst
dotnet build -f net10.0-maccatalyst

# Clean and rebuild
dotnet clean && dotnet build

# Run the application (uses default platform)
dotnet run

# Run on specific platform
dotnet run -f net10.0-windows10.0.19041.0
```

### Known Build Issues
- **Nullable Reference Warnings**: Build generates 200+ CS8618, CS8625, CS8602, CS8603 warnings due to non-nullable reference types with EF Core and DI patterns
- **Package Conflicts**: Microsoft.Extensions.Configuration.Binder version conflicts (2.1.0 vs 2.1.1) from Microsoft.Azure.WebJobs dependency
- **Obsolete API Warnings**: DisplayAlert methods generate CS0618 warnings - use DisplayAlertAsync instead
- These warnings are expected and don't prevent successful compilation and execution

### Database Operations
```bash
# Add new Entity Framework migration (from project directory)
dotnet-ef migrations add <MigrationName> --project .

# Update database with pending migrations
dotnet-ef database update --project .

# List pending migrations
dotnet-ef migrations list --project .

# Remove last migration (before applying to database)
dotnet-ef migrations remove --project .

# Generate SQL script for migrations
dotnet-ef migrations script --project .
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
- Uses unified pathfinding service for optimal route calculation

**UnifiedPathfindingService** - Advanced railway pathfinding system
- BFS-based pathfinding algorithms for train routing
- Switch configuration optimization
- Conflict detection and avoidance
- Platform-to-node mapping for station routing
- Route plan generation with required switch positions

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
- System configuration object with all settings (duplicates appsettings.json)
- Dependency injection setup for all services
- Database context registration with SQL Server
- Note: Configuration exists in both appsettings.json AND MauiProgram.cs hard-coded values

### Dependency Injection Services Registration

The application registers the following services in MauiProgram.cs:
- `ApplicationDbContext` - EF Core database context
- `SystemConfiguration` and sub-configurations (MQTT, Track, Timetable)
- `MainPageViewModel` and `MainPage` - MVVM main screen
- `ITimetableRepository` and `TimetableRepository` - Data access
- `AdminMQTTService` - Admin MQTT communication
- `StatusNotificationService` - Real-time status broadcasting
- `VirtualClock` - Time simulation system
- `UnifiedPathfindingService` - Railway pathfinding algorithms
- `ITrackGraphFactory` and `TrackGraphFactory` - Track graph creation
- `TrackHandlerService` - Automated train scheduling
- `AdminPanelPage` - Admin control interface

## Key Development Notes

### Configuration Management
**Important**: Configuration is duplicated between appsettings.json and hard-coded values in MauiProgram.cs. When making changes, update both locations to maintain consistency.

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
4. Update configuration in BOTH appsettings.json AND MauiProgram.cs (important!)
5. Consider MQTT topic implications for new features
6. Add corresponding Entity Framework DbSets to ApplicationDbContext.cs
7. Test on multiple target platforms if UI changes are made

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
- Update ApplicationDbContext.cs with new DbSets for new entities

### Pathfinding System Usage:
The project includes a sophisticated pathfinding system documented in `PATHFINDING_USAGE_EXAMPLE.md`:
- Use `UnifiedPathfindingService` for train routing calculations
- Access through dependency injection in services
- Supports BFS-based pathfinding with conflict detection
- Provides required switch configurations for routes
- Integrates with TrackHandlerService for automated routing

### Platform-Specific Development:
- **Android**: Requires Android SDK and Android Workload
- **iOS**: Requires Xcode and iOS development on Mac
- **Windows**: Requires Windows 10 SDK version 19041 or higher
- **Mac Catalyst**: Requires macOS Catalina or higher
- Use platform-specific folders for native implementations

This system is designed for real-time railway operations management and requires careful consideration of timing, concurrency, and hardware integration aspects.