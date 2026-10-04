"""VRM Animation (.vrma) -> humanoid pose stream (JSON) for HumanoidClipBaker.cs.

A .vrma is a glTF whose node hierarchy is a humanoid in T-pose (rest) plus rotation/translation channels.
Each humanoid bone's world rotation relative to its rest world rotation is the delta we bake onto Nino's
T-pose. glTF is right-handed; converting to Unity negates X (UniGLTF convention), and both face +Z.
"""
import json, struct, os, argparse
import numpy as np
from qmath import *

VRM_TO_UNITY = {
    'hips': 'Hips', 'spine': 'Spine', 'chest': 'Chest', 'upperChest': 'UpperChest', 'neck': 'Neck', 'head': 'Head',
    'leftEye': 'LeftEye', 'rightEye': 'RightEye', 'jaw': 'Jaw',
}
for side, us in (('left', 'Left'), ('right', 'Right')):
    for a, b in (('UpperLeg', 'UpperLeg'), ('LowerLeg', 'LowerLeg'), ('Foot', 'Foot'), ('Toes', 'Toes'),
                 ('Shoulder', 'Shoulder'), ('UpperArm', 'UpperArm'), ('LowerArm', 'LowerArm'), ('Hand', 'Hand'),
                 ('ThumbMetacarpal', 'ThumbProximal'), ('ThumbProximal', 'ThumbIntermediate'), ('ThumbDistal', 'ThumbDistal')):
        VRM_TO_UNITY[side + a] = us + b
    for fing in ('Index', 'Middle', 'Ring', 'Little'):
        for seg in ('Proximal', 'Intermediate', 'Distal'):
            VRM_TO_UNITY[side + fing + seg] = us + fing + seg

COMP = {5120: 'b', 5121: 'B', 5122: 'h', 5123: 'H', 5125: 'I', 5126: 'f'}
NCOMP = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4, 'MAT4': 16}

def load_glb(path):
    b = open(path, 'rb').read()
    jl = struct.unpack_from('<I', b, 12)[0]
    j = json.loads(b[20:20+jl])
    o = 20 + jl
    bl = struct.unpack_from('<I', b, o)[0]
    return j, b[o+8:o+8+bl]

def accessor(j, binb, i):
    a = j['accessors'][i]; bv = j['bufferViews'][a['bufferView']]
    nc = NCOMP[a['type']]; fmt = COMP[a['componentType']]
    off = bv.get('byteOffset', 0) + a.get('byteOffset', 0)
    size = struct.calcsize(fmt)
    stride = bv.get('byteStride', size * nc)
    out = np.zeros((a['count'], nc))
    for k in range(a['count']):
        out[k] = struct.unpack_from('<' + fmt * nc, binb, off + k * stride)
    if a.get('normalized') and fmt != 'f':
        out = out / {'b': 127, 'B': 255, 'h': 32767, 'H': 65535}[fmt]
    return out

def sample(times, vals, interp, t, is_rot):
    if t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = np.searchsorted(times, t) - 1
    if interp == 'STEP': return vals[i]
    u = (t - times[i]) / (times[i+1] - times[i])
    if is_rot: return qslerp(qnorm(vals[i]), qnorm(vals[i+1]), u)
    return vals[i] + (vals[i+1] - vals[i]) * u

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('vrma'); ap.add_argument('out'); ap.add_argument('--name', required=True)
    ap.add_argument('--fps', type=float, default=30)
    ap.add_argument('--loop', action='store_true')
    ap.add_argument('--start', type=float, default=0.0); ap.add_argument('--end', type=float)
    ap.add_argument('--outdir', help='Unity asset folder for the baked clip')
    a = ap.parse_args()
    rest = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'nino_rest.json')))['bones']
    j, binb = load_glb(a.vrma)
    nodes = j['nodes']
    parent = {}
    for i, nd in enumerate(nodes):
        for c in nd.get('children', []): parent[c] = i
    human = j['extensions']['VRMC_vrm_animation']['humanoid']['humanBones']
    hb = {VRM_TO_UNITY[k]: v['node'] for k, v in human.items() if k in VRM_TO_UNITY}
    restT = {i: np.array(nd.get('translation', [0, 0, 0]), float) for i, nd in enumerate(nodes)}
    restR = {i: np.array(nd.get('rotation', [0, 0, 0, 1]), float) for i, nd in enumerate(nodes)}
    restS = {i: np.array(nd.get('scale', [1, 1, 1]), float) for i, nd in enumerate(nodes)}
    anim = j['animations'][0]
    chans = {}
    dur = 0
    for ch in anim['channels']:
        s = anim['samplers'][ch['sampler']]
        times = accessor(j, binb, s['input'])[:, 0]; vals = accessor(j, binb, s['output'])
        chans[(ch['target']['node'], ch['target']['path'])] = (times, vals, s.get('interpolation', 'LINEAR'))
        dur = max(dur, times[-1])

    def world(t):
        Wr, Wp = {}, {}
        def get(i):
            if i in Wr: return
            tr = restT[i]; r = restR[i]
            if (i, 'translation') in chans: tr = sample(*chans[(i, 'translation')][:2], chans[(i, 'translation')][2], t, False)
            if (i, 'rotation') in chans: r = qnorm(sample(*chans[(i, 'rotation')][:2], chans[(i, 'rotation')][2], t, True))
            if i in parent:
                p = parent[i]; get(p)
                Wr[i] = qmul(Wr[p], r); Wp[i] = Wp[p] + qrot(Wr[p], tr * restS[p])
            else:
                Wr[i] = r; Wp[i] = tr
        for i in range(len(nodes)): get(i)
        return Wr, Wp

    def rest_world():
        Wr, Wp = {}, {}
        def get(i):
            if i in Wr: return
            if i in parent:
                p = parent[i]; get(p)
                Wr[i] = qmul(Wr[p], restR[i]); Wp[i] = Wp[p] + qrot(Wr[p], restT[i] * restS[p])
            else:
                Wr[i] = restR[i]; Wp[i] = restT[i]
        for i in range(len(nodes)): get(i)
        return Wr, Wp

    R0, P0 = rest_world()
    hips_node = hb['Hips']
    scale = rest['Hips'][1] / P0[hips_node][1]
    to_u_q = lambda q: np.array([q[0], -q[1], -q[2], q[3]])
    to_u_p = lambda p: np.array([-p[0], p[1], p[2]])
    names = [n for n in hb if n in rest]
    end = a.end if a.end is not None else dur
    nfr = int(round((end - a.start) * a.fps)) + 1
    out = dict(name=a.name, fps=a.fps, loop=a.loop, inPlace=True, source=os.path.basename(a.vrma), bones=names, frames=[])
    if a.outdir: out['outDir'] = a.outdir
    for k in range(nfr):
        t = a.start + k / a.fps
        Wr, Wp = world(t)
        hp = to_u_p(Wp[hips_node]) * scale
        qs = []
        for n in names:
            i = hb[n]
            d = qmul(Wr[i], qinv(R0[i]))
            qs.append([round(float(x), 6) for x in qnorm(to_u_q(d))])
        out['frames'].append(dict(p=[round(float(x), 5) for x in hp], q=qs))
    json.dump(out, open(a.out, 'w', encoding='utf-8'))
    print('wrote', a.out, nfr, 'frames', 'dur', round(dur, 2), 'hips scale', round(scale, 3), 'bones', len(names))

if __name__ == '__main__':
    main()
