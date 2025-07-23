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
    
    [Menu(null, "Minimum delay between movements in milliseconds")]
    public RangeNode<int> MinMoveDelayMs { get; set; } = new RangeNode<int>(100, 50, 1000);
    
    [Menu(null, "Maximum delay between movements in milliseconds")]
    public RangeNode<int> MaxMoveDelayMs { get; set; } = new RangeNode<int>(500, 100, 2000);
    
    [Menu(null, "How close to reach each waypoint in pixels")]
    public RangeNode<int> MovementPrecision { get; set; } = new RangeNode<int>(10, 5, 50);
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
    
    public BotSettings BotSettings { get; set; } = new BotSettings();
    public MovementSettings MovementSettings { get; set; } = new MovementSettings();
    public DebugSettings DebugSettings { get; set; } = new DebugSettings();
    
    [Menu(null, "Start or stop the bot")]
    public HotkeyNode StartStopHotkey { get; set; } = new HotkeyNode(Keys.F1);
    
    [Menu(null, "Emergency stop the bot immediately")]
    public HotkeyNode EmergencyStopHotkey { get; set; } = new HotkeyNode(Keys.F2);
} 