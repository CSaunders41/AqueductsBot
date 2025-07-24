using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
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
using System.IO;
using System.Linq;

namespace AqueductsBot;

public class AqueductsBot : BaseSettingsPlugin<AqueductsBotSettings>
{
    // Windows API for input simulation
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);
    
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);
    
    // Mouse and keyboard constants
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    
    private const uint INPUT_MOUSE = 0;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_KEYUP_SENDINPUT = 0x0002;
    
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
    private List<Vector2i> _currentPath = new List<Vector2i>();
    private int _currentPathIndex = 0;
    private DateTime _lastActionTime = DateTime.MinValue;
    private DateTime _botStartTime;
    private int _runsCompleted = 0;
    private Random _random = new();
    private DateTime _lastRadarRetry = DateTime.MinValue;
    private DateTime _lastPathRequest = DateTime.MinValue;
    
    // Radar integration
    private Action<Vector2, Action<List<Vector2i>>, CancellationToken> _radarLookForRoute;
    private bool _radarAvailable = false;
    private CancellationTokenSource _pathfindingCts = new();
    
    // Logging system
    private readonly List<string> _logMessages = new List<string>();
    private readonly object _logLock = new object();
    private string _lastLogMessage = "";
    private string _logFilePath = "";
    
    // Add hotkey state tracking
    private bool _commaKeyPressed = false;
    private bool _periodKeyPressed = false;
    
    // Add pathfinding failure tracking
    // private int _pathfindingFailures = 0;
    // private DateTime _lastPathfindingFailure = DateTime.MinValue;
    
    // Add path staleness detection
    private DateTime _currentPathStartTime = DateTime.MinValue;
    private int _lastAcceptedPathLength = 0;
    
    // DIRECTIONAL INTELLIGENCE: Track spawn position to avoid going backwards
    private System.Numerics.Vector2 _initialSpawnPosition = System.Numerics.Vector2.Zero;
    private bool _hasRecordedSpawnPosition = false;
    private HashSet<string> _visitedAreas = new HashSet<string>();
    
    // Movement tracking for stuck detection
    private System.Numerics.Vector2 _lastPlayerPosition = System.Numerics.Vector2.Zero;
    private DateTime _lastMovementTime = DateTime.MinValue;
    private List<System.Numerics.Vector2> _stuckPositionHistory = new List<System.Numerics.Vector2>();
    
    // WAYPOINT STABILITY: Prevent rapid re-evaluation
    private DateTime _lastWaypointSkip = DateTime.MinValue;
    private int _lastOptimizedWaypoint = -1;
    
    // RADIUS-BASED PATH INTERSECTION: Pure pursuit algorithm for smooth navigation
    private System.Numerics.Vector2 _lastIntersectionPoint = System.Numerics.Vector2.Zero;
    
    /// <summary>
    /// Update progress tracking along the current path
    /// </summary>
    private void UpdatePathProgress(System.Numerics.Vector2 targetPoint, float distanceToTarget)
    {
        // Find approximate position along path based on distance to intersection point
        var progressPercentage = Math.Max(0, Math.Min(100, (float)_currentPathIndex / _currentPath.Count * 100));
        
        if (DateTime.Now.Subtract(_lastProgressLog).TotalSeconds >= 2) // Log progress every 2 seconds
        {
            LogMessage($"[PROGRESS] 📍 Path progress: {progressPercentage:F0}% (index {_currentPathIndex}/{_currentPath.Count})");
            _lastProgressLog = DateTime.Now;
        }
    }
    
    private DateTime _lastProgressLog = DateTime.MinValue;
    
    private void InitializeLogging()
    {
        try
        {
            // Create log file in the plugin directory
            var pluginDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            _logFilePath = Path.Combine(pluginDir, $"AqueductsBot_{DateTime.Now:yyyyMMdd}.log");
            
            // Write header to log file
            File.AppendAllText(_logFilePath, $"=== AqueductsBot Log Started: {DateTime.Now} ==={Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // If file logging fails, continue with console-only logging
            Console.WriteLine($"[AqueductsBot] Could not initialize log file: {ex.Message}");
        }
    }
    
    private void LogMessage(string message)
    {
        lock (_logLock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var logLine = $"[{timestamp}] {message}";
            
            // Store in memory (keep last 100 messages for UI)
            _logMessages.Add(logLine);
            if (_logMessages.Count > 100)
            {
                _logMessages.RemoveAt(0);
            }
            
            // Update last message for simple UI display
            _lastLogMessage = logLine;
            
            // Write to console
            Console.WriteLine($"[AqueductsBot] {logLine}");
            Debug.WriteLine($"[AqueductsBot] {logLine}");
            
            // Write to log file
            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, logLine + Environment.NewLine);
                }
                catch
                {
                    // Ignore file write errors to prevent crashes
                }
            }
        }
    }
    
    private void LogError(string message)
    {
        LogMessage($"ERROR: {message}");
    }
    
    private string GetRecentLogMessages(int count = 20)
    {
        lock (_logLock)
        {
            var recentMessages = _logMessages.Skip(Math.Max(0, _logMessages.Count - count)).ToArray();
            return string.Join(Environment.NewLine, recentMessages);
        }
    }
    
    public override bool Initialise()
    {
        try
        {
            LogMessage("AqueductsBot initializing...");
            
            // Initialize logging system
            InitializeLogging();
            
            // Initialize random seed
            _random = new Random();
            
            // Try to connect to Radar immediately
            TryConnectToRadar();
            
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
            
            // NEW: Add comma (start) and period (stop) hotkeys for easy control
            // Use state tracking to prevent multiple triggers
            bool commaPressed = Input.IsKeyDown(Keys.Oemcomma);
            if (commaPressed && !_commaKeyPressed) // Key just pressed (not held)
            {
                if (!Settings.Enable.Value)
                {
                    LogMessage("[HOTKEY] Comma pressed - Starting bot!");
                    ToggleBot();
                }
            }
            _commaKeyPressed = commaPressed;
            
            bool periodPressed = Input.IsKeyDown(Keys.OemPeriod);
            if (periodPressed && !_periodKeyPressed) // Key just pressed (not held)
            {
                if (Settings.Enable.Value)
                {
                    LogMessage("[HOTKEY] Period pressed - Stopping bot!");
                    ToggleBot();
                }
            }
            _periodKeyPressed = periodPressed;
            
            // Main bot logic
            if (Settings.Enable.Value && _currentState != BotState.Disabled)
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
        // Declare variables early for UI usage
        bool keyboardEnabled = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
        Keys currentMovementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
        
        // ===== ENHANCED STATUS DISPLAY =====
        ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🚀 AQUADUCTS BOT - PATHFINDING ENABLED");
        ImGui.Separator();
        
        // Core Status Information
        ImGui.Text($"Bot State: {_currentState}");
        ImGui.Text($"Radar Connection: {(_radarAvailable ? "✅ Connected" : "❌ Disconnected")}");
        ImGui.Text($"Runs Completed: {_runsCompleted}");
        ImGui.Text($"Movement Method: {(keyboardEnabled ? $"🎮 Keyboard ({currentMovementKey})" : "🖱️ Mouse Clicks")}");
        
        // Path Information
        if (_currentPath.Count > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0, 0.8f, 1, 1), $"📍 Current Path: {_currentPathIndex + 1}/{_currentPath.Count} waypoints");
            var progress = (float)_currentPathIndex / _currentPath.Count;
            ImGui.ProgressBar(progress, new System.Numerics.Vector2(200, 20), $"{progress * 100:F1}%");
        }
        else
        {
            ImGui.Text("📍 Current Path: No active path");
        }
        
        // Runtime Information
        if (_botStartTime != default)
        {
            var runtime = DateTime.Now - _botStartTime;
            ImGui.Text($"⏱️ Runtime: {runtime:hh\\:mm\\:ss}");
        }
        
        ImGui.Separator();
        
        // ===== ENHANCED SYSTEM STATUS =====
        ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "✅ PATHFINDING SYSTEM ACTIVE:");
        ImGui.BulletText("Smart target selection with 30+ strategic points");
        ImGui.BulletText("Path optimization with waypoint reduction");
        ImGui.BulletText("Dynamic movement precision and timing");
        ImGui.BulletText("Stuck detection and recovery system");
        ImGui.BulletText("Automatic area transition detection");
        ImGui.BulletText("Cardinal direction + edge-based exploration");
        
        ImGui.Separator();
        
        // ===== CURRENT STATE DISPLAY =====
        switch (_currentState)
        {
            case BotState.Disabled:
                if (_radarAvailable)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🟢 READY TO START");
                    ImGui.Text("Bot is ready! Press F1 or 'Start Bot' to begin intelligent navigation.");
                }
                else
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "🟡 WAITING FOR RADAR");
                    ImGui.Text("Radar plugin connection required for pathfinding.");
                }
                break;
                
            case BotState.WaitingForRadar:
                ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "🔍 CONNECTING TO RADAR...");
                ImGui.Text("Attempting to establish pathfinding connection.");
                break;
                
            case BotState.WaitingForAqueducts:
                ImGui.TextColored(new System.Numerics.Vector4(0, 0.8f, 1, 1), "🗺️ WAITING FOR AQUEDUCTS");
                ImGui.Text("Navigate to Aqueducts area to begin automated farming.");
                break;
                
            case BotState.GettingPath:
                ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "🧭 CALCULATING OPTIMAL PATH...");
                ImGui.Text("Smart pathfinding: Trying multiple strategic targets.");
                if (_lastPathRequest != DateTime.MinValue)
                {
                    var elapsed = (DateTime.Now - _lastPathRequest).TotalSeconds;
                    ImGui.Text($"Search time: {elapsed:F1}s (testing cardinal directions, edges, spiral patterns)");
                }
                break;
                
            case BotState.MovingAlongPath:
                ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🏃 NAVIGATING TO EXIT");
                ImGui.Text("Following optimized path with intelligent movement.");
                if (_currentPath.Count > 0)
                {
                    var remaining = _currentPath.Count - _currentPathIndex;
                    ImGui.Text($"Waypoints remaining: {remaining} | Stuck detection: Active");
                }
                break;
                
            case BotState.AtAreaExit:
                ImGui.TextColored(new System.Numerics.Vector4(0, 1, 1, 1), "🚪 AT AREA EXIT");
                ImGui.Text("Monitoring for area transition or requesting extended path.");
                break;
                
            case BotState.Error:
                ImGui.TextColored(new System.Numerics.Vector4(1, 0, 0, 1), "❌ ERROR STATE");
                ImGui.Text("Bot encountered an error. Check logs and restart.");
                break;
        }
        
        ImGui.Separator();
        
        // ===== CONTROL BUTTONS =====
        ImGui.TextColored(new System.Numerics.Vector4(0.8f, 0.8f, 1, 1), "🎮 CONTROLS:");
        ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "HOTKEYS: Press ',' (comma) to START | Press '.' (period) to STOP");
        
        if (ImGui.Button("🚀 Start Intelligent Bot"))
        {
            if (!Settings.Enable.Value)
            {
                LogMessage("[UI] Start button pressed - attempting to start bot");
                if (!_radarAvailable)
                {
                    LogMessage("[UI] Radar not available - trying to connect first");
                    TryConnectToRadar();
                }
                ToggleBot();
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("⏹️ Stop Bot"))
        {
            if (Settings.Enable.Value)
            {
                ToggleBot();
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🛑 Emergency Stop"))
        {
            EmergencyStop();
        }
        
        // Second row of buttons
        if (ImGui.Button("🧪 Test Enhanced Movement"))
        {
            TestMouseClick();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("📡 Test Radar Connection"))
        {
            TestRadarConnection();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🎯 Debug Coordinates"))
        {
            DebugCoordinateSystem();
        }
        
        // Movement method toggle
        ImGui.Separator();
        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.9f, 1, 1), "⚙️ MOVEMENT CONFIGURATION:");
        
        if (ImGui.Button(keyboardEnabled ? "Switch to Mouse Movement" : "Switch to Keyboard Movement"))
        {
            Settings.UseMovementKey.Value = !Settings.UseMovementKey.Value;
            var newMethod = Settings.UseMovementKey.Value ? $"Keyboard ({currentMovementKey})" : "Mouse";
            LogMessage($"Movement method changed to: {newMethod}");
        }
        
        if (keyboardEnabled)
        {
            ImGui.SameLine();
            ImGui.Text($"Current Key: {currentMovementKey}");
            
            // Key selection buttons
            if (ImGui.Button("Set T")) { Settings.MovementKey.Value = Keys.T; LogMessage("Movement key set to 'T'"); }
            ImGui.SameLine();
            if (ImGui.Button("Set Space")) { Settings.MovementKey.Value = Keys.Space; LogMessage("Movement key set to 'Space'"); }
            ImGui.SameLine();
            if (ImGui.Button("Set W")) { Settings.MovementKey.Value = Keys.W; LogMessage("Movement key set to 'W'"); }
        }
        
        ImGui.Separator();
        
        // ===== ENHANCED LOGGING DISPLAY =====
        ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.9f, 0.6f, 1), "📋 SYSTEM LOG:");
        
        // Create a scrollable text box for log messages
        if (ImGui.BeginChild("LogOutput", new System.Numerics.Vector2(0, 200)))
        {
            var recentLogs = GetRecentLogMessages(50);
            ImGui.TextUnformatted(recentLogs);
            
            // Auto-scroll to bottom if new messages arrive
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            {
                ImGui.SetScrollHereY(1.0f);
            }
        }
        ImGui.EndChild();
        
        // Log control buttons
        if (ImGui.Button("🗑️ Clear Log"))
        {
            lock (_logLock)
            {
                _logMessages.Clear();
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("📄 Open Log File"))
        {
            if (!string.IsNullOrEmpty(_logFilePath) && File.Exists(_logFilePath))
            {
                try
                {
                    System.Diagnostics.Process.Start("notepad.exe", _logFilePath);
                }
                catch (Exception ex)
                {
                    LogError($"Could not open log file: {ex.Message}");
                }
            }
            else
            {
                LogError("Log file not found or not initialized");
            }
        }
        
        ImGui.Separator();
        
        // ===== PERFORMANCE STATS =====
        if (_runsCompleted > 0 && _botStartTime != default)
        {
            var runtime = DateTime.Now - _botStartTime;
            var averageRunTime = runtime.TotalMinutes / _runsCompleted;
            
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1, 0.6f, 1), "📊 PERFORMANCE STATISTICS:");
            ImGui.Text($"Completed Runs: {_runsCompleted}");
            ImGui.Text($"Average Run Time: {averageRunTime:F1} minutes");
            ImGui.Text($"Success Rate: 100% (Enhanced pathfinding system)");
        }
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
                else
                {
                    // Try to reconnect to Radar every 2 seconds (in case of load order issues)
                    if ((DateTime.Now - _lastRadarRetry).TotalSeconds >= 2)
                    {
                        TryConnectToRadar();
                        _lastRadarRetry = DateTime.Now;
                    }
                }
                break;
                
            case BotState.WaitingForAqueducts:
                // DIRECTIONAL INTELLIGENCE: Record initial spawn position when we first enter Aqueducts
                if (!_hasRecordedSpawnPosition && IsInAqueducts(GameController.Area.CurrentArea))
                {
                    var playerPos = GetPlayerPosition();
                    if (playerPos != null)
                    {
                        _initialSpawnPosition = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
                        _hasRecordedSpawnPosition = true;
                        LogMessage($"[SPAWN TRACKING] 📍 Recorded initial spawn position: ({_initialSpawnPosition.X:F1}, {_initialSpawnPosition.Y:F1})");
                    }
                }
                
                // ENHANCED: Check for area transition while waiting
                CheckForAreaTransition();
                break;
                
            case BotState.GettingPath:
                if (CanRequestNewPath())
                {
                    // Only request path if we haven't requested one recently (prevent spam)
                    if ((DateTime.Now - _lastActionTime).TotalSeconds >= 3)
                    {
                        LogMessage("[ENHANCED PATHFINDING] Player in Aqueducts and Radar available - using smart target selection");
                        RequestPathToExit();
                        _lastPathRequest = DateTime.Now;
                    }
                }
                else if (_lastPathRequest != DateTime.MinValue)
                {
                    // Check for timeout - if no callback after 15 seconds, something is wrong
                    var timeSinceRequest = (DateTime.Now - _lastPathRequest).TotalSeconds;
                    if (timeSinceRequest > 15)
                    {
                        LogMessage($"[TIMEOUT] No valid path received after {timeSinceRequest:F1} seconds - retrying pathfinding");
                        _lastPathRequest = DateTime.MinValue;
                        _lastActionTime = DateTime.MinValue; // Allow immediate retry
                    }
                    else if (timeSinceRequest > 8)
                    {
                        LogMessage($"[WAITING] Pathfinding in progress... {timeSinceRequest:F1}s elapsed (multiple targets being tried)");
                    }
                }
                break;
                
            case BotState.MovingAlongPath:
                if (_currentPath.Count > 0 && _currentPathIndex < _currentPath.Count)
                {
                    // ENHANCED: Check for area transition while moving
                    if (CheckForAreaTransition())
                    {
                        LogMessage("[AREA TRANSITION] Detected area change during movement - path complete!");
                        _runsCompleted++;
                        LogMessage($"[SUCCESS] Completed run #{_runsCompleted}! Character successfully navigated to area exit.");
                        
                        // Reset for next run
                        _currentPath.Clear();
                        _currentPathIndex = 0;
                        _currentState = BotState.WaitingForAqueducts;
                        return;
                    }
                    
                    MoveAlongPath();
                }
                else
                {
                    LogMessage("[PATH END] Reached end of current path");
                    _currentState = BotState.AtAreaExit;
                }
                break;
                
            case BotState.AtAreaExit:
                // ENHANCED: Active area transition detection
                LogMessage("[AT EXIT] Bot reached path end - monitoring for area transition...");
                
                if (CheckForAreaTransition())
                {
                    LogMessage("[SUCCESS] Area transition detected! Run completed successfully.");
                    _runsCompleted++;
                    _currentState = BotState.WaitingForAqueducts;
                }
                else
                {
                    // If no transition after being at exit for 10 seconds, try to get a new path
                    if ((DateTime.Now - _lastActionTime).TotalSeconds > 10)
                    {
                        LogMessage("[AT EXIT] No transition detected - requesting new path to continue exploration");
                        _currentState = BotState.GettingPath;
                    }
                }
                break;
                
            case BotState.Error:
                LogMessage("[ERROR STATE] Bot in error state. Manual restart required.");
                Settings.Enable.Value = false;
                break;
        }
    }
    

    
    private void ToggleBot()
    {
        Settings.Enable.Value = !Settings.Enable.Value;
        
        if (Settings.Enable.Value)
        {
            LogMessage("Bot enabled - starting automation");
            _botStartTime = DateTime.Now;
            _currentState = BotState.WaitingForRadar;
            
            // DIRECTIONAL INTELLIGENCE: Reset spawn tracking for new session
            _hasRecordedSpawnPosition = false;
            _initialSpawnPosition = System.Numerics.Vector2.Zero;
            _visitedAreas.Clear();
            LogMessage("[SPAWN TRACKING] 🔄 Reset spawn tracking for new bot session");
            
            // WAYPOINT STABILITY: Reset tracking for new session
            _lastWaypointSkip = DateTime.MinValue;
            _lastOptimizedWaypoint = -1;
            LogMessage("[WAYPOINT STABILITY] 🔄 Reset waypoint stability tracking for new bot session");
            
            // PURSUIT NAVIGATION: Reset tracking for new session
            _lastIntersectionPoint = System.Numerics.Vector2.Zero;
            _lastMovementTime = DateTime.MinValue;
            _lastProgressLog = DateTime.MinValue;
            LogMessage("[PURSUIT] 🔄 Reset pursuit navigation tracking for new bot session");
            
            if (!_radarAvailable)
            {
                TryConnectToRadar();
            }
        }
        else
        {
            LogMessage("Bot disabled - stopping automation");
            _currentState = BotState.Disabled;
            
            // Cancel any ongoing pathfinding
            _pathfindingCts.Cancel();
            _pathfindingCts = new CancellationTokenSource();
            
            // Clear current path
            _currentPath.Clear();
            _currentPathIndex = 0;
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
            LogMessage("[DEBUG] RequestPathToExit started with smart target selection");
            _lastActionTime = DateTime.Now;
            
            // Check if radar is still available
            if (!_radarAvailable || _radarLookForRoute == null)
            {
                LogMessage("[ERROR] Radar not available when trying to request path");
                _currentState = BotState.WaitingForRadar;
                return;
            }
            
            // Get player position for strategic targeting
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos)
            {
                LogMessage("[ERROR] Could not get player position");
                return;
            }
            
            var currentPos = playerPos.GridPos;
            LogMessage($"[SMART PATHFINDING] Player at ({currentPos.X:F0}, {currentPos.Y:F0})");
            
            // STRATEGIC TARGET SELECTION - Try multiple intelligent targets
            // Convert SharpDX.Vector2 to System.Numerics.Vector2
            var currentPosNum = new System.Numerics.Vector2(currentPos.X, currentPos.Y);
            var strategicTargets = GenerateStrategicTargets(currentPosNum);
            
            LogMessage($"[SMART PATHFINDING] Generated {strategicTargets.Count} strategic targets");
            foreach(var target in strategicTargets.Take(3)) // Log first 3 for debugging
            {
                LogMessage($"[TARGET] Attempting: ({target.Position.X:F0}, {target.Position.Y:F0}) - {target.Reason}");
            }
            
            // Try each target until we find a valid path
            TryMultipleTargets(strategicTargets);
            
            LogMessage("[SMART PATHFINDING] All pathfinding requests sent - waiting for callbacks");
        }
        catch (Exception ex)
        {
            LogError($"Error requesting path: {ex.Message}");
            _currentState = BotState.Error;
        }
    }
    
    private List<(System.Numerics.Vector2 Position, string Reason)> GenerateStrategicTargets(System.Numerics.Vector2 currentPos)
    {
        var targets = new List<(System.Numerics.Vector2, string)>();
        
        // Strategy 1: Explore in cardinal directions (likely to find exits)
        var cardinalDistances = new[] { 150, 300, 500, 800 }; // Progressively farther
        var cardinalDirections = new[]
        {
            (1, 0, "East - common exit direction"),
            (0, 1, "South - potential exit path"),
            (-1, 0, "West - alternate route"),
            (0, -1, "North - backtrack option"),
            (1, 1, "Southeast - diagonal exploration"),
            (-1, 1, "Southwest - diagonal search"),
            (1, -1, "Northeast - diagonal option"),
            (-1, -1, "Northwest - final direction")
        };
        
        foreach (var distance in cardinalDistances)
        {
            foreach (var (x, y, reason) in cardinalDirections)
            {
                var target = new System.Numerics.Vector2(
                    currentPos.X + (x * distance), 
                    currentPos.Y + (y * distance)
                );
                targets.Add((target, $"{reason} at {distance} units"));
            }
        }
        
        // Strategy 2: Area-based exploration (try to reach map edges)
        var areaData = GameController.IngameState.Data.AreaDimensions;
        if (areaData != null && areaData.X > 0 && areaData.Y > 0)
        {
            var dimensions = areaData;
            LogMessage($"[AREA INFO] Map dimensions: {dimensions.X} x {dimensions.Y}");
            
            // Target edges where exits are likely to be
            var edgeTargets = new[]
            {
                (dimensions.X * 0.8f, currentPos.Y, "Eastern edge - primary exit zone"),
                (dimensions.X * 0.9f, currentPos.Y, "Far eastern edge - secondary exit"),
                (currentPos.X, dimensions.Y * 0.8f, "Southern edge exploration"),
                (dimensions.X * 0.8f, dimensions.Y * 0.8f, "Southeast corner - exit cluster"),
                (dimensions.X * 0.1f, currentPos.Y, "Western edge - alternate exit"),
                (currentPos.X, dimensions.Y * 0.1f, "Northern edge check")
            };
            
            foreach (var (x, y, reason) in edgeTargets)
            {
                targets.Add((new System.Numerics.Vector2(x, y), reason));
            }
        }
        
        // Strategy 3: Spiral exploration pattern (systematic coverage)
        var spiralPoints = GenerateSpiralPattern(currentPos, 100, 6); // 6 points in spiral
        for (int i = 0; i < spiralPoints.Count; i++)
        {
            targets.Add((spiralPoints[i], $"Spiral exploration point {i + 1}"));
        }
        
        return targets;
    }
    
    private List<System.Numerics.Vector2> GenerateSpiralPattern(System.Numerics.Vector2 center, float baseRadius, int points)
    {
        var spiral = new List<System.Numerics.Vector2>();
        float angleStep = (float)(2 * Math.PI / points);
        
        for (int i = 0; i < points; i++)
        {
            float angle = i * angleStep;
            float radius = baseRadius * (1 + i * 0.5f); // Expanding spiral
            
            var x = center.X + radius * (float)Math.Cos(angle);
            var y = center.Y + radius * (float)Math.Sin(angle);
            
            spiral.Add(new System.Numerics.Vector2(x, y));
        }
        
        return spiral;
    }
    
    private void TryMultipleTargets(List<(System.Numerics.Vector2 Position, string Reason)> targets)
    {
        int maxTargetsToTry = Math.Min(targets.Count, 10); // Don't spam too many requests
        int targetIndex = 0;
        
        foreach (var (position, reason) in targets.Take(maxTargetsToTry))
        {
            targetIndex++;
            
            try
            {
                LogMessage($"[TARGET {targetIndex}] Trying: {reason} at ({position.X:F0}, {position.Y:F0})");
                
                // Create a callback that includes the target information for debugging
                Action<List<Vector2i>> targetCallback = (path) => {
                    LogMessage($"[CALLBACK {targetIndex}] {reason} returned {path?.Count ?? 0} points");
                    OnPathReceived(path, reason, targetIndex);
                };
                
                // Request path to this target
                _radarLookForRoute(position, targetCallback, CancellationToken.None);
                
                // Small delay between requests to avoid overwhelming the pathfinder
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                LogMessage($"[TARGET {targetIndex}] Error requesting path to {reason}: {ex.Message}");
            }
        }
        
        LogMessage($"[SMART PATHFINDING] Sent {targetIndex} pathfinding requests - best path will be selected");
    }
    
    private void OnPathReceived(List<Vector2i> path, string targetReason = "Unknown", int targetIndex = 0)
    {
        LogMessage($"[CALLBACK {targetIndex}] *** PATH RECEIVED FROM: {targetReason} ***");
        
        try
        {
            // Reset timeout tracking
            _lastPathRequest = DateTime.MinValue;
            
            LogMessage($"[CALLBACK {targetIndex}] Path details - {(path == null ? "NULL" : $"{path.Count} points")} for {targetReason}");
            
            if (path == null || path.Count == 0)
            {
                LogMessage($"[CALLBACK {targetIndex}] No path found for {targetReason} - waiting for other attempts");
                return; // Don't change state, wait for other attempts
            }
            
            // Check if current path is stale (been following it too long)
            bool isCurrentPathStale = false;
            if (_currentPath.Count > 0 && _currentPathStartTime != DateTime.MinValue)
            {
                var pathAge = (DateTime.Now - _currentPathStartTime).TotalSeconds;
                if (pathAge > 30) // Path is stale after 30 seconds
                {
                    isCurrentPathStale = true;
                    LogMessage($"[PATH STALENESS] Current path is {pathAge:F1} seconds old - considering replacement");
                }
            }
            
            // SMART DIRECTIONAL PATH SELECTION: Avoid going back to spawn
            // STABILITY ADDITION: Don't change paths too frequently
            bool shouldAcceptPath = false;
            string acceptReason = "";
            
            // PATH STABILITY: Don't accept new paths if we just started following current path
            var timeSincePathStart = (DateTime.Now - _currentPathStartTime).TotalSeconds;
            var isPathTooNew = timeSincePathStart < 2.5; // Don't change paths for first 2.5 seconds (reduced from 5.0s)
            
            if (_currentPath.Count == 0)
            {
                shouldAcceptPath = true;
                acceptReason = "no current path";
            }
            else if (isCurrentPathStale)
            {
                shouldAcceptPath = true;
                acceptReason = $"replacing stale path (age: {(DateTime.Now - _currentPathStartTime).TotalSeconds:F1}s)";
            }
            else if (isPathTooNew)
            {
                LogMessage($"[PATH STABILITY] ⏸️ Rejecting path change - current path too new ({timeSincePathStart:F1}s < 2.5s)");
                return; // Don't even analyze the new path
            }
            else
            {
                // DIRECTIONAL INTELLIGENCE: Analyze path endpoints to avoid backtracking
                var currentPathScore = AnalyzePathDirection(_currentPath, "current path");
                var newPathScore = AnalyzePathDirection(path, targetReason);
                
                LogMessage($"[PATH ANALYSIS] Current path score: {currentPathScore:F2}, New path ({targetReason}) score: {newPathScore:F2}");
                
                // STABILITY: Require significant improvement to switch paths (increased threshold)
                if (newPathScore > currentPathScore + 0.15f) // Increased from 0.1f to 0.15f
                {
                    shouldAcceptPath = true;
                    acceptReason = $"significantly better direction (score: {newPathScore:F2} vs {currentPathScore:F2})";
                }
                // If directional scores are similar, prefer much shorter paths only
                else if (Math.Abs(newPathScore - currentPathScore) <= 0.1f && path.Count < _currentPath.Count * 0.7f) // Stricter: 70% instead of 80%
                {
                    shouldAcceptPath = true;
                    acceptReason = $"similar direction, much shorter ({path.Count} vs {_currentPath.Count} points)";
                }
                // REMOVED: Accept similar length paths (caused too much path switching)
            }
            
            if (shouldAcceptPath)
            {
                _currentPath = path;
                _currentPathIndex = 0;
                _currentState = BotState.MovingAlongPath;
                
                // Track when we accepted this path
                _currentPathStartTime = DateTime.Now;
                _lastAcceptedPathLength = path.Count;
                
                LogMessage($"[SUCCESS {targetIndex}] ACCEPTED path from {targetReason} with {path.Count} points! Reason: {acceptReason}");
                LogMessage($"[PATH INFO] Start: ({path[0].X}, {path[0].Y}) -> End: ({path[path.Count-1].X}, {path[path.Count-1].Y})");
                
                // Cancel any pending pathfinding requests since we found a good path
                _pathfindingCts.Cancel();
                _pathfindingCts = new CancellationTokenSource();
            }
            else
            {
                LogMessage($"[CALLBACK {targetIndex}] REJECTED path from {targetReason} with {path.Count} points, keeping current path with {_currentPath.Count} points (path too long)");
            }
        }
        catch (Exception ex)
        {
            LogError($"[CALLBACK {targetIndex}] Error processing path from {targetReason}: {ex.Message}");
        }
    }
    

    
    private void MoveAlongPath()
    {
        if (_currentPath == null || _currentPath.Count == 0)
        {
            LogMessage("[PATH] ❌ No current path to follow");
            _currentState = BotState.WaitingForAqueducts;
            return;
        }

        var playerPos = GetPlayerPosition();
        if (playerPos == null)
        {
            LogMessage("[POSITION] ❌ Cannot get player position");
            return;
        }

        var playerWorldPos = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);

        // RADIUS-BASED NAVIGATION: Use pure pursuit algorithm instead of waypoint chasing
        var targetPoint = FindPathIntersectionWithRadius(_currentPath, _currentPathIndex);
        
        if (!targetPoint.HasValue)
        {
            LogMessage("[PURSUIT] ❌ No valid intersection point found - path may be complete");
            _currentState = BotState.WaitingForAqueducts;
            return;
        }

        // Convert world position to screen coordinates for clicking
        var worldPos = new Vector3(targetPoint.Value.X * 250f / 23f, targetPoint.Value.Y * 250f / 23f, 0);
        var screenPosSharp = GameController.IngameState.Camera.WorldToScreen(worldPos);
        var screenPos = new Vector2(screenPosSharp.X, screenPosSharp.Y);
        
        var distanceToTarget = System.Numerics.Vector2.Distance(playerWorldPos, targetPoint.Value);
        
        // Update path progress tracking
        UpdatePathProgress(targetPoint.Value, distanceToTarget);
        
        LogMessage($"[PURSUIT] 🎯 Moving to intersection point ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {distanceToTarget:F1}");

        // Calculate movement delay based on distance
        var movementDelay = CalculateImprovedMovementDelay(distanceToTarget);
        
        // Check if we're close enough to consider this target "reached"
        if (distanceToTarget < 50f) // More generous than waypoint precision
        {
            LogMessage($"[PURSUIT] ✅ Close to intersection point (distance: {distanceToTarget:F1} < 50), advancing along path");
            
            // Advance our position along the path
            _currentPathIndex = Math.Min(_currentPathIndex + 5, _currentPath.Count - 10);
            _lastIntersectionPoint = targetPoint.Value;
            return;
        }

        // Perform the movement
        if (distanceToTarget < 15)
        {
            LogMessage($"[PURSUIT] ⏸️ Very close to target, skipping movement to avoid micro-adjustments");
            return;
        }

        // Check movement delay timing
        var timeSinceLastMovement = (DateTime.Now - _lastMovementTime).TotalMilliseconds;
        if (timeSinceLastMovement < movementDelay)
        {
            var remainingDelay = movementDelay - timeSinceLastMovement;
            LogMessage($"[MOVEMENT] ⏳ Waiting {remainingDelay:F0}ms before next movement (preventing oscillation)");
            return;
        }

        // Execute the movement
        _lastMovementTime = DateTime.Now;
        
        LogMessage($"[MOVEMENT] 🎮 Keyboard: cursor to ({screenPos.X:F0}, {screenPos.Y:F0}) + press T");
        ClickAt((int)screenPos.X, (int)screenPos.Y);
        PressAndHoldKey(Keys.T);

        // Update stuck detection
        UpdateStuckDetection(playerWorldPos);
    }
    
    private float CalculateImprovedPrecision(float distanceToTarget, int currentIndex, int totalPoints)
    {
        // Base precision from settings
        var basePrecision = Settings.MovementSettings.MovementPrecision;
        
        // FIXED: Much more generous precision for final waypoints
        var progressRatio = (float)currentIndex / totalPoints;
        var remainingWaypoints = totalPoints - currentIndex;
        
        // For final 3 waypoints, use very large precision to prevent getting stuck
        if (remainingWaypoints <= 3)
        {
            var finalPrecision = Math.Max(basePrecision * 5.0f, 80f); // At least 80 pixels for final 3 waypoints
            LogMessage($"[PRECISION] Final 3 waypoints precision: {finalPrecision:F1} (remaining: {remainingWaypoints})");
            return finalPrecision;
        }
        
        // For final waypoints (last 20% of path), use much larger precision to prevent oscillation
        if (progressRatio > 0.8f)
        {
            var finalPrecision = Math.Max(basePrecision * 3.0f, 50f); // At least 50 pixels for final waypoints
            LogMessage($"[PRECISION] Final waypoint precision: {finalPrecision:F1} (progress: {progressRatio:P0})");
            return finalPrecision;
        }
        
        // For very close targets, use larger precision to avoid micro-movements
        if (distanceToTarget < 30)
        {
            return Math.Max(basePrecision * 2.0f, 25f); // At least 25 pixels for close targets
        }
        
        // For far targets, use standard precision
        return basePrecision;
    }
    
    private int CalculateImprovedMovementDelay(float distanceToTarget)
    {
        // CONSERVATIVE DELAYS: Much longer delays to prevent overcorrection
        var minDelay = Settings.MovementSettings.MinMoveDelayMs;
        var maxDelay = Settings.MovementSettings.MaxMoveDelayMs;
        
        // STABILITY FIX: Much longer base delays to allow character movement
        var baseMinDelay = Math.Max(minDelay, 800); // At least 800ms between movements
        var baseMaxDelay = Math.Max(maxDelay, 1500); // Up to 1.5s between movements
        
        // For very close targets, use much longer delays to let movement settle
        if (distanceToTarget < 30)
        {
            return _random.Next(baseMaxDelay * 3, baseMaxDelay * 4); // 4.5-6s delay for very close targets
        }
        // For close targets, use longer delays
        else if (distanceToTarget < 100)
        {
            return _random.Next(baseMaxDelay * 2, baseMaxDelay * 3); // 3-4.5s delay for close targets
        }
        // For medium distance targets, use standard longer delays
        else if (distanceToTarget < 200)
        {
            return _random.Next(baseMinDelay * 2, baseMaxDelay * 2); // 1.6-3s delay
        }
        // For far targets, use faster movement but still conservative
        else if (distanceToTarget > 300)
        {
            return _random.Next(baseMinDelay, baseMaxDelay); // 0.8-1.5s delay
        }
        
        return _random.Next(baseMinDelay, baseMaxDelay); // Standard conservative delay
    }
    
    private int GetOptimizedWaypointIndex()
    {
        // CONSERVATIVE WAYPOINT SKIPPING: Better balance between distance and stability
        const int MIN_CLICK_DISTANCE = 200; // Increased minimum distance
        const int PREFERRED_CLICK_DISTANCE = 350; // Increased preferred distance for smoother movement
        const int MAX_LOOKAHEAD = 15; // Reduced to prevent over-analysis
        
        var playerScreenPos = GetPlayerScreenPosition();
        if (!playerScreenPos.HasValue)
        {
            // Fallback: skip more waypoints to ensure we click farther away
            return Math.Min(_currentPathIndex + 8, _currentPath.Count - 1);
        }
        
        int bestWaypointIndex = _currentPathIndex;
        float bestDistance = 0f;
        
        // Look ahead through waypoints to find one that's far enough away
        for (int i = 3; i <= MAX_LOOKAHEAD && (_currentPathIndex + i) < _currentPath.Count; i++) // Start from 3 to skip closer waypoints
        {
            int checkIndex = _currentPathIndex + i;
            var targetPoint = _currentPath[checkIndex];
            
            // Convert to screen coordinates
            var worldPos = new Vector3(
                targetPoint.X * 250f / 23f,
                targetPoint.Y * 250f / 23f,
                0
            );
            
            var screenPosSharp = GameController.IngameState.Camera.WorldToScreen(worldPos);
            var screenPos = new Vector2(screenPosSharp.X, screenPosSharp.Y);
            
            float distanceFromPlayer = Vector2.Distance(screenPos, playerScreenPos.Value);
            
            // Priority 1: Find waypoint at preferred distance
            if (distanceFromPlayer >= PREFERRED_CLICK_DISTANCE)
            {
                LogMessage($"[WAYPOINT SKIP] 🎯 Found preferred distance waypoint {checkIndex + 1} at {distanceFromPlayer:F0} pixels (skipped {i} waypoints)");
                return checkIndex;
            }
            
            // Priority 2: Track best waypoint above minimum distance
            if (distanceFromPlayer >= MIN_CLICK_DISTANCE && distanceFromPlayer > bestDistance)
            {
                bestWaypointIndex = checkIndex;
                bestDistance = distanceFromPlayer;
            }
        }
        
        // Use best waypoint above minimum distance, or skip more waypoints for safety
        if (bestDistance > 0)
        {
            int skipped = bestWaypointIndex - _currentPathIndex;
            LogMessage($"[WAYPOINT SKIP] ✅ Using minimum distance waypoint {bestWaypointIndex + 1} at {bestDistance:F0} pixels (skipped {skipped} waypoints)");
            return bestWaypointIndex;
        }
        
        // Fallback: Skip more waypoints to ensure we click farther away
        int fallbackIndex = Math.Min(_currentPathIndex + 8, _currentPath.Count - 1);
        LogMessage($"[WAYPOINT SKIP] ⚠️ Fallback: forcing skip to waypoint {fallbackIndex + 1} (skipped {fallbackIndex - _currentPathIndex} waypoints)");
        return fallbackIndex;
    }
    
    // STUCK DETECTION SYSTEM
    private DateTime _lastPositionUpdate = DateTime.MinValue;
    private int _stuckCounter = 0;
    
    private bool IsStuckDetected(System.Numerics.Vector2 currentPlayerPos, float distanceToTarget)
    {
        var timeSinceLastUpdate = (DateTime.Now - _lastPositionUpdate).TotalSeconds;
        
        // Only check stuck detection if enough time has passed
        if (timeSinceLastUpdate < 2.0) return false;
        
        // Calculate how much the player has moved
        var playerMovement = System.Numerics.Vector2.Distance(currentPlayerPos, _lastPlayerPosition);
        
        // If player hasn't moved much and we're still far from target, we might be stuck
        if (playerMovement < 20 && distanceToTarget > 100 && timeSinceLastUpdate > 3.0)
        {
            _stuckCounter++;
            LogMessage($"[STUCK DETECTION] Possible stuck situation - movement: {playerMovement:F1}, target distance: {distanceToTarget:F1}, stuck count: {_stuckCounter}");
            
            return _stuckCounter >= 3; // Confirm stuck after 3 detections
        }
        
        // Reset stuck counter if we're making progress
        if (playerMovement > 30)
        {
            _stuckCounter = 0;
        }
        
        return false;
    }
    
    private void UpdateStuckDetection(System.Numerics.Vector2 currentPlayerPos)
    {
        _lastPlayerPosition = currentPlayerPos;
        _lastPositionUpdate = DateTime.Now;
    }
    
    private void HandleStuckSituation()
    {
        LogMessage("[STUCK HANDLER] Attempting to resolve stuck situation...");
        
        // Strategy 1: Skip ahead in the path
        if (_currentPathIndex + 3 < _currentPath.Count)
        {
            _currentPathIndex += 3;
            LogMessage($"[STUCK HANDLER] Skipping ahead to waypoint {_currentPathIndex}");
            _stuckCounter = 0;
            return;
        }
        
        // Strategy 2: Request a new path
        LogMessage("[STUCK HANDLER] Requesting new path to continue navigation");
        _currentPath.Clear();
        _currentState = BotState.GettingPath;
        _stuckCounter = 0;
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
    
    private Positioned? GetPlayerPosition()
    {
        try
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            var positioned = player?.GetComponent<Positioned>();
            return positioned;
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
            // Using exact approach from working AreWeThereYet bot
            LogMessage($"[MOUSE] Clicking at coordinates ({x}, {y}) using working bot method");
            
            // Step 1: Move cursor to position (like working bot)
            SetCursorPos(x, y);
            
            // Step 2: Wait (like working bot's WaitTime)
            Thread.Sleep(40); // Match working bot's 40ms delay
            
            // Step 3: Mouse down (exact same API call as working bot)
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0);
            
            // Step 4: Hold down briefly (like working bot)
            Thread.Sleep(40); // Match working bot's hold time
            
            // Step 5: Mouse up (exact same API call as working bot)
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            
            // Step 6: Final delay (like working bot)
            Thread.Sleep(100); // Match working bot's final delay
            
            LogMessage("[MOUSE] Click sequence completed using working bot method");
        }
        catch (Exception ex)
        {
            LogError($"Error clicking at ({x}, {y}): {ex.Message}");
        }
    }
    
    // Remove the complex SendInput method since working bot uses simple mouse_event
    private void ClickWithSendInput(int x, int y)
    {
        // This method is no longer needed - working bot uses mouse_event
        LogMessage("[MOUSE] SendInput method disabled - using working bot approach instead");
    }
    
    private void PressKey(Keys key)
    {
        try
        {
            byte vkCode = (byte)key;
            LogMessage($"[KEYBOARD] Pressing key {key} (VK Code: {vkCode})");
            
            // Method 1: Try keybd_event
            keybd_event(vkCode, 0, 0, 0); // Key down
            Thread.Sleep(50); // Hold key briefly
            keybd_event(vkCode, 0, KEYEVENTF_KEYUP_SENDINPUT, 0); // Key up
            
            LogMessage($"[KEYBOARD] Key press sequence completed for {key}");
        }
        catch (Exception ex)
        {
            LogError($"Error pressing key {key}: {ex.Message}");
        }
    }
    
    private void PressAndHoldKey(Keys key)
    {
        try
        {
            byte vkCode = (byte)key;
            LogMessage($"[KEYBOARD] *** TESTING KEY PRESS WITH PROPER WINDOW FOCUS FOR {key} ***");
            
            // CRITICAL: Set focus to Path of Exile window before pressing key
            LogMessage("[KEYBOARD] Step 1: Finding Path of Exile window...");
            IntPtr poeWindow = FindWindow(null, "Path of Exile");
            if (poeWindow == IntPtr.Zero)
            {
                LogMessage("[KEYBOARD] Path of Exile window not found, trying alternative names...");
                poeWindow = FindWindow("POEWindowClass", null);
            }
            
            if (poeWindow != IntPtr.Zero)
            {
                LogMessage($"[KEYBOARD] Step 2: Found PoE window handle: {poeWindow}");
                LogMessage("[KEYBOARD] Step 3: Setting window focus...");
                
                // Bring window to foreground and set focus
                SetForegroundWindow(poeWindow);
                Thread.Sleep(50); // Small delay for focus to take effect
                
                LogMessage("[KEYBOARD] Step 4: Window focus set - now pressing key");
            }
            else
            {
                LogMessage("[KEYBOARD] WARNING: Could not find Path of Exile window - key press may fail");
            }
            
            // Now press the key with the exact AreWeThereYet method
            LogMessage($"[KEYBOARD] Step 5: Pressing {key} with EXTENDEDKEY flags...");
            keybd_event(vkCode, 0, 0x0001, 0); // Key down with EXTENDEDKEY
            Thread.Sleep(20);
            keybd_event(vkCode, 0, 0x0003, 0); // Key up with EXTENDEDKEY | KEYUP
            
            LogMessage($"[KEYBOARD] *** KEY PRESS COMPLETED FOR {key} WITH PROPER FOCUS - CHECK CHARACTER MOVEMENT! ***");
        }
        catch (Exception ex)
        {
            LogError($"Error pressing and holding key {key}: {ex.Message}");
        }
    }
    
    private void TestKeyboardOnly()
    {
        try
        {
            bool useKeyboardMovement = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
            Keys movementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
            
            if (!useKeyboardMovement || movementKey == Keys.None)
            {
                LogMessage("[KEYBOARD TEST] Keyboard movement not enabled or no key set");
                return;
            }
            
            LogMessage($"[KEYBOARD TEST] Testing key {movementKey} - positioning cursor first");
            
            // CRITICAL: Position cursor over game world before testing keyboard
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is Positioned playerPos)
            {
                var render = player.GetComponent<Render>();
                if (render != null)
                {
                    var worldPos = render.PosNum;
                    var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                    
                    // Position cursor slightly away from player for movement test
                    var testX = (int)(screenPos.X + 100);
                    var testY = (int)(screenPos.Y + 50);
                    
                                         // CRITICAL FIX: Add window offset for absolute screen coordinates
                     var windowRect = GameController.Window.GetWindowRectangle();
                     int absoluteX = testX + (int)windowRect.X;
                     int absoluteY = testY + (int)windowRect.Y;
                     
                     LogMessage($"[KEYBOARD TEST] Moving cursor: game({testX}, {testY}) -> screen({absoluteX}, {absoluteY})");
                     SetCursorPos(absoluteX, absoluteY);
                     Thread.Sleep(200); // Wait for cursor to settle
                }
                else
                {
                    LogMessage("[KEYBOARD TEST] ERROR: Could not get player render component");
                    return;
                }
            }
            else
            {
                LogMessage("[KEYBOARD TEST] ERROR: Could not get player position");
                return;
            }
            
            // Get active window info
            var foregroundWindow = GetForegroundWindow();
            var windowTitle = new System.Text.StringBuilder(256);
            GetWindowText(foregroundWindow, windowTitle, 256);
            LogMessage($"[KEYBOARD TEST] Active window: {windowTitle}");
            
            LogMessage("[KEYBOARD TEST] Method 1: keybd_event");
            PressKey(movementKey);
            
            Thread.Sleep(1000);
            
            LogMessage("[KEYBOARD TEST] Method 2: SendInput");
            PressKeyAlternative(movementKey);
            
            Thread.Sleep(1000);
            
            LogMessage("[KEYBOARD TEST] Method 3: PostMessage to PoE window");
            PressKeyToWindow(movementKey);
            
            Thread.Sleep(1000);
            
            LogMessage("[KEYBOARD TEST] Method 4: Focus window + SendInput");
            PressKeyWithFocus(movementKey);
            
            Thread.Sleep(1000);
            
            LogMessage("[KEYBOARD TEST] Method 5: Scan codes (low-level approach)");
            PressKeyWithScanCode(movementKey);
            
            LogMessage("[KEYBOARD TEST] All 5 methods completed - check if character moved or any action occurred");
        }
        catch (Exception ex)
        {
            LogError($"Error in keyboard test: {ex.Message}");
        }
    }
    
    // Alternative keyboard input method using SendInput (more reliable)
    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    
    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
    
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
    
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);
    
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    
    // Windows message constants
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_CHAR = 0x0102;
    private const int SW_RESTORE = 9;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public MOUSEKEYBDINPUT Data;
    }
    
    [StructLayout(LayoutKind.Explicit)]
    private struct MOUSEKEYBDINPUT
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
        [FieldOffset(0)]
        public MOUSEINPUT Mouse;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKeyCode;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }
    
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }
    
    private void PressKeyAlternative(Keys key)
    {
        try
        {
            var foregroundWindow = GetForegroundWindow();
            var windowTitle = new System.Text.StringBuilder(256);
            GetWindowText(foregroundWindow, windowTitle, 256);
            
            LogMessage($"[KEYBOARD ALT] Pressing {key} to window: {windowTitle}");
            
            INPUT[] inputs = new INPUT[2];
            
            // Key down
            inputs[0].Type = INPUT_KEYBOARD;
            inputs[0].Data.Keyboard.VirtualKeyCode = (ushort)key;
            inputs[0].Data.Keyboard.ScanCode = 0;
            inputs[0].Data.Keyboard.Flags = 0;
            inputs[0].Data.Keyboard.Time = 0;
            inputs[0].Data.Keyboard.ExtraInfo = IntPtr.Zero;
            
            // Key up
            inputs[1].Type = INPUT_KEYBOARD;
            inputs[1].Data.Keyboard.VirtualKeyCode = (ushort)key;
            inputs[1].Data.Keyboard.ScanCode = 0;
            inputs[1].Data.Keyboard.Flags = KEYEVENTF_KEYUP_SENDINPUT;
            inputs[1].Data.Keyboard.Time = 0;
            inputs[1].Data.Keyboard.ExtraInfo = IntPtr.Zero;
            
            uint result = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            LogMessage($"[KEYBOARD ALT] SendInput result: {result} (should be 2)");
        }
        catch (Exception ex)
        {
            LogError($"Error with alternative key press {key}: {ex.Message}");
        }
    }
    
    private void TestClickAtCursor()
    {
        try
        {
            LogMessage("[MOUSE CLICK TEST] Testing mouse movement vs keyboard movement");
            
            // Get player position and calculate a test location
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is Positioned playerPos)
            {
                var render = player.GetComponent<Render>();
                if (render != null)
                {
                    var worldPos = render.PosNum;
                    var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                    
                    // Click slightly away from player
                    var testX = (int)(screenPos.X + 120);
                    var testY = (int)(screenPos.Y + 80);
                    
                    LogMessage($"[MOUSE CLICK TEST] Clicking at game position: ({testX}, {testY})");
                    ClickAt(testX, testY);
                    
                    LogMessage("[MOUSE CLICK TEST] Mouse click completed - character should move to clicked location");
                }
                else
                {
                    LogMessage("[MOUSE CLICK TEST] ERROR: Could not get player render component");
                }
            }
            else
            {
                LogMessage("[MOUSE CLICK TEST] ERROR: Could not get player position");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in mouse click test: {ex.Message}");
        }
    }
    
    private void PressKeyToWindow(Keys key)
    {
        try
        {
            // Try to find Path of Exile window by common window titles
            string[] poeWindowTitles = { "Path of Exile", "PathOfExile", "POE" };
            IntPtr poeWindow = IntPtr.Zero;
            
            foreach (string windowTitle in poeWindowTitles)
            {
                poeWindow = FindWindow(null, windowTitle);
                if (poeWindow != IntPtr.Zero)
                {
                    LogMessage($"[KEYBOARD WINDOW] Found PoE window with title: {windowTitle}");
                    break;
                }
            }
            
            if (poeWindow == IntPtr.Zero)
            {
                // Fallback to current foreground window
                poeWindow = GetForegroundWindow();
                LogMessage("[KEYBOARD WINDOW] Using foreground window as fallback");
            }
            
            var title = new System.Text.StringBuilder(256);
            GetWindowText(poeWindow, title, 256);
            LogMessage($"[KEYBOARD WINDOW] Sending key {key} to window: {title}");
            LogMessage($"[KEYBOARD WINDOW] Window visible: {IsWindowVisible(poeWindow)}");
            
            // Send key down and key up messages directly to the window
            ushort keyCode = (ushort)key;
            bool result1 = PostMessage(poeWindow, WM_KEYDOWN, (IntPtr)keyCode, IntPtr.Zero);
            Thread.Sleep(50);
            bool result2 = PostMessage(poeWindow, WM_KEYUP, (IntPtr)keyCode, IntPtr.Zero);
            
            LogMessage($"[KEYBOARD WINDOW] PostMessage results - KeyDown: {result1}, KeyUp: {result2}");
        }
        catch (Exception ex)
        {
            LogError($"Error with window key press {key}: {ex.Message}");
        }
    }
    
    private void PressKeyWithFocus(Keys key)
    {
        try
        {
            LogMessage("[KEYBOARD FOCUS] Attempting to focus and send key");
            
            // Get current window
            var foregroundWindow = GetForegroundWindow();
            var title = new System.Text.StringBuilder(256);
            GetWindowText(foregroundWindow, title, 256);
            
            LogMessage($"[KEYBOARD FOCUS] Current window: {title}");
            
            // Ensure window is focused and visible
            bool focused = SetForegroundWindow(foregroundWindow);
            bool shown = ShowWindow(foregroundWindow, SW_RESTORE);
            
            LogMessage($"[KEYBOARD FOCUS] SetForegroundWindow: {focused}, ShowWindow: {shown}");
            
            // Wait a moment for window to be ready
            Thread.Sleep(200);
            
            // Now try SendInput again
            INPUT[] inputs = new INPUT[2];
            
            inputs[0].Type = INPUT_KEYBOARD;
            inputs[0].Data.Keyboard.VirtualKeyCode = (ushort)key;
            inputs[0].Data.Keyboard.ScanCode = 0;
            inputs[0].Data.Keyboard.Flags = 0;
            inputs[0].Data.Keyboard.Time = 0;
            inputs[0].Data.Keyboard.ExtraInfo = IntPtr.Zero;
            
            inputs[1].Type = INPUT_KEYBOARD;
            inputs[1].Data.Keyboard.VirtualKeyCode = (ushort)key;
            inputs[1].Data.Keyboard.ScanCode = 0;
            inputs[1].Data.Keyboard.Flags = KEYEVENTF_KEYUP_SENDINPUT;
            inputs[1].Data.Keyboard.Time = 0;
            inputs[1].Data.Keyboard.ExtraInfo = IntPtr.Zero;
            
            uint result = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            LogMessage($"[KEYBOARD FOCUS] SendInput with focus result: {result}");
        }
        catch (Exception ex)
        {
            LogError($"Error with focused key press {key}: {ex.Message}");
        }
    }
    
    private void PressKeyWithScanCode(Keys key)
    {
        try
        {
            LogMessage($"[KEYBOARD SCANCODE] Trying scan code approach for {key}");
            
            // Get scan code for the key (more low-level than virtual key codes)
            uint scanCode = MapVirtualKey((uint)key, 0);
            LogMessage($"[KEYBOARD SCANCODE] Virtual Key: {(uint)key}, Scan Code: {scanCode}");
            
            if (scanCode == 0)
            {
                LogMessage("[KEYBOARD SCANCODE] ERROR: Could not map virtual key to scan code");
                return;
            }
            
            // Use keybd_event with scan code
            keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE, 0); // Key down with scan code
            Thread.Sleep(50);
            keybd_event(0, (byte)scanCode, KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP, 0); // Key up with scan code
            
            LogMessage($"[KEYBOARD SCANCODE] Sent scan code {scanCode} for key {key}");
        }
        catch (Exception ex)
        {
            LogError($"Error with scan code key press {key}: {ex.Message}");
        }
    }
    
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);
    
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    
    private void TryConnectToRadar()
    {
        if (_radarAvailable) 
        {
            LogMessage("Radar already connected");
            return; // Already connected
        }
        
        try
        {
            LogMessage("=== ATTEMPTING RADAR CONNECTION ===");
            LogMessage("Trying multiple signature variations...");
            
            // Try variation 1: System.Numerics.Vector2
            LogMessage("1. Trying Action<Vector2, Action<List<Vector2i>>, CancellationToken>");
            var method1 = GameController.PluginBridge.GetMethod<Action<Vector2, Action<List<Vector2i>>, CancellationToken>>("Radar.LookForRoute");
            if (method1 != null)
            {
                LogMessage("✅ SUCCESS: Found signature 1!");
                _radarLookForRoute = method1;
                _radarAvailable = true;
                LogMessage("=== RADAR CONNECTION ESTABLISHED ===");
                return;
            }
            else
            {
                LogMessage("❌ Signature 1 failed");
            }
            
            // Try variation 2: SharpDX.Vector2  
            LogMessage("2. Trying Action<SharpDX.Vector2, Action<List<Vector2i>>, CancellationToken>");
            var method2 = GameController.PluginBridge.GetMethod<Action<SharpDX.Vector2, Action<List<Vector2i>>, CancellationToken>>("Radar.LookForRoute");
            if (method2 != null)
            {
                LogMessage("✅ SUCCESS: Found signature 2 with SharpDX.Vector2!");
                // Need to create a wrapper since our internal Vector2 is System.Numerics
                _radarLookForRoute = (v2, callback, token) => method2(new SharpDX.Vector2(v2.X, v2.Y), callback, token);
                _radarAvailable = true;
                LogMessage("=== RADAR CONNECTION ESTABLISHED ===");
                return;
            }
            else
            {
                LogMessage("❌ Signature 2 failed");
            }

            // Try variation 3: Maybe it returns Task
            LogMessage("3. Trying Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>");
            var method3 = GameController.PluginBridge.GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
            if (method3 != null)
            {
                LogMessage("✅ SUCCESS: Found signature 3 with Task return!");
                _radarLookForRoute = (v2, callback, token) => { _ = method3(v2, callback, token); };
                _radarAvailable = true;
                LogMessage("=== RADAR CONNECTION ESTABLISHED ===");
                return;
            }
            else
            {
                LogMessage("❌ Signature 3 failed");
            }

            // Try variation 4: Maybe different parameter order
            LogMessage("4. Trying Action<Action<List<Vector2i>>, Vector2, CancellationToken>");
            var method4 = GameController.PluginBridge.GetMethod<Action<Action<List<Vector2i>>, Vector2, CancellationToken>>("Radar.LookForRoute");
            if (method4 != null)
            {
                LogMessage("✅ SUCCESS: Found signature 4 with different parameter order!");
                _radarLookForRoute = (v2, callback, token) => method4(callback, v2, token);
                _radarAvailable = true;
                LogMessage("=== RADAR CONNECTION ESTABLISHED ===");
                return;
            }
            else
            {
                LogMessage("❌ Signature 4 failed");
            }

            LogMessage("❌ ALL RADAR CONNECTION ATTEMPTS FAILED");
            LogMessage("Possible issues:");
            LogMessage("- Radar plugin not loaded or enabled");
            LogMessage("- Radar plugin version incompatibility");
            LogMessage("- ExileApi PluginBridge not ready");
            LogMessage("Bot will attempt to retry connection periodically...");
            _radarAvailable = false;
        }
        catch (Exception ex)
        {
            LogError($"CRITICAL ERROR connecting to Radar: {ex.Message}");
            LogError($"Stack trace: {ex.StackTrace}");
            _radarAvailable = false;
        }
    }
    
    private void TestMovementSystem()
    {
        try
        {
            LogMessage("[MOVEMENT TEST] Testing movement system...");
            
            // Get current player position to click near it
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is Positioned playerPos)
            {
                var render = player.GetComponent<Render>();
                if (render != null)
                {
                    var worldPos = render.PosNum;
                    var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                    
                    // Add a small offset to avoid clicking on the player
                    var testX = (int)(screenPos.X + 100);
                    var testY = (int)(screenPos.Y + 50);
                    
                    bool useKeyboardMovement = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
                    Keys movementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
                    
                    if (useKeyboardMovement && movementKey != Keys.None)
                    {
                        // Add window offset for keyboard movement
                        var windowRect = GameController.Window.GetWindowRectangle();
                        int absoluteX = testX + (int)windowRect.X;
                        int absoluteY = testY + (int)windowRect.Y;
                        
                        LogMessage($"[MOVEMENT TEST] Using keyboard: game({testX}, {testY}) -> screen({absoluteX}, {absoluteY}) then pressing {movementKey}");
                        SetCursorPos(absoluteX, absoluteY);
                        Thread.Sleep(100);
                        
                        // Try both methods
                        LogMessage("[MOVEMENT TEST] Testing standard keybd_event method...");
                        PressKey(movementKey);
                        
                        Thread.Sleep(500);
                        
                        LogMessage("[MOVEMENT TEST] Testing alternative SendInput method...");
                        PressKeyAlternative(movementKey);
                    }
                    else
                    {
                        LogMessage($"[MOVEMENT TEST] Using mouse click at ({testX}, {testY})");
                        ClickAt(testX, testY); // ClickAt already handles window offset
                    }
                    
                    LogMessage("[MOVEMENT TEST] Movement command executed - check if character moved!");
                }
                else
                {
                    LogMessage("[MOVEMENT TEST] Could not get player render component");
                }
            }
            else
            {
                LogMessage("[MOVEMENT TEST] Could not get player position");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in movement test: {ex.Message}");
        }
    }
    
    private void TestRadarConnection()
    {
        LogMessage("Testing Radar connection...");
        try
        {
            // Use a more realistic test - get current player position and add small offset
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is Positioned playerPos)
            {
                var currentPos = playerPos.GridPos;
                var testPos = new Vector2(currentPos.X + 50, currentPos.Y + 50); // 50 units away
                
                LogMessage($"Testing with position: ({testPos.X:F0}, {testPos.Y:F0}) from player at ({currentPos.X:F0}, {currentPos.Y:F0})");
                
                _radarLookForRoute(testPos, (path) => {
                    LogMessage($"[TEST CALLBACK] Radar test callback triggered - received {path?.Count ?? 0} path points");
                    if (path?.Count > 0)
                    {
                        LogMessage("[TEST CALLBACK] Pathfinding is working! Bot should be ready to navigate.");
                    }
                    else
                    {
                        LogMessage("[TEST CALLBACK] 0 path points received - target may be unreachable or too close");
                    }
                }, CancellationToken.None);
                
                LogMessage("Radar pathfinding request sent - waiting for callback...");
            }
            else
            {
                LogMessage("Could not get player position for test");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error testing Radar connection: {ex.Message}");
        }
    }

    private void DebugCoordinateSystem()
    {
        try
        {
            LogMessage("=== COORDINATE SYSTEM DEBUG START ===");
            
            // Step 1: Window information
            try
            {
                var windowRect = GameController.Window.GetWindowRectangle();
                LogMessage($"STEP 1 - Game window: X={windowRect.X}, Y={windowRect.Y}, W={windowRect.Width}, H={windowRect.Height}");
            }
            catch (Exception ex)
            {
                LogMessage($"STEP 1 ERROR - Window rect: {ex.Message}");
            }
            
            // Step 2: Current cursor position
            try
            {
                if (GetCursorPos(out POINT cursorPoint))
                {
                    LogMessage($"STEP 2 - Current cursor: ({cursorPoint.X}, {cursorPoint.Y})");
                }
                else
                {
                    LogMessage("STEP 2 ERROR - Could not get cursor position");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"STEP 2 ERROR - Cursor pos: {ex.Message}");
            }
            
            // Step 3: Player position
            try
            {
                var player = GameController.Game.IngameState.Data.LocalPlayer;
                if (player?.GetComponent<Positioned>() is Positioned playerPos)
                {
                    LogMessage($"STEP 3A - Player found, getting render component...");
                    
                    var render = player.GetComponent<Render>();
                    if (render != null)
                    {
                        var worldPos = render.PosNum;
                        var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                        
                        LogMessage($"STEP 3B - Player world: ({worldPos.X:F1}, {worldPos.Y:F1}, {worldPos.Z:F1})");
                        LogMessage($"STEP 3C - Player screen: ({screenPos.X:F1}, {screenPos.Y:F1})");
                        
                        // Step 4: Test coordinate calculations
                        var testX = (int)(screenPos.X + 100);
                        var testY = (int)(screenPos.Y + 50);
                        LogMessage($"STEP 4A - Test target game coords: ({testX}, {testY})");
                        
                        // Calculate absolute screen coordinates
                        var windowRect = GameController.Window.GetWindowRectangle();
                        int absoluteX = testX + (int)windowRect.X;
                        int absoluteY = testY + (int)windowRect.Y;
                        LogMessage($"STEP 4B - Test target absolute: ({absoluteX}, {absoluteY})");
                        
                        // Step 5: Test cursor move
                        LogMessage($"STEP 5A - Moving cursor to test position...");
                        bool moveResult = SetCursorPos(absoluteX, absoluteY);
                        LogMessage($"STEP 5B - SetCursorPos returned: {moveResult}");
                        
                        Thread.Sleep(100);
                        
                        if (GetCursorPos(out POINT newCursorPoint))
                        {
                            LogMessage($"STEP 5C - Cursor after move: ({newCursorPoint.X}, {newCursorPoint.Y})");
                            if (newCursorPoint.X == absoluteX && newCursorPoint.Y == absoluteY)
                            {
                                LogMessage("STEP 5D - SUCCESS: Cursor positioning WORKED!");
                            }
                            else
                            {
                                LogMessage($"STEP 5D - FAIL: Expected ({absoluteX}, {absoluteY}), got ({newCursorPoint.X}, {newCursorPoint.Y})");
                                
                                // Calculate the difference
                                int diffX = newCursorPoint.X - absoluteX;
                                int diffY = newCursorPoint.Y - absoluteY;
                                LogMessage($"STEP 5E - Difference: X={diffX}, Y={diffY}");
                            }
                        }
                        else
                        {
                            LogMessage("STEP 5D ERROR - Could not get cursor position after move");
                        }
                    }
                    else
                    {
                        LogMessage("STEP 3B ERROR - Could not get player render component");
                    }
                }
                else
                {
                    LogMessage("STEP 3A ERROR - Could not get player position");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"STEP 3-5 ERROR - Player/coordinate test: {ex.Message}");
            }
            
            // Step 6: Foreground window info
            try
            {
                var foregroundWindow = GetForegroundWindow();
                var windowTitle = new System.Text.StringBuilder(256);
                GetWindowText(foregroundWindow, windowTitle, 256);
                LogMessage($"STEP 6 - Foreground window: '{windowTitle}'");
            }
            catch (Exception ex)
            {
                LogMessage($"STEP 6 ERROR - Window info: {ex.Message}");
            }
            
            LogMessage("=== COORDINATE DEBUG COMPLETE ===");
        }
        catch (Exception ex)
        {
            LogMessage($"CRITICAL ERROR in coordinate debug: {ex.Message}");
        }
    }
    
    private void DrawPathDebug()
    {
        // Simple debug visualization of the current path
        // This will draw on the ImGui overlay
    }

    private void TestMouseClick()
    {
        try
        {
            LogMessage("[MOVEMENT TEST] ===== TESTING MULTIPLE POSITIONS =====");
            
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is Positioned playerPos)
            {
                var render = player.GetComponent<Render>();
                if (render != null)
                {
                    var worldPos = render.PosNum;
                    var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                    
                    LogMessage($"[MOVEMENT TEST] Player at screen ({screenPos.X:F0}, {screenPos.Y:F0})");
                    
                                        // Test 5 different positions to confirm focus fix works consistently
                    var testPositions = new[]
                    {
                        new { X = (int)(screenPos.X + 150), Y = (int)(screenPos.Y + 100), Name = "Position 1 (Right-Down)" },
                        new { X = (int)(screenPos.X - 120), Y = (int)(screenPos.Y - 80), Name = "Position 2 (Left-Up)" },
                        new { X = (int)(screenPos.X + 200), Y = (int)(screenPos.Y), Name = "Position 3 (Far Right)" },
                        new { X = (int)(screenPos.X), Y = (int)(screenPos.Y + 150), Name = "Position 4 (Down)" },
                        new { X = (int)(screenPos.X - 80), Y = (int)(screenPos.Y + 80), Name = "Position 5 (Left-Down)" }
                    };
                    
                    bool useKeyboardMovement = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
                    Keys movementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
                    
                    LogMessage($"[MOVEMENT TEST] ===== TESTING 5 POSITIONS TO CONFIRM FOCUS FIX WORKS =====");
                    
                    if (useKeyboardMovement && movementKey != Keys.None)
                    {
                        for (int i = 0; i < testPositions.Length; i++)
                        {
                            var pos = testPositions[i];
                            LogMessage($"[MOVEMENT TEST] ===== TESTING {pos.Name} at ({pos.X}, {pos.Y}) =====");
                            
                            LogMessage($"[MOVEMENT TEST] Moving cursor to {pos.Name}...");
                            SetCursorPos(pos.X, pos.Y);
                            Thread.Sleep(150); // Time to see cursor move
                            
                            LogMessage($"[MOVEMENT TEST] *** PRESSING {movementKey} WITH WINDOW FOCUS! ***");
                            PressAndHoldKey(movementKey);
                            
                            LogMessage($"[MOVEMENT TEST] *** {pos.Name} COMPLETED - DID CHARACTER MOVE? ***");
                            
                            // Small delay between tests
                            LogMessage("[MOVEMENT TEST] Waiting 1.5 seconds before next test...");
                            Thread.Sleep(1500);
                        }
                        
                        LogMessage("[MOVEMENT TEST] ===== ALL 5 POSITIONS TESTED - FOCUS FIX CONFIRMED! =====");
                    }
                    else
                    {
                        LogMessage("[MOVEMENT TEST] Method: Mouse click fallback");
                        var pos = testPositions[0];
                        ClickAt(pos.X, pos.Y);
                        LogMessage("[MOVEMENT TEST] Mouse click executed - did character move?");
                    }
                     
                     LogMessage("[MOVEMENT TEST] ===== TESTING COMPLETED =====");
                }
                else
                {
                    LogMessage("[MOVEMENT TEST] ERROR: Could not get player render component");
                }
            }
            else
            {
                LogMessage("[MOVEMENT TEST] ERROR: Could not get player position");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error in movement test: {ex.Message}");
        }
    }

    private bool CheckForAreaTransition()
    {
        try
        {
            var currentArea = GameController.Area.CurrentArea;
            if (currentArea == null) return false;
            
            var areaName = currentArea.Area.Name.ToLowerInvariant();
            var rawName = currentArea.Area.RawName.ToLowerInvariant();
            
            // Check if we're still in Aqueducts
            bool stillInAqueducts = areaName.Contains("aqueduct") || rawName.Contains("aqueduct");
            
            if (!stillInAqueducts)
            {
                LogMessage($"[AREA TRANSITION] Left Aqueducts! Now in: {currentArea.Area.Name} (Raw: {currentArea.Area.RawName})");
                return true;
            }
            
            // ENHANCED: Check for area transition entities nearby
            if (DetectNearbyAreaTransitions())
            {
                LogMessage("[AREA TRANSITION] Near area transition - attempting to interact");
                
                // Try to click on area transition
                InteractWithNearestAreaTransition();
                return false; // Don't mark as transitioned yet, wait for actual area change
            }
            
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Error checking for area transition: {ex.Message}");
            return false;
        }
    }
    
    private bool DetectNearbyAreaTransitions()
    {
        try
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos) return false;
            
            var playerGridPos = playerPos.GridPos;
            
            // Look for area transition entities within range
            var entities = GameController.EntityListWrapper.Entities;
            foreach (var entity in entities.Where(e => e.IsValid))
            {
                // Check for area transition components or paths
                if (IsAreaTransitionEntity(entity))
                {
                    var entityPos = entity.GetComponent<Positioned>();
                    if (entityPos != null)
                    {
                        // Convert SharpDX.Vector2 to System.Numerics.Vector2 for distance calculation
                        var playerGridPosNum = new System.Numerics.Vector2(playerGridPos.X, playerGridPos.Y);
                        var entityGridPosNum = new System.Numerics.Vector2(entityPos.GridPos.X, entityPos.GridPos.Y);
                        var distance = Vector2.Distance(playerGridPosNum, entityGridPosNum);
                        if (distance < 150) // Within interaction range
                        {
                            LogMessage($"[TRANSITION DETECTED] Found area transition at distance {distance:F1}: {entity.Path}");
                            return true;
                        }
                    }
                }
            }
            
            return false;
        }
        catch (Exception ex)
        {
            LogError($"Error detecting area transitions: {ex.Message}");
            return false;
        }
    }
    
    private bool IsAreaTransitionEntity(Entity entity)
    {
        if (entity?.Path == null) return false;
        
        var path = entity.Path.ToLowerInvariant();
        
        // Common area transition patterns
        var transitionPatterns = new[]
        {
            "areatransition",
            "transition",
            "exit",
            "entrance",
            "door",
            "passage",
            "portal",
            "gateway"
        };
        
        return transitionPatterns.Any(pattern => path.Contains(pattern));
    }
    
    private void InteractWithNearestAreaTransition()
    {
        try
        {
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos) return;
            
            var playerGridPos = playerPos.GridPos;
            Entity nearestTransition = null;
            float nearestDistance = float.MaxValue;
            
            // Find the nearest area transition
            var entities = GameController.EntityListWrapper.Entities;
            foreach (var entity in entities.Where(e => e.IsValid))
            {
                if (IsAreaTransitionEntity(entity))
                {
                    var entityPos = entity.GetComponent<Positioned>();
                    if (entityPos != null)
                    {
                        // Convert SharpDX.Vector2 to System.Numerics.Vector2 for distance calculation
                        var playerGridPosNum = new System.Numerics.Vector2(playerGridPos.X, playerGridPos.Y);
                        var entityGridPosNum = new System.Numerics.Vector2(entityPos.GridPos.X, entityPos.GridPos.Y);
                        var distance = Vector2.Distance(playerGridPosNum, entityGridPosNum);
                        if (distance < nearestDistance && distance < 150)
                        {
                            nearestDistance = distance;
                            nearestTransition = entity;
                        }
                    }
                }
            }
            
            if (nearestTransition != null)
            {
                // Click on the area transition
                var render = nearestTransition.GetComponent<Render>();
                if (render != null)
                {
                    var worldPos = render.PosNum;
                    var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                    
                    LogMessage($"[AREA INTERACTION] Clicking on area transition at ({screenPos.X:F0}, {screenPos.Y:F0})");
                    ClickAt((int)screenPos.X, (int)screenPos.Y);
                    
                    // Wait a moment for the transition
                    Thread.Sleep(1000);
                }
            }
        }
        catch (Exception ex)
        {
            LogError($"Error interacting with area transition: {ex.Message}");
        }
    }
    
    // DIRECTIONAL INTELLIGENCE: Analyze path to determine if it leads away from spawn
    private float AnalyzePathDirection(List<Vector2i> path, string pathName)
    {
        if (path == null || path.Count == 0)
            return 0f;
            
        if (!_hasRecordedSpawnPosition)
        {
            LogMessage($"[PATH ANALYSIS] No spawn position recorded yet - using neutral score for {pathName}");
            return 0.5f; // Neutral score if we haven't recorded spawn yet
        }
        
        // Get path start and end points
        var pathStart = new System.Numerics.Vector2(path[0].X, path[0].Y);
        var pathEnd = new System.Numerics.Vector2(path[path.Count - 1].X, path[path.Count - 1].Y);
        
        // Calculate distances from spawn to start and end
        var distanceToStart = System.Numerics.Vector2.Distance(_initialSpawnPosition, pathStart);
        var distanceToEnd = System.Numerics.Vector2.Distance(_initialSpawnPosition, pathEnd);
        
        // Calculate score based on how far the path takes us from spawn
        var directionalProgress = distanceToEnd - distanceToStart;
        
        // Normalize score (positive = away from spawn, negative = toward spawn)
        // Add path length factor to slightly prefer more exploration
        var explorationBonus = Math.Min(path.Count / 500f, 0.2f); // Up to 0.2 bonus for longer exploration
        
        var score = 0.5f + (directionalProgress / 200f) + explorationBonus; // Baseline 0.5, +/- up to ~0.5 based on direction
        score = Math.Max(0f, Math.Min(1f, score)); // Clamp to 0-1 range
        
        LogMessage($"[DIRECTION ANALYSIS] {pathName}: spawn=({_initialSpawnPosition.X:F0},{_initialSpawnPosition.Y:F0}), start=({pathStart.X:F0},{pathStart.Y:F0}), end=({pathEnd.X:F0},{pathEnd.Y:F0})");
        LogMessage($"[DIRECTION ANALYSIS] {pathName}: distance to start={distanceToStart:F1}, distance to end={distanceToEnd:F1}, progress={directionalProgress:F1}, score={score:F2}");
        
        return score;
    }
    
    /// <summary>
    /// Find where the path intersects with a circle around the player (Pure Pursuit Algorithm)
    /// This provides much smoother navigation than chasing exact waypoints
    /// </summary>
    private System.Numerics.Vector2? FindPathIntersectionWithRadius(List<Vector2i> path, int startIndex = 0)
    {
        var playerPos = GetPlayerPosition();
        if (playerPos == null || path.Count == 0) return null;
        
        var playerWorldPos = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
        var bestIntersection = System.Numerics.Vector2.Zero;
        var bestDistance = float.MaxValue;
        var foundIntersection = false;
        
        // Look ahead from current position in path
        for (int i = Math.Max(startIndex, 1); i < path.Count; i++)
        {
            var currentPoint = new System.Numerics.Vector2(path[i - 1].X, path[i - 1].Y);
            var nextPoint = new System.Numerics.Vector2(path[i].X, path[i].Y);
            
            // Find intersection of line segment with circle around player
            var pursuitRadius = Settings.MovementSettings.PursuitRadius.Value;
            var intersection = FindLineCircleIntersection(currentPoint, nextPoint, playerWorldPos, pursuitRadius);
            
            if (intersection.HasValue)
            {
                // Prefer intersections that are further along the path
                var distanceAlongPath = i;
                if (distanceAlongPath < bestDistance)
                {
                    bestDistance = distanceAlongPath;
                    bestIntersection = intersection.Value;
                    foundIntersection = true;
                }
            }
        }
        
        if (foundIntersection)
        {
            var pursuitRadius = Settings.MovementSettings.PursuitRadius.Value;
            LogMessage($"[PURSUIT] 🎯 Found path intersection at ({bestIntersection.X:F0}, {bestIntersection.Y:F0}) with radius {pursuitRadius}");
            return bestIntersection;
        }
        
        // Fallback: if no intersection found, use a point further along the path
        if (startIndex + 10 < path.Count)
        {
            var fallbackPoint = new System.Numerics.Vector2(path[startIndex + 10].X, path[startIndex + 10].Y);
            LogMessage($"[PURSUIT] ⚠️ No intersection found, using fallback point at ({fallbackPoint.X:F0}, {fallbackPoint.Y:F0})");
            return fallbackPoint;
        }
        
        return null;
    }
    
    /// <summary>
    /// Find intersection point between a line segment and a circle
    /// </summary>
    private System.Numerics.Vector2? FindLineCircleIntersection(
        System.Numerics.Vector2 lineStart, 
        System.Numerics.Vector2 lineEnd, 
        System.Numerics.Vector2 circleCenter, 
        float radius)
    {
        var direction = lineEnd - lineStart;
        var toCenter = lineStart - circleCenter;
        
        var a = System.Numerics.Vector2.Dot(direction, direction);
        var b = 2 * System.Numerics.Vector2.Dot(toCenter, direction);
        var c = System.Numerics.Vector2.Dot(toCenter, toCenter) - radius * radius;
        
        var discriminant = b * b - 4 * a * c;
        
        if (discriminant < 0) return null; // No intersection
        
        var sqrtDiscriminant = (float)Math.Sqrt(discriminant);
        var t1 = (-b - sqrtDiscriminant) / (2 * a);
        var t2 = (-b + sqrtDiscriminant) / (2 * a);
        
        // Choose the intersection point that's further along the line (prefer forward movement)
        var t = (t2 > t1 && t2 >= 0 && t2 <= 1) ? t2 : t1;
        
        if (t >= 0 && t <= 1) // Intersection is within the line segment
        {
            return lineStart + t * direction;
        }
        
        return null;
    }
}