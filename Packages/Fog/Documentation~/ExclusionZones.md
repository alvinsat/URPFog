# Fog Exclusion Zones

The URPFog package now supports exclusion zones - areas where fog will not be rendered. This feature uses colliders to define areas that should be excluded from fog effects.

## Setup Instructions

### 1. Enable Exclusion Zones in Volume Settings

1. Open your Volume profile that contains the `Full Screen Fog` override
2. In the `Exclusion Zones` section, enable `Enable Exclusion Zones`
3. Add colliders to the `Exclusion Zone Colliders` array
4. Adjust `Exclusion Zone Smoothing` to control edge softness (0 = hard edges, 1 = very soft edges)

### 2. Using the Demo Components (Recommended)

The package includes helper components to make exclusion zones easier to use:

#### FogExclusionZoneDemo
Add this component to any GameObject with a collider to mark it as an exclusion zone:

```csharp
// Add this to a GameObject with a collider
var exclusionZone = gameObject.AddComponent<FogExclusionZoneDemo>();
exclusionZone.enableExclusion = true;
exclusionZone.showGizmos = true; // Shows red wireframe in scene view
```

#### FogExclusionZoneManager
Add this component to automatically collect and manage exclusion zones:

```csharp
// Add this to a GameObject in your scene
var manager = gameObject.AddComponent<FogExclusionZoneManager>();
manager.autoCollectExclusionZones = true; // Automatically finds FogExclusionZoneDemo components
manager.updateEveryFrame = false; // Set to true for moving exclusion zones
```

### 3. Manual Setup

You can also manually assign colliders:

1. Create GameObjects with colliders (BoxCollider, SphereCollider, etc.)
2. Add these colliders to the `Exclusion Zone Colliders` array in your Volume profile
3. Enable `Enable Exclusion Zones`

## How It Works

1. **Collider Detection**: The system projects each collider's bounds to screen space
2. **Mask Generation**: Creates a mask texture where white = normal fog, black = excluded areas
3. **Fog Sampling**: The fog shader samples this mask to reduce/eliminate fog in excluded areas
4. **Edge Smoothing**: The `Exclusion Zone Smoothing` parameter controls how soft the exclusion edges are

## Supported Colliders

- BoxCollider
- SphereCollider
- CapsuleCollider
- MeshCollider (using bounds)
- Any collider type (uses bounds as approximation)

## Performance Notes

- Exclusion zones add a small overhead for mask generation
- Performance impact scales with the number of exclusion zone colliders
- The mask is generated at screen resolution, so higher resolutions will have higher costs
- Moving colliders require updates every frame (set `updateEveryFrame = true` in FogExclusionZoneManager)

## Limitations

- Current implementation uses collider bounds for screen projection (not exact geometry)
- Works best with convex shapes like boxes and spheres
- Complex concave shapes will use their bounding box
- Exclusion zones don't account for depth - they affect the entire depth range in excluded screen areas

## Example Scene Setup

1. Create a scene with fog enabled (FullScreenFog in a Volume)
2. Add some objects with colliders (cubes, spheres, etc.)
3. Add `FogExclusionZoneDemo` components to these objects
4. Add a `FogExclusionZoneManager` to automatically manage the exclusion zones
5. Enable exclusion zones in your Volume profile
6. The fog should now be excluded around your colliders!

## Troubleshooting

- **No exclusion effect**: Check that `Enable Exclusion Zones` is enabled in the Volume
- **Exclusion zones not updating**: Set `updateEveryFrame = true` for moving objects
- **Jagged edges**: Increase the `Exclusion Zone Smoothing` value
- **Performance issues**: Reduce the number of exclusion zone colliders or optimize their bounds