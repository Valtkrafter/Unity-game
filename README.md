# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles responsive character movement, aligning the character's forward direction with the camera's planar view.
  - **Calibrated Movement Speeds**: Walk `2.2 m/s` · Run `5.0 m/s` · Sprint `10.0 m/s` (precisely tuned to eliminate foot sliding).
  - Smooth animation parameter damping (`0.15s`) using cached parameter hashes (`Speed`, `IsGrounded`, `VerticalVelocity`).
  - Gravity-based jumping (`jumpHeight: 1.8`, `gravity: -25.0`).
  - Rotation smoothing (`0.08s`).
- **Character Model — Nino Nakano (VRM)**: Active player avatar imported via UniVRM.
  - Located at `Assets/Models/Nino Nakano/` (VRM source: `5394265126170879566.vrm`).
  - Instantiated in the scene as `Player > Nino_Model` with local transforms synchronized to clean T-pose (`pos 0,0,0 · rot 0,0,0 · scale 1,1,1`).
  - SkinnedMeshRenderers configured with `Bone4` quality and `Update When Offscreen = true`.
  - `applyRootMotion = false` — translation is managed entirely by `AnimeCharacterController`.
  - **VRM SpringBone Physics Stabilization**:
    - **Hair Groups (17 components)**: `Gravity Dir (0, -1, 0)`, `Power: 0.25`, `Stiffness: 0.15`, `Drag: 0.4` (prevents horn/antenna flipping during forward motion).
    - **Skirt & Coat Groups (6 components)**: `Gravity Dir (0, -1, 0)`, `Power: 0.20`, `Stiffness: 0.25`, `Drag: 0.4` (eliminates high-frequency lower-body vibration).
- **CharacterController** (on `Player` root):
  - `Height: 1.6` · `Center Y: 0.8` · `Radius: 0.35`

### 2. Animation System
- **Active Locomotion Clips** (`Assets/Animations/`):
  - **Idle (`X Bot@Female Standing Pose.fbx`)**: Clean, flat-foot neutral stance.
  - **Walk (`X Bot@Female Walk.fbx`)**: Straightforward stride with centered pelvic translation.
  - **Run (`X Bot@Running.fbx`)**: Forward running stride with verified knee hinge orientation.
  - **Jump (`Female Locomotion Pack/jump.fbx`)**: Single-shot jump action.
- **Humanoid Retargeting & Rig Settings**:
  - **Toe Bone De-coupling**: `LeftToes` and `RightToes` are unmapped from animation avatar configurations to prevent Mixamo toe-roll rotations from deforming VRM anime shoe meshes.
  - **Root Transform Rotation**: `bakeIntoPose = true`, `keepOriginalOrientation = false` (Body Orientation — locks pure forward alignment along Z-axis).
  - **Root Transform Position (Y)**: `bakeIntoPose = true`, `keepOriginalPositionY = true` (Original — locks pelvis height to prevent knee popping).
  - **Root Transform Position (XZ)**: `bakeIntoPose = true`, `keepOriginalPositionXZ = false` (Center of Mass — absorbs lateral displacement).
  - **Curve Filtering**: Lateral translation tracks (`RootT.x`) and inverted knee/foot twist artifacts flattened to ensure smooth, natural joint flexion.
- **Animator Controller** (`Assets/Animations/Nino_LocomotionController.controller`):
  - **Parameters**: `Speed` (Float), `Jump` (Trigger), `SpecialIdle` (Trigger), `VerticalVelocity` (Float), `IsGrounded` (Bool).
  - **Base Layer Settings**: `IK Pass = false`, `iKOnFeet = false` across all states (prevents hyper-extending knees on phantom ground planes).
  - **Locomotion Blend Tree** (1D, driven by `Speed`):
    - `0.0` → `Female Standing Pose` (100% idle at rest)
    - `2.2` → `Female Walk` (100% walk cadence)
    - `5.0` → `Running` (100% run cadence)
  - **Jump State**: Triggered by `Jump` parameter with crossfade exit back to Locomotion.

### 3. Camera System
- **Third-Person Orbit Camera (`ThirdPersonOrbitCamera.cs`)**:
  - Orbits smoothly around the player character.
  - Supports mouse look for pitch and yaw.
  - Supports zooming in and out using the mouse scroll wheel.
  - Prevents clipping through geometry by repositioning closer to the player when obstacles block the line of sight.

### 4. Environment
- A basic testing arena (`Ground_Arena`) is set up for movement and camera collision testing.

## Project Structure

```
Assets/
├── Animations/
│   ├── Nino_LocomotionController.controller  # Active locomotion state machine
│   ├── X Bot@Female Standing Pose.fbx         # Clean neutral idle stance
│   ├── X Bot@Female Walk.fbx                  # Centered forward walk clip
│   ├── X Bot@Running.fbx                      # Calibrated forward run clip
│   └── Female Locomotion Pack/                # Supplementary clips (jump, strafes, turns)
│       └── jump.fbx
├── Editor/
│   └── PurgeHuTaoAndUpgradeNino.cs            # Asset maintenance utilities
├── Models/
│   └── Nino Nakano/                           # Active VRM player avatar
│       ├── 5394265126170879566.vrm
│       ├── 5394265126170879566.prefab
│       ├── 5394265126170879566.Avatar/
│       ├── 5394265126170879566.Materials/
│       ├── 5394265126170879566.Meshes/
│       ├── 5394265126170879566.Textures/
│       └── 5394265126170879566.BlendShapes/
└── Scripts/
    ├── AnimeCharacterController.cs            # Movement and locomotion logic
    └── ThirdPersonOrbitCamera.cs              # Orbit camera with collision damping
```

## Scene Hierarchy

```
Player                          (CharacterController, AnimeCharacterController)
 └── Nino_Model                 (Animator → Nino_LocomotionController, Humanoid)
      └── [VRM bone hierarchy]
```

## Controls

| Input | Action |
|---|---|
| WASD / Arrow Keys | Move |
| Left Ctrl + Move | Walk (slow) |
| Move (default) | Run |
| Left Shift + Move | Sprint |
| Space | Jump |
| Mouse | Camera orbit (pitch / yaw) |
| Scroll Wheel | Camera zoom |

---

*Note: This README is automatically updated by the Antigravity AI assistant to maintain the current context and list of features as development progresses.*
