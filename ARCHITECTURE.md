# Train Control System - Object-Oriented Architecture

## Overview

This document describes the refactored object-oriented architecture of the Train Control System, which has been transformed from a monolithic file into a well-structured, maintainable codebase following SOLID principles and object-oriented best practices.

## Project Structure

```
TrainStuff/
├── Models/                     # Data models and database context
│   ├── Entities.cs            # All entity classes (Train, Station, etc.)
│   └── ApplicationDbContext.cs # Entity Framework database context
├── Configuration/              # Configuration classes
│   └── SystemConfiguration.cs # MQTT, database, and system settings
├── Services/                   # Business logic services
│   ├── TrackManager.cs       # Track and signal management
│   ├── TimetableManager.cs   # Timetable operations and scheduling
│   ├── TrainController.cs    # Individual train control
│   ├── TrainManagerService.cs # Train coordination service
│   └── MachinistServiceFactory.cs # Dynamic machinist service management
├── MQTT/                       # MQTT communication classes
│   ├── TrackMQTTConnector.cs  # Track-related MQTT communication
│   ├── TimetableMQTTConnector.cs # Timetable MQTT communication
│   └── TrainMQTTConnector.cs  # Train control MQTT communication
├── Repositories/              # Data access layer
│   └── TimetableRepository.cs # Timetable database operations
├── BackgroundServices/        # Long-running background services
│   ├── MachinistService.cs   # Individual machinist background service
│   ├── TimetableSchedulerService.cs # Timetable scheduling
│   ├── SystemMonitorService.cs # System health monitoring
│   └── ConsoleInterface.cs    # Interactive console interface
├── Program.cs                # Application entry point and DI setup
├── appsettings.json          # Configuration file
└── CLAUDE.md                 # Claude development guidance
```

## Architecture Patterns

### 1. **Repository Pattern**
- `TimetableRepository` handles all database operations
- Abstracts data access logic from business logic
- Provides clean separation between data and service layers

### 2. **Factory Pattern**
- `MachinistServiceFactory` creates and manages machinist services
- Handles dynamic lifecycle management
- Provides centralized control over service instances

### 3. **Service Layer Pattern**
- Business logic separated into focused services
- Each service has a single responsibility
- Services communicate through well-defined interfaces

### 4. **Background Service Pattern**
- Long-running operations use .NET BackgroundService
- Proper cancellation token handling
- Graceful shutdown management

### 5. **Dependency Injection**
- Constructor injection throughout the application
- Proper service lifetime management
- Testable and maintainable code

## Key Components

### Models Layer
- **Entities**: All domain entities (Train, Station, Signal, etc.)
- **DbContext**: Entity Framework database configuration
- **Enums**: Type-safe enumerations for states and configurations

### Services Layer
- **TrackManager**: Manages track state, sections, and signals
- **TimetableManager**: Handles timetable operations and scheduling
- **TrainController**: Controls individual train operations
- **TrainManagerService**: Coordinates train operations (deprecated direct control)
- **MachinistServiceFactory**: Dynamic machinist service lifecycle management

### MQTT Layer
- **TrackMQTTConnector**: Track-related MQTT communications
- **TimetableMQTTConnector**: Timetable status MQTT communications
- **TrainMQTTConnector**: Train control MQTT communications

### Background Services
- **MachinistService**: Individual train control service (one per train)
- **TimetableSchedulerService**: Processes scheduled departures
- **SystemMonitorService**: Monitors system health
- **ConsoleInterface**: Interactive command interface

### Repository Layer
- **TimetableRepository**: All timetable database operations
- Thread-safe operations with proper semaphore usage

## Machinist Architecture

The system implements a scalable machinist architecture where:

1. **Each train has its own dedicated MachinistService**
2. **Machinists poll the timetable** for their assigned train's schedule
3. **Independent MQTT connections** for each machinist
4. **Automatic journey management** based on timetable entries
5. **Graceful startup and shutdown** of all machinist services

## Communication Flow

1. **User adds timetable entry** → TimetableManager → Database
2. **Machinist polls timetable** → Finds scheduled entry → Starts journey
3. **Machinist controls train** via dedicated MQTT topics
4. **Track manager monitors** track state and signals
5. **System monitor checks** health and performance

## Benefits of This Architecture

### Scalability
- Add more trains by creating additional machinist services
- Each machinist operates independently
- No single point of failure for train control

### Maintainability
- Clear separation of concerns
- Each class has a single responsibility
- Easy to test individual components

### Extensibility
- New features can be added without modifying existing code
- Easy to add new MQTT connectors or services
- Plugin-style architecture for background services

### Reliability
- Proper error handling throughout
- Graceful shutdown management
- Thread-safe operations

### Performance
- Optimized database access with proper connection handling
- Asynchronous operations where appropriate
- Efficient MQTT communication

## Configuration

The system uses a hierarchical configuration structure:

- **SystemConfiguration**: Root configuration class
- **MQTTConfiguration**: MQTT broker and topic settings
- **MachinistTopicsConfiguration**: Train-specific topic templates
- **TrackConfiguration**: Track management settings
- **TimetableConfiguration**: Scheduling and retention settings

## Error Handling

- Comprehensive logging throughout all layers
- Proper exception handling with context
- Graceful degradation when services fail
- Circuit breaker patterns for external dependencies

This architecture provides a solid foundation for a scalable, maintainable railway control system that follows object-oriented principles and modern .NET practices.