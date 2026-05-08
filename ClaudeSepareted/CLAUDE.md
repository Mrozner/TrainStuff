# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a **.NET MAUI Train Control System** - a sophisticated multi-platform mobile application for managing train schedules, routes, and real-time railway operations using MQTT communication and SQL Server database integration.

### Technology Stack
- **Framework**: .NET 10.0 MAUI (Multi-platform App UI)
- **Database**: Microsoft SQL Server with Entity Framework Core 10.0
- **Real-time Communication**: MQTTnet 5.0.1.1416 for train control messaging
- **Architecture**: Clean Architecture with MVVM pattern
- **Target Platforms**: Android, iOS, macOS Catalyst, Windows (Tizen commented out)

## Prerequisites

### Development Environment
- **.NET 10.0 SDK** - Required for building and running
- **MAUI Workloads** - Install with `dotnet workload install maui`
- **SQL Server** - Local or remote SQL Server instance for database
- **MQTT Broker** - Access to MQTT broker at 172.22.2.2:1883 (hardware integration)

### Platform-Specific Requirements
- **Windows**: Windows 10 SDK version 19041 or higher (minimum OS version 10.0.17763.0)
- **Android**: Minimum SDK 21.0 (Android 5.0 Lollipop)
- **iOS**: Minimum iOS 11.0
- **Mac Catalyst**: Minimum macOS 13.1 (Catalyst)
- **Tizen**: Minimum Tizen 6.5 (currently commented out in csproj)

## Development Commands

### Build and Run
```bash
# Build the entire solution for all platforms (expects warnings)
dotnet build

# Build for specific platform
dotnet build -f net10.0-windows10.0.19041.0  # Windows
dotnet build -f net10.0-android              # Android
dotnet build -f net10.0-ios                  # iOS
dotnet build -f net10.0-maccatalyst          # Mac Catalyst

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
- `/Configuration` - System configuration classes (MQTT, Track, Timetable settings)
- `/Domain` - Entity Framework models (Train, Platforms, TrackConnections, etc.)
- `/Services` - Business logic services (TrackHandler, TrainManager, VirtualClock)
- `/ViewModels` - MVVM ViewModels with data binding
- `/DataAccess` - EF Core DbContext and repository implementations
- `/Models` - UI-specific models and drawing helpers
- `/Platforms` - Platform-specific implementations (Android, iOS, Windows, MacCatalyst, Tizen)
- `/Resources` - MAUI resources (images, fonts, raw assets)

**Key Files:**
- `MauiProgram.cs` - Application entry point and DI container configuration
- `App.xaml.cs` - Application initialization
- `MainPage.xaml` + `MainPage.xaml.cs` - Main UI screen
- `AdminPanelPage.xaml` + `AdminPanelPage.xaml.cs` - Admin control interface
- `ApplicationDbContext.cs` - EF Core database context with all DbSets
- `appsettings.json` - Configuration file (duplicate of MauiProgram.cs hard-coded values)

### Database Integration

**Database:** SQL Server `TrainControllerSystem`
**Connection:**
- Server: LAPTOP-ANHCTCLU\\SQLEXPRESS (development - change as needed)
- Authentication: SQL Server with APPLOGIN credentials
- Credentials: User Id = APPLOGIN, Password = 12345
- TrustServerCertificate: Enabled for development

**DbContext Factory Pattern:**
- `IDbContextFactory<ApplicationDbContext>` registered for Singleton services
- Allows Singleton services to create DbContext instances on demand
- Prevents EF Core entity tracking issues across different DI scopes
- Used by SignalControllerService and other Singleton services that need database access
- Each database operation gets its own DbContext, disposed after use

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

**Important Domain Classes:**
- `RocrailCommandFactory` (Domain/RocrailCommandFactory.cs) - Static factory for generating Rocrail XML commands (switches, signals, locomotives, system power)
- `Direction` (Domain/Direction.cs) - Track direction handling
- `Enums` (Domain/Enums.cs) - All system enumerations (Speed, TrainState, EntryState, RouteState, ObjectType)
  - `TrainState.WaitingForClearance` - Train stopped at red signal waiting for track ahead to clear (added for dynamic block enforcement)
  - `ObjectType.Switch` - Railway switch objects for turnout control (added for hardware synchronization)

### MQTT Communication System

**MQTT Broker:** 172.22.2.2:1883 (critical - all hardware integration depends on this)

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

**PathfindingService** (`Services/PathfindingService.cs`) - Central railway pathfinding system
- High-level pathfinding operations with business logic
- Platform routing, train-aware pathfinding, and conflict detection
- Switch configuration calculation and management
- Platform availability checking
- Wraps TrackGraph for low-level graph operations
- Uses RoutingTableCacheService for performance optimization

**TrackGraph** (`Services/TrackGraph.cs`) - Low-level graph-based railway network representation
- Graph data structure with nodes (SubSections) and edges (connections)
- Loads track topology from database views and lookup tables
- BFS (Breadth-First Search) pathfinding algorithm
- Platform-to-node mapping with Hungarian character normalization
- Switch configuration calculation for routes
- Handles bidirectional track navigation
- Edge validation to prevent invalid paths
- Cached by ITrackGraphFactory for performance

**RoutingTableCacheService** (`Services/RoutingTableCacheService.cs`) - Route pre-calculation and caching
- Pre-calculates all possible routes between platforms at startup
- Caches routes using RouteKey (sourcePlatformId, destPlatformId, direction)
- Reduces runtime computational overhead
- Provides instant route lookup vs dynamic pathfinding
- Logs statistics: total routes, successful/failed counts, timing metrics

**SwitchConfigurationService** (`Services/SwitchConfigurationService.cs`) - Switch lock management and hardware synchronization
- Centralized switch reservation and release system
- Prevents switch configuration conflicts between trains
- Rolling switch release as trains progress through routes
- Thread-safe switch ownership tracking with ConcurrentDictionary
- MQTT command generation for Rocrail switch operations
- **Hardware Synchronization**: `SyncAllSwitchesToDefaultAsync()` - Safely synchronizes all physical switches to "straight" position with 200ms delays between commands to prevent DCC short-circuits

**TrackOccupancyService** (`Services/TrackOccupancyService.cs`) - Real-time physical occupancy tracking
- Central source of truth for train positions (SubSectionName → TrainName)
- Real-time position updates via Rocrail MQTT feedback messages
- Thread-safe occupancy tracking using ConcurrentDictionary
- Automatic train movement detection and event firing
- Adjacent section validation to prevent false positives
- Support for train registration and query operations
- Automatic obstruction detection for unidentified sensor triggers
- Event: `OnTrainMoved` fires when trains change position

**BlockReservationService** (`Services/BlockReservationService.cs`) - N-block look-ahead logical reservations
- Manages LOGICAL reservations (intent) separate from PHYSICAL occupancy
- Just-in-time block reservation where trains reserve blocks as they approach
- Collision detection by checking both physical occupancy and logical reservation
- Rolling block release as trains progress through their route
- Supports dynamic traffic flow without deadlocking on global route locks
- Distinguishes between where trains ARE vs where trains INTEND to go

**TrackHandlerService** (`Services/TrackHandlerService.cs`) - Automated train scheduling and routing system
- Manages train movements through track topology
- Handles switch and signal control
- Integrates with VirtualClock for simulation timing
- Uses PathfindingService for optimal route calculation
- Coordinates with BlockReservationService for collision avoidance
- Dynamic rolling-block operation instead of static reservation
- **Startup Sequence**: Automatically synchronizes hardware switches to safe default state during system initialization with proper power-on delays
- **Major Improvements**: Fixed semaphore race conditions, eliminated silent failures, implemented lightweight DTO pattern for caching, enforced cancellation token propagation, and standardized error logging
- Enhanced logging throughout with structured, contextual messages
- Improved memory footprint and performance by caching only necessary data
- See `TRACKHANDLER_IMPROVEMENTS_SUMMARY.md` for detailed improvement summary

**TrainMovementMonitor** (`Services/TrainMovementMonitor.cs`) - Look-ahead logic for dynamic block enforcement
- Calculates next expected subsection based on RoutePlan
- Checks if next section is occupied (look-ahead logic)
- Automatic train stop at red signals
- Automatic resume when track clears
- Subscribes to TrackOccupancyService.OnTrainMoved events
- Implements `TrainState.WaitingForClearance` for trains stopped at signals

**SignalControllerService** (`Services/SignalControllerService.cs`) - Traffic light control bridge
- Implements IAsyncDisposable for proper lifecycle management
- Subscribes to TrackOccupancyService.OnTrainMoved
- Calls ObjectsLibrary.GetLightInfo() for signal lookup
- Sets RED signals behind trains (rear protection)
- Sets GREEN signals ahead of trains (clearance)
- Look-ahead logic for predictive signal control
- Full cancellation token support for graceful shutdown
- Emergency stop capability via SetAllSignalsToRedAsync()
- Uses IDbContextFactory to create DbContext instances on demand
- **Major Refactoring**: 4-phase improvement including concurrency fixes, cancellation token propagation, architectural data flow fixes, and implementation of mocked logic
- See `SIGNAL_CONTROLLER_IMPROVEMENTS.md` for detailed refactoring summary

**RocrailCommandService** (`Services/RocrailCommandService.cs`) - Rocrail MQTT communication
- Sends Rocrail XML commands via MQTT
- Command deduplication to prevent duplicate MQTT messages
- Support for switches, signals, locomotives, and track feedback
- Uses MqttInfrastructureService for connection

**MqttInfrastructureService** - Centralized MQTT communication
- Single MQTT connection for entire application (shared by all services)
- Publishes commands to Rocrail service topics
- Subscribes to feedback topics (track status, sensor data)
- Handles connection management and error recovery
- **Comprehensive Refactoring**: 4-phase refactor addressing concurrency safety, memory leak prevention, connection resilience, and code modernization
- Thread-safe unsubscribe operations with retry logic
- Auto-reconnect capability with 5-second retry intervals
- Prevents reconnection task leaks during connection flapping
- Optimistic concurrency patterns for maximum performance
- See `MQTT_REFACTOR_SUMMARY.md` for detailed refactoring summary

**VirtualClock** (`Services/VirtualClock.cs`) - Time simulation system
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
- **Hardware Switch Synchronization**: "Váltók Szinkronizálása" button to safely synchronize all physical switches to default "straight" position
- **Route Cache Management**: "Útvonal Cache Újraépítése" button to rebuild the routing table cache
- **System Reset**: "Éjféli Reset" button to force system reset to initial state

### Configuration Management

**CRITICAL:** Configuration is duplicated between two locations - ALWAYS update both:
1. **appsettings.json** - JSON configuration file
2. **MauiProgram.cs** - Hard-coded configuration object (lines 27-57)

Both contain:
- Database connection string
- MQTT broker settings and topic configurations
- Track configuration (MaxOccupiedSections: 100)
- Timetable settings (SchedulerIntervalSeconds: 30)
- Virtual clock parameters

### Dependency Injection Services Registration

All services registered in `MauiProgram.cs`:
- `ApplicationDbContext` - EF Core database context (Scoped)
- `IDbContextFactory<ApplicationDbContext>` - Factory for Singleton services to create DbContext instances (Singleton)
- `SystemConfiguration` and sub-configurations (MQTT, Track, Timetable) (Singleton)
- `MainPageViewModel` and `MainPage` - MVVM main screen (Singleton)
- `ITimetableRepository` and `TimetableRepository` - Data access (Singleton)
- `RocrailCommandService` - Rocrail MQTT communication (Singleton)
- `StatusNotificationService` - Real-time status broadcasting (Singleton)
- `ConcurrentDictionary<int, Platforms>` and `ConcurrentDictionary<int, Sections>` - Shared dictionaries for pathfinding (Singleton)
- `VirtualClock` - Time simulation system (Singleton)
- `MqttInfrastructureService` - Centralized MQTT connection (Singleton)
- `RoutingTableCacheService` - Route pre-calculation and caching (Singleton)
- `SwitchConfigurationService` - Switch lock management (Singleton)
- `PathfindingService` - Railway pathfinding algorithms (Singleton)
- `ITrackGraphFactory` and `TrackGraphFactory` - Track graph creation (Singleton)
- `TrackOccupancyService` - Real-time physical occupancy tracking (Singleton)
- `TrackHandlerService` - Automated train scheduling (Singleton)
- `BlockReservationService` - N-block look-ahead logical reservations (Singleton)
- `SignalControllerService` - Traffic light control based on train movements (Singleton)
- `AdminPanelViewModel` and `AdminPanelPage` - Admin control interface (Transient)

**Service Lifetime Notes:**
- Most services are registered as Singleton (shared instance)
- `AdminPanelViewModel` and `AdminPanelPage` are registered as Transient (new instance each time)
  - **AdminPanelPage Dependencies**: Requires `SwitchConfigurationService` and `IDbContextFactory<ApplicationDbContext>` for hardware synchronization feature
- All services receive dependencies through constructor injection
- `ApplicationDbContext` is Scoped (should be short-lived)
- `IDbContextFactory<ApplicationDbContext>` allows Singleton services to create DbContext instances on demand
- `TrackGraphFactory` implements caching pattern - creates TrackGraph once and reuses it
- Shared dictionaries (`ConcurrentDictionary<int, Platforms>`, `ConcurrentDictionary<int, Sections>`) are used for thread-safe pathfinding optimization
- Services implement `IAsyncDisposable` for proper resource cleanup (e.g., SignalControllerService)

## Key Development Notes

### Configuration Management
**CRITICAL:** Configuration is duplicated between appsettings.json and hard-coded values in MauiProgram.cs. When making changes, update BOTH locations to maintain consistency.

### Null Reference Warnings
The build generates many CS8618 (non-nullable field) and CS8625 (null literal) warnings. These are expected due to the codebase using non-nullable reference types with Entity Framework and dependency injection patterns.

### Entity Framework Patterns
- Domain models use `[NotMapped]` for runtime properties
- Navigation properties are configured separately
- Database context uses SQL Server with TrustServerCertificate
- Enum values are stored as strings in the database (see ApplicationDbContext.cs lines 39-69)
- Uses raw SQL for database view queries: `FromSqlRaw("SELECT * FROM V_Lookup_Section_NextSection WHERE Section_DB_ID = {0}", sectionId)`

### MQTT Integration
- Services use dependency injection for MQTT client
- Async/await patterns for message handling
- Error handling for network connectivity issues
- Single shared connection via MqttInfrastructureService (DO NOT create multiple connections)
- TrackOccupancyService subscribes to `rocrail/service/info/fb` for position feedback
- RocrailCommandService sends commands via `rocrail/service/client` topic
- Event-driven architecture with OnTrainMoved events for real-time updates
- Auto-reconnect capability with 5-second retry intervals
- Thread-safe subscribe/unsubscribe operations with proper cleanup
- See `MQTT_REFACTOR_SUMMARY.md` for detailed MQTT service improvements

### MAUI-Specific Considerations
- XAML pages use data binding to ViewModels
- Platform-specific implementations in `/Platforms` directory
- GraphicsView for custom timeline visualization
- Picker controls for train/platform selection
- Target frameworks include Android, iOS, Mac Catalyst, and Windows 10 (version 19041+)
- Use `ImplicitUsings` and `Nullable` reference types enabled in project

### Thread Safety
- TrackHandlerService uses locking mechanisms for concurrent operations
- Dictionary collections for managing active trains and commands
- CancellationToken for service cancellation

### Key Enumerations
The system uses several important enumerations defined in `Domain/Enums.cs`:
- `Speed`: STOP (0), SLOW (30), MEDIUM (60), HIGH (90) - train speed levels
- `TrainState`: Waiting, Moving, Stopped, PrepareToStop, Arrived, WaitingForClearance - train operational states
  - `WaitingForClearance` - Train stopped at red signal waiting for track ahead to clear
- `EntryState`: Upcoming, InProgress, Arrived - timetable entry status
- `RouteState`: InTime, Delay - schedule adherence status
- `ObjectType`: Signal, Hall, RFID, Switch - railway object types for sensors and turnouts

**Direction Boolean Pattern:**
- `true` = forward/normal direction
- `false` = reverse/opposite direction
- Used throughout: train movement, track connections, pathfinding
- Database view `V_Lookup_Section_NextSection` has a `Direction` bit column

### Rocrail Integration
The system integrates with Rocrail model train software through:
- `RocrailCommandFactory` (Domain/RocrailCommandFactory.cs) - Creates XML commands for Rocrail
- MQTT topics for bidirectional communication with Rocrail server
- Support for switches, signals, locomotives, and track feedback
- Command deduplication to prevent duplicate MQTT messages

### Hardware Safety Constraints
**CRITICAL:** Physical turnout solenoids draw massive power and can cause DCC command station short-circuits if switched too rapidly:
- **Switch Synchronization**: Must use strictly sequential commands with **200ms minimum delays** between each switch operation
- **Power-On Delay**: System must wait **1 second after power-on** before firing switch solenoids to allow DCC capacitors to fully charge
- **NEVER use Task.WhenAll** for bulk switch operations - always use sequential foreach loops
- Always use `SwitchConfigurationService.SyncAllSwitchesToDefaultAsync()` for hardware synchronization, never manual parallel operations
- These constraints prevent damage to DCC command stations and power supplies

### Modern Architecture Patterns

**Dependency Injection Patterns:**
- Constructor injection for all service dependencies
- Singleton services for shared state and caching
- Transient services for UI components that need fresh instances
- Factory pattern (`IDbContextFactory`) for scoped dependencies in singleton services
- Proper async disposal patterns with `IAsyncDisposable`

**Concurrency and Thread Safety:**
- `ConcurrentDictionary` for thread-safe collections without locks
- Optimistic concurrency patterns with retry logic
- `volatile` keyword for visibility across threads
- `Interlocked` operations for atomic flag updates
- Semaphore-based mutual exclusion for critical sections
- Cancellation token propagation throughout async call chains

**Resource Management:**
- `IAsyncDisposable` implementation for graceful service shutdown
- Proper event handler cleanup to prevent memory leaks
- Background task lifecycle management
- MQTT subscription cleanup with `Unsubscribe()` method
- DbContext factory pattern to prevent entity tracking issues

**Error Handling and Observability:**
- Structured logging with contextual information
- Proper exception propagation (no silent failures)
- Critical error logging with stack traces
- User-friendly error messages via StatusNotificationService
- Debug prefixes for service-specific log filtering

### Pathfinding System Architecture

The project has a sophisticated four-layer pathfinding and traffic control system:

1. **TrackGraph** (`Services/TrackGraph.cs`) - Low-level graph representation
   - Graph data structure with nodes (SubSections) and edges (connections)
   - Loads from database using `V_Lookup_Section_NextSection` view
   - BFS pathfinding algorithm
   - Platform-to-node mapping with Hungarian character normalization

2. **PathfindingService** (`Services/PathfindingService.cs`) - High-level pathfinding operations
   - Wraps TrackGraph with business logic
   - Train movement planning and conflict detection
   - Switch configuration calculation via SwitchConfigurationService
   - Platform availability checking
   - Route plan generation with caching via RoutingTableCacheService

3. **TrackOccupancyService** - Real-time physical occupancy tracking
   - Tracks where trains actually are right now (PHYSICAL occupancy)
   - Subscribes to Rocrail MQTT feedback for position updates
   - Fires `OnTrainMoved` events when trains change position
   - Automatic obstruction detection for unidentified objects

4. **BlockReservationService** - N-block look-ahead logical reservations
   - Tracks where trains INTEND to go next (LOGICAL reservations)
   - Just-in-time block reservation as trains approach sections
   - Collision detection using both physical and logical occupancy
   - Rolling block release as trains progress

5. **TrackHandlerService** - Integration layer
   - Uses PathfindingService for route calculations
   - Executes train movements with MQTT commands
   - Coordinates with BlockReservationService for collision avoidance
   - Dynamic rolling-block operation instead of static reservation

**Usage:** Always use PathfindingService for pathfinding operations, never TrackGraph directly (except for low-level operations).

See `DYNAMIC_BLOCK_ENFORCEMENT.md` for implementation details of the dynamic block enforcement system and `TRACK_OCCUPANCY_INTEGRATION.md` for the track occupancy integration guide.

### Dynamic Block Enforcement System

The system implements **dynamic rolling-block operation** instead of static reservation-based control:

**Key Features:**
- Trains automatically stop at red signals
- Trains automatically resume when track clears
- Multiple trains can follow each other safely
- Rolling switch release as trains progress through routes
- Real-time position tracking via MQTT

**Architecture Components:**
- `TrainMovementMonitor` - Look-ahead logic to detect occupied sections ahead
- `TrackOccupancyService` - Real-time position tracking and event firing
- `SignalControllerService` - Traffic light control based on occupancy
- `BlockReservationService` - Logical reservations separate from physical occupancy
- `TrainState.WaitingForClearance` - Distinguishes trains stopped at signals from other stopped states

**Physical vs Logical Separation:**
- **PHYSICAL**: TrackOccupancyService tracks where trains actually are right now
- **LOGICAL**: BlockReservationService tracks where trains INTEND to go next

This separation enables dynamic traffic flow without deadlocking on global route locks.

See `DYNAMIC_BLOCK_ENFORCEMENT.md` and `TRACK_OCCUPANCY_INTEGRATION.md` for implementation details.

### Major Architectural Improvements

The codebase has undergone comprehensive refactoring across multiple services to address critical issues and modernize the architecture:

**Recent Major Improvements:**
- **SignalControllerService** - 4-phase refactoring including lifecycle management, cancellation token propagation, architectural data flow fixes, and implementation of mocked logic (see `SIGNAL_CONTROLLER_IMPROVEMENTS.md`)
- **TrackHandlerService** - Critical fixes for semaphore race conditions, silent failures, EF Core entity tracking issues, and logging inconsistencies (see `TRACKHANDLER_IMPROVEMENTS_SUMMARY.md`)
- **MqttInfrastructureService** - Comprehensive 4-phase refactor addressing concurrency safety, memory leak prevention, connection resilience, and code modernization (see `MQTT_REFACTOR_SUMMARY.md`)
- **Dynamic Block Enforcement** - Implemented rolling-block operation with automatic stop/resume at signals (see `DYNAMIC_BLOCK_ENFORCEMENT.md`)
- **Track Occupancy Integration** - Centralized real-time tracking of train positions with event-driven architecture (see `TRACK_OCCUPANCY_INTEGRATION.md`)

**Integration and Testing:**
- Complete integration checklist for collision avoidance system (see `FINAL_INTEGRATION_CHECKLIST.md`)
- All services now implement proper cancellation token propagation for graceful shutdown
- Enhanced logging throughout with structured, contextual messages
- Thread-safe operations using optimistic concurrency patterns

## Testing

**Current Status:** The project does not have automated tests.

**Testing Approach:**
- Manual testing via AdminPanelPage for train control and MQTT command testing
- Integration testing with real hardware (Rocrail model train software)
- Console logging with `[ServiceName-DEBUG]` prefixes for debugging
- SQL Server Profiler for database query monitoring
- MQTT broker logs for tracking hardware command delivery

**Recommended Testing Areas:**
1. **Service Initialization** - Verify all services initialize correctly in the right order
2. **Pathfinding** - Test route calculation between various platforms
3. **Collision Avoidance** - Test multiple trains on same route
4. **Signal Control** - Verify automatic signal updates based on train positions
5. **MQTT Communication** - Test command delivery and feedback processing
6. **Database Operations** - Verify EF Core migrations and CRUD operations
7. **Graceful Shutdown** - Test application shutdown without hanging or resource leaks

## Working with This Codebase

### When Adding New Features:
1. Follow the Clean Architecture pattern - keep business logic in Domain layer
2. Register new services in MauiProgram.cs dependency injection
3. Add database changes through Entity Framework migrations
4. Update configuration in BOTH appsettings.json AND MauiProgram.cs (critical!)
5. Consider MQTT topic implications for new features
6. Add corresponding Entity Framework DbSets to ApplicationDbContext.cs
7. Test on multiple target platforms if UI changes are made
8. For pathfinding features, use PathfindingService (never TrackGraph directly)
9. For traffic control, consider both physical occupancy (TrackOccupancyService) and logical reservations (BlockReservationService)
10. For real-time updates, subscribe to appropriate events (e.g., OnTrainMoved)
11. **CRITICAL**: For switch operations, always use SwitchConfigurationService
    - Includes automatic locking to prevent conflicts
    - For bulk synchronization, use `SyncAllSwitchesToDefaultAsync()` with built-in safety delays
    - NEVER use `Task.WhenAll` for multiple switches - sequential operation prevents DCC short-circuits
12. **Hardware Safety**: When working with physical switches, always observe 200ms minimum delays between commands and 1-second power-on delay

### When Debugging:
- Check VirtualClock time for timing-related issues
- Verify MQTT broker connectivity (172.22.2.2:1883)
- Monitor SQL Server connection and permissions
- Use the Admin Panel for manual control during testing
- Check StatusNotificationService logs for system events
- For pathfinding issues, check routing table statistics during initialization
- Look for `[ServiceName-DEBUG]` prefixes in console output for detailed service logs
- Check TrackOccupancyService occupancy map for train positions
- Check BlockReservationService logical reservations for intent tracking
- Monitor `OnTrainMoved` events for real-time position updates
- Refer to service-specific improvement documents for detailed debugging information:
  - `SIGNAL_CONTROLLER_IMPROVEMENTS.md` - Signal controller debugging
  - `TRACKHANDLER_IMPROVEMENTS_SUMMARY.md` - Track handler debugging
  - `MQTT_REFACTOR_SUMMARY.md` - MQTT service debugging

**Debugging Tools:**
- AdminPanelPage - Manual train speed/direction control and MQTT command testing
- Console output - Services write detailed logs (search for `[ServiceName-DEBUG]` prefixes)
- SQL Server Profiler - Monitor database queries and connection issues
- MQTT broker logs - Track hardware command delivery (if available)
- TrackOccupancyService occupancy map - Real-time train positions
- BlockReservationService logical reservations - Train intent tracking
- Routing table cache - Pre-calculated routes and statistics

### Database Schema Changes:
- Always use Entity Framework migrations
- Test migrations on development database first
- Consider impact on existing timetable entries
- Update related views if modifying track topology
- Update ApplicationDbContext.cs with new DbSets for new entities

### Pathfinding System Usage:
The project uses a four-layer pathfinding architecture:
1. **TrackGraph** - Low-level graph data structure with BFS pathfinding
2. **PathfindingService** - High-level pathfinding operations with business logic
3. **TrackOccupancyService** - Real-time physical occupancy tracking
4. **BlockReservationService** - N-block look-ahead logical reservations

**Usage Pattern:**
```csharp
// Always use PathfindingService through dependency injection
public class YourService
{
    private readonly PathfindingService _pathfinder;
    private readonly BlockReservationService _blockReservation;
    private readonly TrackOccupancyService _occupancyService;

    public YourService(
        PathfindingService pathfinder,
        BlockReservationService blockReservation,
        TrackOccupancyService occupancyService)
    {
        _pathfinder = pathfinder;
        _blockReservation = blockReservation;
        _occupancyService = occupancyService;
    }

    // Initialize pathfinding service (loads TrackGraph and builds routing cache)
    await _pathfinder.InitializeAsync();

    // Find route between platforms (uses cache for instant lookup)
    var routePlan = await _pathfinder.FindDirectPlatformRouteAsync(
        sourcePlatform,
        destPlatform,
        direction: true
    );

    if (routePlan != null && routePlan.HasPath)
    {
        // Reserve blocks for the journey (logical reservations)
        var success = await _blockReservation.ReserveBlocksForRouteAsync(
            routePlan,
            trainName: "Train1"
        );

        if (success)
        {
            // Configure switches for the route
            await _pathfinder.ConfigureSwitchesAsync(routePlan);

            // Register train position for real-time tracking
            await _occupancyService.RegisterTrainAsync(
                trainName: "Train1",
                initialSubSection: "section_1",
                direction: true
            );
        }
    }
}
```

**Key Methods in PathfindingService:**
- `InitializeAsync()` - Initialize service, load TrackGraph, build routing cache
- `FindDirectPlatformRouteAsync(sourcePlatform, destPlatform, direction)` - Find path between platforms (uses cache)
- `ConfigureSwitchesAsync(routePlan)` - Configure switches for route
- `CalculateTrainPathAsync(train)` - Calculate physical track route (conflict detection handled by block reservation system)
- `FindAvailablePlatformAsync(stationId, allTrains)` - Find available platform at station

**Key Methods in BlockReservationService:**
- `TryReserveBlock(string sectionName, string trainName)` - Reserve a single block
- `ReserveBlocksForRouteAsync(RoutePlan routePlan, string trainName)` - Reserve all blocks for a route
- `ReleaseBlocksBehindTrainAsync(string trainName, string clearedSubSection)` - Release blocks as train progresses
- `ReleaseAllTrainBlocksAsync(string trainName)` - Release all blocks held by a train
- `IsBlockReserved(string sectionName)` - Check if block is reserved
- `GetReservingTrain(string sectionName)` - Get which train reserved a block

**Key Methods in TrackOccupancyService:**
- `RegisterTrainAsync(string trainName, string initialSubSection, bool direction)` - Register train for tracking
- `UpdateTrainPositionAsync(string trainName, string newSubSection, bool direction)` - Update train position
- `IsSectionOccupied(string sectionName)` - Check if section is physically occupied
- `GetTrainAtSection(string sectionName)` - Get which train occupies a section
- `GetTrainPosition(string trainName)` - Get train's current position
- Event: `OnTrainMoved` - Fires when train changes position

**Result Classes:**
- `RoutePlan` - Contains `Path`, `HasPath`, `TotalDistance`, `GetSwitchConfigurations()`
- `PreCalculatedRoute` - Cached route with `RouteExists`, `RoutePath`, `RoutePlan`, `RequiredSwitches`, `RouteDescription`
- `TrainMovedEventArgs` - Event args with `TrainId`, `PreviousSubSection`, `CurrentSubSection`, `Direction`

### Pathfinding Debugging
When pathfinding fails (returns null routes), check:
1. Graph connectivity - verify `V_Lookup_Section_NextSection` view has data
2. Platform mapping - ensure platform names match database format (watch for Hungarian characters: é, á, ű, etc.)
3. Node validation - verify source and destination nodes exist in graph
4. Edge loading - check if edges were loaded from database connections
5. Direction mismatch - verify direction parameter (true/false) matches database expectations
6. Service initialization - ensure PathfindingService.InitializeAsync() was called
7. Routing table cache - check initialization logs for route coverage statistics

### Hungarian Character Handling
The system handles Hungarian station names with special characters (é, á, ű, etc.):
- Platform name normalization in TrackGraph for matching
- Case-insensitive platform key generation
- Special character handling in database queries
- Be aware of character encoding when working with station names

### Platform-Specific Development:
- **Android**: Requires Android SDK and Android Workload
- **iOS**: Requires Xcode and iOS development on Mac
- **Windows**: Requires Windows 10 SDK version 19041 or higher
- **Mac Catalyst**: Requires macOS Catalina or higher
- Use platform-specific folders for native implementations

### Common Issues and Solutions

**Issue**: Build succeeds with warnings but pathfinding returns null
**Solution**: Check database view `V_Lookup_Section_NextSection` has data. Verify platform names match database format (watch for Hungarian characters). Ensure PathfindingService is initialized (`InitializeAsync()` called). Check routing table statistics in initialization logs.

**Issue**: Service initialization fails with routing table build errors
**Solution**: The PathfindingService pre-calculates all routes at startup via RoutingTableCacheService. Check console logs for routing table builder statistics. Failed routes are cached and won't cause initialization to fail.

**Issue**: Switch configuration conflicts between multiple trains
**Solution**: Use SwitchConfigurationService's lock management. Switches are reserved during route planning and released as trains progress. Check for stuck locks from crashed trains.

**Issue**: Trains not stopping at red signals
**Solution**: Check if TrainMovementMonitor is properly configured with RoutePlan. Verify TrackOccupancyService is receiving MQTT feedback and firing OnTrainMoved events. Check if TrainState.WaitingForClearance is being set.

**Issue**: Trains not resuming after track clears
**Solution**: Verify TrainMovementMonitor is subscribed to TrackOccupancyService.OnTrainMoved. Check if the waiting section matches the cleared section in the event args. Ensure train is in WaitingForClearance state.

**Issue**: MQTT commands not reaching hardware
**Solution**: Verify broker connectivity (172.22.2.2:1883). Check that MqttInfrastructureService is initialized. Use AdminPanelPage to test manual commands. Check RocrailCommandService for command deduplication issues.

**Issue**: Trains not moving despite being scheduled
**Solution**: Check VirtualClock is running. Verify train has valid CurrentSubSection_DB_ID and DestinationPlatform_DB_ID. Check TrackHandlerService is processing entries. Verify blocks are reserved via BlockReservationService.

**Issue**: Database connection errors
**Solution**: Verify SQL Server is running. Update connection string in BOTH appsettings.json and MauiProgram.cs. Check APPLOGIN user has permissions.

**Issue**: Configuration changes not taking effect
**Solution**: Remember to update configuration in BOTH appsettings.json AND MauiProgram.cs hard-coded values (lines 27-57).

**Issue**: TrackOccupancyService showing wrong positions
**Solution**: Check if MQTT subscription to `rocrail/service/info/fb` is active. Verify Rocrail feedback messages are being received. Check for adjacent section validation issues.

**Issue**: BlockReservationService conflicts
**Solution**: Distinguish between physical occupancy (TrackOccupancyService) and logical reservations (BlockReservationService). Ensure blocks are released as trains progress, not just at journey end.

**Issue**: DCC command station short-circuits during switch operations
**Solution**: Ensure you're using `SwitchConfigurationService.SyncAllSwitchesToDefaultAsync()` which enforces 200ms delays between commands. Never use `Task.WhenAll` for switch operations. Verify 1-second power-on delay is implemented in startup sequence.

**Issue**: Switches not responding to synchronization commands
**Solution**: Verify MQTT broker connectivity (172.22.2.2:1883). Check that ObjectType enum includes "Switch" value. Verify Objects table has switch entries with ObjectType.Switch. Check RocrailCommandService for command deduplication issues. Use AdminPanelPage to test individual switch commands.

### Performance Optimization

**Routing Table Pre-Calculation:**
The RoutingTableCacheService implements a caching system that pre-calculates all possible routes at startup:
- Route calculations are cached in `_routingTable` using `RouteKey` (sourcePlatformId, destPlatformId, direction)
- Initialization logs show statistics: total routes calculated, successful/failed counts, timing metrics
- Cache lookup is instant vs dynamic pathfinding which can take seconds per route
- Cache is built in `BuildRoutingTableAsync()` during PathfindingService initialization
- Failed routes are cached to prevent repeated failed lookups

**Event-Driven Architecture:**
- TrackOccupancyService uses `ConcurrentDictionary` for thread-safe occupancy tracking
- `OnTrainMoved` events enable real-time updates without polling
- Adjacent section validation prevents false positives from sensor noise
- Automatic train identification via database queries reduces manual registration

**Switch Locking Mechanism:**
- SwitchConfigurationService manages switch locks with `ConcurrentDictionary<string, string>`
- Rolling switch release as trains progress through routes (not just at journey end)
- Switch locks are released via `ReleaseSwitchesBehindTrainAsync()` as train clears sections
- Thread-safe operations prevent race conditions between multiple trains

**Block Reservation System:**
- BlockReservationService uses logical reservations separate from physical occupancy
- Just-in-time reservation reduces lock contention
- Rolling block release enables dynamic traffic flow
- N-block look-ahead enables trains to follow each other safely

**Shared Dictionaries:**
- `Dictionary<int, Platforms>` and `Dictionary<int, Sections>` are shared across services
- Reduces database queries for frequently accessed entities
- Populated during service initialization

### Common Classes and Data Structures

**Common/RouteKey.cs** - Key for routing table cache:
- Properties: `SourcePlatformId`, `DestinationPlatformId`, `Direction`
- Used as dictionary key in `_routingTable`
- Implements `Equals()` and `GetHashCode()` for dictionary lookups

**Common/PreCalculatedRoute.cs** - Cached route result:
- Properties: `RouteExists`, `RoutePath`, `RoutePlan`, `RequiredSwitches`, `RouteDescription`
- Methods: `CreateSuccessful()`, `CreateFailed()` static factory methods

**Common/Classes**:
- `HallSensorData` - Hall sensor position data for train detection
- `RFIDSensorData` - RFID reader data for train identification
- `SignalRequest` / `SignalResponse` - Signal operation messages
- `SignalStateChangeResponse` - Signal change notifications

**Services/TrainMovedEventArgs.cs** - Event arguments for train movement:
- Properties: `TrainId`, `PreviousSubSection`, `CurrentSubSection`, `Direction`
- Used by TrackOccupancyService.OnTrainMoved event
- Enables real-time position updates across the system

### Event-Driven Architecture

The system uses an event-driven architecture for real-time updates:

**TrackOccupancyService Events:**
- `OnTrainMoved` - Fires when a train changes position
  - Subscribers: TrainMovementMonitor, SignalControllerService, BlockReservationService
  - Enables automatic stop/resume at signals
  - Triggers rolling block releases
  - Updates traffic signals based on train positions

**Event Flow Example:**
```
1. Rocrail MQTT feedback received → TrackOccupancyService
2. TrackOccupancyService updates occupancy map
3. TrackOccupancyService fires OnTrainMoved event
4. TrainMovementMonitor checks if next section is occupied
5. If occupied: Stop train, set state to WaitingForClearance
6. If not occupied: Resume train if in WaitingForClearance state
7. SignalControllerService updates traffic signals
8. BlockReservationService releases logical reservations
```

This event-driven architecture enables dynamic, rolling-block operation without polling or centralized coordination.

## Additional Documentation

The project includes detailed documentation for major architectural improvements and systems:

**Core Systems:**
- `DYNAMIC_BLOCK_ENFORCEMENT.md` - Complete implementation guide for the dynamic block enforcement system
- `TRACK_OCCUPANCY_INTEGRATION.md` - Track occupancy integration guide with architecture changes and testing procedures
- `FINAL_INTEGRATION_CHECKLIST.md` - Complete integration checklist for collision avoidance system

**Service Improvements:**
- `SIGNAL_CONTROLLER_IMPROVEMENTS.md` - 4-phase refactoring summary for SignalControllerService
- `TRACKHANDLER_IMPROVEMENTS_SUMMARY.md` - Critical fixes and architectural improvements for TrackHandlerService
- `MQTT_REFACTOR_SUMMARY.md` - Comprehensive refactoring summary for MqttInfrastructureService

**Refer to these documents for:**
- Detailed implementation guides and architecture diagrams
- Step-by-step integration procedures
- Testing recommendations and verification procedures
- Troubleshooting specific service issues
- Understanding recent code changes and improvements
