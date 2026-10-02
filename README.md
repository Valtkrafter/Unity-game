# Wuwa Clone - Unity Game

This repository contains the source code for a 3D third-person Unity game featuring an anime-style character. The goal is to build an action RPG experience similar to Wuthering Waves (Wuwa).

## Current Features & State

### 1. Character & Movement
- **Anime Character Controller (`AnimeCharacterController.cs`)**: Handles smooth character movement, aligning the character's forward direction with the camera's view.
- **Character Model (Hu Tao)**: The player character is Hu Tao. 
  - *Technical Note:* The model scale is set to `(10, 10, 10)` to correctly match the 1.6-meter tall physical collision capsule. 
  - A rogue camera originally embedded in the model has been removed to prevent rendering interference.

### 2. Camera System
- **Third-Person Orbit Camera (`ThirdPersonOrbitCamera.cs`)**: 
  - Orbits smoothly around the player character.
  - Supports mouse look for pitch and yaw.
  - Supports zooming in and out using the mouse scroll wheel.
  - Prevents clipping through geometry by repositioning closer to the player when obstacles block the line of sight.

### 3. Environment
- A basic testing arena (`Ground_Arena`) is set up for movement and camera collision testing.

---

*Note: This README is automatically updated by the Antigravity AI assistant to maintain the current context and list of features as development progresses.*
