"""Blender (VRM armature, Nino) -> pose streams for Assets/Editor/HumanoidClipBaker.cs.   Run INSIDE Blender (exec this file, then call the functions).

A pose stream is, per frame:  p = hips world position and  q[k] = world rotation of humanoid bone k relative to the T-pose, both in UNITY space
(x y z w quaternions), plus an optional 'blend' = {blend shape name: [value 0..1 per frame]} for the Face mesh.

Blender (right-handed, Z up, the model faces -Y)  ->  Unity (left-handed, Y up, the model faces +Z):   (x, y, z) -> (-x, z, -y)
Rotations convert as  R_unity = M R_blender M^T  (M = that axis matrix).  The same pose math as the toolkit's world-axis rig:
the delta of a bone is  R_pose * R_rest^-1  (world), which does not depend on the bones' local axes.

Typical use (clip name, Blender frame range, fps, loop):
    export_stream('Nino_Idle', range(1, 122), 24, True, OUT_DIR)
    export_face_action('Nino_Idle_Face', bpy.data.actions['Nino_Idle_Face'], range(1, 290), 24, True, FACE_KEYS)   # 12 s, mouth always closed
    export_custom_shapes(['Nino_cheek_puff', 'Nino_mouth_pout'], CUSTOM_PATH)
"""
import json
import os

import bpy
from mathutils import Matrix

M = Matrix(((-1.0, 0.0, 0.0), (0.0, 0.0, 1.0), (0.0, -1.0, 0.0)))
MT = M.transposed()
UNITY_OUT_DIR = r"C:\Users\valen\Wuwa_Clone\Tools\AnimConvert\out"
UNITY_CUSTOM_DIR = r"C:\Users\valen\Wuwa_Clone\Tools\AnimConvert\blendshapes"
# the shapes the idle / walk face loops animate (the idle face holds Fcl_MTH_Joy at 0: the mouth never opens while she stands)
FACE_KEYS = ['Fcl_EYE_Close', 'Fcl_EYE_Joy', 'Fcl_EYE_Fun', 'Fcl_MTH_Fun', 'Fcl_MTH_Joy', 'Fcl_BRW_Fun']

# VRM humanoid name (lowerCamel, as in the toolkit's H dict) -> HumanBodyBones name
HUMANOID = {
    'hips': 'Hips', 'spine': 'Spine', 'chest': 'Chest', 'upperChest': 'UpperChest', 'neck': 'Neck', 'head': 'Head',
    'leftEye': 'LeftEye', 'rightEye': 'RightEye',
    'leftShoulder': 'LeftShoulder', 'rightShoulder': 'RightShoulder', 'leftUpperArm': 'LeftUpperArm', 'rightUpperArm': 'RightUpperArm',
    'leftLowerArm': 'LeftLowerArm', 'rightLowerArm': 'RightLowerArm', 'leftHand': 'LeftHand', 'rightHand': 'RightHand',
    'leftUpperLeg': 'LeftUpperLeg', 'rightUpperLeg': 'RightUpperLeg', 'leftLowerLeg': 'LeftLowerLeg', 'rightLowerLeg': 'RightLowerLeg',
    'leftFoot': 'LeftFoot', 'rightFoot': 'RightFoot', 'leftToes': 'LeftToes', 'rightToes': 'RightToes',
}
for _side in ('left', 'right'):
    for _f in ('Thumb', 'Index', 'Middle', 'Ring', 'Little'):
        for _p in ('Proximal', 'Intermediate', 'Distal'):
            HUMANOID['%s%s%s' % (_side, _f, _p)] = '%s%s%s' % (_side.capitalize(), _f, _p)


def _bones(H):
    return [(HUMANOID[k], H[k]) for k in HUMANOID if k in H and H[k] in bpy.data.objects['Armature'].pose.bones]


def export_stream(name, frames, fps, loop, out_dir=UNITY_OUT_DIR, asset_dir='Assets/Animations/Nino', H=None, speed=0.0,
                  blend_keys=None, body=True, source='Blender'):
    """frames = Blender frame numbers (the pose / shape-key actions must already be assigned).  blend_keys: None = no face curves,
    'auto' = every shape key that moves, or a list of names.  body=False writes a face-only stream (no bones)."""
    arm = bpy.data.objects['Armature']
    face = bpy.data.objects['Face']
    sc = bpy.context.scene
    H = H or bpy.app.driver_namespace['NINO_C']['H']
    bl = _bones(H) if body else []
    rest = {b: (arm.matrix_world @ arm.data.bones[b].matrix_local).to_3x3().normalized() for _, b in bl}
    rest_inv = {b: r.inverted() for b, r in rest.items()}
    keys = face.data.shape_keys.key_blocks
    names = [k.name for k in keys if k.name != 'Basis']
    out_frames, blend_series = [], {n: [] for n in names}
    cur = sc.frame_current
    for f in frames:
        sc.frame_set(int(f))
        if body:
            pbs = arm.pose.bones
            hp = M @ (arm.matrix_world @ pbs[H['hips']].head)
            qs = []
            for _, b in bl:
                R = (arm.matrix_world @ pbs[b].matrix).to_3x3().normalized()
                q = (M @ (R @ rest_inv[b]) @ MT).to_quaternion()
                qs.append([round(q.x, 6), round(q.y, 6), round(q.z, 6), round(q.w, 6)])
            out_frames.append({'p': [round(hp.x, 6), round(hp.y, 6), round(hp.z, 6)], 'q': qs})
        else:
            out_frames.append({'p': [0.0, 0.0, 0.0], 'q': []})
        for n in names:
            blend_series[n].append(round(keys[n].value, 5))
    sc.frame_set(cur)
    blend = None
    if blend_keys:
        sel = names if blend_keys == 'auto' else list(blend_keys)
        blend = {n: blend_series[n] for n in sel if max(abs(v) for v in blend_series[n]) > 1e-4}
    stream = {'name': name, 'source': source, 'fps': fps, 'loop': bool(loop), 'speed': speed, 'outDir': asset_dir,
              'bones': [u for u, _ in bl], 'frames': out_frames}
    if blend is not None:
        stream['blend'] = blend
        stream['blendPath'] = 'Face'
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, name + '.json')
    with open(path, 'w') as fh:
        json.dump(stream, fh, separators=(',', ':'))
    return path, len(out_frames), (sorted(blend) if blend else [])


def export_face_action(name, action, frames, fps, loop, keys, out_dir=UNITY_OUT_DIR, asset_dir='Assets/Animations/Nino'):
    """Face-only stream sampled straight from a shape-key action's F-curves (no scene evaluation: fast, and the timeline does not move).
    keys = the shape names to write.  A constant-0 curve is kept on purpose: it pins the shape at 0 in Unity (e.g. the idle face keeps
    Fcl_MTH_Joy at 0 so the mouth stays closed) instead of leaving the value to whatever the other face state animates."""
    curves = {}
    for layer in action.layers:
        for strip in layer.strips:
            for bag in strip.channelbags:
                for fc in bag.fcurves:
                    if fc.data_path.startswith('key_blocks["') and fc.data_path.endswith('"].value'):
                        curves[fc.data_path[len('key_blocks["'):-len('"].value')]] = fc
    blend = {}
    for k in keys:
        fc = curves.get(k)
        blend[k] = [round(fc.evaluate(f), 5) if fc is not None else 0.0 for f in frames]
    n = len(list(frames))
    stream = {'name': name, 'source': 'Blender', 'fps': fps, 'loop': bool(loop), 'speed': 0.0, 'outDir': asset_dir, 'bones': [],
              'frames': [{'p': [0.0, 0.0, 0.0], 'q': []} for _ in range(n)], 'blend': blend, 'blendPath': 'Face'}
    os.makedirs(out_dir, exist_ok=True)
    path = os.path.join(out_dir, name + '.json')
    with open(path, 'w') as fh:
        json.dump(stream, fh, separators=(',', ':'))
    return path, n, sorted(blend)


def walk_ground_speed(frames, fps, H=None):
    """in-place walk -> ground speed (m/s) of the planted foot: median backward speed of the ankle over the frames where it is within 5 mm of its lowest point
    (central differences, so the frames next to the lift-off / touch-down do not skew it)"""
    arm = bpy.data.objects['Armature']
    sc = bpy.context.scene
    H = H or bpy.app.driver_namespace['NINO_C']['H']
    cur = sc.frame_current
    series = {'left': [], 'right': []}
    for f in frames:
        sc.frame_set(int(f))
        for sd in series:
            p = arm.matrix_world @ arm.pose.bones[H[sd + 'Foot']].head
            series[sd].append((p.y, p.z))
    sc.frame_set(cur)
    speeds = []
    for sd, s in series.items():
        n = len(s)
        zmin = min(z for _, z in s)
        for i in range(n):                                            # the cycle is closed (last frame == first): index modulo n - 1
            m = n - 1
            y0, y1 = s[(i - 1) % m][0], s[(i + 1) % m][0]
            if s[i % m][1] < zmin + 0.005 and s[(i - 1) % m][1] < zmin + 0.008 and s[(i + 1) % m][1] < zmin + 0.008:
                speeds.append(abs(y1 - y0) / 2.0 * fps)
    speeds.sort()
    return (speeds[len(speeds) // 2] if speeds else 0.0), speeds


def export_custom_shapes(names, path=None, mesh_name='Face'):
    """vertex deltas of custom Face shape keys, in the Unity MESH-LOCAL space of the imported VRM Face mesh, which is Blender's local space with Y and Z swapped
    (checked against the imported mesh: unity = (x, z, y)).  Also writes the basis positions in that space so the importer can match vertices by position."""
    ob = bpy.data.objects[mesh_name]
    kb = ob.data.shape_keys.key_blocks
    basis = kb['Basis']
    n = len(ob.data.vertices)
    out = {'mesh': 'Face (merged).baked', 'vertexCount': n,
           'basis': [round(c, 6) for v in basis.data for c in (v.co.x, v.co.z, v.co.y)], 'shapes': {}}
    for nm in names:
        k = kb[nm]
        idx, d = [], []
        for i in range(n):
            dx, dy, dz = k.data[i].co.x - basis.data[i].co.x, k.data[i].co.y - basis.data[i].co.y, k.data[i].co.z - basis.data[i].co.z
            if dx * dx + dy * dy + dz * dz > 1e-12:
                idx.append(i)
                d.extend([round(dx, 6), round(dz, 6), round(dy, 6)])
        out['shapes'][nm] = {'idx': idx, 'd': d}
    path = path or os.path.join(UNITY_CUSTOM_DIR, 'nino_face_custom.json')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, 'w') as fh:
        json.dump(out, fh, separators=(',', ':'))
    return path, {nm: len(v['idx']) for nm, v in out['shapes'].items()}
