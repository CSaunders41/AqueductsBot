using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using System.Windows.Forms;

namespace AqueductsBot;

public class AqueductsBotSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(false);
    
    [Menu("Bot Settings")]
    public EmptyNode BotSettingsNode { get; set; } = new EmptyNode();
    
    [Menu("Max Runs (0 = infinite)", parentIndex = 1)]
    public RangeNode<int> MaxRuns { get; set; } = new RangeNode<int>(0, 0, 1000);
    
    [Menu("Max Runtime Minutes (0 = infinite)", parentIndex = 1)]
    public RangeNode<int> MaxRuntimeMinutes { get; set; } = new RangeNode<int>(0, 0, 300);
    
    [Menu("Movement Settings")]
    public EmptyNode MovementSettingsNode { get; set; } = new EmptyNode();
    
    [Menu("Min Move Delay (ms)", parentIndex = 2)]
    public RangeNode<int> MinMoveDelayMs { get; set; } = new RangeNode<int>(100, 50, 1000);
    
    [Menu("Max Move Delay (ms)", parentIndex = 2)]
    public RangeNode<int> MaxMoveDelayMs { get; set; } = new RangeNode<int>(500, 100, 2000);
    
    [Menu("Movement Precision (pixels)", parentIndex = 2)]
    public RangeNode<int> MovementPrecision { get; set; } = new RangeNode<int>(10, 5, 50);
    
    [Menu("Debug Settings")]
    public EmptyNode DebugSettingsNode { get; set; } = new EmptyNode();
    
    [Menu("Debug Mode", parentIndex = 3)]
    public ToggleNode DebugMode { get; set; } = new ToggleNode(false);
    
    [Menu("Show Path Points", parentIndex = 3)]
    public ToggleNode ShowPathPoints { get; set; } = new ToggleNode(true);
    
    [Menu("Hotkeys")]
    public EmptyNode HotkeysNode { get; set; } = new EmptyNode();
    
    [Menu("Start/Stop Bot", parentIndex = 4)]
    public HotkeyNode StartStopHotkey { get; set; } = new HotkeyNode(Keys.F1);
    
    [Menu("Emergency Stop", parentIndex = 4)]
    public HotkeyNode EmergencyStopHotkey { get; set; } = new HotkeyNode(Keys.F2);
} 