# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles responsive character movement, aligning the character's forward direction with the camera's planar view.
  - **Stride-Matched Movement Speeds**: Walk `0.95 m/s` · Run `3.4 m/s` · Sprint `4.6 m/s`. These are the natural ground speeds of the Mixamo clips on Nino, measured from the planted foot (`Tools/Locomotion/2. Measure Natural Clip Speeds`), so feet stay locked to the floor (measured planted-foot slip ≈ 0.1–0.2 m/s, previously 3–9 m/s).
  - **Smooth starts & stops**: acceleration `9 m/s²` (idle → run in ~0.4 s, one walking step into the run) and deceleration `7 m/s²` (run → idle in ~0.5 s, one or two slowing steps). Movement speed and the Animator `Speed` parameter share the same ramped value, so the feet stay in sync with the blend tree.
  - Cached parameter hashes (`Speed`, `IsGrounded`, `VerticalVelocity`).
  - Static input overrides (`UseInputOverride`, `InputOverride`, `WalkOverride`, `SprintOverride`, `JumpOverride`) for automated testing.
  - Gravity-based jumping (`jumpHeight: 1.8`, `gravity: -25.0`) with a `0.07 s` takeoff delay so the body leaves the ground when the push-off animation's feet do. Sets `AirProgress` (0 = takeoff, 0.5 = apex, 1 = landing) from the vertical velocity.
  - Rotation smoothing (`0.08s`).
- **Character Model — Nino Nakano (VRM)**: Active player avatar imported via UniVRM.
  - Located at `Assets/Models/Nino Nakano/` (VRM source: `5394265126170879566.vrm`).
  - Instantiated in the scene as `Player > Nino_Model` at real-world size (~1.65 m, `humanScale 0.96`): `pos 0,-0.02,0 · rot 0,0,0 · scale 1,1,1`. The `-0.02` offset compensates the CharacterController skin width (`0.05`) so the shoe soles sit on the floor (measured from the baked mesh in Play Mode: idle sole at `-0.1 cm`).
  - SkinnedMeshRenderers configured with `Bone4` quality and `Update When Offscreen = true`.
  - `applyRootMotion = false` plus `DiscardRootMotion` (handles `OnAnimatorMove`) — translation and height are managed entirely by `AnimeCharacterController`; any unbaked root motion is dropped instead of being written into the pose.
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
- **Active Locomotion Clips** (`Assets/Animations/`) — the original Mixamo motion, unedited and played at 1x:
  - **Idle (`Female Locomotion Pack/idle.fbx`)**: 8.3 s breathing idle in the hand-on-hip pose (1–2 cm sway of head, chest and hands). Replaces the single-frame `X Bot@Female Standing Pose`, which is the same pose frozen.
  - **Walk (`X Bot@Female Walk.fbx`)**: 1.333 s cycle, heel-strike → roll → toe-off.
  - **Run (`X Bot@Running.fbx`)**: 0.7 s cycle (~17 frames at 24 fps), forefoot strike with a real flight phase.
  - **Jump (`Female Locomotion Pack/jump.fbx`)**: split into `Jump_Takeoff` (frames 29–37, push-off after the anticipation crouch), `Jump_Air` (37–63) and `Jump_Land` (63–100). `Jump_Air` leaves its vertical motion unbaked so the clip's own 0.27 m hop is dropped and only the physics jump lifts the body. All three use `level +0.04` so the feet sit on the floor.
- **Humanoid Retargeting (`Assets/Editor/MixamoLocomotionSetup.cs`, `Tools/Locomotion/1. Reimport Mixamo Clips With Clean T-Pose`)**:
  - **Clean shared reference T-pose**: Mixamo files downloaded without skin have no bind pose, so Unity used each file's first frame as the humanoid reference. That gave tilted legs (~7°) and a different hip height per clip (the crouched run got body scale `0.939` vs `1.054` for idle), which made the run float ~10 cm and bent Nino's legs. All four clips now use one generated T-pose (level hips, vertical spine and legs, horizontal arms, feet forward, hips at standing height `1.054 m`).
  - **Toes**: not mapped in the clip avatars, so Mixamo toe-roll never deforms the VRM shoe meshes.
  - **Root Transform Rotation**: baked, Body Orientation. **Position Y**: baked, Original, `level 0` (no offset hacks). **Position XZ**: baked, Center of Mass (jump keeps Original).
  - No curve edits or "stabilized"/"corrected" clip copies.
- **Animator Controller** (`Assets/Animations/Nino_LocomotionController.controller`, assigned directly on `Nino_Model`):
  - **Parameters**: `Speed` (Float), `Jump` (Trigger), `SpecialIdle` (Trigger), `VerticalVelocity` (Float), `IsGrounded` (Bool).
  - **Base Layer Settings**: `IK Pass = false`, `iKOnFeet = false`.
  - **Locomotion Blend Tree** (1D, driven by `Speed`; set by `Tools/Locomotion/3. Apply Speeds To Blend Tree And Scene`):
    - `0.0` → `Female Standing Pose`
    - `0.95` → `Female Walk` (1x)
    - `3.4` → `Running` (1x)
    - `4.6` → `Running` (1.35x — sprint; a dedicated Mixamo sprint clip is needed for faster sprinting)
  - **Jump States**: `Locomotion → Jump_Takeoff` (`Jump` trigger, 0.08 s) `→ Jump_Air` (end of takeoff; motion time = `AirProgress`, so the pose follows the physics arc for any height, and walking off a ledge also enters it) `→ Jump_Land` (`IsGrounded` and falling) `→ Locomotion` (after 20% when moving — run out of the landing — or 60% when standing). `Jump_Land → Jump_Takeoff` allows chained jumps.
  - **Parameters**: `AirProgress` (Float) added.
- **24 fps Locomotion Capture** (`Tools/Locomotion/4. Capture 24fps Sequence (Play Mode)`, `Assets/Scripts/Dev/LocomotionFrameCapture.cs`, editor-only):
  - Locks game time to exactly 1/24 s per frame and records idle / walk / run / sprint, start (idle → run), stop (run → idle), standing jump, running jump and a camera strafe from the game camera, a side view and a front view (contact sheets in `Captures/Locomotion_24fps/`, git-ignored). Each phase starts from the arena centre.
  - `report.txt` logs per frame: ground speed, planted-foot slip, shoe-sole height (lowest baked mesh vertex vs. floor), hip height and knee flexion.

### 4. Camera System
- **Wuthering Waves style camera (`ThirdPersonOrbitCamera.cs`)**:
  - Mouse orbit with frame-rate independent sensitivity (`0.12°` per pixel), pitch `-35°..70°`, starts behind the character at `12°`.
  - Scroll-wheel zoom (`1.6–8 m`, default `4.2 m`); the camera pulls closer when looking up so it never digs into the ground.
  - Tight horizontal follow (`0.05 s`) with a softer vertical follow (`0.18 s`), so jumps and steps don't jolt the view.
  - Auto-recenter: while running sideways the camera swings round behind the character (up to `70°/s`), and pitch eases back to `12°`. Pauses for `0.6 s` after any mouse input and never fights you when running towards the camera.
  - FOV `50°`, widening by `5°` while sprinting.
  - Collision: snaps in front of walls instantly, eases back out over `0.25 s`.
  - Cursor locked in play; hold **Left Alt** to free it, **Esc** unlocks, click relocks.

### 5. Environment
- A basic testing arena (`Ground_Arena`) is set up for movement and camera collision testing.

## Project Structure

```
Assets/
├── Animations/
│   ├── Nino_LocomotionController.controller  # Active locomotion state machine
│   ├── X Bot@Female Standing Pose.fbx         # Single-frame version of the idle pose (not in the blend tree)
│   ├── X Bot@Female Walk.fbx                  # Walk clip
│   ├── X Bot@Running.fbx                      # Run clip (also sprint at 1.35x)
│   └── Female Locomotion Pack/                # Supplementary clips (strafes, turns)
│       ├── idle.fbx                           # Breathing idle (active)
│       └── jump.fbx                           # Jump_Takeoff / Jump_Air / Jump_Land
├── Editor/
│   ├── MixamoLocomotionSetup.cs               # Mixamo import (clean T-pose), speed measurement, 24fps capture
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
    ├── ThirdPersonOrbitCamera.cs              # WuWa-style follow camera
    ├── DiscardRootMotion.cs                   # Drops root motion on the character's Animator
    └── Dev/LocomotionFrameCapture.cs          # Editor-only 24fps capture + foot diagnostics
```

## Scene Hierarchy

```
Player                          (CharacterController, AnimeCharacterController)
 └── Nino_Model                 (Animator → Nino_LocomotionController, Humanoid, DiscardRootMotion)
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
| Left Alt (hold) | Free the cursor |
| Esc / Left Click | Unlock / relock the cursor |

---

*Note: This README is automatically updated by the Antigravity AI assistant to maintain the current context and list of features as development progresses.*
