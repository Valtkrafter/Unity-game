#!/usr/bin/env bash
# Rebuilds every pose stream in Tools/AnimConvert/out from the downloaded source motions.
# Then in Unity: Tools/Animation/Bake Pose Streams To Humanoid Clips, and
# Tools/Locomotion/3. Apply Speeds To Blend Tree And Scene.
#
# Sources (not in the repo): SRC defaults to ~/Downloads/TQQ animation
#   - tweekcrystal "Various Walk Cycles" (.vmd, credit: tweekcrystal)
#   - Mahlazer walking motion (.vmd, LearnMMD)
#   - VRoid Project VRMA_MotionPack (.vrma, "Animation credits to pixiv Inc.'s VRoid Project")
set -e
cd "$(dirname "$0")"
export PYTHONIOENCODING=utf-8
SRC="${SRC:-$HOME/Downloads/TQQ animation}"
W="$SRC/motion_data_dl_various_walk_cycles_by_tweekcrystal_d53gco2/Walk Cycles"
mkdir -p out vrma

# --- Nino's in-game locomotion (Assets/Animations/Nino) ---------------------------------------------
# Walk: tweekcrystal's feminine "Normal walk" (feet on a line, heel kick) with Nino's posture:
# smaller kick, more hip sway, chest out, chin up.
python vmd2clip.py "$W/Normal walk.vmd" out/Walk_Nino.json --name Walk_Nino \
  --lift 0.75 --sway 1.5 --chest -4 --chin -3 --outdir Assets/Animations/Nino &
# Run: "Female run" (authored on a taller model, so strides are scaled to 90%).
python vmd2clip.py "$W/Female run.vmd" out/Run_Nino.json --name Run_Nino \
  --scale 0.9 --chin -2 --outdir Assets/Animations/Nino &

# --- Untouched candidates for comparison (Assets/Animations/Candidates) ------------------------------
python vmd2clip.py "$W/Normal walk.vmd" out/Walk_MMD_Normal.json --name Walk_MMD_Normal &
python vmd2clip.py "$W/Lazy walk.vmd" out/Walk_MMD_Lazy.json --name Walk_MMD_Lazy &
python vmd2clip.py "$W/Cool guy walk.vmd" out/Walk_MMD_Cool.json --name Walk_MMD_Cool --scale 0.9 &
python vmd2clip.py "$W/Female run.vmd" out/Run_MMD_Female.json --name Run_MMD_Female --scale 0.9 &
python vmd2clip.py "$W/Male run.vmd" out/Run_MMD_Male.json --name Run_MMD_Male --scale 0.9 &
python vmd2clip.py "$SRC/Mahlazer_Walking_Motion_951Frames/Mahlazer Walking Motion 951Frames.vmd" \
  out/Walk_MMD_Mahlazer.json --name Walk_MMD_Mahlazer --start 444 --period 39 --dy -0.035 &
wait

# --- VRoid gestures (Assets/Animations/Nino/Gestures) ------------------------------------------------
# ModelPose (hand on hip, touches her hair) is Nino's idle flourish (SpecialIdle state); the others are
# ready for the interaction system.
unzip -o -q -j "$SRC/VRMA_MotionPack.zip" "VRMA_MotionPack/vrma/*" -d vrma
i=0
for n in ShowFullBody Greeting PeaceSign Shoot Spin ModelPose Squat; do
  i=$((i+1))
  python vrma2clip.py "vrma/VRMA_0$i.vrma" "out/VRoid_$n.json" --name "VRoid_$n" --outdir Assets/Animations/Nino/Gestures &
done
wait
