# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles smooth character movement, aligning the character's forward direction with the camera's view.
  - Walk / Run / Sprint speed tiers (`3.0` / `6.5` / `10.0`)
  - Gravity-based jumping (`jumpHeight: 1.8`)
  - Rotation smoothing (`0.08s`)
- **Character Model — Nino Nakano (VRM)**: The active player avatar is **Nino Nakano**, imported via UniVRM.
  - Located at `Assets/Models/Nino Nakano/` (VRM source: `5394265126170879566.vrm`).
  - Instantiated in the scene as `Player > Nino_Model` with local transforms reset to identity (`pos 0,0,0 · rot 0,0,0 · scale 1,1,1`).
  - Humanoid `VrmAvatar` is valid and assigned to the `Animator` component.
  - `applyRootMotion = false` — movement is driven entirely by `AnimeCharacterController`.
  - 20 materials, all rendering correctly under URP (no missing/pink shaders).
- **CharacterController** (on `Player` root):
  - `Height: 1.6` · `Center Y: 0.8` · `Radius: 0.35`

### 2. Animation System
- **Mixamo Locomotion Pack** (`Assets/Animations/Female Locomotion Pack/`):
  - 10 humanoid FBX clips from Mixamo: idle, walking, running, jump, left/right strafe, left/right strafe walk, left/right turn.
  - All clips imported with `avatarSetup = CreateFromThisModel` to avoid "Transform hierarchy does not match" errors with the VRM skeleton.
  - Locomotion clips: `loopTime = true`, root motion baked into pose (Rotation, Y, XZ).
  - Jump clip: `loopTime = false`.
- **Animator Controller** (`Assets/Animations/Nino_LocomotionController.controller`):
  - **Parameters**: `Speed` (Float), `Jump` (Trigger), `SpecialIdle` (Trigger).
  - **Locomotion Blend Tree** (1D, driven by `Speed`):
    - `0.0` → idle
    - `2.5` → walking
    - `6.0` → running
    - Automatic thresholds disabled.
  - **Jump State**: Triggered by `Jump` parameter.
    - Locomotion → Jump: no exit time, 0.1s crossfade.
    - Jump → Locomotion: exit time @ 85%, 0.2s crossfade.
  - Assigned to `Nino_Model` Animator component.

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
│   └── Female Locomotion Pack/   # 10 Mixamo humanoid FBX clips
│       ├── idle.fbx
│       ├── walking.fbx
│       ├── running.fbx
│       ├── jump.fbx
│       ├── left strafe.fbx / left strafe walk.fbx
│       ├── right strafe.fbx / right strafe walk.fbx
│       ├── left turn.fbx / right turn.fbx
│       └── Nino_LocomotionController.controller
├── Editor/
│   └── FixMixamoLocomotionImports.cs  # One-shot import fixer tool
├── Models/
│   ├── HuoTao/              # Legacy Hu Tao model (no longer active in scene)
│   └── Nino Nakano/          # Active VRM avatar
│       ├── 5394265126170879566.vrm
│       ├── 5394265126170879566.prefab
│       ├── 5394265126170879566.Avatar/
│       ├── 5394265126170879566.Materials/
│       ├── 5394265126170879566.Meshes/
│       ├── 5394265126170879566.Textures/
│       └── 5394265126170879566.BlendShapes/
└── Scripts/
    ├── AnimeCharacterController.cs
    └── ThirdPersonOrbitCamera.cs
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
