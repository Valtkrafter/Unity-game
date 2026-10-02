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

### 2. Shading, Materials & Rendering (Unity Toon Shader / URP)
- **Official Unity Toon Shader (UTS3 / URP)**:
  - Migrated avatar materials from standard unlit to official `Unity Toon Shader` (`Toon/Toon` via `com.unity.toonshader`).
  - **Calibrated 2-Step Anime Cel-Shading**:
    - `BaseColor_Step`: `0.50` - `0.55` (crisp threshold for anime shadow demarcation).
    - `BaseShade_Feather`: `0.05` (eliminates muddy gradients, producing sharp cel boundaries).
    - `_Use_BaseAs1st = 1` & `_Use_1stAs2nd = 1`: Preserves diffuse texture details (buttons, seams, pleats) under shadows.
    - `_Set_SystemShadowsToBase = 1`: Real-time directional light shadows integrate cleanly without geometric self-shadow noise.
  - **Anime-Accurate Palette & Shading Calibration**:
    - **Hair (`HAIR_01`, `02`, `03`, `HairBack`)**: Base `#FFFFFF` with rich magenta shade (`#C43D75`) and dark magenta ink outline (`#52142E`).
    - **School Uniform Skirt (`Bottoms_01_CLOTH_01`, `02`, `03`)**: Authentic pastel lime anime green (`#9EE37D`) with medium lime cel shade (`#6DB84D`) and dark ink green outline (`#2E5A26`).
    - **School Shirt & Collar (`Onepiece_00_CLOTH`, `Tops_02_CLOTH`)**: Crisp white base with soft lavender-grey anime shade (`#A5A0B8`).
    - **Cardigan / Sweater (`Tops_01_CLOTH`)**: Native purple with cool-tinted indigo shade (`#3A3052`).
    - **Body Skin (`Body_00_SKIN`)**: Soft warm peach anime shade (`#E29D86`).
    - **Face Skin (`Face_00_SKIN`)**: Pure white base with delicate blush shade (`#FBE6E3`), broad illumination step `0.1`, and zero outline width (`0.0`).
  - **Inverted-Hull Outlines**:
    - Mode: `Normal Direction` (`_OUTLINE_NML`).
    - Width: `1.0` (subtle anime ink line).
    - Color-coded per material for soft, natural transitions (magenta for hair, dark green for skirt, charcoal for clothing).
  - **Face & Eye Preservation**:
    - `Face` SkinnedMeshRenderer: `receiveShadows = false` (completely prevents bangs/hair from casting jagged polygon shadow shards on the face).
    - Eyes (`EyeIris`, `EyeHighlight`, `EyeWhite`): Preserved on `Universal Render Pipeline/Unlit` for maximum luminescence and clarity.
    - Eyebrows (`FaceBrow`): Explicit `renderQueue = 3001` so eyebrows layer cleanly over hair strands.
- **Material Backup**:
  - Original VRM URP Unlit materials backed up safely at `Assets/Models/Nino Nakano/Backup_Materials_URP_Unlit/`.

### 3. Animation System
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

### 4. Camera System
- **Third-Person Orbit Camera (`ThirdPersonOrbitCamera.cs`)**:
  - Orbits smoothly around the player character.
  - Supports mouse look for pitch and yaw.
  - Supports zooming in and out using the mouse scroll wheel.
  - Prevents clipping through geometry by repositioning closer to the player when obstacles block the line of sight.

### 5. Environment
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
│   ├── RestoreNinoMaterials.cs                # Material & shadow restoration tool
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
