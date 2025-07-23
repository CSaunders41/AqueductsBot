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
    
    private const uint MOUSEEVENTF_LEFTDOWN = 0x02;
    private const uint MOUSEEVENTF_LEFTUP = 0x04;
    private const uint KEYEVENTF_KEYUP = 0x02;
    
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
    private DateTime _lastRadarRetry = DateTime.MinValue;
    private DateTime _lastPathRequest = DateTime.MinValue;
    
    // Radar integration
    private Action<Vector2, Action<List<Vector2i>>, CancellationToken> _radarLookForRoute;
    private bool _radarAvailable = false;
    private CancellationTokenSource _pathfindingCts = new();
    
    public override bool Initialise()
    {
        try
        {
            LogMessage("AqueductsBot initializing...");
            
            // Try to connect to Radar
            TryConnectToRadar();
            
            // Register hotkeys
            Input.RegisterKey(Settings.StartStopHotkey);
            Input.RegisterKey(Settings.EmergencyStopHotkey);
            Input.RegisterKey(Settings.MovementKey);
            Input.RegisterKey(Settings.MovementSettings.MovementKey); // Keep nested one too
            
            Settings.StartStopHotkey.OnValueChanged += () => Input.RegisterKey(Settings.StartStopHotkey);
            Settings.EmergencyStopHotkey.OnValueChanged += () => Input.RegisterKey(Settings.EmergencyStopHotkey);
            Settings.MovementKey.OnValueChanged += () => Input.RegisterKey(Settings.MovementKey);
            Settings.MovementSettings.MovementKey.OnValueChanged += () => Input.RegisterKey(Settings.MovementSettings.MovementKey);
            
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
        // Declare variables early for UI usage
        bool keyboardEnabled = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
        Keys currentMovementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
        
        // Show current bot status
        ImGui.Text($"Bot State: {_currentState}");
        ImGui.Text($"Radar Available: {_radarAvailable}");
        ImGui.Text($"Runs Completed: {_runsCompleted}");
        
        if (_botStartTime != default)
        {
            var runtime = DateTime.Now - _botStartTime;
            ImGui.Text($"Runtime: {runtime:hh\\:mm\\:ss}");
        }
        
        // Show helpful instructions
        if (_currentState == BotState.Disabled && _radarAvailable)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), "✅ Ready! Press F1 or 'Start/Stop Bot' to begin");
        }
        else if (_currentState == BotState.Disabled && !_radarAvailable)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "⚠️ Waiting for Radar connection...");
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
        
        ImGui.SameLine();
        if (ImGui.Button("Force Radar Reconnect"))
        {
            _radarAvailable = false;
            TryConnectToRadar();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Test Movement"))
        {
            TestMovementSystem();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Test Keyboard"))
        {
            TestKeyboardOnly();
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Refresh Settings"))
        {
            LogMessage("Settings refreshed - check Movement Settings submenu");
        }
        
        ImGui.SameLine();
        if (ImGui.Button(keyboardEnabled ? "Disable Keyboard Movement" : "Enable Keyboard Movement"))
        {
            Settings.UseMovementKey.Value = !Settings.UseMovementKey.Value;
            LogMessage($"Keyboard movement {(Settings.UseMovementKey.Value ? "enabled" : "disabled")} - using key: {currentMovementKey}");
        }
        
        ImGui.Separator();
        
        // Show settings status for debugging
        ImGui.Text($"Movement Method: {(keyboardEnabled ? $"Key({currentMovementKey})" : "Mouse")}");
        ImGui.Text($"Movement Key Enabled: {keyboardEnabled}");
        ImGui.Text($"Movement Key Value: {currentMovementKey}");
        
        if (keyboardEnabled && currentMovementKey == Keys.None)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1, 0.5f, 0, 1), "⚠️ Movement key enabled but no key set!");
        }
        
        // Quick instructions and controls
        if (!keyboardEnabled)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.7f, 1, 1), "💡 Click 'Enable Keyboard Movement' button above to use 'T' key instead of mouse");
        }
        else
        {
            ImGui.TextColored(new System.Numerics.Vector4(0, 1, 0, 1), $"[OK] Keyboard movement active - Bot will press '{currentMovementKey}' key");
        }
        
        // Movement key selector in main UI
        ImGui.Text("Change Movement Key:");
        if (ImGui.Button("Set to 'T'")) { Settings.MovementKey.Value = Keys.T; LogMessage("Movement key set to 'T'"); }
        ImGui.SameLine();
        if (ImGui.Button("Set to Space")) { Settings.MovementKey.Value = Keys.Space; LogMessage("Movement key set to 'Space'"); }
        ImGui.SameLine();
        if (ImGui.Button("Set to 'W'")) { Settings.MovementKey.Value = Keys.W; LogMessage("Movement key set to 'W'"); }
        
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
                // Wait for player to enter Aqueducts
                // AreaChange will handle the transition
                break;
                
            case BotState.GettingPath:
                if (CanRequestNewPath())
                {
                    // Only request path if we haven't requested one recently (prevent spam)
                    if ((DateTime.Now - _lastActionTime).TotalSeconds >= 3)
                    {
                        LogMessage("[TARGET] Player in Aqueducts and Radar available - requesting path to exit");
                        RequestPathToExit();
                        _lastPathRequest = DateTime.Now;
                    }
                }
                else if (_lastPathRequest != DateTime.MinValue)
                {
                    // Check for timeout - if no callback after 10 seconds, something is wrong
                    var timeSinceRequest = (DateTime.Now - _lastPathRequest).TotalSeconds;
                    if (timeSinceRequest > 10)
                    {
                        LogMessage($"[TIMEOUT] No callback received after {timeSinceRequest:F1} seconds - retrying pathfinding");
                        _lastPathRequest = DateTime.MinValue;
                        _lastActionTime = DateTime.MinValue; // Allow immediate retry
                    }
                    else if (timeSinceRequest > 5)
                    {
                        LogMessage($"[WAITING] Still waiting for callback... {timeSinceRequest:F1}s elapsed");
                    }
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
            
            // Create new cancellation token source for the new bot session
            _pathfindingCts?.Cancel(); // Cancel old one if it exists
            _pathfindingCts = new CancellationTokenSource();
            LogMessage("[DEBUG] Created new cancellation token source");
            
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
            LogMessage("[DEBUG] RequestPathToExit started");
            _lastActionTime = DateTime.Now;
            
            // Check if radar is still available
            if (!_radarAvailable || _radarLookForRoute == null)
            {
                LogMessage("[ERROR] Radar not available when trying to request path");
                _currentState = BotState.WaitingForRadar;
                return;
            }
            
            // For now, use a simple target point. Later we can make this smarter
            // by finding actual area transitions
            var player = GameController.Game.IngameState.Data.LocalPlayer;
            if (player?.GetComponent<Positioned>() is not Positioned playerPos)
            {
                LogMessage("[ERROR] Could not get player position");
                return;
            }
            
            // Better target: find a more strategic position
            var currentPos = playerPos.GridPos;
            
            // Try to find area exit or significant distance target
            // For now, use a larger offset to ensure we get a meaningful path
            var targetPos = new Vector2(currentPos.X + 200, currentPos.Y + 100);
            
            LogMessage($"[DEBUG] Calling Radar pathfinding from ({currentPos.X:F0}, {currentPos.Y:F0}) to ({targetPos.X:F0}, {targetPos.Y:F0})");
            LogMessage($"[DEBUG] Cancellation token status - IsCancelled: {_pathfindingCts.Token.IsCancellationRequested}");
            
            // Try multiple callback approaches to see which one works
            LogMessage("[DEBUG] Testing callback with immediate result...");
            
            // Test 1: Simple lambda with CancellationToken.None
            LogMessage("[DEBUG] Trying with CancellationToken.None...");
            _radarLookForRoute(targetPos, (path) => {
                LogMessage($"[CALLBACK TEST] Lambda callback triggered with {path?.Count ?? -1} points");
                OnPathReceived(path);
            }, CancellationToken.None);
            
            LogMessage("[DEBUG] Radar pathfinding call completed - waiting for callback");
        }
        catch (Exception ex)
        {
            LogError($"Error requesting path: {ex.Message}");
            _currentState = BotState.Error;
        }
    }
    
    private void OnPathReceived(List<Vector2i> path)
    {
        LogMessage("[DEBUG] *** OnPathReceived CALLBACK TRIGGERED ***");
        
        try
        {
            // Reset timeout tracking
            _lastPathRequest = DateTime.MinValue;
            
            LogMessage($"[DEBUG] OnPathReceived called - path is {(path == null ? "null" : $"not null with {path.Count} points")}");
            
            if (path == null || path.Count == 0)
            {
                LogMessage("[WARNING] No path received from Radar - will retry pathfinding");
                // Instead of stopping, let it retry pathfinding on next tick
                return;
            }
            
            _currentPath = path;
            _currentPathIndex = 0;
            _currentState = BotState.MovingAlongPath;
            
            LogMessage($"[SUCCESS] Received path with {path.Count} points - starting movement!");
            
            if (Settings.DebugSettings.DebugMode)
            {
                LogMessage($"Path preview: Start({path[0].X}, {path[0].Y}) -> End({path[path.Count-1].X}, {path[path.Count-1].Y})");
            }
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
                
                var precision = Settings.MovementSettings.MovementPrecision; // Keep using nested for precision setting
                if (distance < precision)
                {
                    // Close enough, move to next point
                    _currentPathIndex++;
                    LogMessage($"Reached waypoint {_currentPathIndex}/{_currentPath.Count}");
                    return;
                }
            }
            
            // Check if enough time has passed since last action
            var timeSinceLastAction = (DateTime.Now - _lastActionTime).TotalMilliseconds;
            var requiredDelay = _random.Next(Settings.MovementSettings.MinMoveDelayMs, Settings.MovementSettings.MaxMoveDelayMs); // Keep using nested for timing settings
            
            if (timeSinceLastAction >= requiredDelay)
            {
                // Perform the move using selected method
                bool useKeyboardMovement = Settings.UseMovementKey || Settings.MovementSettings.UseMovementKey;
                Keys movementKey = Settings.MovementKey.Value != Keys.None ? Settings.MovementKey.Value : Settings.MovementSettings.MovementKey.Value;
                
                if (useKeyboardMovement && movementKey != Keys.None)
                {
                    // Use keyboard movement: Move cursor to target, then press key
                    SetCursorPos((int)screenPos.X, (int)screenPos.Y);
                    Thread.Sleep(10);
                    PressKey(movementKey);
                }
                else
                {
                    // Use mouse click movement
                    ClickAt((int)screenPos.X, (int)screenPos.Y);
                }
                
                _lastActionTime = DateTime.Now;
                
                if (Settings.DebugSettings.DebugMode)
                {
                    var moveMethod = useKeyboardMovement ? $"Key({movementKey})" : "Mouse";
                    LogMessage($"Moving to point {_currentPathIndex} using {moveMethod}: ({targetPoint.X}, {targetPoint.Y}) -> Screen({screenPos.X:F0}, {screenPos.Y:F0})");
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
    
    private void PressKey(Keys key)
    {
        try
        {
            byte vkCode = (byte)key;
            LogMessage($"[KEYBOARD] Pressing key {key} (VK Code: {vkCode})");
            
            // Method 1: Try keybd_event
            keybd_event(vkCode, 0, 0, 0); // Key down
            Thread.Sleep(50); // Hold key briefly
            keybd_event(vkCode, 0, KEYEVENTF_KEYUP, 0); // Key up
            
            LogMessage($"[KEYBOARD] Key press sequence completed for {key}");
        }
        catch (Exception ex)
        {
            LogError($"Error pressing key {key}: {ex.Message}");
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
            
            LogMessage($"[KEYBOARD TEST] Testing key {movementKey} without mouse movement");
            
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
            
            LogMessage("[KEYBOARD TEST] Both methods completed - check if character moved or any action occurred");
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
    
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP_SENDINPUT = 0x0002;
    
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
    
    private void TryConnectToRadar()
    {
        if (_radarAvailable) return; // Already connected
        
        try
        {
            LogMessage("Attempting to connect to Radar plugin...");
            LogMessage("Trying multiple signature variations...");
            
            // Try variation 1: System.Numerics.Vector2
            LogMessage("1. Trying Action<Vector2, Action<List<Vector2i>>, CancellationToken>");
            var method1 = GameController.PluginBridge.GetMethod<Action<Vector2, Action<List<Vector2i>>, CancellationToken>>("Radar.LookForRoute");
            if (method1 != null)
            {
                LogMessage("✅ Found signature 1!");
                _radarLookForRoute = method1;
                _radarAvailable = true;
                TestRadarConnection();
                return;
            }
            
            // Try variation 2: SharpDX.Vector2  
            LogMessage("2. Trying Action<SharpDX.Vector2, Action<List<Vector2i>>, CancellationToken>");
            var method2 = GameController.PluginBridge.GetMethod<Action<SharpDX.Vector2, Action<List<Vector2i>>, CancellationToken>>("Radar.LookForRoute");
            if (method2 != null)
            {
                LogMessage("✅ Found signature 2 with SharpDX.Vector2!");
                // Need to create a wrapper since our internal Vector2 is System.Numerics
                _radarLookForRoute = (v2, callback, token) => method2(new SharpDX.Vector2(v2.X, v2.Y), callback, token);
                _radarAvailable = true;
                TestRadarConnection();
                return;
            }

            // Try variation 3: Maybe it returns Task
            LogMessage("3. Trying Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>");
            var method3 = GameController.PluginBridge.GetMethod<Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>>("Radar.LookForRoute");
            if (method3 != null)
            {
                LogMessage("✅ Found signature 3 with Task return!");
                _radarLookForRoute = (v2, callback, token) => { _ = method3(v2, callback, token); };
                _radarAvailable = true;
                TestRadarConnection();
                return;
            }

            // Try variation 4: Maybe different parameter order
            LogMessage("4. Trying Action<Action<List<Vector2i>>, Vector2, CancellationToken>");
            var method4 = GameController.PluginBridge.GetMethod<Action<Action<List<Vector2i>>, Vector2, CancellationToken>>("Radar.LookForRoute");
            if (method4 != null)
            {
                LogMessage("✅ Found signature 4 with different parameter order!");
                _radarLookForRoute = (v2, callback, token) => method4(callback, v2, token);
                _radarAvailable = true;
                TestRadarConnection();
                return;
            }

            LogMessage("❌ Could not find any matching 'Radar.LookForRoute' signature");
            LogMessage("All 4 signature variations failed:");
            LogMessage("  1. Action<Vector2, Action<List<Vector2i>>, CancellationToken>");
            LogMessage("  2. Action<SharpDX.Vector2, Action<List<Vector2i>>, CancellationToken>");
            LogMessage("  3. Func<Vector2, Action<List<Vector2i>>, CancellationToken, Task>");
            LogMessage("  4. Action<Action<List<Vector2i>>, Vector2, CancellationToken>");
            _radarAvailable = false;
        }
        catch (Exception ex)
        {
            LogError($"Error connecting to Radar: {ex.Message}");
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
                        LogMessage($"[MOVEMENT TEST] Using keyboard movement - moving cursor to ({testX}, {testY}) then pressing {movementKey}");
                        SetCursorPos(testX, testY);
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
                        ClickAt(testX, testY);
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
            }
            else
            {
                LogMessage("⚠️ Could not get player position for test - will test with (0,0)");
                var testPos = new Vector2(0, 0);
                _radarLookForRoute(testPos, (path) => {
                    LogMessage($"✅ Radar test successful - received {path?.Count ?? 0} path points");
                }, CancellationToken.None);
            }
        }
        catch (Exception testEx)
        {
            LogError($"❌ Radar connection test failed: {testEx.Message}");
            _radarAvailable = false;
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