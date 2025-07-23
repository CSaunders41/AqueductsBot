using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using GameOffsets.Native;
using ImGuiNET;
using SharpDX;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace AqueductsBot;

public class AqueductsBot : BaseSettingsPlugin<AqueductsBotSettings>
{
    // Windows API for input simulation
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    
    // Bot state
    private enum BotState
    {
        Disabled,
        WaitingForRadar,
        WaitingForAqueducts,
        GettingPath,
        MovingAlongPath,
        AtAreaExit,
        Error
    }
    
    private BotState _currentState = BotState.Disabled;
    private List<Vector2i> _currentPath = new();
    private int _currentPathIndex = 0;
    private DateTime _lastActionTime = DateTime.Now;
    private DateTime _botStartTime;
    private int _runsCompleted = 0;
    private Random _random = new();
    private string _lastLogMessage = "";
    
    // Radar integration
    private Action<Vector2, Action<List<Vector2i>>, CancellationToken> _radarLookForRoute;
    private bool _radarAvailable = false;
    private CancellationTokenSource _pathfindingCts = new();
    
    public override bool Initialise()
    {
        try
        {
            LogMessage("AqueductsBot initializing...");
            
            // Try to get Radar's pathfinding method
            var radarMethod = GameController.PluginBridge.GetMethod<Action<Vector2, Action<List<Vector2i>>, CancellationToken>>("Radar.LookForRoute");
            if (radarMethod != null)
            {
                _radarLookForRoute = radarMethod;
                _radarAvailable = true;
                LogMessage("Successfully connected to Radar plugin");
            }
            else
            {
                LogMessage("WARNING: Could not connect to Radar plugin. Make sure Radar is loaded.");
                _radarAvailable = false;
            }
            
            // Register hotkeys
            Input.RegisterKey(Settings.StartStopHotkey);
            Input.RegisterKey(Settings.EmergencyStopHotkey);
            
            Settings.StartStopHotkey.OnValueChanged += () => Input.RegisterKey(Settings.StartStopHotkey);
            Settings.EmergencyStopHotkey.OnValueChanged += () => Input.RegisterKey(Settings.EmergencyStopHotkey);
            
            LogMessage("AqueductsBot initialized successfully");
            return true;
        }
        catch (Exception ex)
        {
            LogError($"Failed to initialize AqueductsBot: {ex.Message}");
            return false;
        }
    }
    
    public override void AreaChange(AreaInstance area)
    {
        try
        {
            LogMessage($"Area changed to: {area.Area.Name} (RawName: {area.Area.RawName})");
            
            // Reset path when changing areas
            _currentPath.Clear();
            _currentPathIndex = 0;
            
            // Check if we're in Aqueducts
            if (IsInAqueducts(area))
            {
                LogMessage("Detected Aqueducts area!");
                if (_currentState == BotState.WaitingForAqueducts)
                {
                    _currentState = BotState.GettingPath;
                }
            }
            else
            {
                LogMessage($"Not in Aqueducts. Current area: {area.Area.Name}");
                if (_currentState != BotState.Disabled)
                {
                    _currentState = BotState.WaitingForAqueducts;
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in AreaChange: {ex.Message}");
        }
    }
    
    public override void Render()
    {
        try
        {
            // Handle hotkeys
            if (Settings.StartStopHotkey.PressedOnce())
            {
                ToggleBot();
            }
            
            if (Settings.EmergencyStopHotkey.PressedOnce())
            {
                EmergencyStop();
            }
            
            // Main bot logic
            if (Settings.Enable && _currentState != BotState.Disabled)
            {
                ProcessBotLogic();
            }
            
            // Debug rendering
            if (Settings.DebugSettings.DebugMode && Settings.DebugSettings.ShowPathPoints && _currentPath.Count > 0)
            {
                DrawPathDebug();
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in Render: {ex.Message}");
            _currentState = BotState.Error;
        }
    }
    
    public override void DrawSettings()
    {
        // Show current bot status
        ImGui.Text($"Bot State: {_currentState}");
        ImGui.Text($"Radar Available: {_radarAvailable}");
        ImGui.Text($"Runs Completed: {_runsCompleted}");
        
        if (_botStartTime != default)
        {
            var runtime = DateTime.Now - _botStartTime;
            ImGui.Text($"Runtime: {runtime:hh\\:mm\\:ss}");
        }
        
        ImGui.Text($"Current Path Points: {_currentPath.Count}");
        ImGui.Text($"Path Progress: {_currentPathIndex}/{_currentPath.Count}");
        
        if (ImGui.Button("Start/Stop Bot"))
        {
            ToggleBot();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Emergency Stop"))
        {
            EmergencyStop();
        }
        
        ImGui.Separator();
        ImGui.Text("Recent Log:");
        ImGui.TextWrapped(_lastLogMessage);
    }
    
    private void ProcessBotLogic()
    {
        switch (_currentState)
        {
            case BotState.WaitingForRadar:
                if (_radarAvailable)
                {
                    _currentState = BotState.WaitingForAqueducts;
                    LogMessage("Radar available, waiting for Aqueducts area");
                }
                break;
                
            case BotState.WaitingForAqueducts:
                // Wait for player to enter Aqueducts
                // AreaChange will handle the transition
                break;
                
            case BotState.GettingPath:
                if (CanRequestNewPath())
                {
                    RequestPathToExit();
                }
                break;
                
            case BotState.MovingAlongPath:
                if (_currentPath.Count > 0 && _currentPathIndex < _currentPath.Count)
                {
                    MoveAlongPath();
                }
                else
                {
                    LogMessage("Reached end of path");
                    _currentState = BotState.AtAreaExit;
                }
                break;
                
            case BotState.AtAreaExit:
                LogMessage("Bot completed path to area exit. Manual transition required.");
                // For now, stop the bot here. Later we can add automatic area transition
                _currentState = BotState.Disabled;
                Settings.Enable.Value = false;
                break;
                
            case BotState.Error:
                LogMessage("Bot in error state. Manual restart required.");
                Settings.Enable.Value = false;
                break;
        }
    }
    
    private void ToggleBot()
    {
        if (Settings.Enable)
        {
            LogMessage("Stopping bot...");
            Settings.Enable.Value = false;
            _currentState = BotState.Disabled;
            _pathfindingCts.Cancel();
        }
        else
        {
            LogMessage("Starting bot...");
            Settings.Enable.Value = true;
            _botStartTime = DateTime.Now;
            _runsCompleted = 0;
            
            if (!_radarAvailable)
            {
                _currentState = BotState.WaitingForRadar;
                LogMessage("Waiting for Radar plugin...");
            }
            else if (!IsInAqueducts(GameController.Area.CurrentArea))
            {
                _currentState = BotState.WaitingForAqueducts;
                LogMessage("Waiting for Aqueducts area...");
            }
            else
            {
                _currentState = BotState.GettingPath;
                LogMessage("In Aqueducts, getting path...");
            }
        }
    }
    
    private void EmergencyStop()
    {
        LogMessage("EMERGENCY STOP activated!");
        Settings.Enable.Value = false;
        _currentState = BotState.Disabled;
        _pathfindingCts.Cancel();
        _currentPath.Clear();
    }
    
    private bool IsInAqueducts(AreaInstance area)
    {
        // Check for Aqueducts area - you may need to adjust this based on exact area names
        var areaName = area.Area.Name.ToLowerInvariant();
        var rawName = area.Area.RawName.ToLowerInvariant();
        
        return areaName.Contains("aqueduct") || rawName.Contains("aqueduct");
    }
    
    private bool CanRequestNewPath()
    {
        // Don't spam path requests
        return (DateTime.Now - _lastActionTime).TotalMilliseconds > 1000;
    }
    
    private void RequestPathToExit()
    {
        try
        {
            LogMessage("Requesting path to area exit...");
            _lastActionTime = DateTime.Now;
            
            // For now, use a simple target point. Later we can make this smarter
            // by finding actual area transitions
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos)
            {
                LogMessage("Could not get player position");
                return;
            }
            
            // Simple target: move in a direction (this is a placeholder)
            var currentPos = playerPos.GridPos;
            var targetPos = new Vector2(currentPos.X + 100, currentPos.Y + 100);
            
            _radarLookForRoute(targetPos, OnPathReceived, _pathfindingCts.Token);
        }
        catch (Exception ex)
        {
            LogError($"Error requesting path: {ex.Message}");
            _currentState = BotState.Error;
        }
    }
    
    private void OnPathReceived(List<Vector2i> path)
    {
        try
        {
            if (path == null || path.Count == 0)
            {
                LogMessage("No path received from Radar");
                return;
            }
            
            _currentPath = path;
            _currentPathIndex = 0;
            _currentState = BotState.MovingAlongPath;
            
            LogMessage($"Received path with {path.Count} points");
        }
        catch (Exception ex)
        {
            LogError($"Error processing received path: {ex.Message}");
        }
    }
    
    private void MoveAlongPath()
    {
        try
        {
            if (_currentPathIndex >= _currentPath.Count)
                return;
                
            var targetPoint = _currentPath[_currentPathIndex];
            
            // Convert grid coordinates to screen coordinates
            var worldPos = new Vector3(
                targetPoint.X * 250f / 23f, // GridToWorldMultiplier from Radar
                targetPoint.Y * 250f / 23f,
                0
            );
            
            var screenPosSharp = GameController.IngameState.Camera.WorldToScreen(worldPos);
            var screenPos = new Vector2(screenPosSharp.X, screenPosSharp.Y);
            
            // Check if we need to move
            var playerScreenPos = GetPlayerScreenPosition();
            if (playerScreenPos.HasValue)
            {
                var distance = Vector2.Distance(screenPos, playerScreenPos.Value);
                
                if (distance < Settings.MovementSettings.MovementPrecision)
                {
                    // Close enough, move to next point
                    _currentPathIndex++;
                    LogMessage($"Reached waypoint {_currentPathIndex}/{_currentPath.Count}");
                    return;
                }
            }
            
            // Check if enough time has passed since last action
            var timeSinceLastAction = (DateTime.Now - _lastActionTime).TotalMilliseconds;
            var requiredDelay = _random.Next(Settings.MovementSettings.MinMoveDelayMs, Settings.MovementSettings.MaxMoveDelayMs);
            
            if (timeSinceLastAction >= requiredDelay)
            {
                // Perform the move
                ClickAt((int)screenPos.X, (int)screenPos.Y);
                _lastActionTime = DateTime.Now;
                
                if (Settings.DebugSettings.DebugMode)
                {
                    LogMessage($"Moving to point {_currentPathIndex}: ({targetPoint.X}, {targetPoint.Y}) -> Screen({screenPos.X:F0}, {screenPos.Y:F0})");
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in MoveAlongPath: {ex.Message}");
        }
    }
    
    private Vector2? GetPlayerScreenPosition()
    {
        try
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var render = player?.GetComponent<Render>();
            if (render == null) return null;
            
            var worldPos = GameController.IngameState.Camera.WorldToScreen(render.PosNum);
            return new Vector2(worldPos.X, worldPos.Y);
        }
        catch
        {
            return null;
        }
    }
    
    private void ClickAt(int x, int y)
    {
        try
        {
            SetCursorPos(x, y);
            Thread.Sleep(10); // Small delay between cursor move and click
            
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            Thread.Sleep(50); // Hold click briefly
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
        }
        catch (Exception ex)
        {
            LogError($"Error clicking at ({x}, {y}): {ex.Message}");
        }
    }
    
    private void DrawPathDebug()
    {
        // Simple debug visualization of the current path
        // This will draw on the ImGui overlay
    }
    
    private void LogMessage(string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var logLine = $"[{timestamp}] {message}";
        _lastLogMessage = logLine;
        
        // Also output to debug console if available
        Debug.WriteLine($"[AqueductsBot] {logLine}");
        
        // You might want to write to a log file here too
        Console.WriteLine($"[AqueductsBot] {logLine}");
    }
    
    private void LogError(string message)
    {
        LogMessage($"ERROR: {message}");
    }
} 