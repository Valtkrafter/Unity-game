"""MMD .vmd motion -> humanoid pose stream (JSON) for HumanoidClipBaker.cs.

Rebuilds an MMD-standard skeleton with Nino's own proportions (Unity coordinates, model facing +Z),
evaluates the VMD bezier tracks, solves the leg IK (足ＩＫ / つま先ＩＫ) analytically, and writes per-frame
world rotations of the humanoid bones relative to Nino's T-pose plus the hips position.
MMD's rest pose has the arms angled down (A-pose); ARM_REST_DEG compensates for that.
"""
import json, struct, os, argparse
import numpy as np
from qmath import *

MMD_UNIT = 0.08          # metres per MMD unit
ARM_REST_DEG = 37.0      # MMD rest-pose arm angle below horizontal

# ---------------------------------------------------------------- VMD parsing / evaluation
def parse_vmd(path):
    b = open(path, 'rb').read()
    n = struct.unpack_from('<I', b, 50)[0]; o = 54
    tracks = {}
    for _ in range(n):
        name = b[o:o+15].split(b'\0')[0].decode('shift_jis', 'replace')
        fr, = struct.unpack_from('<I', b, o+15)
        p = struct.unpack_from('<3f', b, o+19); q = struct.unpack_from('<4f', b, o+31)
        ip = b[o+47:o+111]
        tracks.setdefault(name, []).append((fr, np.array(p), qnorm(np.array(q)), ip))
        o += 111
    for k in tracks:
        d = {}
        for key in tracks[k]: d[key[0]] = key   # last key wins on duplicate frames
        tracks[k] = sorted(d.values(), key=lambda x: x[0])
    return tracks

def bezier(x1, y1, x2, y2, t):
    if x1 == y1 and x2 == y2: return t
    lo, hi = 0.0, 1.0
    for _ in range(25):
        s = (lo + hi) / 2
        x = 3*(1-s)**2*s*x1 + 3*(1-s)*s*s*x2 + s**3
        if x < t: lo = s
        else: hi = s
    s = (lo + hi) / 2
    return 3*(1-s)**2*s*y1 + 3*(1-s)*s*s*y2 + s**3

def eval_track(keys, f):
    """(pos, quat) in MMD space at frame f."""
    if not keys: return np.zeros(3), IDENT.copy()
    if f <= keys[0][0]: return keys[0][1], keys[0][2]
    if f >= keys[-1][0]: return keys[-1][1], keys[-1][2]
    lo, hi = 0, len(keys) - 1
    while hi - lo > 1:
        m = (lo + hi) // 2
        if keys[m][0] <= f: lo = m
        else: hi = m
    k0, k1 = keys[lo], keys[hi]
    t = (f - k0[0]) / (k1[0] - k0[0])
    ip = k1[3]   # interpolation is stored on the destination key
    def ch(i):
        return bezier(ip[i]/127, ip[4+i]/127, ip[8+i]/127, ip[12+i]/127, t)
    p = np.array([k0[1][a] + (k1[1][a] - k0[1][a]) * ch(a) for a in range(3)])
    q = qslerp(k0[2], k1[2], ch(3))
    return p, q

# MMD models face -Z; rotate 180 degrees about Y so they face +Z (both systems are left-handed).
def to_unity_pos(p): return np.array([-p[0], p[1], -p[2]]) * MMD_UNIT
def to_unity_quat(q): return np.array([-q[0], q[1], -q[2], q[3]])

# ---------------------------------------------------------------- skeleton
FINGERS = [('親指', ['０', '１', '２'], 'Thumb', ['Proximal', 'Intermediate', 'Distal']),
           ('人指', ['１', '２', '３'], 'Index', ['Proximal', 'Intermediate', 'Distal']),
           ('中指', ['１', '２', '３'], 'Middle', ['Proximal', 'Intermediate', 'Distal']),
           ('薬指', ['１', '２', '３'], 'Ring', ['Proximal', 'Intermediate', 'Distal']),
           ('小指', ['１', '２', '３'], 'Little', ['Proximal', 'Intermediate', 'Distal'])]

def build_skeleton(rest):
    P = lambda n: np.array(rest[n][:3])
    hips = P('Hips'); spine = P('Spine')
    bones = {}   # name -> (parent, rest position)
    def add(n, parent, pos): bones[n] = (parent, np.array(pos, float))
    add('全ての親', None, [0, 0, 0])
    add('センター', '全ての親', [0, hips[1] * 0.7, 0])
    add('グルーブ', 'センター', [0, hips[1] * 0.7, 0])
    add('腰', 'グルーブ', spine)
    add('上半身', '腰', spine)
    add('上半身2', '上半身', P('Chest'))
    add('首', '上半身2', P('Neck'))
    add('頭', '首', P('Head'))
    add('下半身', '腰', spine)
    for side, us in (('左', 'Left'), ('右', 'Right')):
        add(side+'肩P', '上半身2', P(us+'Shoulder'))
        add(side+'肩', side+'肩P', P(us+'Shoulder'))
        add(side+'腕', side+'肩', P(us+'UpperArm'))
        add(side+'腕捩', side+'腕', (P(us+'UpperArm') + P(us+'LowerArm')) / 2)
        add(side+'ひじ', side+'腕捩', P(us+'LowerArm'))
        add(side+'手捩', side+'ひじ', (P(us+'LowerArm') + P(us+'Hand')) / 2)
        add(side+'手首', side+'手捩', P(us+'Hand'))
        for jp, nums, en, segs in FINGERS:
            parent = side+'手首'
            for num, seg in zip(nums, segs):
                n = side + jp + num
                add(n, parent, P(us+en+seg)); parent = n
        add(side+'足', '下半身', P(us+'UpperLeg'))
        add(side+'ひざ', side+'足', P(us+'LowerLeg'))
        add(side+'足首', side+'ひざ', P(us+'Foot'))
        add(side+'つま先', side+'足首', P(us+'Toes'))
        foot = P(us+'Foot')
        add(side+'足IK親', '全ての親', [foot[0], 0, foot[2]])
        add(side+'足ＩＫ', side+'足IK親', foot)
        add(side+'つま先ＩＫ', side+'足ＩＫ', P(us+'Toes'))
    order = []
    def visit(n):
        if n in order: return
        p = bones[n][0]
        if p: visit(p)
        order.append(n)
    for n in bones: visit(n)
    return bones, order

def humanoid_map():
    """humanoid bone -> (MMD source bone, arm-rest correction side)"""
    m = {'Hips': ('下半身', None), 'Spine': ('上半身', None), 'Chest': ('上半身2', None),
         'UpperChest': ('上半身2', None), 'Neck': ('首', None), 'Head': ('頭', None)}
    for side, us in (('左', 'Left'), ('右', 'Right')):
        m[us+'Shoulder'] = (side+'肩', None)
        m[us+'UpperArm'] = (side+'腕', us); m[us+'LowerArm'] = (side+'ひじ', us); m[us+'Hand'] = (side+'手首', us)
        for jp, nums, en, segs in FINGERS:
            for num, seg in zip(nums, segs):
                m[us+en+seg] = (side+jp+num, us)
        m[us+'UpperLeg'] = (side+'足', None); m[us+'LowerLeg'] = (side+'ひざ', None)
        m[us+'Foot'] = (side+'足首', None); m[us+'Toes'] = (side+'足首', None)
    return m

def arm_rest(deg):
    return {'Left': qaxis([0, 0, 1], np.radians(deg)), 'Right': qaxis([0, 0, 1], -np.radians(deg))}

# ---------------------------------------------------------------- pose solve
def qpow(q, k):
    """Scale a rotation's angle by k."""
    q = q if q[3] >= 0 else -q
    ang = 2 * np.arccos(min(1.0, q[3]))
    if ang < 1e-6: return IDENT.copy()
    return qaxis(q[:3], ang * k)

class Style:
    """Character tweaks applied while solving: foot lift, hip sway, posture."""
    def __init__(self, lift=1.0, sway=1.0, chest=0.0, chin=0.0, center_dy=0.0):
        self.lift, self.sway, self.chest, self.chin, self.center_dy = lift, sway, chest, chin, center_dy

def solve_frame(tracks, bones, order, f, trans_scale, stats, style=None, foot_dy=None):
    style = style or Style()
    W = {}; Pw = {}
    for n in order:
        parent, rest = bones[n]
        p, q = eval_track(tracks.get(n), f)
        t = to_unity_pos(p) * trans_scale; r = to_unity_quat(q)
        if n == 'センター': t = t + np.array([0, style.center_dy, 0])
        if n.endswith('足ＩＫ') or n.endswith('つま先ＩＫ'): t = t * np.array([1, style.lift, 1])
        if foot_dy and n in ('左足ＩＫ', '右足ＩＫ'): t = t + np.array([0, foot_dy[n[0]], 0])
        if n == '下半身' and style.sway != 1.0: r = qpow(r, style.sway)
        # +X rotation leans forward in Unity space, so negative values open the chest / lift the chin
        if n == '上半身' and style.chest: r = qmul(r, qaxis([1, 0, 0], np.radians(style.chest)))
        if n == '頭' and style.chin: r = qmul(r, qaxis([1, 0, 0], np.radians(style.chin)))
        if n in ('左足', '右足', '左ひざ', '右ひざ'): r = IDENT   # driven by IK
        if parent is None:
            W[n] = r; Pw[n] = rest + t
        else:
            W[n] = qmul(W[parent], r)
            Pw[n] = Pw[parent] + qrot(W[parent], rest - bones[parent][1] + t)
    for side in ('左', '右'):
        hipn, kneen, anklen, toen = side+'足', side+'ひざ', side+'足首', side+'つま先'
        u1 = bones[kneen][1] - bones[hipn][1]; u2 = bones[anklen][1] - bones[kneen][1]
        H = Pw[hipn]; T = Pw[side+'足ＩＫ']
        F0 = W[hipn]
        d = np.linalg.norm(T - H)
        reach = lambda th: np.linalg.norm(u1 + qrot(qaxis([1, 0, 0], th), u2))
        dmax = reach(np.radians(0.5)); dmin = reach(np.radians(165))
        if d > dmax * 1.005: stats['overreach'] = stats.get('overreach', 0) + 1
        d = min(max(d, dmin), dmax)
        lo, hi = np.radians(0.5), np.radians(165)
        for _ in range(40):
            mid = (lo + hi) / 2
            if reach(mid) > d: lo = mid
            else: hi = mid
        th = (lo + hi) / 2
        a_local = u1 + qrot(qaxis([1, 0, 0], th), u2)
        v_local = qrot(qinv(F0), T - H)
        Wh = qmul(F0, qfromto(a_local, v_local))   # minimal swing from the FK pose, like MMD's CCD
        Wk = qmul(Wh, qaxis([1, 0, 0], th))
        W[hipn] = Wh; W[kneen] = Wk
        Pw[kneen] = H + qrot(Wh, u1); Pw[anklen] = Pw[kneen] + qrot(Wk, u2)
        stats.setdefault('knee', []).append(np.degrees(th))
        _, qa = eval_track(tracks.get(anklen), f)
        Wa0 = qmul(Wk, to_unity_quat(qa))
        toe_off = bones[toen][1] - bones[anklen][1]
        TT = Pw[side+'つま先ＩＫ']
        W[anklen] = qmul(qfromto(qrot(Wa0, toe_off), TT - Pw[anklen]), Wa0)
        Pw[toen] = Pw[anklen] + qrot(W[anklen], toe_off)
    return W, Pw

def hips_world(W, Pw, bones, rest_hips):
    piv = bones['下半身'][1]
    return Pw['下半身'] + qrot(W['下半身'], rest_hips - piv)

def sole_points(rest):
    """Heel and toe-tip points under Nino's shoes, relative to each ankle (rest pose, foot flat on y=0)."""
    pts = {}
    for side, us in (('左', 'Left'), ('右', 'Right')):
        a = np.array(rest[us+'Foot'][:3]); t = np.array(rest[us+'Toes'][:3])
        pts[side] = (np.array([a[0], 0, a[2] - 0.03]) - a, np.array([t[0], 0, t[2] + 0.045]) - a)
    return pts

def contact_height(W, Pw, side, pts):
    ank = Pw[side+'足首']; R = W[side+'足首']
    return min(ank[1] + qrot(R, pts[side][0])[1], ank[1] + qrot(R, pts[side][1])[1])

def ground_feet(solve, nframes, pts, iters=4, near=0.035, far=0.07):
    """Plant the support foot: lower/raise each foot IK target so its lowest sole point touches y=0 while the
    foot is near the ground, fading out as it lifts into the swing (MMD targets fit the author's model, not Nino)."""
    dy = {s: np.zeros(nframes) for s in ('左', '右')}
    for _ in range(iters):
        frames = [solve(f, {s: dy[s][f] for s in dy}) for f in range(nframes)]
        for s in dy:
            c = np.array([contact_height(W, Pw, s, pts) for W, Pw in frames])
            ank = np.array([Pw[s+'足首'] for W, Pw in frames])
            v = np.zeros(nframes)
            v[1:-1] = np.linalg.norm((ank[2:] - ank[:-2])[:, [0, 2]], axis=1) * 15   # m/s at 30 fps
            w = np.clip((far - c) / (far - near), 0, 1)
            # A foot that has stopped is planted (sole on y = 0); a moving foot is never pulled down (no early
            # touchdown / skid on heel strike) and keeps 1 cm of clearance so a low swing doesn't scrape the floor.
            still = np.clip((0.6 - v) / 0.35, 0, 1)
            corr = still * (-c * w) + (1 - still) * np.maximum(0.0, 0.01 - c)
            corr = np.convolve(np.pad(corr, 1, mode='edge'), np.ones(3) / 3, mode='valid')
            dy[s] = dy[s] + corr
    frames = [solve(f, {s: dy[s][f] for s in dy}) for f in range(nframes)]
    return frames, dy

GAIT_BONES = ('左足', '右足', '左ひざ', '右ひざ', '左腕', '右腕', '上半身', '左ひじ', '右ひじ')

def feat(W):
    h = W['下半身']; v = []
    for n in GAIT_BONES:
        q = qmul(qinv(h), W[n]); q = q if q[3] >= 0 else -q
        v.extend(q[:3])
    return np.array(v)

def find_loop(F, n):
    best = None
    for P in range(14, min(90, n - 2)):
        errs = np.abs(F[:n-P] - F[P:n]).mean(axis=1)
        s = int(np.argmin(errs + 0.02 * np.abs(np.arange(n - P) + P / 2 - n / 2) / n))
        if best is None or errs[s] < best[2] * 0.85:
            best = (s, P, errs[s])
    return best

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('vmd'); ap.add_argument('out'); ap.add_argument('--name')
    ap.add_argument('--start', type=int); ap.add_argument('--period', type=int)
    ap.add_argument('--scale', type=float, default=1.0, help='scale for all translations')
    ap.add_argument('--arm', type=float, default=ARM_REST_DEG, help='MMD rest arm angle (deg)')
    ap.add_argument('--dy', type=float, default=0.0, help='extra センター height offset (m)')
    ap.add_argument('--lift', type=float, default=1.0, help='scale of foot IK height (swing-foot lift)')
    ap.add_argument('--sway', type=float, default=1.0, help='scale of 下半身 (pelvis) rotation')
    ap.add_argument('--chest', type=float, default=0.0, help='extra 上半身 pitch, deg (negative = chest out)')
    ap.add_argument('--chin', type=float, default=0.0, help='extra 頭 pitch, deg (negative = chin up)')
    ap.add_argument('--outdir', help='Unity asset folder for the baked clip')
    ap.add_argument('--no-ground', action='store_true', help='skip planting the support foot on y=0')
    ap.add_argument('--analyze', action='store_true')
    a = ap.parse_args()
    rest = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'nino_rest.json')))['bones']
    tracks = parse_vmd(a.vmd)
    bones, order = build_skeleton(rest)
    last = max(k[-1][0] for k in tracks.values())
    stats = {}
    style = Style(a.lift, a.sway, a.chest, a.chin, a.dy)
    pts = sole_points(rest)
    if a.no_ground:
        frames = [solve_frame(tracks, bones, order, f, a.scale, stats, style) for f in range(last + 1)]
    else:
        frames, _ = ground_feet(lambda f, d: solve_frame(tracks, bones, order, f, a.scale, stats, style, d), last + 1, pts)
    rest_hips = np.array(rest['Hips'][:3])
    F = np.array([feat(W) for W, _ in frames])
    hp = np.array([hips_world(W, Pw, bones, rest_hips) for W, Pw in frames])
    n = len(frames)
    s, P, err = find_loop(F, n)
    if a.start is not None: s = a.start
    if a.period is not None: P = a.period
    knees = np.array(stats.get('knee', []))
    support = [min(contact_height(W, Pw, '左', pts), contact_height(W, Pw, '右', pts)) for W, Pw in frames]
    # planted-foot slide in the take: velocity of the lower ankle along the travel direction (m/s)
    ank = np.array([[Pw['左足首'], Pw['右足首']] for _, Pw in frames])
    tdir = (hp[s+P] - hp[s]); tdir[1] = 0
    tdir = tdir / np.linalg.norm(tdir) if np.linalg.norm(tdir) > 1e-3 else np.array([0, 0, 1.0])
    slide = []
    for f in range(s, s + P):
        low = 0 if ank[f, 0, 1] < ank[f, 1, 1] else 1
        slide.append(np.dot(ank[f+1, low] - ank[f, low], tdir) * 30)
    foot_speed = float(np.median(slide))
    travel = hp[s+P] - hp[s]
    speed = np.linalg.norm(travel[[0, 2]]) / (P / 30)
    info = dict(frames=n, start=s, period=P, seconds=round(P/30, 3), loop_err=round(float(np.abs(F[s]-F[s+P]).mean()), 4),
                travel=travel.round(3).tolist(), speed=round(float(speed), 3),
                planted_foot_speed=round(foot_speed, 3),
                support_cm=[round(float(np.min(support[s:s+P+1])) * 100, 1), round(float(np.median(support[s:s+P+1])) * 100, 1), round(float(np.max(support[s:s+P+1])) * 100, 1)], overreach=stats.get('overreach', 0), knee=[round(float(knees.min()), 1), round(float(knees.max()), 1)],
                hips_y=[round(float(hp[s:s+P+1, 1].min()), 3), round(float(hp[s:s+P+1, 1].max()), 3)])
    print(json.dumps(info))
    if a.analyze: return
    if np.linalg.norm(travel[[0, 2]]) < 0.05:   # in place: keep the hips' facing
        fw = qrot(frames[s][0]['下半身'], [0, 0, 1]); yaw = np.arctan2(fw[0], fw[2])
    else:
        yaw = np.arctan2(travel[0], travel[2])
    fix = qaxis([0, 1, 0], -yaw)
    origin = hp[s].copy(); origin[1] = 0
    hmap = humanoid_map(); ar = arm_rest(a.arm)
    names = list(hmap.keys())
    # In place: remove the steady forward travel (the game drives speed itself), keep sway and surge.
    step = qrot(fix, travel) / P; step[1] = 0
    out = dict(name=a.name or os.path.splitext(os.path.basename(a.vmd))[0], fps=30, loop=True, inPlace=True,
               speed=round(float(np.linalg.norm(step) * 30) if np.linalg.norm(step) > 1e-4 else max(0.0, -foot_speed), 4), source=os.path.basename(a.vmd), bones=names, frames=[])
    if a.outdir: out['outDir'] = a.outdir
    for f in range(s, s + P + 1):
        W, Pw = frames[f]
        hpos = qrot(fix, hips_world(W, Pw, bones, rest_hips) - origin) - step * (f - s)
        qs = []
        for hn in names:
            src, arm = hmap[hn]
            q = qmul(fix, W[src])
            if arm: q = qmul(q, ar[arm])
            qs.append([round(float(x), 6) for x in qnorm(q)])
        out['frames'].append(dict(p=[round(float(x), 5) for x in hpos], q=qs))
    json.dump(out, open(a.out, 'w', encoding='utf-8'), ensure_ascii=False)
    print('wrote', a.out, len(out['frames']), 'frames')

if __name__ == '__main__':
    main()
