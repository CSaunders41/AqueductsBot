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
    
    // Separate bot automation state from plugin state
    private bool _botEnabled = false;
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
    
    // Movement debug file logging
    private string _movementDebugFilePath = "";
    private readonly object _movementDebugLock = new object();
    
    // Add hotkey state tracking
    private bool _commaKeyPressed = false;
    private bool _periodKeyPressed = false;
    
    // Add state logging tracking
    private DateTime _lastStateLog = DateTime.MinValue;
    
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
    
    // 🎯 COORDINATE SYSTEM FIX: Grid to World conversion constants
    private const int TILE_TO_GRID_CONVERSION = 23;
    private const int TILE_TO_WORLD_CONVERSION = 250;
    private const float GRID_TO_WORLD_MULTIPLIER = TILE_TO_WORLD_CONVERSION / (float)TILE_TO_GRID_CONVERSION;
    
    // WAYPOINT STABILITY: Prevent rapid re-evaluation
    private DateTime _lastWaypointSkip = DateTime.MinValue;
    private int _lastOptimizedWaypoint = -1;
    
    // RADIUS-BASED PATH INTERSECTION: Pure pursuit algorithm for smooth navigation
    private System.Numerics.Vector2 _lastIntersectionPoint = System.Numerics.Vector2.Zero;
    private DateTime _lastPathAdvancement = DateTime.MinValue;
    private int _stuckTargetCount = 0;
    private System.Numerics.Vector2 _lastTargetPoint = System.Numerics.Vector2.Zero;
    
    // Fields for click tracking and logging buffer
    private System.Numerics.Vector2 _lastClickScreenPos = System.Numerics.Vector2.Zero;
    private int _duplicateClickCount = 0;
    private const int MAX_DUPLICATE_CLICKS = 3;
    private readonly List<string> _movementDebugBuffer = new List<string>();
    private DateTime _lastFileWrite = DateTime.MinValue;
    
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
            _movementDebugFilePath = Path.Combine(pluginDir, $"AqueductsBot_Movement_{DateTime.Now:yyyyMMdd}.log");
            
            // Write header to log file
            File.AppendAllText(_logFilePath, $"=== AqueductsBot Log Started: {DateTime.Now} ==={Environment.NewLine}");
            File.AppendAllText(_movementDebugFilePath, $"=== AqueductsBot Movement Debug Log Started: {DateTime.Now} ==={Environment.NewLine}");
        }
        catch (Exception ex)
        {
            // If file logging fails, continue with console-only logging
            Console.WriteLine($"[AqueductsBot] Could not initialize log file: {ex.Message}");
        }
    }
    
    private void LogMessage(string message)
    {
        // Only log if debug messages are enabled
        if (!Settings.DebugSettings.DebugMode.Value) return;
        
        LogMessageInternal(message);
    }
    
    private void LogImportant(string message)
    {
        // Always log important messages regardless of debug settings
        LogMessageInternal(message);
    }
    
    private void LogMessageInternal(string message)
    {
        lock (_logLock)
        { 
            if (message == _lastLogMessage) return; // Prevent spam
            _lastLogMessage = message;
            
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var fullMessage = $"[{timestamp}] {message}";
            
            _logMessages.Add(fullMessage);
            
            // Keep only last 50 messages to prevent memory issues
            if (_logMessages.Count > 50)
            {
                _logMessages.RemoveAt(0);
            }
            
            // FIXED: Don't create recursive loop - log directly to ExileApi console
            try 
            {
                // Use ExileCore's direct logging to avoid recursion
                DebugWindow.LogMsg(fullMessage);
            }
            catch
            {
                // If ExileCore logging fails, just continue - don't recurse
            }
        }
    }
    
    private void LogError(string message)
    {
        // FIXED: Use LogImportant to avoid recursion, and don't add "ERROR:" prefix to avoid double prefixing
        LogImportant(message);
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
            
            // NEW: Debug intersection hotkey
            if (Settings.DebugSettings.DebugIntersectionHotkey.PressedOnce())
            {
                LogImportant($"[DEBUG HOTKEY] {Settings.DebugSettings.DebugIntersectionHotkey.Value} pressed - Finding pursuit circle intersection...");
                DebugIntersectionPoint();
            }
            
            // NEW: Add comma (start) and period (stop) hotkeys for easy control
            // Use state tracking to prevent multiple triggers
            bool commaPressed = Input.IsKeyDown(Keys.Oemcomma);
            if (commaPressed && !_commaKeyPressed) // Key just pressed (not held)
            {
                if (!_botEnabled)
                {
                    LogImportant("[HOTKEY] Comma pressed - Starting bot!");
                    StartBot();
                }
            }
            _commaKeyPressed = commaPressed;
            
            bool periodPressed = Input.IsKeyDown(Keys.OemPeriod);
            if (periodPressed && !_periodKeyPressed) // Key just pressed (not held)
            {
                if (_botEnabled)
                {
                    LogImportant("[HOTKEY] Period pressed - Stopping bot!");
                    StopBot();
                }
            }
            _periodKeyPressed = periodPressed;
            
            // Main bot logic - only run if bot is enabled (separate from plugin enable)
            if (_botEnabled && _currentState != BotState.Disabled)
            {
                // BASIC DIAGNOSTIC: Log directly to file without using LogMovementDebug
                if (DateTime.Now.Subtract(_lastStateLog).TotalSeconds >= 3)
                {
                    try
                    {
                        var diagnosticMsg = $"[{DateTime.Now:HH:mm:ss.fff}] [DIAGNOSTIC] Bot running - State: {_currentState}, Path: {_currentPath.Count}, Index: {_currentPathIndex}{Environment.NewLine}";
                        File.AppendAllText(_movementDebugFilePath, diagnosticMsg);
                    }
                    catch (Exception ex)
                    {
                        LogMessage($"[DIAGNOSTIC ERROR] Can't write to file: {ex.Message}");
                    }
                    
                    LogMovementDebug($"[BOT STATE] Current state: {_currentState}, Path count: {_currentPath.Count}, Path index: {_currentPathIndex}");
                    _lastStateLog = DateTime.Now;
                }
                
                ProcessBotLogic();
            }
            
            // Debug rendering
            if (Settings.DebugSettings.DebugMode.Value && Settings.DebugSettings.ShowPathPoints.Value && _currentPath.Count > 0)
            {
                DrawPathDebug();
            }
            
            // Show player calculation circle if enabled - using exact Aim-Bot approach
            if (Settings.MovementSettings.ShowPlayerCircle.Value)
            {
                var playerRender = GameController.Player.GetComponent<Render>();
                if (playerRender != null)
                {
                    // Convert SharpDX.Vector3 to System.Numerics.Vector3
                    var sharpDxPos = playerRender.Pos;
                    var pos = new Vector3(sharpDxPos.X, sharpDxPos.Y, sharpDxPos.Z);
                    DrawEllipseToWorld(pos, (int)Settings.MovementSettings.PursuitRadius.Value, 25, 2, Color.LawnGreen);
                }
                DrawTargetPoint(); // Also show where we're actually targeting
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
        // First, draw the automatic settings UI with all our sliders and checkboxes
        base.DrawSettings();
        
        ImGui.Separator();
        
        // Draw popout windows if enabled
        if (Settings.DebugSettings.ShowPopoutStatus.Value)
        {
            DrawPopoutStatusWindow();
        }
        
        if (Settings.DebugSettings.ShowPopoutPathfinding.Value)
        {
            DrawPopoutPathfindingWindow();
        }
        
        // Streamlined main status display
        DrawMainStatusDisplay();
    }
    
    private void DrawPopoutStatusWindow()
    {
        var showWindow = Settings.DebugSettings.ShowPopoutStatus.Value;
        if (ImGui.Begin("AqueductsBot Status", ref showWindow))
        {
            // Declare variables for UI usage
            bool keyboardEnabled = Settings.MovementSettings.UseMovementKey.Value;
            Keys currentMovementKey = Settings.MovementSettings.MovementKey.Value;
            
            ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🚀 AQUADUCTS BOT STATUS");
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
        }
        ImGui.End();
        
        // Update the setting if the window was closed
        Settings.DebugSettings.ShowPopoutStatus.Value = showWindow;
    }
    
    private void DrawPopoutPathfindingWindow()
    {
        var showWindow = Settings.DebugSettings.ShowPopoutPathfinding.Value;
        if (ImGui.Begin("AqueductsBot Pathfinding", ref showWindow))
        {
            ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🧭 PATHFINDING SYSTEM");
            ImGui.Separator();
            
            ImGui.BulletText("Smart target selection with 30+ strategic points");
            ImGui.BulletText("Path optimization with waypoint reduction");
            ImGui.BulletText("Dynamic movement precision and timing");
            ImGui.BulletText("Stuck detection and recovery system");
            ImGui.BulletText("Automatic area transition detection");
            ImGui.BulletText("Cardinal direction + edge-based exploration");
            
            ImGui.Separator();
            
            // Current State Display
            switch (_currentState)
            {
                case BotState.Disabled:
                    if (_radarAvailable)
                    {
                        ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🟢 READY TO START");
                        ImGui.Text("Bot is ready! Press F1 or start button to begin.");
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
        }
        ImGui.End();
        
        // Update the setting if the window was closed
        Settings.DebugSettings.ShowPopoutPathfinding.Value = showWindow;
    }
    
    private void DrawMainStatusDisplay()
    {
        // Streamlined main display
        ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "🚀 AQUADUCTS BOT - PATHFINDING ENABLED");
        
        // Quick status line
        var statusColor = _currentState switch
        {
            BotState.Disabled => new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1),
            BotState.WaitingForRadar => new System.Numerics.Vector4(1, 0.5f, 0, 1),
            BotState.WaitingForAqueducts => new System.Numerics.Vector4(0, 0.8f, 1, 1),
            BotState.MovingAlongPath => new System.Numerics.Vector4(0, 1, 0, 1),
            BotState.AtAreaExit => new System.Numerics.Vector4(0, 1, 1, 1),
            BotState.Error => new System.Numerics.Vector4(1, 0, 0, 1),
            _ => new System.Numerics.Vector4(1, 1, 1, 1)
        };
        
        var botStatus = _botEnabled ? "Running" : "Stopped";
        ImGui.TextColored(statusColor, $"Bot: {botStatus} | State: {_currentState} | Radar: {(_radarAvailable ? "Connected" : "Disconnected")} | Runs: {_runsCompleted}");
        
        if (_currentPath.Count > 0)
        {
            var progress = (float)_currentPathIndex / _currentPath.Count;
            ImGui.ProgressBar(progress, new System.Numerics.Vector2(-1, 20), $"Path Progress: {_currentPathIndex + 1}/{_currentPath.Count} ({progress * 100:F1}%)");
        }
        
        ImGui.Separator();
        
        // Control buttons in a more compact layout
        if (ImGui.Button("🚀 Start Bot"))
        {
            if (!_botEnabled)
            {
                StartBot();
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("⏹️ Stop Bot"))
        {
            if (_botEnabled)
            {
                StopBot();
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🛑 Emergency Stop"))
        {
            EmergencyStop();
        }
        
        ImGui.SameLine();
        ImGui.TextColored(new System.Numerics.Vector4(1, 1, 0, 1), "Hotkeys: ',' start | '.' stop");
        
        // Test buttons in second row
        if (ImGui.Button("🧪 Test Movement"))
        {
            TestMouseClick();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("📡 Test Radar"))
        {
            TestRadarConnection();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🎯 Debug Coords"))
        {
            DebugCoordinateSystem();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🔍 Debug Intersection"))
        {
            DebugIntersectionPoint();
        }
        
        // Add helpful text about the debug intersection feature
        ImGui.Text($"Debug Intersection: Press {Settings.DebugSettings.DebugIntersectionHotkey.Value} or click button above");
        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 0.7f, 1), "Moves mouse to where pursuit circle intersects with radar path");
        
        // Third row - File access and diagnostics
        if (ImGui.Button("📁 Open Movement Log"))
        {
            try
            {
                if (File.Exists(_movementDebugFilePath))
                {
                    System.Diagnostics.Process.Start("notepad.exe", _movementDebugFilePath);
                    LogMessage($"[FILE] Opening movement debug file: {_movementDebugFilePath}");
                }
                else
                {
                    LogMessage($"[FILE] Movement debug file doesn't exist: {_movementDebugFilePath}");
                }
            }
            catch (Exception ex)
            {
                LogMessage($"[FILE] Error opening movement debug file: {ex.Message}");
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("🔧 Test File Write"))
        {
            try
            {
                var testMessage = $"TEST WRITE - {DateTime.Now}: Bot enabled: {_botEnabled}, State: {_currentState}";
                File.AppendAllText(_movementDebugFilePath, testMessage + Environment.NewLine);
                LogMessage($"[TEST] Successfully wrote to file: {_movementDebugFilePath}");
            }
            catch (Exception ex)
            {
                LogMessage($"[TEST] Failed to write to file: {ex.Message}");
            }
        }
        
        ImGui.SameLine();
        if (ImGui.Button("📊 Show File Path"))
        {
            LogMessage($"[FILE INFO] Movement debug file path: {_movementDebugFilePath}");
            LogMessage($"[FILE INFO] File exists: {File.Exists(_movementDebugFilePath)}");
            LogMessage($"[FILE INFO] Bot enabled: {_botEnabled}, State: {_currentState}");
        }
        
        // Movement configuration
        bool keyboardEnabled = Settings.MovementSettings.UseMovementKey.Value;
        Keys currentMovementKey = Settings.MovementSettings.MovementKey.Value;
        
        ImGui.Text($"Movement: {(keyboardEnabled ? $"Keyboard ({currentMovementKey})" : "Mouse Clicks")}");
        
        // Compact logging
        ImGui.Separator();
        ImGui.Text("Recent Log Messages:");
        
        if (ImGui.BeginChild("CompactLog", new System.Numerics.Vector2(0, 100)))
        {
            var recentLogs = GetRecentLogMessages(20);
            ImGui.TextUnformatted(recentLogs);
            
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
            {
                ImGui.SetScrollHereY(1.0f);
            }
        }
        ImGui.EndChild();
        
        if (ImGui.Button("🗑️ Clear Log"))
        {
            lock (_logLock)
            {
                _logMessages.Clear();
            }
        }
    }
    
    private void ProcessBotLogic()
    {
        // CHECK: Runtime limit reached?
        if (Settings.BotSettings.MaxRuntimeMinutes.Value > 0 && _botStartTime != default)
        {
            var runtime = DateTime.Now - _botStartTime;
            if (runtime.TotalMinutes >= Settings.BotSettings.MaxRuntimeMinutes.Value)
            {
                LogMessage($"[AUTO STOP] ⏰ Reached maximum runtime limit ({Settings.BotSettings.MaxRuntimeMinutes.Value} minutes) - stopping bot!");
                StopBot();
                return;
            }
        }
        
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
                    // Try to reconnect to Radar based on configured interval
                    if ((DateTime.Now - _lastRadarRetry).TotalSeconds >= Settings.TimingSettings.RadarRetryInterval.Value)
                    {
                        TryConnectToRadar();
                        _lastRadarRetry = DateTime.Now;
                    }
                }
                break;
                
            case BotState.WaitingForAqueducts:
                // CHECK: Are we already in Aqueducts? (Handles bot startup mid-area)
                if (IsInAqueducts(GameController.Area.CurrentArea))
                {
                    LogMessage("✅ Already in Aqueducts - transitioning to pathfinding!");
                    _currentState = BotState.GettingPath;
                    
                    // DIRECTIONAL INTELLIGENCE: Record initial spawn position when we first enter Aqueducts
                    if (!_hasRecordedSpawnPosition)
                    {
                        var playerPos = GetPlayerPosition();
                        if (playerPos != null)
                        {
                            _initialSpawnPosition = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
                            _hasRecordedSpawnPosition = true;
                            LogMessage($"[SPAWN TRACKING] 📍 Recorded initial spawn position: ({_initialSpawnPosition.X:F1}, {_initialSpawnPosition.Y:F1})");
                        }
                    }
                    break;
                }
                
                // ENHANCED: Check for area transition while waiting
                CheckForAreaTransition();
                break;
                
            case BotState.GettingPath:
                // TRANSITION FIX: If we have a valid path, start moving!
                if (_currentPath != null && _currentPath.Count > 0)
                {
                    LogMessage($"[STATE TRANSITION] ✅ Got valid path with {_currentPath.Count} points - transitioning to movement!");
                    _currentState = BotState.MovingAlongPath;
                    _currentPathIndex = 0;
                    break;
                }
                
                if (CanRequestNewPath())
                {
                    // Only request path if we haven't requested one recently (prevent spam)
                    var waypointCheckFrequency = Settings.RadarSettings.WaypointCheckFrequency.Value / 1000.0; // Convert ms to seconds
                    if ((DateTime.Now - _lastActionTime).TotalSeconds >= waypointCheckFrequency)
                    {
                        LogMessage("[ENHANCED PATHFINDING] Player in Aqueducts and Radar available - using smart target selection");
                        RequestPathToExit();
                        _lastPathRequest = DateTime.Now;
                    }
                }
                else if (_lastPathRequest != DateTime.MinValue)
                {
                    // Check for timeout based on configured path request timeout
                    var timeSinceRequest = (DateTime.Now - _lastPathRequest).TotalSeconds;
                    if (timeSinceRequest > Settings.RadarSettings.PathRequestTimeout.Value)
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
                        
                        // CHECK: Max runs limit reached?
                        if (Settings.BotSettings.MaxRuns.Value > 0 && _runsCompleted >= Settings.BotSettings.MaxRuns.Value)
                        {
                            LogMessage($"[AUTO STOP] ✅ Reached maximum runs limit ({Settings.BotSettings.MaxRuns.Value}) - stopping bot!");
                            StopBot();
                            return;
                        }
                        
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
                    
                    // CHECK: Max runs limit reached?
                    if (Settings.BotSettings.MaxRuns.Value > 0 && _runsCompleted >= Settings.BotSettings.MaxRuns.Value)
                    {
                        LogMessage($"[AUTO STOP] ✅ Reached maximum runs limit ({Settings.BotSettings.MaxRuns.Value}) - stopping bot!");
                        StopBot();
                        return;
                    }
                    
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
        if (_botEnabled)
        {
            StopBot();
        }
        else
        {
            StartBot();
        }
    }
    
    private void StartBot()
    {
        _botEnabled = true;
        LogImportant("Bot enabled - starting automation");
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
        _lastPathAdvancement = DateTime.MinValue;
        _stuckTargetCount = 0;
        _lastTargetPoint = System.Numerics.Vector2.Zero;
        LogMessage("[PURSUIT] 🔄 Reset pursuit navigation tracking for new bot session");
        
        if (!_radarAvailable)
        {
            TryConnectToRadar();
        }
    }
    
    private void StopBot()
    {
        _botEnabled = false;
        LogImportant("Bot disabled - stopping automation");
        _currentState = BotState.Disabled;
        
        // Cancel any ongoing pathfinding
        _pathfindingCts.Cancel();
        _pathfindingCts = new CancellationTokenSource();
        
        // Clear current path
        _currentPath.Clear();
        _currentPathIndex = 0;
    }
    
    private void EmergencyStop()
    {
        LogImportant("EMERGENCY STOP activated!");
        StopBot();
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
        // Don't spam path requests - use configured interval
        return (DateTime.Now - _lastActionTime).TotalMilliseconds > Settings.RadarSettings.WaypointCheckFrequency.Value;
    }
    
    private void RequestPathToExit()
    {
        try
        {
                    LogMessage("[DEBUG] RequestPathToExit started with smart target selection");
        LogMovementDebug("[PATHFINDING] RequestPathToExit started - requesting path from radar");
        _lastActionTime = DateTime.Now;
            
            // Check if radar is still available
            if (!_radarAvailable || _radarLookForRoute == null)
            {
                if (Settings.DebugSettings.ShowRadarStatus.Value)
                {
                    LogMessage("[ERROR] Radar not available when trying to request path");
                }
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
                Thread.Sleep(Settings.MovementSettings.AutoClickDelay.Value);
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
                if (pathAge > Settings.TimingSettings.PathStalenessTime.Value) // Path is stale after configured time
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
                
                // STABILITY: Require significant improvement to switch paths (configurable threshold)
                if (newPathScore > currentPathScore + Settings.ConfigurationSettings.PathScoreThreshold.Value)
                {
                    shouldAcceptPath = true;
                    acceptReason = $"significantly better direction (score: {newPathScore:F2} vs {currentPathScore:F2})";
                }
                // If directional scores are similar, prefer much shorter paths only
                else if (Math.Abs(newPathScore - currentPathScore) <= Settings.ConfigurationSettings.PathScoreThreshold.Value && path.Count < _currentPath.Count * 0.7f) // Use configurable threshold
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
        // REASONABLE DELAYS: Allow actual movement while preventing spam
        var minDelay = Settings.MovementSettings.MinMoveDelayMs;
        var maxDelay = Settings.MovementSettings.MaxMoveDelayMs;
        
        // Use more reasonable base delays that allow movement
        var baseMinDelay = Math.Max(minDelay, 300); // At least 300ms between movements
        var baseMaxDelay = Math.Max(maxDelay, 800); // Up to 800ms between movements
        
        // For very close targets, use slightly longer delays but still allow movement
        if (distanceToTarget < 15f)
        {
            return _random.Next(baseMaxDelay, baseMaxDelay + 400); // 800-1200ms for very close
        }
        // For close targets, use reasonable delays
        else if (distanceToTarget < 50f)
        {
            return _random.Next(baseMinDelay + 200, baseMaxDelay); // 500-800ms for close targets
        }
        // For medium distance targets, use standard delays
        else if (distanceToTarget < 150f)
        {
            return _random.Next(baseMinDelay, baseMaxDelay); // 300-800ms standard
        }
        // For far targets, use faster movement
        else if (distanceToTarget > 300f)
        {
            return _random.Next(baseMinDelay, baseMinDelay + 200); // 300-500ms for far targets
        }
        
        return _random.Next(baseMinDelay, baseMaxDelay); // Standard delay
    }
    
    private int GetOptimizedWaypointIndex()
    {
        // CONSERVATIVE WAYPOINT SKIPPING: Better balance between distance and stability
        var MIN_CLICK_DISTANCE = Settings.ConfigurationSettings.MinClickDistance.Value;
        var PREFERRED_CLICK_DISTANCE = Settings.ConfigurationSettings.PreferredClickDistance.Value;
        var MAX_LOOKAHEAD = Settings.ConfigurationSettings.MaxLookaheadWaypoints.Value;
        
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
            // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
            var worldPos = new Vector3(
                targetPoint.X * GRID_TO_WORLD_MULTIPLIER,
                targetPoint.Y * GRID_TO_WORLD_MULTIPLIER,
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
            var windowRect = GameController.Window.GetWindowRectangle();
            var dpiScale = GetDpiScale();
            int absoluteX = (int)(x * dpiScale + windowRect.X);
            int absoluteY = (int)(y * dpiScale + windowRect.Y);
            LogMovementDebug($"[CLICK] Adjusted for DPI {dpiScale:F2}: Game ({x}, {y}) → Absolute ({absoluteX}, {absoluteY})");

            var screenPos = new System.Numerics.Vector2(absoluteX, absoluteY);
            
            if (screenPos == _lastClickScreenPos)
            {
                _duplicateClickCount++;
                if (_duplicateClickCount >= MAX_DUPLICATE_CLICKS)
                {
                    LogMovementDebug("[DUPLICATE CLICK] Too many clicks at same position - triggering stuck recovery");
                    HandleStuckSituation();
                    _duplicateClickCount = 0;
                    return;
                }
            }
            else
            {
                _duplicateClickCount = 0;
            }
            _lastClickScreenPos = screenPos;

            if (!EnsureGameWindowFocused()) 
            {
                LogMovementDebug("[CLICK] Failed to focus game window");
                return;
            }

            var playerPosBefore = GetPlayerPosition()?.GridPos;
            
            SetCursorPos(absoluteX, absoluteY);
            
            INPUT[] inputs = new INPUT[2];
            inputs[0] = CreateMouseInput(MOUSEEVENTF_LEFTDOWN, 0, 0);
            inputs[1] = CreateMouseInput(MOUSEEVENTF_LEFTUP, 0, 0);
            SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
            
            Thread.Sleep(100);
            
            var playerPosAfter = GetPlayerPosition()?.GridPos;
            if (playerPosBefore.HasValue && playerPosAfter.HasValue && playerPosBefore.Value == playerPosAfter.Value)
            {
                LogMovementDebug($"[INPUT FAILURE] Character did not move after clicking at ({x}, {y})");
            }
        }
        catch (Exception ex)
        {
            LogError($"Error clicking at ({x}, {y}): {ex.Message}");
        }
    }

    private INPUT CreateMouseInput(uint flags, int dx, int dy)
    {
        return new INPUT
        {
            Type = INPUT_MOUSE,
            Data = new MOUSEKEYBDINPUT 
            { 
                Mouse = new MOUSEINPUT 
                { 
                    Flags = flags, 
                    X = dx, 
                    Y = dy,
                    MouseData = 0,
                    Time = 0,
                    ExtraInfo = IntPtr.Zero 
                } 
            }
        };
    }

    private bool EnsureGameWindowFocused()
    {
        IntPtr poeWindow = FindWindow(null, "Path of Exile");
        if (poeWindow == IntPtr.Zero) return false;
        
        if (GetForegroundWindow() != poeWindow)
        {
            SetForegroundWindow(poeWindow);
            Thread.Sleep(50);
        }
        
        return GetForegroundWindow() == poeWindow;
    }

    private float GetDpiScale()
    {
        using (var graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero))
        {
            return graphics.DpiX / 96f;
        }
    }
    
    private void DebugIntersectionPoint()
    {
        try
        {
            LogMessage("=== DEBUG INTERSECTION POINT START ===");
            
            // 1. Validate we have the necessary components
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos)
            {
                LogMessage("[DEBUG INTERSECTION] ERROR: Cannot get player position");
                return;
            }
            
            if (_currentPath == null || _currentPath.Count == 0)
            {
                LogMessage("[DEBUG INTERSECTION] ERROR: No current radar path available");
                LogMessage("[DEBUG INTERSECTION] TIP: Start the bot or ensure you're in Aqueducts with an active path");
                return;
            }
            
            var render = player.GetComponent<Render>();
            if (render == null)
            {
                LogMessage("[DEBUG INTERSECTION] ERROR: Cannot get player render component");
                return;
            }
            
            // 2. Get player position and pursuit radius  
            var playerWorldPos = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
            var pursuitRadius = Settings.MovementSettings.PursuitRadius.Value;
            
            LogMessage($"[DEBUG INTERSECTION] Player at world: ({playerWorldPos.X:F1}, {playerWorldPos.Y:F1})");
            LogMessage($"[DEBUG INTERSECTION] Pursuit radius: {pursuitRadius:F1}");
            LogMessage($"[DEBUG INTERSECTION] Current path has {_currentPath.Count} waypoints, current index: {_currentPathIndex}");
            
            // 3. Find the intersection point using the same method as the bot
            var intersectionPoint = FindPathIntersectionWithSpecificRadius(_currentPath, _currentPathIndex, pursuitRadius);
            
            if (!intersectionPoint.HasValue)
            {
                LogMessage("[DEBUG INTERSECTION] ❌ NO INTERSECTION FOUND!");
                LogMessage("[DEBUG INTERSECTION] This means the current radar path doesn't cross the pursuit circle");
                LogMessage("[DEBUG INTERSECTION] Possible reasons:");
                LogMessage("[DEBUG INTERSECTION] - Path is too far away");
                LogMessage("[DEBUG INTERSECTION] - Path segments are too short");  
                LogMessage("[DEBUG INTERSECTION] - Current path index is too far along");
                
                // Try to find ANY intersection with a larger radius as a fallback
                LogMessage("[DEBUG INTERSECTION] Trying with 2x radius as fallback...");
                var fallbackIntersection = FindPathIntersectionWithSpecificRadius(_currentPath, 0, pursuitRadius * 2f);
                if (fallbackIntersection.HasValue)
                {
                    LogMessage($"[DEBUG INTERSECTION] ✅ FALLBACK: Found intersection at 2x radius: ({fallbackIntersection.Value.X:F1}, {fallbackIntersection.Value.Y:F1})");
                    intersectionPoint = fallbackIntersection;
                }
                else
                {
                    LogMessage("[DEBUG INTERSECTION] ❌ No intersection even with 2x radius - path may be completely disconnected");
                    return;
                }
            }
            else
            {
                var distanceFromPlayer = System.Numerics.Vector2.Distance(playerWorldPos, intersectionPoint.Value);
                LogMessage($"[DEBUG INTERSECTION] ✅ FOUND intersection at: ({intersectionPoint.Value.X:F1}, {intersectionPoint.Value.Y:F1})");
                LogMessage($"[DEBUG INTERSECTION] Distance from player: {distanceFromPlayer:F1} (expected: {pursuitRadius:F1})");
            }
            
            // 4. Convert world coordinates to screen coordinates
            // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
            var worldPos = new Vector3(intersectionPoint.Value.X * GRID_TO_WORLD_MULTIPLIER, intersectionPoint.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0);
            var screenPosSharp = GameController.IngameState.Camera.WorldToScreen(worldPos);
            var screenPos = new Vector2(screenPosSharp.X, screenPosSharp.Y);
            
            LogMessage($"[DEBUG INTERSECTION] Grid to world: ({intersectionPoint.Value.X:F1}, {intersectionPoint.Value.Y:F1}) → ({worldPos.X:F1}, {worldPos.Y:F1})");
            LogMessage($"[DEBUG INTERSECTION] World to screen: ({worldPos.X:F1}, {worldPos.Y:F1}) → ({screenPos.X:F1}, {screenPos.Y:F1})");
            
            // 5. Validate screen coordinates are reasonable
            var gameWindow = GameController.Window.GetWindowRectangle();
            var isOnScreen = screenPos.X >= 0 && screenPos.X <= gameWindow.Width && 
                           screenPos.Y >= 0 && screenPos.Y <= gameWindow.Height;
            
            LogMessage($"[DEBUG INTERSECTION] Game window: {gameWindow.Width}x{gameWindow.Height}");
            LogMessage($"[DEBUG INTERSECTION] Screen position valid: {isOnScreen}");
            
            if (!isOnScreen)
            {
                LogMessage("[DEBUG INTERSECTION] ⚠️ WARNING: Intersection point is off-screen!");
                LogMessage("[DEBUG INTERSECTION] This could indicate a coordinate system issue");
            }
            
            // 6. Move mouse cursor to the intersection point (with window offset)
            var windowRect = GameController.Window.GetWindowRectangle();
            int absoluteX = (int)(screenPos.X + windowRect.X);
            int absoluteY = (int)(screenPos.Y + windowRect.Y);
            
            LogMessage($"[DEBUG INTERSECTION] Moving cursor to absolute screen: ({absoluteX}, {absoluteY})");
            LogMessage($"[DEBUG INTERSECTION] (Game coords: ({screenPos.X:F1}, {screenPos.Y:F1}) + Window offset: ({windowRect.X}, {windowRect.Y}))");
            
            bool moveResult = SetCursorPos(absoluteX, absoluteY);
            LogMessage($"[DEBUG INTERSECTION] SetCursorPos result: {moveResult}");
            
            // 7. Verify cursor actually moved to the right place
            Thread.Sleep(100); // Give time for cursor to move
            if (GetCursorPos(out POINT actualCursor))
            {
                LogMessage($"[DEBUG INTERSECTION] Actual cursor position: ({actualCursor.X}, {actualCursor.Y})");
                LogMessage($"[DEBUG INTERSECTION] Expected cursor position: ({absoluteX}, {absoluteY})");
                
                int deltaX = Math.Abs(actualCursor.X - absoluteX);
                int deltaY = Math.Abs(actualCursor.Y - absoluteY);
                
                if (deltaX <= 2 && deltaY <= 2)
                {
                    LogMessage("[DEBUG INTERSECTION] ✅ SUCCESS: Cursor moved to correct position!");
                }
                else
                {
                    LogMessage($"[DEBUG INTERSECTION] ⚠️ WARNING: Cursor position mismatch - Delta: ({deltaX}, {deltaY})");
                }
            }
            
            // 8. Also store this for visual display (same as regular movement)
            _lastTargetWorldPos = intersectionPoint.Value;
            
            LogMessage("=== DEBUG INTERSECTION POINT COMPLETE ===");
            LogMessage("The mouse cursor should now be positioned at the intersection of:");
            LogMessage($"- The GREEN pursuit circle (radius: {pursuitRadius:F1}) around your character");
            LogMessage("- The current RADAR path waypoints");
            LogMessage("This is where the bot would normally click to move!");
            
        }
        catch (Exception ex)
        {
            LogError($"[DEBUG INTERSECTION] CRITICAL ERROR: {ex.Message}");
            LogError($"[DEBUG INTERSECTION] Stack trace: {ex.StackTrace}");
        }
    }
    
    private void TestKeyboardOnly()
    {
        try
        {
            bool useKeyboardMovement = Settings.MovementSettings.UseMovementKey.Value;
            Keys movementKey = Settings.MovementSettings.MovementKey.Value;
            
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
            PressAndHoldKey(movementKey);
            
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
    [DllImport("user32.dll", SetLastError = true)]
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
    
    private void PressAndHoldKey(Keys key)
    {
        try
        {
            byte vkCode = (byte)key;
            LogMessage($"[KEYBOARD] Pressing key {key} (VK Code: {vkCode}) with keybd_event");
            
            // Key down
            keybd_event(vkCode, 0, 0, 0);
            Thread.Sleep(50); // Hold key briefly
            // Key up
            keybd_event(vkCode, 0, KEYEVENTF_KEYUP_SENDINPUT, 0);
            
            LogMessage($"[KEYBOARD] Key press sequence completed for {key}");
        }
        catch (Exception ex)
        {
            LogError($"Error pressing key {key}: {ex.Message}");
        }
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
                    
                    bool useKeyboardMovement = Settings.MovementSettings.UseMovementKey.Value;
                    Keys movementKey = Settings.MovementSettings.MovementKey.Value;
                    
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
                        PressAndHoldKey(movementKey);
                        
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
                    
                    bool useKeyboardMovement = Settings.MovementSettings.UseMovementKey.Value;
                    Keys movementKey = Settings.MovementSettings.MovementKey.Value;
                    
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
                    
                    // Wait based on configured area transition delay  
                    Thread.Sleep(Settings.TimingSettings.AreaTransitionDelay.Value);
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
        
        if (Settings.DebugSettings.LogPathAnalysis.Value)
        {
            LogMessage($"[DIRECTION ANALYSIS] {pathName}: spawn=({_initialSpawnPosition.X:F0},{_initialSpawnPosition.Y:F0}), start=({pathStart.X:F0},{pathStart.Y:F0}), end=({pathEnd.X:F0},{pathEnd.Y:F0})");
            LogMessage($"[DIRECTION ANALYSIS] {pathName}: distance to start={distanceToStart:F1}, distance to end={distanceToEnd:F1}, progress={directionalProgress:F1}, score={score:F2}");
        }
        
        return score;
    }
    
    /// <summary>
    /// Find where the path intersects with a circle of specific radius around the player
    /// </summary>
    private System.Numerics.Vector2? FindPathIntersectionWithSpecificRadius(List<Vector2i> path, int startIndex, float radius)
    {
        var playerPos = GetPlayerPosition();
        if (playerPos == null || path.Count == 0) return null;
        
        var playerWorldPos = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
        
        LogMovementDebug($"[SPECIFIC RADIUS] Looking for intersection with radius {radius:F1} from index {startIndex}");
        
        // Look for intersection with the specific radius only
        for (int i = Math.Max(startIndex, 1); i < path.Count; i++)
        {
            var currentPoint = new System.Numerics.Vector2(path[i - 1].X, path[i - 1].Y);
            var nextPoint = new System.Numerics.Vector2(path[i].X, path[i].Y);
            
            // Skip very short segments to avoid numerical issues
            var segmentLength = System.Numerics.Vector2.Distance(currentPoint, nextPoint);
            if (segmentLength < Settings.ConfigurationSettings.MinSegmentLength.Value) continue;
            
            // Find intersection of line segment with circle around player
            var intersection = FindLineCircleIntersection(currentPoint, nextPoint, playerWorldPos, radius);
            
            if (intersection.HasValue)
            {
                var distanceToIntersection = System.Numerics.Vector2.Distance(playerWorldPos, intersection.Value);
                var tolerance = Settings.ConfigurationSettings.CircleIntersectionTolerance.Value;
                var minRadius = radius * tolerance; 
                var maxRadius = radius * (2.0f - tolerance);
                
                if (distanceToIntersection >= minRadius && distanceToIntersection <= maxRadius)
                {
                    LogMovementDebug($"[SPECIFIC RADIUS] Found intersection at ({intersection.Value.X:F0}, {intersection.Value.Y:F0}), distance: {distanceToIntersection:F1}");
                    return intersection.Value;
                }
            }
        }
        
        LogMovementDebug($"[SPECIFIC RADIUS] No intersection found with radius {radius:F1}");
        return null;
    }

    /// <summary>
    /// Find target point at pursuit circle perimeter in direction of path/destination
    /// This ensures consistent click distance and eliminates path segment size dependencies
    /// </summary>
    private System.Numerics.Vector2? FindPerimeterTarget(List<Vector2i> path, int startIndex = 0)
    {
        var playerPos = GetPlayerPosition();
        if (playerPos == null || path.Count == 0) return null;
        
        var playerWorldPos = new System.Numerics.Vector2(playerPos.GridPos.X, playerPos.GridPos.Y);
        var pursuitRadius = Settings.MovementSettings.PursuitRadius.Value;
        
        LogMovementDebug($"[PERIMETER] Player at ({playerWorldPos.X:F0}, {playerWorldPos.Y:F0}), radius: {pursuitRadius:F1}");
        
        // 1. DETERMINE TARGET DIRECTION
        var targetDirection = DeterminePathDirection(path, startIndex, playerWorldPos);
        
        if (targetDirection.Length() < 0.1f)
        {
            LogMovementDebug($"[PERIMETER] ❌ No valid direction found");
            return null;
        }
        
        // 2. PROJECT TO PERIMETER
        var normalizedDirection = System.Numerics.Vector2.Normalize(targetDirection);
        var perimeterPoint = playerWorldPos + (normalizedDirection * pursuitRadius);
        
        LogMovementDebug($"[PERIMETER] Direction: ({targetDirection.X:F1}, {targetDirection.Y:F1}) → Normalized: ({normalizedDirection.X:F3}, {normalizedDirection.Y:F3})");
        LogMovementDebug($"[PERIMETER] ✅ Target at ({perimeterPoint.X:F0}, {perimeterPoint.Y:F0}), distance: {pursuitRadius:F1} (exact)");
        
        // 3. VALIDATE AND ADJUST TARGET POINT
        if (IsTargetPointValid(perimeterPoint))
        {
            return perimeterPoint;
        }
        
        LogMovementDebug($"[PERIMETER] ⚠️ Target off-screen, finding on-screen perimeter point");
        
        // Find alternative direction that keeps us on screen at perimeter distance
        var onScreenPerimeterPoint = FindOnScreenPerimeterPoint(playerWorldPos, pursuitRadius);
        if (onScreenPerimeterPoint.HasValue)
        {
            LogMovementDebug($"[PERIMETER] ✅ On-screen perimeter target: ({onScreenPerimeterPoint.Value.X:F0}, {onScreenPerimeterPoint.Value.Y:F0}), distance: {pursuitRadius:F1}");
            return onScreenPerimeterPoint.Value;
        }
        
        LogMovementDebug($"[PERIMETER] ❌ No on-screen perimeter point found");
        return null;
    }
    
    /// <summary>
    /// Determine the direction from player toward path/destination - PRIORITIZE PATH FOLLOWING
    /// </summary>
    private System.Numerics.Vector2 DeterminePathDirection(List<Vector2i> path, int startIndex, System.Numerics.Vector2 playerPos)
    {
        var destination = new System.Numerics.Vector2(path[path.Count - 1].X, path[path.Count - 1].Y);
        var destinationDistance = System.Numerics.Vector2.Distance(playerPos, destination);
        
        LogMovementDebug($"[DIRECTION] Destination: ({destination.X:F0}, {destination.Y:F0}), distance: {destinationDistance:F1}");
        
        // STRATEGY 1: Follow the Radar path waypoints (PRIMARY APPROACH)
        var pathDirection = GetPathDirection(path, startIndex, playerPos);
        if (pathDirection.HasValue && pathDirection.Value.Length() > 5f)
        {
            LogMovementDebug($"[DIRECTION] ✅ Following Radar path waypoints: ({pathDirection.Value.X:F1}, {pathDirection.Value.Y:F1})");
            return pathDirection.Value;
        }
        
        // STRATEGY 2: Only use destination direction when VERY close (within pursuit radius)
        if (destinationDistance < Settings.MovementSettings.PursuitRadius.Value * 0.5f)
        {
            LogMovementDebug($"[DIRECTION] ✅ Very close to destination, using direct approach");
            return destination - playerPos;
        }
        
        // STRATEGY 3: If no good path direction, look ahead further in the path
        var lookaheadDirection = GetLookaheadDirection(path, startIndex, playerPos);
        if (lookaheadDirection.HasValue)
        {
            LogMovementDebug($"[DIRECTION] ✅ Using lookahead direction: ({lookaheadDirection.Value.X:F1}, {lookaheadDirection.Value.Y:F1})");
            return lookaheadDirection.Value;
        }
        
        // FALLBACK: Use destination direction (last resort)
        LogMovementDebug($"[DIRECTION] ⚠️ Fallback to destination direction");
        return destination - playerPos;
    }
    
    /// <summary>
    /// Get direction based on nearby path waypoints (follow the actual Radar path)
    /// </summary>
    private System.Numerics.Vector2? GetPathDirection(List<Vector2i> path, int startIndex, System.Numerics.Vector2 playerPos)
    {
        // Look at next 5-10 waypoints to determine path direction
        var endIndex = Math.Min(startIndex + 10, path.Count);
        var validPoints = new List<System.Numerics.Vector2>();
        
        for (int i = Math.Max(startIndex, 0); i < endIndex; i++)
        {
            var waypoint = new System.Numerics.Vector2(path[i].X, path[i].Y);
            var distance = System.Numerics.Vector2.Distance(playerPos, waypoint);
            
            // Include waypoints that are reasonably ahead of us
            if (distance >= 20f && distance <= Settings.MovementSettings.PursuitRadius.Value * 2f)
            {
                validPoints.Add(waypoint);
            }
        }
        
        if (validPoints.Count == 0)
        {
            LogMovementDebug($"[PATH DIRECTION] No valid waypoints found in range");
            return null;
        }
        
        // Use the furthest valid waypoint as our target direction
        var targetWaypoint = validPoints.Last();
        var direction = targetWaypoint - playerPos;
        
        LogMovementDebug($"[PATH DIRECTION] Target waypoint: ({targetWaypoint.X:F0}, {targetWaypoint.Y:F0}), {validPoints.Count} valid points");
        return direction;
    }
    
    /// <summary>
    /// Look further ahead in the path when near waypoints are too close
    /// </summary>
    private System.Numerics.Vector2? GetLookaheadDirection(List<Vector2i> path, int startIndex, System.Numerics.Vector2 playerPos)
    {
        // Look much further ahead - up to 20-30 waypoints
        var lookaheadDistance = Math.Min(30, path.Count - startIndex);
        
        for (int i = 5; i <= lookaheadDistance; i += 5)
        {
            var lookaheadIndex = Math.Min(startIndex + i, path.Count - 1);
            var waypoint = new System.Numerics.Vector2(path[lookaheadIndex].X, path[lookaheadIndex].Y);
            var distance = System.Numerics.Vector2.Distance(playerPos, waypoint);
            
            if (distance >= Settings.MovementSettings.PursuitRadius.Value * 0.3f)
            {
                LogMovementDebug($"[LOOKAHEAD] Found good waypoint at index {lookaheadIndex}: ({waypoint.X:F0}, {waypoint.Y:F0}), distance: {distance:F1}");
                return waypoint - playerPos;
            }
        }
        
        LogMovementDebug($"[LOOKAHEAD] No suitable lookahead waypoint found");
        return null;
    }
    

    
    /// <summary>
    /// Validate that target point is reasonable (on screen, not too close, etc.)
    /// </summary>
    private bool IsTargetPointValid(System.Numerics.Vector2 targetPoint)
    {
        // Check if target is within reasonable screen bounds
        // Use SAME coordinate system as visual circle (direct coordinates, no scaling)
        var worldPos = new Vector3(targetPoint.X, targetPoint.Y, 0);
        var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
        var gameWindow = GameController.Window.GetWindowRectangle();
        
        var margin = 100f;
        var isOnScreen = screenPos.X >= margin && screenPos.X <= gameWindow.Width - margin && 
                        screenPos.Y >= margin && screenPos.Y <= gameWindow.Height - margin;
        
        LogMovementDebug($"[VALIDATION] World: ({targetPoint.X:F0}, {targetPoint.Y:F0}) → Screen: ({screenPos.X:F0}, {screenPos.Y:F0}), on screen: {isOnScreen}");
        
        return isOnScreen;
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
        var directionLength = direction.Length();
        
        // Only debug if we might find an intersection (radius is reasonable compared to segment)
        bool debugThis = radius <= directionLength * 50f; // Only debug when radius isn't massively larger than segment
        
        // Avoid division by zero for very short segments
        if (directionLength < 0.001f) 
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ❌ Segment too short: {directionLength:F3}");
            return null;
        }
        
        // Normalize direction vector
        direction = direction / directionLength;
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Normalized direction: ({direction.X:F3},{direction.Y:F3})");
        
        var toCircleCenter = circleCenter - lineStart;
        var projectionLength = System.Numerics.Vector2.Dot(toCircleCenter, direction);
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ToCenter: ({toCircleCenter.X:F1},{toCircleCenter.Y:F1}), Projection: {projectionLength:F1}");
        
        // Find closest point on infinite line to circle center
        var closestPoint = lineStart + direction * projectionLength;
        var distanceToCenter = System.Numerics.Vector2.Distance(closestPoint, circleCenter);
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Closest point: ({closestPoint.X:F1},{closestPoint.Y:F1}), Distance to center: {distanceToCenter:F1}");
        
        // No intersection if line is too far from circle
        if (distanceToCenter > radius)
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ❌ Line too far from circle: {distanceToCenter:F1} > {radius:F1}");
            return null;
        }
        
        // Calculate intersection points
        var halfChordLength = (float)Math.Sqrt(radius * radius - distanceToCenter * distanceToCenter);
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Half chord length: {halfChordLength:F1}");
        
        // Two potential intersection points
        var intersection1 = closestPoint - direction * halfChordLength;
        var intersection2 = closestPoint + direction * halfChordLength;
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Intersection 1: ({intersection1.X:F1},{intersection1.Y:F1})");
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Intersection 2: ({intersection2.X:F1},{intersection2.Y:F1})");
        
        // Check which intersections are within the line segment
        // Note: direction is already normalized, so dot product gives projection length directly
        var t1 = System.Numerics.Vector2.Dot(intersection1 - lineStart, direction);
        var t2 = System.Numerics.Vector2.Dot(intersection2 - lineStart, direction);
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] t1: {t1:F1}, t2: {t2:F1}");
        
        // Since direction is normalized, we need to compare against the actual segment length
        var segmentLength = directionLength;
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] Segment length: {segmentLength:F1}, checking bounds [0, {segmentLength:F1}]");
        
        // Choose the intersection that's further along the path (prefer forward progress)
        System.Numerics.Vector2? bestIntersection = null;
        float bestT = -1f;
        
        if (t1 >= 0 && t1 <= segmentLength && t1 > bestT)
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ✅ t1 VALID: {t1:F1} in [0, {segmentLength:F1}]");
            bestIntersection = intersection1;
            bestT = t1;
        }
        else
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ❌ t1 INVALID: {t1:F1} not in [0, {segmentLength:F1}]");
        }
        
        if (t2 >= 0 && t2 <= segmentLength && t2 > bestT)
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ✅ t2 VALID: {t2:F1} in [0, {segmentLength:F1}], better than {bestT:F1}");
            bestIntersection = intersection2;
            bestT = t2;
        }
        else
        {
            if (debugThis) LogMovementDebug($"[INTERSECTION MATH] ❌ t2 INVALID: {t2:F1} not in [0, {segmentLength:F1}] or not better than {bestT:F1}");
        }
        
        if (debugThis) LogMovementDebug($"[INTERSECTION MATH] 📍 RESULT: {(bestIntersection.HasValue ? $"({bestIntersection.Value.X:F1},{bestIntersection.Value.Y:F1})" : "None")}");
        return bestIntersection;
    }
    
    // Add after the existing DrawPathDebug method (around line 2690)
    
    // Remove the separate DrawPlayerCircle method - now using Aim-Bot's direct approach
    
    // Implementation of DrawEllipseToWorld method (copied exactly from Aim-Bot)
    private void DrawEllipseToWorld(Vector3 vector3Pos, int radius, int points, int lineWidth, Color color)
    {
        var plottedCirclePoints = new List<Vector3>();
        for (var i = 0; i <= 360; i += 360 / points)
        {
            var angle = i * (Math.PI / 180f);
            var x = (float)(vector3Pos.X + radius * Math.Cos(angle));
            var y = (float)(vector3Pos.Y + radius * Math.Sin(angle));
            plottedCirclePoints.Add(new Vector3(x, y, vector3Pos.Z));
        }

        for (var i = 0; i < plottedCirclePoints.Count; i++)
        {
            if (i >= plottedCirclePoints.Count - 1)
            {
                continue;
            }

            var camera = GameController.Game.IngameState.Camera;
            Vector2 point1 = camera.WorldToScreen(plottedCirclePoints[i]);
            Vector2 point2 = camera.WorldToScreen(plottedCirclePoints[i + 1]);
            Graphics.DrawLine(point1, point2, lineWidth, color);
        }
    }

    // Add target point visualization
    private System.Numerics.Vector2? _lastTargetWorldPos = null;
    

    
    // Circle drawing debug tracking
    private DateTime _lastCircleErrorLog = DateTime.MinValue;
    private bool _firstCircleDrawLogged = false;
    
    private void DrawTargetPoint()
    {
        try
        {
            if (_lastTargetWorldPos == null || _currentPath.Count == 0) return;
            
            // Convert world position to screen position using ExileCore's Graphics
            var camera = GameController.Game.IngameState.Camera;
            // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
            var targetScreenPos = camera.WorldToScreen(new Vector3(_lastTargetWorldPos.Value.X * GRID_TO_WORLD_MULTIPLIER, _lastTargetWorldPos.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0));
            
            // Draw a red circle at the target point using Graphics.DrawLine (small circle)
            // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates for drawing
            DrawEllipseToWorld(new Vector3(_lastTargetWorldPos.Value.X * GRID_TO_WORLD_MULTIPLIER, _lastTargetWorldPos.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0), 8, 16, 3, Color.Red);
            
            // Draw crosshair using Graphics.DrawLine
            var crosshairSize = 12;
            Graphics.DrawLine(
                new Vector2(targetScreenPos.X - crosshairSize, targetScreenPos.Y),
                new Vector2(targetScreenPos.X + crosshairSize, targetScreenPos.Y),
                3, Color.White
            );
            Graphics.DrawLine(
                new Vector2(targetScreenPos.X, targetScreenPos.Y - crosshairSize),
                new Vector2(targetScreenPos.X, targetScreenPos.Y + crosshairSize),
                3, Color.White
            );
        }
        catch (Exception ex)
        {
            // Don't spam errors for rendering issues
            if (Settings.DebugSettings.DebugMode.Value)
            {
                LogMessage($"[TARGET POINT ERROR] Error drawing target point: {ex.Message}");
            }
        }
    }

    private void LogMovementDebug(string message)
    {
        if (Settings.DebugSettings.DebugMode.Value)
            LogMessage(message);

        if (!Settings.DebugSettings.SaveMovementDebugToFile.Value) return;

        lock (_movementDebugLock)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            _movementDebugBuffer.Add($"[{timestamp}] {message}");

            if (DateTime.Now.Subtract(_lastFileWrite).TotalSeconds >= 1)
            {
                File.AppendAllText(_movementDebugFilePath, string.Join(Environment.NewLine, _movementDebugBuffer) + Environment.NewLine);
                _movementDebugBuffer.Clear();
                _lastFileWrite = DateTime.Now;
            }
        }
    }

    /// <summary>
    /// Find a point on the pursuit circle perimeter that's visible on screen
    /// </summary>
    private System.Numerics.Vector2? FindOnScreenPerimeterPoint(System.Numerics.Vector2 playerPos, float radius)
    {
        var gameWindow = GameController.Window.GetWindowRectangle();
        var margin = 100f;
        
        // Test 8 cardinal/intercardinal directions at MULTIPLE radii
        var directions = new[]
        {
            new System.Numerics.Vector2(1, 0),    // East
            new System.Numerics.Vector2(0.707f, 0.707f),  // Northeast  
            new System.Numerics.Vector2(0, 1),    // North
            new System.Numerics.Vector2(-0.707f, 0.707f), // Northwest
            new System.Numerics.Vector2(-1, 0),   // West
            new System.Numerics.Vector2(-0.707f, -0.707f), // Southwest
            new System.Numerics.Vector2(0, -1),   // South
            new System.Numerics.Vector2(0.707f, -0.707f)   // Southeast
        };
        
        // Try progressively smaller radii until we find something on-screen
        for (float radiusMultiplier = 1.0f; radiusMultiplier >= 0.3f; radiusMultiplier -= 0.1f)
        {
            var adjustedRadius = radius * radiusMultiplier;
            
            foreach (var direction in directions)
            {
                var testPoint = playerPos + (direction * adjustedRadius);
                
                // Test if this point is on screen
                // Use SAME coordinate system as visual circle (direct coordinates, no scaling)
                var worldPos = new Vector3(testPoint.X, testPoint.Y, 0);
                var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
                
                var isOnScreen = screenPos.X >= margin && screenPos.X <= gameWindow.Width - margin && 
                                screenPos.Y >= margin && screenPos.Y <= gameWindow.Height - margin;
                
                if (isOnScreen)
                {
                    LogMovementDebug($"[ON-SCREEN PERIMETER] Found direction ({direction.X:F3}, {direction.Y:F3}) → ({testPoint.X:F0}, {testPoint.Y:F0}) at screen ({screenPos.X:F0}, {screenPos.Y:F0}), radius: {adjustedRadius:F1} ({radiusMultiplier:F1}x)");
                    return testPoint;
                }
            }
        }
        
        // If even reduced radii don't work, try a comprehensive sweep with very small radius
        var minRadius = Math.Max(radius * 0.2f, 50f); // At least 50 units away
        
        for (int angle = 0; angle < 360; angle += 30)  // Every 30 degrees
        {
            var radians = angle * Math.PI / 180.0;
            var direction = new System.Numerics.Vector2((float)Math.Cos(radians), (float)Math.Sin(radians));
            var testPoint = playerPos + (direction * minRadius);
            
            // Use SAME coordinate system as visual circle (direct coordinates, no scaling)
            var worldPos = new Vector3(testPoint.X, testPoint.Y, 0);
            var screenPos = GameController.IngameState.Camera.WorldToScreen(worldPos);
            
            var isOnScreen = screenPos.X >= margin && screenPos.X <= gameWindow.Width - margin && 
                            screenPos.Y >= margin && screenPos.Y <= gameWindow.Height - margin;
            
            if (isOnScreen)
            {
                LogMovementDebug($"[ON-SCREEN PERIMETER] Found sweep direction at {angle}° → ({testPoint.X:F0}, {testPoint.Y:F0}) at screen ({screenPos.X:F0}, {screenPos.Y:F0}), radius: {minRadius:F1}");
                return testPoint;
            }
        }
        
        LogMovementDebug($"[ON-SCREEN PERIMETER] ❌ No direction found within screen bounds even with radius reduction");
        return null;
    }

    private bool IsPlayerStuck()
    {
        var currentPos = GetPlayerPosition()?.GridPos;
        if (currentPos == null) return false;

        var currentPosNum = new System.Numerics.Vector2(currentPos.Value.X, currentPos.Value.Y);
        _stuckPositionHistory.Add(currentPosNum);
        if (_stuckPositionHistory.Count > Settings.MovementSettings.StuckDetectionThreshold.Value)
            _stuckPositionHistory.RemoveAt(0);

        if (_stuckPositionHistory.Count < Settings.MovementSettings.StuckDetectionThreshold.Value)
            return false;

        var avgPos = new System.Numerics.Vector2(
            _stuckPositionHistory.Average(p => p.X),
            _stuckPositionHistory.Average(p => p.Y)
        );
        var maxDistance = _stuckPositionHistory.Max(p => System.Numerics.Vector2.Distance(p, avgPos));
        return maxDistance < Settings.MovementSettings.MovementPrecision.Value;
    }

    private void MoveAlongPath()
    {
        if (IsPlayerStuck())
        {
            HandleStuckSituation();
            return;
        }
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

        // PERIMETER-BASED NAVIGATION: Always click at pursuit circle perimeter
        var pursuitRadius = Settings.MovementSettings.PursuitRadius.Value;
        LogMovementDebug($"[PERIMETER START] Finding perimeter target with radius {pursuitRadius:F1} from path index {_currentPathIndex}");
        
        var targetPoint = FindPerimeterTarget(_currentPath, _currentPathIndex);
        
        if (!targetPoint.HasValue)
        {
            LogMovementDebug("[PERIMETER] ❌ CRITICAL: Perimeter targeting failed - trying fallback");
            
            // Simple fallback - advance path index and target destination directly
            _currentPathIndex = Math.Min(_currentPathIndex + 3, _currentPath.Count - 1);
            
            if (_currentPathIndex >= _currentPath.Count - 1)
            {
                LogMessage("[PERIMETER] 📍 Reached end of path");
                _currentState = BotState.AtAreaExit;
                return;
            }
            
            // MAINTAIN PERIMETER DISTANCE: Try to find ANY on-screen perimeter point
            var emergencyPerimeterPoint = FindOnScreenPerimeterPoint(playerWorldPos, pursuitRadius);
            if (emergencyPerimeterPoint.HasValue)
            {
                targetPoint = emergencyPerimeterPoint.Value;
                var perimeterDistance = System.Numerics.Vector2.Distance(playerWorldPos, targetPoint.Value);
                LogMovementDebug($"[PERIMETER] 🔧 Using emergency perimeter fallback: ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {perimeterDistance:F1}");
            }
            else
            {
                // Last resort - but still try to maintain reasonable distance
                var destination = _currentPath[_currentPath.Count - 1];
                var destinationDirection = destination - new Vector2i((int)playerWorldPos.X, (int)playerWorldPos.Y);
                var destinationDistance = new System.Numerics.Vector2(destinationDirection.X, destinationDirection.Y).Length();
                
                if (destinationDistance < pursuitRadius * 0.5f)
                {
                    // Destination too close - project it out to at least half pursuit radius
                    var normalizedDestDir = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(destinationDirection.X, destinationDirection.Y));
                    var adjustedTarget = playerWorldPos + (normalizedDestDir * pursuitRadius * 0.6f);
                    targetPoint = adjustedTarget;
                    LogMovementDebug($"[PERIMETER] 🔧 Using projected destination: ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {pursuitRadius * 0.6f:F1}");
                }
                else
                {
                    targetPoint = new System.Numerics.Vector2(destination.X, destination.Y);
                    LogMovementDebug($"[PERIMETER] 🔧 Using destination fallback: ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {destinationDistance:F1}");
                }
            }
        }
        else
        {
            var actualDistance = System.Numerics.Vector2.Distance(playerWorldPos, targetPoint.Value);
            LogMovementDebug($"[PERIMETER SUCCESS] ✅ Target at perimeter: ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {actualDistance:F1}, expected: {pursuitRadius:F1}");
            
            // Validation: Distance should match pursuit radius exactly (within small tolerance)
            if (Math.Abs(actualDistance - pursuitRadius) > 5f)
            {
                LogMovementDebug($"[PERIMETER WARNING] ⚠️ Distance mismatch: expected {pursuitRadius:F1}, got {actualDistance:F1}");
            }
        }

        // 🎯 VALIDATE PATH INTERSECTION IS CAMERA-VISIBLE
        var currentDistance = System.Numerics.Vector2.Distance(playerWorldPos, targetPoint.Value);
        LogMovementDebug($"[PATH INTERSECTION] Target at ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {currentDistance:F1}");
        
        // Check if the path intersection point is visible on screen
        // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
        var testWorldPos = new Vector3(targetPoint.Value.X * GRID_TO_WORLD_MULTIPLIER, targetPoint.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0);
        var testScreenPos = GameController.IngameState.Camera.WorldToScreen(testWorldPos);
        var gameWindow = GameController.Window.GetWindowRectangle();
        var margin = 100f;
        var isVisible = testScreenPos.X >= margin && testScreenPos.X <= gameWindow.Width - margin && 
                       testScreenPos.Y >= margin && testScreenPos.Y <= gameWindow.Height - margin;
        
        if (!isVisible)
        {
            LogMovementDebug($"[PATH INTERSECTION] ⚠️ Intersection off-screen at ({testScreenPos.X:F0}, {testScreenPos.Y:F0}) - finding camera-visible intersection");
            
                         // Try to find a path intersection with progressively smaller radii until we get one that's visible
             var baseRadius = Settings.MovementSettings.PursuitRadius.Value;
             System.Numerics.Vector2? visibleIntersection = null;
             
             LogMovementDebug($"[CAMERA AWARE] 🔍 Original intersection off-screen, trying smaller radii from {baseRadius:F1}");
             
             for (float radiusMultiplier = 0.8f; radiusMultiplier >= 0.3f; radiusMultiplier -= 0.1f)
             {
                 var testRadius = baseRadius * radiusMultiplier;
                 LogMovementDebug($"[CAMERA AWARE] Testing radius {testRadius:F1} (multiplier: {radiusMultiplier:F1})");
                 
                 var testIntersection = FindPathIntersectionWithSpecificRadius(_currentPath, _currentPathIndex, testRadius);
                 
                 if (testIntersection.HasValue)
                 {
                     // Test if this intersection is visible
                     // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
                     var testWorld = new Vector3(testIntersection.Value.X * GRID_TO_WORLD_MULTIPLIER, testIntersection.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0);
                     var testScreen = GameController.IngameState.Camera.WorldToScreen(testWorld);
                     var testIsVisible = testScreen.X >= margin && testScreen.X <= gameWindow.Width - margin && 
                                        testScreen.Y >= margin && testScreen.Y <= gameWindow.Height - margin;
                     
                     LogMovementDebug($"[CAMERA AWARE] Intersection at ({testIntersection.Value.X:F0}, {testIntersection.Value.Y:F0}), screen: ({testScreen.X:F0}, {testScreen.Y:F0}), visible: {testIsVisible}");
                     
                     if (testIsVisible)
                     {
                         visibleIntersection = testIntersection;
                         LogMovementDebug($"[CAMERA AWARE] ✅ SELECTED visible intersection with radius {testRadius:F1}: ({testIntersection.Value.X:F0}, {testIntersection.Value.Y:F0})");
                         break;
                     }
                     else
                     {
                         LogMovementDebug($"[CAMERA AWARE] ❌ Intersection still not visible, trying smaller radius");
                     }
                 }
                 else
                 {
                     LogMovementDebug($"[CAMERA AWARE] ❌ No intersection found with radius {testRadius:F1}");
                 }
             }
            
            if (visibleIntersection.HasValue)
            {
                targetPoint = visibleIntersection.Value;
            }
            else
            {
                LogMovementDebug($"[PATH INTERSECTION] ⚠️ No camera-visible intersection found, keeping original target");
            }
        }
        else
        {
            LogMovementDebug($"[PATH INTERSECTION] ✅ Intersection is camera-visible");
        }
        
        // Convert world position to screen coordinates for clicking
        // 🎯 COORDINATE FIX: Convert grid coordinates to world coordinates before WorldToScreen
        var worldPos = new Vector3(targetPoint.Value.X * GRID_TO_WORLD_MULTIPLIER, targetPoint.Value.Y * GRID_TO_WORLD_MULTIPLIER, 0);
        var screenPosSharp = GameController.IngameState.Camera.WorldToScreen(worldPos);
        var screenPos = new Vector2(screenPosSharp.X, screenPosSharp.Y);
        
        // 🖥️ CRITICAL: VALIDATE SCREEN COORDINATES BEFORE CLICKING
        var finalGameWindow = GameController.Window.GetWindowRectangle();
        var isWithinGameWindow = screenPos.X >= 0 && screenPos.X <= finalGameWindow.Width && 
                                screenPos.Y >= 0 && screenPos.Y <= finalGameWindow.Height;
        
        LogMovementDebug($"[SCREEN VALIDATION] Game window: {finalGameWindow.Width}x{finalGameWindow.Height}");
        LogMovementDebug($"[SCREEN VALIDATION] Target screen: ({screenPos.X:F0}, {screenPos.Y:F0})");
        LogMovementDebug($"[SCREEN VALIDATION] Within window: {isWithinGameWindow}");
        
        if (!isWithinGameWindow)
        {
            LogMovementDebug($"[SCREEN VALIDATION] ⚠️ Target outside game window! Clamping to safe bounds.");
            
            // Clamp to safe area within game window (with margin for safety)
            var clampMargin = 50f;
            var safeX = Math.Max(clampMargin, Math.Min(finalGameWindow.Width - clampMargin, screenPos.X));
            var safeY = Math.Max(clampMargin, Math.Min(finalGameWindow.Height - clampMargin, screenPos.Y));
            
            LogMovementDebug($"[SCREEN VALIDATION] ✅ Clamped: ({screenPos.X:F0}, {screenPos.Y:F0}) → ({safeX:F0}, {safeY:F0})");
            screenPos = new Vector2(safeX, safeY);
        }
        else
        {
            LogMovementDebug($"[SCREEN VALIDATION] ✅ Target within game window bounds");
        }
        
        var distanceToTarget = System.Numerics.Vector2.Distance(playerWorldPos, targetPoint.Value);
        
        // 📏 FINAL VALIDATION: Log exactly where we're clicking relative to player and circle
        var playerScreenPos = GetPlayerScreenPosition();
        if (playerScreenPos.HasValue)
        {
            var screenDistance = Vector2.Distance(screenPos, playerScreenPos.Value);
            LogMovementDebug($"[CLICK VALIDATION] Player screen: ({playerScreenPos.Value.X:F0}, {playerScreenPos.Value.Y:F0})");
            LogMovementDebug($"[CLICK VALIDATION] Final target screen: ({screenPos.X:F0}, {screenPos.Y:F0})");
            LogMovementDebug($"[CLICK VALIDATION] Screen distance: {screenDistance:F1} pixels");
            LogMovementDebug($"[CLICK VALIDATION] World distance: {distanceToTarget:F1} units (expected: {Settings.MovementSettings.PursuitRadius.Value:F1} - perimeter target)");
            
            // Additional safety check - if screen distance is way too large, something is wrong
            if (screenDistance > 1200) // Increased from 800 to 1200 pixels - less restrictive
            {
                LogMovementDebug($"[CLICK VALIDATION] ❌ BLOCKED: Screen distance too large ({screenDistance:F1} > 1200 pixels) - coordinate issue!");
                return; // Skip this movement to prevent off-screen clicking
            }
            LogMovementDebug($"[CLICK VALIDATION] ✅ Screen distance check passed ({screenDistance:F1} <= 1200 pixels)");
        }
        
        // Update path progress tracking
        UpdatePathProgress(targetPoint.Value, distanceToTarget);
        
        // STUCK DETECTION: Force advancement if targeting same point repeatedly
        var targetDistance = System.Numerics.Vector2.Distance(_lastTargetPoint, targetPoint.Value);
        var remainingPathWaypoints = _currentPath.Count - _currentPathIndex - 1;
        
        if (targetDistance < 5f) // Same target (within 5 units)
        {
            _stuckTargetCount++;
            
            // DYNAMIC STUCK THRESHOLD: Be more sensitive near end of path
            var baseStuckThreshold = Settings.MovementSettings.StuckDetectionThreshold.Value;
            var stuckThreshold = remainingPathWaypoints <= 5 ? Math.Max(baseStuckThreshold - 2, 3) : baseStuckThreshold;
            
            if (_stuckTargetCount >= stuckThreshold)
            {
                LogMovementDebug($"[STUCK DETECTION] 🚨 Stuck on same target for {_stuckTargetCount} attempts (threshold: {stuckThreshold}) - forcing advancement!");
                LogMovementDebug($"[STUCK DEBUG] 📍 Player position hasn't changed from ({playerWorldPos.X:F0}, {playerWorldPos.Y:F0}) - character may be physically blocked!");
                
                // SMART ADVANCEMENT: Less aggressive near end of path  
                var baseAdvancement = (int)(Settings.MovementSettings.PathAdvancementDistance.Value / 25f); // Convert pixels to waypoint steps
                var forceAdvancement = remainingPathWaypoints <= 5 ? Math.Max(baseAdvancement / 2, 3) : baseAdvancement;
                _currentPathIndex = Math.Min(_currentPathIndex + forceAdvancement, _currentPath.Count - 1);
                _stuckTargetCount = 0;
                _lastPathAdvancement = DateTime.Now;
                
                LogMovementDebug($"[FORCED ADVANCEMENT] 📍 Forced advance by {forceAdvancement} to path index {_currentPathIndex}/{_currentPath.Count}");
                
                // If we're at the very end after forced advancement, check for completion
                if (_currentPathIndex >= _currentPath.Count - 1)
                {
                    var distanceToFinalDestination = System.Numerics.Vector2.Distance(playerWorldPos, new System.Numerics.Vector2(_currentPath[_currentPath.Count - 1].X, _currentPath[_currentPath.Count - 1].Y));
                    if (distanceToFinalDestination < 30f)
                    {
                        LogMessage($"[STUCK RESOLUTION] 🎉 Forced to path end and close enough ({distanceToFinalDestination:F1} < 30) - completing path!");
                        _currentState = BotState.AtAreaExit;
                        return;
                    }
                }
            }
        }
        else
        {
            _stuckTargetCount = 0; // Reset if we have a new target
        }
        _lastTargetPoint = targetPoint.Value;
        
        LogMessage($"[PURSUIT] 🎯 Moving to intersection point ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0}), distance: {distanceToTarget:F1}");

        // IMPROVED ADVANCEMENT: Advance based on progress along path direction, not just proximity
        bool shouldAdvance = false;
        string advanceReason = "";
        
        // Method 1: Close to target (original logic)
        if (distanceToTarget < 30f)
        {
            shouldAdvance = true;
            advanceReason = $"close to target (distance: {distanceToTarget:F1} < 30)";
        }
        // Method 2: Player has moved past the current path segment (NEW!)
        else if (_currentPathIndex + 1 < _currentPath.Count)
        {
            var currentWaypoint = new System.Numerics.Vector2(_currentPath[_currentPathIndex].X, _currentPath[_currentPathIndex].Y);
            var nextWaypoint = new System.Numerics.Vector2(_currentPath[_currentPathIndex + 1].X, _currentPath[_currentPathIndex + 1].Y);
            
            // Calculate if player has moved "past" the current path segment
            var pathDirection = nextWaypoint - currentWaypoint;
            var playerDirection = playerWorldPos - currentWaypoint;
            
            // Use dot product to see if player is ahead of the current waypoint along the path
            var pathSegmentLength = pathDirection.Length();
            var normalizedPathDirection = pathSegmentLength > 0 ? pathDirection / pathSegmentLength : System.Numerics.Vector2.Zero;
            var dot = System.Numerics.Vector2.Dot(normalizedPathDirection, playerDirection);
            
            if (dot > pathSegmentLength * 0.5f) // Player is more than halfway past this waypoint
            {
                shouldAdvance = true;
                advanceReason = $"moved past waypoint (progress: {dot:F1}/{pathSegmentLength:F1})";
            }
        }
        
        if (shouldAdvance)
        {
            LogMovementDebug($"[PURSUIT] ✅ Advancing path - {advanceReason}");
            
            // IMPROVED END-OF-PATH HANDLING: Be more conservative near the end
            var remainingWaypoints = _currentPath.Count - _currentPathIndex - 1;
            int advancementAmount;
            
            if (remainingWaypoints <= 3)
            {
                // Very close to end - minimal advancement to avoid overshooting
                advancementAmount = 1;
                LogMessage($"[PATH ADVANCEMENT] 🎯 Near path end ({remainingWaypoints} remaining) - advancing cautiously by {advancementAmount}");
            }
            else if (remainingWaypoints <= 10)
            {
                // Approaching end - moderate advancement
                advancementAmount = distanceToTarget < 15f ? 3 : 2;
                LogMessage($"[PATH ADVANCEMENT] 🎯 Approaching path end ({remainingWaypoints} remaining) - advancing by {advancementAmount}");
            }
            else
            {
                // Normal advancement for middle of path
                advancementAmount = distanceToTarget < 15f ? 5 : 3;
                LogMessage($"[PATH ADVANCEMENT] 📍 Normal advancement ({remainingWaypoints} remaining) - advancing by {advancementAmount}");
            }
            
            var newIndex = Math.Min(_currentPathIndex + advancementAmount, _currentPath.Count - 1);
            
            // Check if we're actually at the final destination
            if (newIndex >= _currentPath.Count - 1 && distanceToTarget < 25f)
            {
                LogMessage($"[PATH COMPLETION] 🎉 Reached final destination! Distance: {distanceToTarget:F1} < 25");
                _currentState = BotState.AtAreaExit;
                return;
            }
            
            _currentPathIndex = newIndex;
            LogMovementDebug($"[PATH ADVANCEMENT] 📍 Advanced path index to {_currentPathIndex}/{_currentPath.Count} (advanced by {advancementAmount}) - {advanceReason}");
            _lastIntersectionPoint = targetPoint.Value;
            
            // Still try to move to get even closer if not extremely close
            if (distanceToTarget > 8f) // Reduced threshold for final movements
            {
                // Execute movement to get closer
                LogMessage($"[MOVEMENT] 🎮 Fine-tuning: cursor to ({screenPos.X:F0}, {screenPos.Y:F0}) + press T");
                ClickAt((int)screenPos.X, (int)screenPos.Y);
                PressAndHoldKey(Keys.T);
                _lastMovementTime = DateTime.Now;
            }
            return;
        }

        // 🔍 DEBUG: Check all movement blocking conditions
        LogMovementDebug($"[MOVEMENT DEBUG] 🔍 Checking movement conditions:");
        LogMovementDebug($"[MOVEMENT DEBUG] - Distance to target: {distanceToTarget:F1}");
        LogMovementDebug($"[MOVEMENT DEBUG] - Should advance: {shouldAdvance}");
        
        // Check if we should skip movement due to being too close for micro-adjustments
        if (distanceToTarget < 10f)
        {
            LogMovementDebug($"[PURSUIT] ⏸️ BLOCKED: Very close to target ({distanceToTarget:F1} < 10), skipping movement");
            return;
        }
        LogMovementDebug($"[MOVEMENT DEBUG] ✅ Distance check passed ({distanceToTarget:F1} >= 10)");

        // Calculate movement delay based on distance
        var movementDelay = CalculateImprovedMovementDelay(distanceToTarget);
        
        // Check movement delay timing
        var timeSinceLastMovement = (DateTime.Now - _lastMovementTime).TotalMilliseconds;
        LogMovementDebug($"[MOVEMENT DEBUG] - Movement delay: {movementDelay}ms");
        LogMovementDebug($"[MOVEMENT DEBUG] - Time since last movement: {timeSinceLastMovement:F0}ms");
        
        if (timeSinceLastMovement < movementDelay)
        {
            var remainingDelay = movementDelay - timeSinceLastMovement;
            LogMovementDebug($"[MOVEMENT] ⏳ BLOCKED: Waiting {remainingDelay:F0}ms before next movement (last: {timeSinceLastMovement:F0}ms ago)");
            return;
        }
        LogMovementDebug($"[MOVEMENT DEBUG] ✅ Timing check passed ({timeSinceLastMovement:F0}ms >= {movementDelay}ms)");

        // Execute the movement
        _lastMovementTime = DateTime.Now;
        
        // 🖥️ FINAL SAFETY CHECK: Verify coordinates before clicking
        LogMovementDebug($"[MOVEMENT] 🎮 EXECUTING MOVEMENT: cursor to ({screenPos.X:F0}, {screenPos.Y:F0}) + press T (distance: {distanceToTarget:F1})");
        
        if (Settings.DebugSettings.ShowMovementDebug.Value)
        {
            LogMovementDebug($"[MOVEMENT DEBUG] 📍 Player at ({playerWorldPos.X:F0}, {playerWorldPos.Y:F0}) → Target ({targetPoint.Value.X:F0}, {targetPoint.Value.Y:F0})");
        }
        
        // Additional sanity check on final screen coordinates
        if (screenPos.X < -1000 || screenPos.X > 5000 || screenPos.Y < -1000 || screenPos.Y > 5000)
        {
            LogMovementDebug($"[MOVEMENT] ❌ BLOCKED: Insane screen coordinates ({screenPos.X:F0}, {screenPos.Y:F0}) - ABORTING CLICK!");
            return; // Don't click with crazy coordinates
        }
        LogMovementDebug($"[MOVEMENT DEBUG] ✅ Final coordinate check passed ({screenPos.X:F0}, {screenPos.Y:F0})");
        
        // Store target position for visual display
        _lastTargetWorldPos = targetPoint.Value;
        
        // DUPLICATE CLICK DETECTION
        var currentScreenPos = new System.Numerics.Vector2(screenPos.X, screenPos.Y);
        var clickTolerance = 10f; // 10 pixel tolerance for "same" click
        var isSameClick = System.Numerics.Vector2.Distance(currentScreenPos, _lastClickScreenPos) <= clickTolerance;
        
        if (isSameClick)
        {
            _duplicateClickCount++;
            LogMovementDebug($"[DUPLICATE DETECTION] 🚨 Same click location detected! Count: {_duplicateClickCount}/{MAX_DUPLICATE_CLICKS}");
            
            if (_duplicateClickCount >= MAX_DUPLICATE_CLICKS)
            {
                LogMovementDebug($"[DUPLICATE DETECTION] ⚠️ MAX DUPLICATES REACHED - FORCING PATH ADVANCEMENT!");
                
                // Force advance the path significantly to break the loop
                var oldIndex = _currentPathIndex;
                _currentPathIndex = Math.Min(_currentPathIndex + 10, _currentPath.Count - 1);
                
                LogMovementDebug($"[FORCED ADVANCEMENT] 📍 Advanced path from {oldIndex} to {_currentPathIndex} due to duplicate clicks");
                
                // Reset duplicate detection
                _duplicateClickCount = 0;
                _lastClickScreenPos = System.Numerics.Vector2.Zero;
                
                // Don't execute the click, just return to recalculate with new path index
                LogMovementDebug($"[DUPLICATE DETECTION] ❌ SKIPPING CLICK - Recalculating with advanced path");
                return;
            }
        }
        else
        {
            // Different click location, reset counter
            _duplicateClickCount = 0;
            LogMovementDebug($"[DUPLICATE DETECTION] ✅ New click location - resetting duplicate counter");
        }
        
        // Update last click position
        _lastClickScreenPos = currentScreenPos;
        
        LogMovementDebug($"[MOVEMENT] ✅ SAFE CLICK: Executing click at ({screenPos.X:F0}, {screenPos.Y:F0})");
        ClickAt((int)screenPos.X, (int)screenPos.Y);
        PressAndHoldKey(Keys.T);

        // Update stuck detection
        UpdateStuckDetection(playerWorldPos);
    }
}