# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles responsive character movement, aligning the character's forward direction with the camera's planar view.
  - **Stride-Matched Movement Speeds**: Walk `1.12 m/s` · Run `3.6 m/s` · Sprint `4.9 m/s`. These are the natural ground speeds of Nino's walk/run clips, measured from the standing foot (`Tools/Locomotion/2. Measure Natural Clip Speeds`), so her feet stay locked to the floor (measured standing-foot slip ≈ 0.06–0.2 m/s).
  - **Idle flourish**: after `8 s` standing still (then every `20 s`), sets the `SpecialIdle` trigger (Nino's model pose).
  - **Smooth starts & stops**: acceleration `9 m/s²` (idle → run in ~0.4 s, one walking step into the run) and deceleration `7 m/s²` (run → idle in ~0.5 s, one or two slowing steps). Movement speed and the Animator `Speed` parameter share the same ramped value, so the feet stay in sync with the blend tree.
  - Cached parameter hashes (`Speed`, `IsGrounded`, `VerticalVelocity`).
  - Static input overrides (`UseInputOverride`, `InputOverride`, `WalkOverride`, `SprintOverride`, `JumpOverride`) for automated testing.
  - Gravity-based jumping (`jumpHeight: 1.8`, `gravity: -25.0`) with a `0.07 s` takeoff delay so the body leaves the ground when the push-off animation's feet do. Sets `AirProgress` (0 = takeoff, 0.5 = apex, 1 = landing) from the vertical velocity.
  - Rotation smoothing (`0.08s`).
- **Character Model — Nino Nakano (VRM)**: Active player avatar imported via UniVRM.
  - Located at `Assets/Models/Nino Nakano/` (VRM source: `5394265126170879566.vrm`).
  - Instantiated in the scene as `Player > Nino_Model` at real-world size (~1.65 m, `humanScale 0.96`): `pos 0,-0.02,0 · rot 0,0,0 · scale 1,1,1`. The `-0.02` offset compensates the CharacterController skin width (`0.05`) so the shoe soles sit on the floor (measured from the baked mesh in Play Mode: idle sole at `-0.1 cm`).
  - SkinnedMeshRenderers: `Bone4` quality, `Update When Offscreen = false` with fixed 2.6 m bounds (no per-frame bounds recomputation).
  - `applyRootMotion = false` plus `DiscardRootMotion` (handles `OnAnimatorMove`) — translation and height are managed entirely by `AnimeCharacterController`; any unbaked root motion is dropped instead of being written into the pose.
  - **VRM SpringBone Physics Stabilization**:
    - **Hair Groups (17 components)**: `Gravity Dir (0, -1, 0)`, `Power: 0.25`, `Stiffness: 0.15`, `Drag: 0.4` (prevents horn/antenna flipping during forward motion).
    - **Skirt front (4 components)**: `Gravity Power 0.20`, `Stiffness 0.25`, `Drag 0.4` — the visible part keeps its bounce.
    - **SkirtCovered (1 component)**: the 16 side/back skirt chains that are usually under the jacket — `Stiffness 0.8`, `Drag 0.7`, `Gravity 0.1`.
    - The `CoatSkirt` bones are unused VRoid template leftovers (no vertices are weighted to them); the jacket hem has its own physics (`JacketHemCloth`, below).
  - **FastSpringBone (`FastSpringBoneActivator`)**: all spring chains run as one Burst-compiled job via UniVRM's `Vrm0XFastSpringboneRuntime` instead of 25 MonoBehaviour updates (LateUpdate 1.03 → 0.31 ms).
- **Cloth** (`Tools/Character/Apply Cloth Clipping Fix`, `Assets/Editor/ClothClippingFix.cs`). Rebuilt from the imported mesh on every run into `Assets/Models/Nino Nakano/ClothFix/` (mesh and texture copies; the imported model is untouched):
  - **Jacket hem physics (`JacketHemCloth`)**: the jacket's lower part hangs from 12 chains × 3 segments placed on the jacket's surface around the hips (`J_Sec_JacketHem_*`, 292 vertices re-weighted). Simulated after the VRM spring bones like a spring bone (stiffness `1.2`, drag `0.3`, gravity `0.25`), but only `35%` of her own movement is felt as inertia, so it sways back a little when running and lifts a little in jumps instead of flying up like an umbrella; swing limit `40°`, `10°` sideways around the body. Every frame it collides with the skirt (564 skirt points skinned on the CPU and compared with a map of the jacket's rest surface, so the panel that is over a point *now* makes way) and with the thighs (tapered capsules `8.5 → 4.9 cm`). ~0.3 ms per frame in the editor.
  - **Skirt fitted to the body**: the skirt keeps its imported shape (an earlier 3 cm "tuck" of its hidden part pushed it into her hips, so her bottom showed through as soon as the jacket moved). Spring-bone influence above each skirt bone's pivot fades out (those points moved into her bottom when the skirt swung out behind a leg), and where the skirt fits tightly its other weights follow the skin under it. The lower skirt still swings on its springs.
  - **Skirt texture cut-outs**: both skirt layers had holes under the front band and on the sides. The band didn't cover its hole exactly (black zigzag across the front of the skirt) and the side holes showed when the jacket moved. Filled wherever the band doesn't cover the skirt (`ClothFix/_17_ClothFix.png`, `_19_ClothFix.png`; the band's soft edge recoloured in `_18_ClothFix.png`).
  - **Hands in the jacket (`ArmClothClearance`)**: each frame, ~700 points of the jacket's lower surface are skinned on the CPU; if a hand/finger tip is inside, the upper arm rotates outward just enough (+1.2 cm clearance), easing back when free. Measured: hands inside the jacket 120/120 idle frames (up to 5.2 cm) → 0, running 25/120 → 0.
  - **Measured** with `ClothLayerAudit` (every frame at 60 fps of idle, walk, run, sprint, start, stop, 180° turn, standing and running jump): legs through the jacket `0` in every phase (running jump before: 23 vertices per frame, up to 5.8 cm); skirt through the jacket at most 6 vertices, ≤ 0.7 cm, at the jacket's front edge (running jump before: 36 per frame, up to 4.9 cm); idle and walk fully clean. The back of the skirt never shows skin; when a knee comes up (run, jumps) up to 18 vertices of the thigh's front and the hip crease reach through the front of the skirt, mostly hidden behind the raised thigh.
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
  - **Double-sided cloth**: all Toon materials render both faces (`_CullMode 0`, as in the original VRoid materials), so the inside of the jacket, skirt and sleeves isn't see-through.
  - **Inverted-Hull Outlines**:
    - Mode: `Normal Direction` (`_OUTLINE_NML`).
    - Width: `1.0` (subtle anime ink line).
    - Color-coded per material for soft, natural transitions (magenta for hair, dark green for skirt, charcoal for clothing).
  - **Face & Eye Preservation**:
    - `Face` SkinnedMeshRenderer: `receiveShadows = false` (completely prevents bangs/hair from casting jagged polygon shadow shards on the face).
    - Eyes (`EyeIris`, `EyeHighlight`, `EyeWhite`): Preserved on `Universal Render Pipeline/Unlit` for maximum luminescence and clarity.
    - Eyebrows (`FaceBrow`): Explicit `renderQueue = 3001` so eyebrows layer cleanly over hair strands.
- **Anti-aliasing & texture fidelity** (`Tools/Character/Upgrade Render Quality`, `Assets/Editor/RenderQualityUpgrade.cs`):
  - **MSAA 4x** (`PC_RPAsset`) for geometry and inverted-hull outline edges + **SMAA High** on the camera for cel-shading steps (MSAA was off).
  - **Character textures**: mipmaps were disabled (textures shimmered/sparkled with distance). Now mipmaps with Kaiser filter, trilinear + 8x anisotropic, **BC7** instead of DXT1/DXT5, alpha coverage preserved for alpha-clipped hair (cutoff 0.5).
- **Material Backup**:
  - Original VRM URP Unlit materials backed up safely at `Assets/Models/Nino Nakano/Backup_Materials_URP_Unlit/`.

### 3. Animation System
- **Nino's locomotion** (`Assets/Animations/Nino/`): anime motion from MMD, converted and restyled for Nino (the Mixamo walk/run looked generic):
  - **Walk (`Walk_Nino.anim`)**: tweekcrystal's feminine *Normal walk*. Her feet step on one line with a light heel kick, and the upper body twists against the hips. Nino styling on top: heel kick 25% lower, hip sway ×1.5, chest out 4°, chin up 3°. 1.0 s cycle, `1.12 m/s`.
  - **Run (`Run_Nino.anim`)**: tweekcrystal's *Female run*, with a forward lean and full arm swing. Strides are scaled to 90% (it was made on a taller model), chin up 2°. 0.53 s cycle, `3.6 m/s`; sprint plays it at 1.35x (`4.9 m/s`).
  - Both play in place (travel removed, sway and bob kept). The standing foot is planted on Nino's own shoes, and both loop seamlessly (the converter picks the cycle with zero pose error).
  - Measured in Play Mode (`Tools/Locomotion/4`):
    - Walk: standing-foot slide averages `0.06 m/s`; shoe sole `-0.4..0.7 cm`.
    - Run: sole `>= 0 cm`; slide `0.2 m/s`.
    - Cloth audit, walk: clean (2 verts / 0.3 cm).
    - Cloth audit, run: the higher knee lift pushes the front of the thigh up to ~4 cm into the skirt for a few frames. The jacket hides it from the game camera.
- **Idle**: `Female Locomotion Pack/idle.fbx` (Mixamo, 8.3 s breathing idle in a hand-on-hip pose).
  - **Idle flourish (`SpecialIdle` state)**: once Nino has stood still for `8 s` (then every `20 s`), `AnimeCharacterController` fires the `SpecialIdle` trigger and she strikes the VRoid *Model pose*: hand on hip, then she touches her hair. Moving or jumping cuts it short.
- **Gestures** (`Assets/Animations/Nino/Gestures/`, VRoid Project motion pack, ready for the interaction system): `VRoid_Greeting`, `VRoid_PeaceSign`, `VRoid_ModelPose`, `VRoid_ShowFullBody`, `VRoid_Spin`, `VRoid_Squat`, `VRoid_Shoot`.
- **Candidates** (`Assets/Animations/Candidates/`, not used in game, kept for comparison): MMD *Normal / Lazy / Cool guy* walks, Mahlazer's walk, *Female / Male* runs.
- **Jump (`Female Locomotion Pack/jump.fbx`)**: split into three states:
  - `Jump_Takeoff`: frames 29–37, the push-off after the anticipation crouch.
  - `Jump_Air`: frames 37–63. Its vertical motion is left unbaked, so the clip's own 0.27 m hop is dropped and only the physics jump lifts the body.
  - `Jump_Land`: frames 63–100.
  - All three use `level +0.04` so the feet sit on the floor.
- **Motion conversion pipeline (`Tools/AnimConvert/`, Python 3 + numpy)**: `convert_all.sh` rebuilds every clip from the downloaded sources (default `~/Downloads/TQQ animation`, not in the repo).
  - `vmd2clip.py` (MMD `.vmd`) first rebuilds an MMD-standard skeleton with **Nino's own proportions** (`nino_rest.json`, exported from her T-pose). It then:
    - evaluates the VMD bezier tracks;
    - converts MMD space (which faces −Z) to Unity;
    - compensates the MMD A-pose arms (37°);
    - solves the leg IK (`足ＩＫ`/`つま先ＩＫ`) analytically, with MMD's minimal-swing behaviour.
  - After that, `vmd2clip.py` applies:
    - **Loop detection**: finds the cycle period/start with the smallest pose difference.
    - **Ground contact**: the MMD foot targets fit the source model, not Nino, so each foot whose target has stopped is planted with the lowest point of Nino's shoe at y = 0. A moving foot is never pulled down (no early touchdown or skid) and keeps 1 cm of swing clearance.
    - **Style options**: `--lift` (foot lift), `--sway` (pelvis rotation), `--chest`/`--chin` (posture), `--scale` (stride for differently sized source models), `--dy`.
    - **Report**: loop error, travel speed, standing-foot speed, knee range and support-foot height.
  - `vrma2clip.py` (VRM Animation `.vrma`): reads the glTF humanoid and outputs each bone's world rotation relative to its T-pose (glTF to Unity: X mirrored), with the hips scaled to Nino.
  - `make_gifs.py`: turns `AnimShowcaseCapture` frames into labelled comparison GIFs.
  - **`HumanoidClipBaker.cs`** (`Tools/Animation/Bake Pose Streams To Humanoid Clips`) applies each pose stream to a hidden copy of Nino and records it with `HumanPoseHandler` as a humanoid clip:
    - Curves: root, all 95 muscles, and **IK goal curves** (`LeftFootT/Q`...). Without the goal curves, any foot-IK playback (for example a Playable) folds the legs.
    - Keyframe reduction keeps every bone within ~2 mm of the per-frame samples.
    - Root settings are in place: rotation, Y and XZ all baked, based on Original.
    - Streams are lowered 3 cm because the model origin sits 3 cm above the floor in the scene (see Character Model).
- **Mixamo retargeting (`Assets/Editor/MixamoLocomotionSetup.cs`, `Tools/Locomotion/1. Reimport Mixamo Clips With Clean T-Pose`)**, still used for the idle and jump:
  - **Clean shared reference T-pose**: Mixamo files downloaded without skin have no bind pose, so Unity used each file's first frame as the humanoid reference.
    - Problem: legs tilted ~7°, and each clip got a different hip height (body scale `0.939` for the crouched run vs `1.054` for idle). The run floated ~10 cm and Nino's legs bent.
    - Fix: every Mixamo clip uses one generated T-pose: level hips, vertical spine and legs, horizontal arms, feet forward, hips at standing height `1.054 m`.
  - **Toes**: not mapped in the clip avatars, so Mixamo toe-roll never deforms the VRM shoe meshes.
  - **Root Transform Rotation**: baked, Body Orientation. **Position Y**: baked, Original, `level 0` (no offset hacks). **Position XZ**: baked, Center of Mass (jump keeps Original).
- **Animator Controller** (`Assets/Animations/Nino_LocomotionController.controller`, assigned directly on `Nino_Model`, rebuilt by `Tools/Locomotion/3. Apply Speeds To Blend Tree And Scene`):
  - **Parameters**: `Speed` (Float), `Jump` (Trigger), `SpecialIdle` (Trigger), `VerticalVelocity` (Float), `IsGrounded` (Bool), `AirProgress` (Float).
  - **Base Layer Settings**: `IK Pass = false`, `iKOnFeet = false`.
  - **Locomotion Blend Tree** (1D, driven by `Speed`). Tool 3 measures each clip's standing-foot speed and writes the thresholds and the controller speeds together. The run falls back to its authored `3.6 m/s`, because it lands flat for only ~2 frames before pushing off the toe. Thresholds:
    - `0.0` → breathing idle
    - `1.12` → `Walk_Nino` (1x)
    - `3.6` → `Run_Nino` (1x)
    - `4.9` → `Run_Nino` (1.35x, sprint)
  - **Jump States**:
    - `Locomotion → Jump_Takeoff`: on the `Jump` trigger, 0.08 s.
    - `Jump_Takeoff → Jump_Air`: at the end of takeoff. Motion time = `AirProgress`, so the pose follows the physics arc for any height; walking off a ledge also enters it.
    - `Jump_Air → Jump_Land`: when `IsGrounded` and falling.
    - `Jump_Land → Locomotion`: after 20% when moving (she runs out of the landing), or 60% when standing.
    - `Jump_Land → Jump_Takeoff` allows chained jumps.
  - **SpecialIdle**:
    - `Locomotion → SpecialIdle`: on the `SpecialIdle` trigger while `Speed < 0.05`, 0.45 s.
    - `SpecialIdle → Locomotion`: at 92% (0.6 s), or immediately when `Speed > 0.1`.
    - `SpecialIdle → Jump_Takeoff`: on `Jump`.
- **24 fps Locomotion Capture** (`Tools/Locomotion/4. Capture 24fps Sequence (Play Mode)`, `Assets/Scripts/Dev/LocomotionFrameCapture.cs`, editor-only):
  - Locks game time to exactly 1/24 s per frame. Records idle, walk, run, sprint, start (idle → run), stop (run → idle), standing jump, running jump and a camera strafe.
  - Each phase starts from the arena centre and is shot from the game camera, a side view and a front view (contact sheets in `Captures/Locomotion_24fps/`, git-ignored).
  - `report.txt` logs per frame: ground speed, standing-foot slip, shoe-sole height (lowest baked mesh vertex vs. floor), hip height and knee flexion.
- **Animation showcase** (`Assets/Scripts/Dev/AnimShowcaseCapture.cs`, editor-only, Play Mode):
  - Plays any clips on the in-game Nino (spring bones, jacket cloth, toon shading) through a PlayableGraph while moving her at the clip's ground speed.
  - Writes front-3/4 and side frames per clip (`Captures/AnimCandidates/`) for `make_gifs.py`.
  - `ClipPreviewSheet.cs` renders quick edit-mode contact sheets (no spring bones).
- **Cloth audit** (`Assets/Scripts/Dev/ClothLayerAudit.cs`, editor-only, Play Mode: `ClothLayerAudit.Begin("Captures/ClothAudit/<name>")`, optional phase list and `closeUp`):
  - Drives the same movement phases at a locked 60 fps. On every frame, after the spring bones, it measures how many vertices of an inner layer are outside the layer that covers them: skin through skirt, skirt through jacket, skin through jacket. It reports the depth and *where* (height × angle around the hips).
  - Writes a contact sheet per phase (git-ignored `Captures/ClothAudit/`). Wide views: game-camera direction, the same with the jacket hidden, from behind at ground level, and from the side. Close-ups at hip height: game direction, front-left, front-right and behind.
- **Credits**:
  - Walk/run cycles: **tweekcrystal** (DeviantArt, "Various Walk Cycles").
  - Walking motion: **Mahlazer** (LearnMMD).
  - Gestures: **Animation credits to pixiv Inc.'s VRoid Project** (VRMA_MotionPack).

### 4. Camera System
- **Wuthering Waves style camera (`ThirdPersonOrbitCamera.cs`)**:
  - Mouse orbit with frame-rate independent sensitivity (`0.12°` per pixel), pitch `-35°..70°`, starts behind the character at `12°`.
  - Scroll-wheel zoom (`1.6–8 m`, default `4.2 m`, `0.6 m` per notch; works with the Input System's uniform ±1-per-notch scroll and the older ±120 range, and even while the cursor is unlocked); the camera pulls closer when looking up so it never digs into the ground.
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
│   ├── Nino/                                  # Generated by Tools/AnimConvert + HumanoidClipBaker
│   │   ├── Walk_Nino.anim                     # Walk (MMD Normal walk, Nino-styled)
│   │   ├── Run_Nino.anim                      # Run (MMD Female run), also sprint at 1.35x
│   │   └── Gestures/                          # VRoid gestures (ModelPose = idle flourish)
│   ├── Candidates/                            # Other converted walks/runs, for comparison only
│   ├── X Bot@Female Standing Pose.fbx         # Single-frame version of the idle pose (not in the blend tree)
│   ├── X Bot@Female Walk.fbx                  # Old Mixamo walk (not in the blend tree)
│   ├── X Bot@Running.fbx                      # Old Mixamo run (not in the blend tree)
│   └── Female Locomotion Pack/                # Supplementary clips (strafes, turns)
│       ├── idle.fbx                           # Breathing idle (active)
│       └── jump.fbx                           # Jump_Takeoff / Jump_Air / Jump_Land
├── Editor/
│   ├── MixamoLocomotionSetup.cs               # Mixamo import (clean T-pose), speed measurement, controller build, 24fps capture
│   ├── HumanoidClipBaker.cs                   # Pose streams (Tools/AnimConvert) -> humanoid clips
│   ├── ClipPreviewSheet.cs                    # Edit-mode clip contact sheets
│   ├── ClothClippingFix.cs                    # Jacket hem rig, skirt weights & texture repair, covered-skirt springs, arm clearance
│   ├── RenderQualityUpgrade.cs                # MSAA, texture mips/BC7/aniso, skinned bounds, fast spring bones
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
│       ├── 5394265126170879566.BlendShapes/
│       └── ClothFix/                          # Generated: fixed Body mesh, repaired skirt textures
└── Scripts/
    ├── AnimeCharacterController.cs            # Movement and locomotion logic
    ├── ThirdPersonOrbitCamera.cs              # WuWa-style follow camera
    ├── DiscardRootMotion.cs                   # Drops root motion on the character's Animator
    ├── ArmClothClearance.cs                   # Keeps hands out of the jacket
    ├── JacketHemCloth.cs                      # Jacket hem physics with skirt/thigh collision
    ├── FastSpringBoneActivator.cs             # Burst job spring bones for the VRM model
    └── Dev/                                   # Editor-only diagnostics
        ├── LocomotionFrameCapture.cs          # 24fps capture + foot diagnostics
        ├── AnimShowcaseCapture.cs             # In-game clip comparison frames (for GIFs)
        ├── CharacterCloseupCapture.cs         # Close-up contact sheets (front/side/back)
        ├── ClothLayerAudit.cs                 # Cloth layer clipping per frame + contact sheets, all movement phases
        └── ClothClipProbe.cs                  # Measures skirt/hand clipping on the baked mesh
Tools/
└── AnimConvert/                               # MMD/VRMA -> humanoid pose streams (Python), see Animation System
    ├── convert_all.sh                         # Rebuilds every stream from the downloaded sources
    ├── vmd2clip.py, vrma2clip.py, qmath.py    # Converters
    ├── make_gifs.py                           # Comparison GIFs from AnimShowcaseCapture
    └── nino_rest.json                         # Nino's T-pose bone positions
```

## Scene Hierarchy

```
Player                          (CharacterController, AnimeCharacterController)
 └── Nino_Model                 (Animator → Nino_LocomotionController, Humanoid, DiscardRootMotion, ArmClothClearance, JacketHemCloth, FastSpringBoneActivator)
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
