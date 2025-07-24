using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace AqueductsBot;

[Submenu]
public class BotSettings
{
    [Menu("Max Runs", "Maximum runs before stopping (0 = infinite)")]
    public RangeNode<int> MaxRuns { get; set; } = new RangeNode<int>(0, 0, 1000);
    
    [Menu("Max Runtime Minutes", "Maximum runtime in minutes before stopping (0 = infinite)")]
    public RangeNode<int> MaxRuntimeMinutes { get; set; } = new RangeNode<int>(0, 0, 300);
}

[Submenu]
public class RadarSettings
{
    [Menu("Radar Path Check Radius", "Radius for checking radar path detection (larger = more area coverage)", 61)]
    public RangeNode<float> RadarPathCheckRadius { get; set; } = new RangeNode<float>(250f, 100f, 500f);
    
    [Menu("Intersection Check Radius", "Radius for path intersection calculations", 62)]
    public RangeNode<float> IntersectionCheckRadius { get; set; } = new RangeNode<float>(200f, 100f, 400f);
    
    [Menu("Path Intersect Range", "Range for radar path intersection detection", 63)]
    public RangeNode<float> PathIntersectRange { get; set; } = new RangeNode<float>(150f, 75f, 300f);
    
    [Menu("Show Player Circle", "Show diameter circle around player representing calculation radius")]
    public ToggleNode ShowPlayerCircle { get; set; } = new ToggleNode(false);
    
    [Menu("Player Circle Radius", "Radius of the visual circle around player (pixels)", 66)]
    public RangeNode<float> PlayerCircleRadius { get; set; } = new RangeNode<float>(200f, 50f, 500f);
    
    [Menu("Waypoint Check Frequency", "How often to check for next waypoint (ms)", 64)]
    public RangeNode<int> WaypointCheckFrequency { get; set; } = new RangeNode<int>(500, 100, 2000);
    
    [Menu("Path Request Timeout", "Maximum time to wait for path response (seconds)", 65)]
    public RangeNode<int> PathRequestTimeout { get; set; } = new RangeNode<int>(15, 5, 30);
}

[Submenu]
public class MovementSettings
{
    [Menu("Use Keyboard Movement", "Use keyboard key for movement instead of mouse clicks")]
    public ToggleNode UseMovementKey { get; set; } = new ToggleNode(false);
    
    [Menu("Movement Key", "Movement key to use (requires UseMovementKey enabled)")]
    public HotkeyNode MovementKey { get; set; } = new HotkeyNode(Keys.T);
    
    [Menu("Movement Precision", "Precision for waypoint detection (pixels)", 51)]
    public RangeNode<float> MovementPrecision { get; set; } = new RangeNode<float>(10f, 5f, 50f);

    [Menu("Min Move Delay", "Minimum delay between movements (ms)", 52)]
    public RangeNode<int> MinMoveDelayMs { get; set; } = new RangeNode<int>(200, 50, 1000);

    [Menu("Max Move Delay", "Maximum delay between movements (ms)", 53)]
    public RangeNode<int> MaxMoveDelayMs { get; set; } = new RangeNode<int>(800, 200, 2000);
    
    [Menu("Pursuit Radius", "Radius for path intersection navigation (larger = smoother, smaller = more precise)", 54)]
    public RangeNode<float> PursuitRadius { get; set; } = new RangeNode<float>(300f, 150f, 500f);
    
    [Menu("Auto-Click Delay", "Delay between automatic clicks (ms)", 55)]
    public RangeNode<int> AutoClickDelay { get; set; } = new RangeNode<int>(100, 25, 500);
    
    [Menu("Stuck Detection Threshold", "Number of failed movements before considering stuck", 56)]
    public RangeNode<int> StuckDetectionThreshold { get; set; } = new RangeNode<int>(5, 3, 15);
    
    [Menu("Path Advancement Distance", "Distance to advance when stuck (pixels)", 57)]
    public RangeNode<float> PathAdvancementDistance { get; set; } = new RangeNode<float>(100f, 50f, 300f);
}

[Submenu]
public class TimingSettings
{
    [Menu("State Check Interval", "How often to check bot state (ms)", 71)]
    public RangeNode<int> StateCheckInterval { get; set; } = new RangeNode<int>(100, 50, 500);
    
    [Menu("Radar Retry Interval", "How often to retry radar connection (seconds)", 72)]
    public RangeNode<int> RadarRetryInterval { get; set; } = new RangeNode<int>(2, 1, 10);
    
    [Menu("Area Transition Delay", "Delay after area transition detection (ms)", 73)]
    public RangeNode<int> AreaTransitionDelay { get; set; } = new RangeNode<int>(1000, 500, 3000);
    
    [Menu("Path Staleness Time", "Time before considering path stale (seconds)", 74)]
    public RangeNode<int> PathStalenessTime { get; set; } = new RangeNode<int>(30, 15, 60);
    
    [Menu("Movement Timeout", "Maximum time to wait for movement completion (ms)", 75)]
    public RangeNode<int> MovementTimeout { get; set; } = new RangeNode<int>(5000, 2000, 10000);
}

[Submenu]
public class DebugSettings
{
    [Menu("Debug Messages", "Enable detailed logging and debug information")]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);
    
    [Menu("Show Path Points", "Show visual path points on screen")]
    public ToggleNode ShowPathPoints { get; set; } = new ToggleNode(true);
    
    [Menu("Show Intersection Points", "Display path intersection calculations")]
    public ToggleNode ShowIntersectionPoints { get; set; } = new ToggleNode(false);
    
    [Menu("Show Movement Debug", "Display movement calculation details")]
    public ToggleNode ShowMovementDebug { get; set; } = new ToggleNode(false);
    
    [Menu("Log Path Analysis", "Log directional path analysis details")]
    public ToggleNode LogPathAnalysis { get; set; } = new ToggleNode(false);
    
    [Menu("Show Radar Status", "Display radar connection and pathfinding status")]
    public ToggleNode ShowRadarStatus { get; set; } = new ToggleNode(true);
    
    [Menu("Popout Status Window", "Show status information in separate moveable window")]
    public ToggleNode ShowPopoutStatus { get; set; } = new ToggleNode(false);
    
    [Menu("Popout Pathfinding Window", "Show pathfinding details in separate moveable window")]
    public ToggleNode ShowPopoutPathfinding { get; set; } = new ToggleNode(false);
}

[Submenu]
public class ConfigurationSettings
{
    [Menu("Min Click Distance", "Minimum distance for mouse clicks (pixels)", 81)]
    public RangeNode<int> MinClickDistance { get; set; } = new RangeNode<int>(200, 50, 400);
    
    [Menu("Preferred Click Distance", "Preferred distance for mouse clicks (pixels)", 82)]
    public RangeNode<int> PreferredClickDistance { get; set; } = new RangeNode<int>(350, 200, 600);
    
    [Menu("Max Lookahead Waypoints", "Maximum waypoints to look ahead for optimization", 83)]
    public RangeNode<int> MaxLookaheadWaypoints { get; set; } = new RangeNode<int>(15, 5, 25);
    
    [Menu("Circle Intersection Tolerance", "Tolerance for circle intersection calculations", 84)]
    public RangeNode<float> CircleIntersectionTolerance { get; set; } = new RangeNode<float>(0.6f, 0.2f, 1.0f);
    
    [Menu("Path Score Threshold", "Minimum score improvement to accept new path", 85)]
    public RangeNode<float> PathScoreThreshold { get; set; } = new RangeNode<float>(0.15f, 0.05f, 0.5f);
}

public class AqueductsBotSettings : ISettings
{
    [Menu("Plugin Enabled", "Enable/disable the entire plugin (use hotkeys/buttons to start/stop bot automation)")]
    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    
    // Removed duplicate movement settings - they're now only in MovementSettings section
    
    [Menu("Bot Settings", "")]
    public BotSettings BotSettings { get; set; } = new BotSettings();
    
    [Menu("Radar Status", "")]
    public RadarSettings RadarSettings { get; set; } = new RadarSettings();
    
    [Menu("Movement Settings", "")]
    public MovementSettings MovementSettings { get; set; } = new MovementSettings();
    
    [Menu("Timing Configuration", "")]
    public TimingSettings TimingSettings { get; set; } = new TimingSettings();
    
    [Menu("Debug Settings", "")]
    public DebugSettings DebugSettings { get; set; } = new DebugSettings();
    
    [Menu("Configuration", "")]
    public ConfigurationSettings ConfigurationSettings { get; set; } = new ConfigurationSettings();
    
    [Menu("Start/Stop Hotkey", "Start or stop the bot")]
    public HotkeyNode StartStopHotkey { get; set; } = new HotkeyNode(Keys.F1);
    
    [Menu("Emergency Stop Hotkey", "Emergency stop the bot immediately")]
    public HotkeyNode EmergencyStopHotkey { get; set; } = new HotkeyNode(Keys.F2);
} 