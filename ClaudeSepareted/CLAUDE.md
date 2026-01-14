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

## Prerequisites

### Development Environment
- **.NET 10.0 SDK** - Required for building and running
- **MAUI Workloads** - Install with `dotnet workload install maui`
- **SQL Server** - Local or remote SQL Server instance for database
- **MQTT Broker** - Access to MQTT broker at 172.22.2.2:1883 (hardware integration)

### Platform-Specific Requirements
- **Windows**: Windows 10 SDK version 19041 or higher
- **Android**: Android SDK and Android development environment
- **iOS/macOS**: Requires macOS with Xcode for iOS/MacCatalyst development
- **Tizen**: Tizen development tools (currently commented out)

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

**Important**: Always run EF commands from the project root directory where the `.csproj` file is located.

### Testing and Debugging
The project supports debugging on all target platforms through Visual Studio or VS Code with MAUI workloads.

### Package Management
```bash
# Restore NuGet packages
dotnet restore

# Add new package
dotnet add package <PackageName>

# Update package
dotnet add package <PackageName> --version <Version>
```

**Key Dependencies**:
- **Entity Framework Core 10.0.0** - Database ORM with SQL Server provider
- **MQTTnet 5.0.1.1416** - MQTT communication for real-time train control
- **Microsoft.Azure.WebJobs 3.0.42** - Azure integration (causes version conflicts)
- **Newtonsoft.Json 13.0.3** - JSON serialization
- **Microsoft.Data.SqlClient 6.1.2** - SQL Server connectivity

## Architecture and Code Structure

### Core Architecture Patterns

**Clean Architecture Implementation:**
- **Domain Layer** (`/Domain`): Business entities and core logic
- **Data Access Layer** (`/DataAccess`): Entity Framework context and repositories
- **Service Layer** (`/Services`): Business services and MQTT handlers
- **Presentation Layer** (`/ViewModels`, XAML pages): MVVM implementation

**Key Directories:**
- `/Common` - Shared data models and communication objects (MQTT messages, sensor data)
  - `HallSensorData` - Hall sensor position data from track
  - `RFIDSensorData` - RFID reader data for train identification
  - `SignalRequest/Response` - Train signal operation messages
  - `SignalStateChangeResponse` - Signal state change notifications
  - `Constants` - System-wide constants
- `/Configuration` - System configuration classes (MQTT, Track, Timetable settings)
- `/Domain` - Entity Framework models (Train, Platforms, TrackConnections, etc.)
- `/Services` - Business logic services (TrackHandler, TrainManager, VirtualClock)
- `/ViewModels` - MVVM ViewModels with data binding
- `/DataAccess` - EF Core DbContext and repository implementations
- `/TrackHandler` - Complex track topology and routing logic
- `/Enums` - Enumeration types (currently empty, available for future enum definitions)
- `/Models` - UI-specific models and drawing helpers
  - `ScheduleDrawable` - Custom drawing for timeline visualization
  - `ScheduleItem` - Timetable entry display model

### Database Integration

**Database:** SQL Server `TrainControllerSystem`
**Connection:**
- Server: LAPTOP-ANHCTCLU\\SQLEXPRESS (development - change as needed)
- Authentication: SQL Server with APPLOGIN credentials
- Credentials: User Id = APPLOGIN, Password = 12345
- TrustServerCertificate: Enabled for development

**Key Entities:**
- `Trains` - Train information and current status
- `Platforms` - Station platforms and track sections
- `TimetableEntries` - Schedule entries with routes
- `TrackConnections` - Railway track topology and connections
- `Sections/SubSections` - Track segmentation for train control

**Database Views:**
- `V_PlatformEntry` - Complex view for platform entry routes
- `VLookupSectionNextSection` - Track section navigation mapping

**Entity Framework DbSets Available:**
- `Trains` - Train entities and current status
- `Stations` - Railway stations
- `Platforms` - Station platforms
- `Sections` - Track sections
- `SubSections` - Track subsections
- `Switches` - Railway switches
- `Objects` - Railway objects (signals, sensors, etc.)
- `TimetableEntries` - Schedule entries
- `TimetableEntriesArrived` - Completed schedule entries
- `TimetableEntriesUpcoming` - Upcoming schedule entries
- `TrackConnection` - Track topology connections
- `VLookupSectionNextSection` - Navigation view
- `VPlatformEntry` - Platform entry view
- `LookupSectionNextSection` - Section-to-section navigation lookup
- `LookupSectionNextSectionSwitches` - Switch constraints for sections
- `LookupSectionNextSectionDestinations` - Destination mapping for sections
- `LookupSectionsSubSections` - Section to subsection mapping
- `LookupSectionNextSectionNextSection` - Multi-section routing lookup

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
- One instance per active train

**TrainArrivalMonitorService** - Train arrival detection system
- Monitors hall sensor data for train positioning
- Detects train arrivals at platforms
- Integrates with RFID data for train identification
- Publishes arrival events to status notification service

**MqttInfrastructureService** - Centralized MQTT communication
- Single MQTT connection for entire application (shared by all services)
- Publishes commands to Rocrail service topics
- Subscribes to feedback topics (track status, sensor data)
- Handles connection management and error recovery

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
- `MqttInfrastructureService` - Centralized MQTT connection management
- `UnifiedPathfindingService` - Railway pathfinding algorithms
- `ITrackGraphFactory` and `TrackGraphFactory` - Track graph creation
- `TrackHandlerService` - Automated train scheduling
- `TrainArrivalMonitorService` - Train arrival detection
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

### Key Enumerations
The system uses several important enumerations defined in `Domain/Enums.cs`:
- `Speed`: STOP (0), SLOW (30), MEDIUM (60), HIGH (90) - train speed levels
- `TrainState`: Waiting, Moving, Stopped, PrepareToStop, Arrived - train operational states
- `EntryState`: Upcoming, InProgress, Arrived - timetable entry status
- `RouteState`: InTime, Delay - schedule adherence status
- `ObjectType`: Signal, Hall, RFID - railway object types for sensors

### Rocrail Integration
The system integrates with Rocrail model train software through:
- `RocrailCommandFactory` (Domain/RocrailCommandFactory.cs) - Creates XML commands for Rocrail
- MQTT topics for bidirectional communication with Rocrail server
- Support for switches, signals, locomotives, and track feedback
- Command deduplication to prevent duplicate MQTT messages

**RocrailCommandFactory Usage:**
```csharp
// System power control
RocrailCommandFactory.SystemPower(true);  // Power on
RocrailCommandFactory.SystemPower(false); // Power off

// Switch control
RocrailCommandFactory.Switch("switch1", "straight"); // or "turn"

// Signal control
RocrailCommandFactory.Signal("sig1", "red"); // or "green", "yellow"

// Train control with Speed enum
RocrailCommandFactory.TrainVelocity("train1", Speed.MEDIUM, true); // forward
RocrailCommandFactory.TrainVelocity("train1", Speed.STOP, false);  // reverse stop

// Train power and mode
RocrailCommandFactory.TrainPower("train1", true);
RocrailCommandFactory.TrainMode("train1", true);  // auto mode
```

### TrackGraph System
The `TrackGraph` class (Services/TrackGraph.cs) provides a graph-based representation of the railway network:
- Loads track topology from database views and lookup tables
- BFS (Breadth-First Search) pathfinding algorithm
- Platform-to-node mapping for station routing
- Switch configuration calculation for routes
- Handles bidirectional track navigation
- Edge validation to prevent invalid paths
- Used by UnifiedPathfindingService for route calculations

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
The project uses `UnifiedPathfindingService` for train routing calculations:
- Access through dependency injection in services
- Supports BFS-based pathfinding with conflict detection
- Provides required switch configurations for routes
- Integrates with TrackHandlerService for automated routing
- Uses `TrackGraph` class for graph-based railway network representation
- Uses `ITrackGraphFactory` to create track graph instances
- See `FindRouteBFS_Issues_Analysis.md` for debugging pathfinding issues

**Note**: The `PATHFINDING_USAGE_EXAMPLE.md` file mentions `RailwayPathfinderService` and `TrainPathfinderIntegration`, but the actual codebase uses `UnifiedPathfindingService` instead.

### Platform-Specific Development:
- **Android**: Requires Android SDK and Android Workload
- **iOS**: Requires Xcode and iOS development on Mac
- **Windows**: Requires Windows 10 SDK version 19041 or higher
- **Mac Catalyst**: Requires macOS Catalina or higher
- Use platform-specific folders for native implementations

This system is designed for real-time railway operations management and requires careful consideration of timing, concurrency, and hardware integration aspects.

### Code Language Notes
- The codebase contains mixed English and Hungarian (Magyar) comments and variable names
- Example: `// Regisztráljuk a DbContext-et` translates to "Register the DbContext"
- Platform names and station names often use Hungarian characters (é, á, ű, etc.)
- Be aware of character encoding when working with station names and database queries

## Common Development Workflows

### First-Time Setup
```bash
# 1. Install .NET MAUI workloads
dotnet workload install maui

# 2. Restore packages
dotnet restore

# 3. Update database connection in appsettings.json if needed
# 4. Ensure SQL Server is running and accessible
# 5. Run database migrations
dotnet-ef database update --project .

# 6. Build and run
dotnet build
dotnet run
```

### Adding New Database Entities
```bash
# 1. Create/modify domain models in /Domain folder
# 2. Add DbSet to ApplicationDbContext.cs
# 3. Create migration
dotnet-ef migrations add AddedNewEntity --project .

# 4. Update database
dotnet-ef database update --project .
```

### Adding New Services
1. Create service interface and implementation in `/Services`
2. Register service in `MauiProgram.cs` dependency injection
3. Add configuration to `appsettings.json` if needed
4. Update hard-coded configuration in `MauiProgram.cs`

### Debugging Common Issues

**MQTT Connection Issues:**
- Verify broker accessibility: 172.22.2.2:1883
- Check network connectivity and firewall settings
- Monitor MQTT topics for message flow

**Database Connection Issues:**
- Verify SQL Server is running
- Check APPLOGIN credentials and permissions
- Ensure TrainControllerSystem database exists
- Verify connection string format

**Build Issues:**
- Nullable reference warnings are expected and can be ignored
- Package conflicts from Microsoft.Azure.WebJobs are known issues
- Use Visual Studio for detailed build error analysis

**Runtime Issues:**
- Check VirtualClock timing for schedule-related problems
- Use Admin Panel for manual system control during debugging
- Monitor console output for pathfinding and routing debug information

### Pathfinding Debugging
When pathfinding fails (returns null routes), check:
1. Graph connectivity - verify `V_Lookup_Section_NextSection` view has data
2. Platform mapping - ensure platform names match database format (watch for Hungarian characters: é, á, ű, etc.)
3. Node validation - verify source and destination nodes exist in graph
4. Edge loading - check if edges were loaded from database connections
5. Direction mismatch - verify direction parameter (true/false) matches database expectations

Refer to `FindRouteBFS_Issues_Analysis.md` for detailed debugging steps and probable fixes.

### Hungarian Character Handling
The system handles Hungarian station names with special characters (é, á, ű, etc.):
- Platform name normalization in TrackGraph for matching
- Case-insensitive platform key generation
- Special character handling in database queries
- Be aware of character encoding when working with station names