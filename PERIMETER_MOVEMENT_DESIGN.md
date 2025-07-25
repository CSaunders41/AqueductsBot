# Perimeter-Based Movement System Design

## Problem Statement

Current circle intersection algorithm fails because:
- **Path segments**: 1.0 unit each (microscopic)
- **Pursuit radius**: ~361 units (massive circle)  
- **Result**: Circle encompasses entire path sections, intersection math fails

## Design Solution: Perimeter Projection

### Core Concept
**Always click at pursuit circle perimeter in the direction of the path/destination**

```
Player Position (Center of Circle)
    │
    │ Pursuit Radius (300+ units)
    │
    ▼
┌─────○─────┐  ← Circle Perimeter (where we ALWAYS click)
│           │
│     P     │  P = Player
│           │
│           │
└───────────┘
    
Direction: Toward path/destination
Click Point: Intersection of direction vector with circle perimeter
```

### Algorithm Design

```csharp
private Vector2? FindPerimeterTarget(List<Vector2i> path, int startIndex)
{
    var playerPos = GetPlayerPosition();
    var radius = Settings.MovementSettings.PursuitRadius.Value;
    
    // 1. DETERMINE TARGET DIRECTION
    Vector2 targetDirection = DeterminePathDirection(path, startIndex, playerPos);
    
    // 2. PROJECT TO PERIMETER  
    Vector2 perimeterPoint = playerPos + (targetDirection.Normalize() * radius);
    
    // 3. VALIDATE & RETURN
    return ValidateTargetPoint(perimeterPoint) ? perimeterPoint : null;
}

private Vector2 DeterminePathDirection(List<Vector2i> path, int startIndex, Vector2 playerPos)
{
    // Option A: Direction to next significant waypoint
    Vector2 nearestPathPoint = FindNearestPathPoint(path, startIndex);
    return nearestPathPoint - playerPos;
    
    // Option B: Direction toward path end/destination  
    Vector2 destination = new Vector2(path[path.Count - 1].X, path[path.Count - 1].Y);
    return destination - playerPos;
    
    // Option C: Weighted combination of both
    return CombineDirections(nearestPathPoint - playerPos, destination - playerPos);
}
```

### Implementation Benefits

1. **Guaranteed Distance**: ALWAYS clicks at exact pursuit radius
2. **No Segment Dependencies**: Works regardless of path segment sizes
3. **Simple & Robust**: No complex intersection calculations
4. **Predictable**: User can see exactly where bot will click (circle perimeter)
5. **Configurable**: Pursuit radius setting directly controls click distance

### Edge Cases Handled

- **Path inside circle**: Direction toward destination
- **Path behind player**: Forward progress prioritization  
- **End of path**: Direct targeting of final waypoint
- **Camera bounds**: Ensure clicks stay on screen

### Comparison to Current System

| Aspect | Current (Intersection) | New (Perimeter) |
|--------|----------------------|-----------------|
| **Click Distance** | Variable (25-400+ units) | **Fixed (pursuit radius)** |
| **Segment Dependency** | High (fails on small segments) | **None** |
| **Predictability** | Low (fallback logic) | **High (always perimeter)** |
| **Path Following** | Precise when working | **Good direction following** |
| **Robustness** | Brittle | **Robust** |

### Configuration Options

- **Pursuit Radius**: Direct control of click distance (existing setting)
- **Direction Weighting**: Balance between "follow path" vs "reach destination"  
- **Update Frequency**: How often to recalculate direction (timing approach)

## Implementation Plan

### Phase 1: Basic Perimeter Targeting
- Replace `FindPathIntersectionWithRadius()` with `FindPerimeterTarget()`
- Simple direction calculation toward path/destination
- Test with current settings

### Phase 2: Direction Optimization  
- Implement smarter direction calculation
- Add weighting between path following vs destination seeking
- Tune for optimal movement

### Phase 3: Advanced Features
- Dynamic radius based on path characteristics
- Predictive targeting for smoother movement
- Integration with stuck detection

## Expected Results

- **Consistent click distance** at pursuit radius perimeter
- **Elimination of stuck-clicking** at close ranges
- **Smoother movement** with predictable targeting
- **User control** via pursuit radius setting

This directly addresses the user's requirement: "mouse to stay at parameter when clicking to move" 