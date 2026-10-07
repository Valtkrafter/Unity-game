# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles responsive character movement, aligning the character's forward direction with the camera's planar view.
  - **Walk is the standard, Shift runs**: moving with WASD walks at `1.33 m/s`; holding **Shift** runs at `2.26 m/s` (no more Ctrl-to-walk / Shift-to-sprint). Both are the natural ground speeds of the Blender walk clip, measured from the planted foot (`Tools/Locomotion/2. Measure Natural Clip Speeds`), so her feet stay locked to the floor (planted-foot slide avg `0.02 m/s` walking, `0.04 m/s` running). Until there is a real run clip, the run is the same walk cycle played `1.7x` faster.
  - **Special idle**: after `8 s` standing still (then every `20 s`), sets the `SpecialIdle` trigger: Nino's **Hmph** (arms crossed, head whips away, puffed cheeks and pout, a glare back, then she lets go).
  - **Smooth starts & stops**: acceleration `9 m/s²` and deceleration `7 m/s²`. Movement speed and the Animator `Speed` parameter share the same ramped value, so the feet stay in sync with the blend tree.
  - Cached parameter hashes (`Speed`, `IsGrounded`, `VerticalVelocity`).
  - Static input overrides (`UseInputOverride`, `InputOverride`, `SprintOverride` = Shift held = run, `JumpOverride`) for automated testing.
  - Gravity-based jumping (`jumpHeight: 1.8`, `gravity: -25.0`). There is no jump animation at the moment (all old clips were removed when the Blender animations replaced them), so Space only moves her physically and `jumpAnimationAvailable` stays off; turn it on once a Blender jump set is in the Animator. `AirProgress` (0 = takeoff, 0.5 = apex, 1 = landing) is still set from the vertical velocity.
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
  - **Measured** with `ClothLayerAudit` (every frame at 60 fps of idle, walk, run, start, stop and a 180° turn with the Blender clips): legs through the jacket `0` in every phase; skin through the skirt at most 1 vertex, 0.3 cm; skirt through the jacket at most 8 vertices, ≤ 0.9 cm, at the jacket's front edge (while running; walking at most 4 vertices, 0.4 cm); idle and stop fully clean. The back of the skirt never shows skin. (The jump phases come back with a Blender jump set.)
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
All of Nino's animation comes from **Blender** (the VRM armature, `Blender_Animations/`): idle, walk and the special idle (Hmph). Every older clip (Unity-chan, Mixamo, MMD, VRoid gestures, Kevin Iglesias, AnimeGirlIdle) was moved out of the project on 2026-10-07 into `C:\Users\valen\Wuwa_Clone_OldAnimations_Backup\` (same folder layout, nothing deleted).
- **Clips** (`Assets/Animations/Nino/`, 24 fps humanoid clips made by `HumanoidClipBaker` from Blender pose streams):
  - `Nino_Idle` (5 s loop, standing pose with breathing and weight shift)
  - `Nino_Walk` (1 s loop, `1.33 m/s`, in place; every stream is lowered 3 cm because the model origin sits 3 cm above the floor in the scene)
  - `Nino_Special_Hmph` (5 s, one-shot, starts and ends on the idle pose; body + the face curves `Fcl_EYE_Close`, `Fcl_EYE_Angry`, `Fcl_BRW_Angry`, `Fcl_MTH_Small`, `Fcl_MTH_Angry` and the custom `Nino_cheek_puff` / `Nino_mouth_pout`)
  - `Nino_Idle_Face` (12 s loop, face only, played while she stands: blinks at irregular times, one closed-mouth "^_^" and a slowly moving closed smile. **The mouth never opens**: `Fcl_MTH_Joy`, the open-mouth smile, is held at 0 on purpose. The loop is 12 s, so it does not visibly repeat while she waits for the special idle)
  - `Nino_Face` (3 s loop, face only, played while she moves: blinks and the happy walk smile, which does open the mouth once per loop)
- **Blender -> Unity pipeline**:
  1. `Tools/AnimConvert/blender_export_streams.py` (run inside Blender) writes pose streams to `Tools/AnimConvert/out/*.json`: per frame the hips position and each humanoid bone's world rotation relative to the T-pose (Blender -> Unity axes: `(x, y, z) -> (-x, z, -y)`), plus the face blend shape weights (`export_face_action` writes a face-only stream straight from a shape-key action, e.g. `Nino_Idle_Face`). It also exports the custom shape-key deltas to `Tools/AnimConvert/blendshapes/nino_face_custom.json`.
  2. `Tools/Animation/Bake Pose Streams To Humanoid Clips` (`HumanoidClipBaker.cs`) bakes the streams into `.anim` clips on Nino's own avatar (muscles, root and IK goal curves, `blendShape.*` curves on `Face`; a stream without bones becomes a face-only clip; blend shape curves use no-overshoot tangents so weights stay in 0..100).
  3. `Tools/Character/Add Custom Face Blend Shapes (from Blender)` (`NinoFaceBlendShapes.cs`) adds `Nino_cheek_puff` and `Nino_mouth_pout` to the VRM Face mesh (vertices matched by position, appended after the 57 VRoid shapes). Re-run it after re-importing the VRM.
  4. `Tools/Locomotion/1. Build Animator From Blender Clips` (`NinoLocomotionSetup.cs`) measures the walk's ground speed, rebuilds the Animator Controller in place and writes the speeds into the scene.
- **Animator Controller** (`Assets/Animations/Nino_LocomotionController.controller`, assigned on `Nino_Model`):
  - **Parameters**: `Speed` (Float), `SpecialIdle` (Trigger), `Jump` (Trigger, unused for now), `VerticalVelocity` (Float), `IsGrounded` (Bool), `AirProgress` (Float).
  - **Base layer**: `Locomotion` blend tree on `Speed`: `0` idle, `1.33` walk, `2.26` walk at `1.7x` (run). `SpecialIdle` (tag `Special`, the Hmph): `Locomotion -> SpecialIdle` on the trigger while `Speed < 0.05` (0.3 s), back to `Locomotion` at 88% of the clip (0.45 s) or immediately when she starts moving.
  - **Face layer** (masked to the Face mesh by `Nino_FaceOnly.mask`): `FaceIdle` (`Nino_Idle_Face`, mouth always closed) while standing and `FaceMove` (`Nino_Face`) while moving, switched at `Speed` 0.1 with a 0.3 s crossfade. `AnimeCharacterController` fades the layer weight to 0 while a `Special`-tagged state plays, so the special's own face shows, and back to 1 afterwards.
- **Checks** (Play Mode, Tools/Locomotion/3 and 4, `ClothLayerAudit`): walk ground speed `1.33 m/s`, planted-foot slide avg `0.02 m/s`, soles on the floor (median `0.0 cm`); run `2.26 m/s`, slide avg `0.04 m/s`; legs through the jacket `0` in every phase, skirt through jacket at most 8 vertices / 0.9 cm at the jacket's front edge; the Hmph runs through the real Animator (Special state, face layer hand-over, puff and pout reach 98 and 100).
- **Capture tools** (editor-only, Play Mode): `Tools/Locomotion/3. Capture 24fps Sequence` (idle, walk, run, start, stop, camera: foot slide, sole height, contact sheets in `Captures/Locomotion_24fps/`) `Tools/Locomotion/4. Capture Special Idle` (face close-up and body frames of the Hmph in `Captures/SpecialIdle/`) and `Tools/Locomotion/5. Capture Idle Face` (7.5 s standing, 2.5 s walking, then standing: face layer state, blend shape weights and the real mouth height measured on the skinned mesh, face close-ups in `Captures/IdleFace/`; standing: open-mouth shape `0`, mouth height 24.5-26.6 mm where open is 30+ mm).
- **Still to make in Blender**: a real run, jump / air / landing, idle<->walk transitions, the other specials (Point, Shy).
- **Credits**: animation authored in Blender for this project. The third-party packs that were removed: Unity-chan (Unity Technologies Japan), Mixamo, Kevin Iglesias Human Animations, AnimeGirlIdleAnimations, MMD walks (tweekcrystal, Mahlazer), VRoid Project gestures.

### 4. Camera System
- **Wuthering Waves style camera (`ThirdPersonOrbitCamera.cs`)**:
  - Mouse orbit with frame-rate independent sensitivity (`0.12°` per pixel), pitch `-35°..70°`, starts behind the character at `12°`.
  - Scroll-wheel zoom (`1.6–8 m`, default `4.2 m`, `0.6 m` per notch; works with the Input System's uniform ±1-per-notch scroll and the older ±120 range, and even while the cursor is unlocked); the camera pulls closer when looking up so it never digs into the ground.
  - Tight horizontal follow (`0.05 s`) with a softer vertical follow (`0.18 s`), so jumps and steps don't jolt the view.
  - Auto-recenter: while running sideways the camera swings round behind the character (up to `70°/s`), and pitch eases back to `12°`. Pauses for `0.6 s` after any mouse input and never fights you when running towards the camera.
  - FOV `50°`, widening by `5°` while running (Shift).
  - Collision: snaps in front of walls instantly, eases back out over `0.25 s`.
  - Cursor locked in play; hold **Left Alt** to free it, **Esc** unlocks, click relocks.

### 5. Environment
- A basic testing arena (`Ground_Arena`) is set up for movement and camera collision testing.

## Project Structure

```
Assets/
├── Animations/
│   ├── Nino_LocomotionController.controller  # Locomotion blend tree + SpecialIdle + Face layer
│   └── Nino/                                  # Baked from Blender by HumanoidClipBaker
│       ├── Nino_Idle.anim, Nino_Walk.anim, Nino_Special_Hmph.anim, Nino_Idle_Face.anim, Nino_Face.anim
│       └── Nino_FaceOnly.mask                 # Face layer mask
├── Editor/
│   ├── NinoLocomotionSetup.cs                 # Animator builder, speed measurement, 24fps / special-idle capture menus
│   ├── NinoFaceBlendShapes.cs                 # Custom Face blend shapes (puff, pout) from Blender
│   ├── HumanoidClipBaker.cs                   # Pose streams (Tools/AnimConvert) -> humanoid clips (+ face curves)
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
| Move (default) | Walk |
| Shift (hold) + Move | Run |
| Space | Jump (physics only, no jump animation yet) |
| Mouse | Camera orbit (pitch / yaw) |
| Scroll Wheel | Camera zoom |
| Left Alt (hold) | Free the cursor |
| Esc / Left Click | Unlock / relock the cursor |

---

*Note: This README is automatically updated by the Antigravity AI assistant to maintain the current context and list of features as development progresses.*
