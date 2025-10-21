# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Train Control System is a .NET 8.0 monolithic application that manages a model railway system through MQTT communication and SQL Server database integration. The system coordinates train movements, track occupancy, signal management, and timetable scheduling.

## Build and Development Commands

```bash
# Build the project
dotnet build

# Run the application
dotnet run

# Clean build artifacts
dotnet clean

# Restore dependencies
dotnet restore

# Publish for release
dotnet publish -c Release
```

## Architecture Overview

The application follows a dependency injection pattern with a monolithic design containing several core services:

### Core Services
- **TrackManager** (Program.cs:371) - Manages track sections, occupancy, and signals
- **TimetableManager** (Program.cs:1466) - Handles train schedules and arrival tracking
- **TrainManagerService** (Program.cs:1599) - Coordinates train operations and speed controls
- **TimetableRepository** (Program.cs:1705) - Database access layer for timetable data

### MQTT Communication
- **TrackMQTTConnector** - Handles track-related MQTT messages
- **TimetableMQTTConnector** - Manages timetable MQTT communications
- **TrainMQTTConnector** - Controls train operations via MQTT
- **MQTTMessageHandler** - Central message routing and processing

### Background Services
- **TimetableSchedulerService** - Scheduled train departure management
- **SystemMonitorService** - System health monitoring
- **ConsoleInterface** - Command-line user interface

## Configuration

The application uses `appsettings.json` for configuration:
- **Database**: SQL Server connection string for TrainControllerSystem database
- **MQTT**: Broker address and topic mappings for track, train, and signal communication
- **Track**: Maximum occupied sections configuration
- **Timetable**: Scheduler interval and data retention settings

## Key Dependencies

- Entity Framework Core 8.0 with SQL Server
- MQTTnet 5.0 for MQTT communication
- Microsoft.Extensions.Hosting for background services
- Newtonsoft.Json for serialization

## Development Notes

- The application uses dependency injection with singleton service lifetime
- MQTT connectors are manually injected into TrackManager after host building (Program.cs:27-34)
- Database context uses NoTracking query behavior for performance
- All services are registered as singletons in the DI container
- The system processes real-time railway operations through MQTT message handling