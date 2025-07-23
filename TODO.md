# Aqueducts Bot Development TODO

## Phase 1: Basic Setup & Detection ✅ **DECIDED**
1. **Setup AqueductsBot Plugin** [[memory:4152597]]
   - Create separate plugin that piggybacks off existing Radar 
   - Use Radar's bridge API: `GameController.PluginBridge.SaveMethod("Radar.LookForRoute", ...)`
   - Copy Radar.csproj structure for dependencies (ExileCore, GameOffsets, etc.)

2. **Basic State Detection**
   - Check that Radar has loaded successfully
   - Detect when player is in Aqueducts area
   - Verify pathfinding data is available

## Phase 2: Core Bot Logic ✅ **DECIDED**
3. **Pre-Map Setup** (MINIMAL SCOPE)
   - Once the line from Radar has been detected and leads to the next area, run the pre-map setup (buffs and whatever else)
   - ~~Add safety checks (health, mana, flask charges)~~ *Moved to Phase 4*

4. **Movement System** 
   - **Approach A**: Windows API input simulation (START HERE)
   - **Approach B**: Memory manipulation (EVALUATE LATER - if read-only)
   - Move along shortest path determined by radar to area exit
   - **Human-like timing**: Random delays 100ms-1000ms between actions
   - Add basic collision detection and stuck prevention

5. **Area Transition**
   - Transition to the next area (portal or waypoint or other means)
   - Detect successful area change

## Phase 3: Loop Management (FUTURE)
6. **Return & Reset**
   - Move to the town/hideout waypoint and open the waypoint selection map
   - Select the aqueduct again, using ctrl-left click and select 'new' to run a fresh map
   
7. **Loop Control**
   - Add timer-based or iteration-based stopping conditions
   - Add pause/resume functionality

## Phase 4: Safety & Advanced Features (FUTURE)
8. **Safety Systems** (NOT REQUIRED INITIALLY)
   - Health/mana monitoring and flask usage
   - Player death detection and recovery
   - Disconnect/lag detection
   - Emergency logout functionality
   - Activity pause on player input detection

9. **Monster Clearing** (FUTURE SCOPE)
   - Target prioritization system
   - Combat integration
   - Loot detection and collection

## Technical Architecture ✅ **DECIDED**

### Movement Implementation Comparison:

**Windows API Input Simulation** (START HERE):
- **Pros**: Simple, reliable, game treats it as normal input
- **Cons**: Can be detected by timing analysis
- **Method**: Use `User32.dll` SendInput or mouse_event
- **Risk**: Low (appears as normal player input)

**Memory Manipulation** (EVALUATE COMPLEXITY):
- **Read-Only Memory**: Reading game state, positions, health (LOW RISK)
- **Write Memory**: Directly editing player position, stats (HIGH RISK - AVOID)
- **Method**: ProcessMemoryUtilities.dll that ExileApi already uses
- **Risk**: Depends on read vs write operations

### Current Project Structure:
- Radar plugin: Already installed and working on Windows machine
- ExileApi: Located at `/Users/chris/Documents/ExileApi-Compiled/`
- Radar source: Available at `/Users/chris/Documents/POE/Radar/`
- Target: Create `AqueductsBot.dll` plugin alongside Radar

## Immediate Next Steps:
1. ✅ Architectural decisions confirmed
2. **CREATE**: AqueductsBot plugin project structure
3. **IMPLEMENT**: Basic Radar integration and state detection  
4. **IMPLEMENT**: Windows API movement system with human-like timing
5. **TEST**: Simple path following to area exit 
