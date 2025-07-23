# AqueductsBot Plugin for ExileApi

A simple bot that uses the Radar plugin to navigate through the Aqueducts area in Path of Exile.

## Features

- **Simple Path Following**: Uses Radar's pathfinding to move to area exits
- **Human-like Movement**: Random timing between 100ms-1000ms to avoid detection
- **State Management**: Clear bot states with extensive logging for debugging
- **Safety Features**: Emergency stop (F2) and error handling
- **Debug Mode**: Visual path display and detailed logging

## Requirements

- Windows OS (uses Windows API for input simulation)
- ExileApi (ExileCore) installed and working
- Radar plugin installed and working
- .NET 8.0 SDK for building

## Installation

### 1. Build the Plugin

```bash
# Navigate to the AqueductsBot project directory
cd /path/to/AqueductsBot

# Build the project
dotnet build -c Release
```

### 2. Install the Plugin

1. Copy the built `AqueductsBot.dll` from `bin/Release/net8.0-windows/` 
2. Place it in your ExileApi plugins directory:
   - Usually: `ExileApi/Plugins/Compiled/AqueductsBot/`
3. Restart ExileApi

### 3. Configure Environment Variable

You may need to set the `exapiPackage` environment variable to point to your ExileApi directory, or modify the `.csproj` file to use absolute paths:

```xml
<Reference Include="ExileCore">
  <HintPath>C:\Path\To\ExileApi\ExileCore.dll</HintPath>
  <Private>False</Private>
</Reference>
```

## Usage

### Basic Setup

1. **Load Path of Exile** in windowed or windowed fullscreen mode
2. **Start ExileApi** and ensure both Radar and AqueductsBot plugins are loaded
3. **Enter the Aqueducts area** 
4. **Open ExileApi menu** (F12) and navigate to AqueductsBot settings

### Controls

- **F1**: Start/Stop Bot (configurable in settings)
- **F2**: Emergency Stop (configurable in settings)
- **Settings Panel**: Shows bot status, runtime, and manual controls

### Bot States

- **Disabled**: Bot is off
- **WaitingForRadar**: Waiting for Radar plugin to be available
- **WaitingForAqueducts**: Waiting for player to enter Aqueducts
- **GettingPath**: Requesting path from Radar
- **MovingAlongPath**: Following the calculated path
- **AtAreaExit**: Reached the area exit (bot stops here)
- **Error**: Error state requiring manual restart

## Configuration

### Movement Settings
- **Min/Max Move Delay**: Random timing between actions (100-1000ms recommended)
- **Movement Precision**: How close to reach each waypoint (pixels)

### Debug Settings
- **Debug Mode**: Enable detailed logging
- **Show Path Points**: Visual display of the path being followed

### Safety Settings
- **Max Runs**: Automatic stop after N completions (0 = infinite)
- **Max Runtime**: Automatic stop after N minutes (0 = infinite)

## Current Limitations

### Version 1.0 Scope (CURRENT)
- ✅ Simple path following to area exits
- ✅ Manual area transitions required
- ✅ Basic Aqueducts detection
- ✅ Windows API input simulation
- ✅ Human-like random timing

### Future Enhancements (TODO)
- ❌ Automatic area transitions and looping
- ❌ Monster detection and clearing
- ❌ Item pickup integration
- ❌ Advanced safety features (health monitoring, etc.)
- ❌ Memory-based movement (vs input simulation)

## Troubleshooting

### Common Issues

1. **"Could not connect to Radar plugin"**
   - Ensure Radar plugin is loaded and working
   - Check ExileApi plugin load order

2. **"Not in Aqueducts"**
   - Check area name detection in logs
   - May need to update `IsInAqueducts()` method for exact area names

3. **Bot not moving**
   - Check Windows API permissions
   - Ensure game is in windowed mode
   - Check movement delay settings

4. **Build errors**
   - Verify .NET 8.0 SDK installed
   - Check ExileCore.dll path in .csproj
   - Ensure all NuGet packages are restored

### Debug Information

The plugin logs extensively to:
- ExileApi console output
- Debug console (if available)
- Settings panel "Recent Log" section

Enable Debug Mode for maximum verbosity.

## Architecture Notes

### Plugin Structure
- **Separate Plugin**: Works alongside Radar, doesn't modify it
- **Bridge API**: Uses `GameController.PluginBridge.GetMethod("Radar.LookForRoute")`
- **State Machine**: Clear bot states for predictable behavior
- **Windows API**: Uses `user32.dll` for mouse input simulation

### Safety Design
- Exception handling around all major operations
- Emergency stop functionality
- Automatic state transitions on errors
- No direct memory writing (read-only memory access)

## Contributing

This is a simple, focused bot designed for learning and basic automation. Future enhancements should maintain the principle of keeping each feature as simple as possible.

### Development Setup
1. Clone alongside the Radar plugin source
2. Use the same development environment as ExileApi
3. Test thoroughly with extensive logging since direct testing may be limited

## Legal Notice

This tool is for educational purposes. Users are responsible for compliance with Path of Exile's Terms of Service and any applicable automation policies. 