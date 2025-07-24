using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace AqueductsBot;

[Submenu]
public class BotSettings
{
    [Menu(null, "Maximum runs before stopping (0 = infinite)")]
    public RangeNode<int> MaxRuns { get; set; } = new RangeNode<int>(0, 0, 1000);
    
    [Menu(null, "Maximum runtime in minutes before stopping (0 = infinite)")]
    public RangeNode<int> MaxRuntimeMinutes { get; set; } = new RangeNode<int>(0, 0, 300);
}

[Submenu]
public class MovementSettings
{
    [Menu(null, "Use keyboard key for movement instead of mouse clicks")]
    public ToggleNode UseMovementKey { get; set; } = new ToggleNode(false);
    
    [Menu(null, "Movement key to use (requires UseMovementKey enabled)")]
    public HotkeyNode MovementKey { get; set; } = new HotkeyNode(Keys.None);
    
    [Menu("Movement Precision", "Precision for waypoint detection (pixels)", 51)]
    public RangeNode<float> MovementPrecision { get; set; } = new RangeNode<float>(10f, 5f, 50f);

    [Menu("Min Move Delay", "Minimum delay between movements (ms)", 52)]
    public RangeNode<int> MinMoveDelayMs { get; set; } = new RangeNode<int>(200, 50, 1000);

    [Menu("Max Move Delay", "Maximum delay between movements (ms)", 53)]
    public RangeNode<int> MaxMoveDelayMs { get; set; } = new RangeNode<int>(800, 200, 2000);
    
    [Menu("Pursuit Radius", "Radius for path intersection navigation (larger = smoother, smaller = more precise)", 54)]
    public RangeNode<float> PursuitRadius { get; set; } = new RangeNode<float>(300f, 150f, 500f);
}

[Submenu]
public class DebugSettings
{
    [Menu(null, "Enable detailed logging and debug information")]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);
    
    [Menu(null, "Show visual path points on screen")]
    public ToggleNode ShowPathPoints { get; set; } = new ToggleNode(true);
}

public class AqueductsBotSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    
    [Menu("Movement", "Use keyboard key for movement instead of mouse clicks")]
    public ToggleNode UseMovementKey { get; set; } = new ToggleNode(false);
    
    [Menu("Movement Key", "Key to press for movement (leave as None to use mouse clicks)")]
    public HotkeyNode MovementKey { get; set; } = new HotkeyNode(Keys.T);
    
    [Menu("Bot Settings", "")]
    public BotSettings BotSettings { get; set; } = new BotSettings();
    
    [Menu("Movement Settings", "")]
    public MovementSettings MovementSettings { get; set; } = new MovementSettings();
    
    [Menu("Debug Settings", "")]
    public DebugSettings DebugSettings { get; set; } = new DebugSettings();
    
    [Menu("Start/Stop Hotkey", "Start or stop the bot")]
    public HotkeyNode StartStopHotkey { get; set; } = new HotkeyNode(Keys.F1);
    
    [Menu("Emergency Stop Hotkey", "Emergency stop the bot immediately")]
    public HotkeyNode EmergencyStopHotkey { get; set; } = new HotkeyNode(Keys.F2);
} 