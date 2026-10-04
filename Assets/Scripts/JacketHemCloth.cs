using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Physics for the lower part of the jacket. The jacket hangs from chains of joints around the hips
/// (J_Sec_JacketHem_*, created by Tools/Character/Apply Cloth Clipping Fix). Every segment swings like a VRM spring
/// bone: pulled back towards its rest direction (stiffness), pulled down (gravity) and slowed down (drag).
/// On top of that it does what a long coat worn over a short skirt needs:
/// - Only part of the character's own movement is felt as inertia, so running makes the hem trail a little
///   instead of flying up, and jumping lifts it a little instead of flipping it over.
/// - Each segment's swing away from its rest direction is limited, and sideways (around the body) even more,
///   as the cloth between neighbouring chains would.
/// - It never ends up inside the skirt or the legs. It runs after the VRM spring bones (the skirt has already
///   moved) and skins the skirt's vertices on the CPU. Each frame every skirt point is compared with the jacket
///   panel that is over it now: the jacket's rest surface (a map of its distance from the hips axis by angle and
///   height) plus how far that panel's chain has moved. The thighs are tapered capsules. A segment that would end
///   up inside either is rotated outward just enough.
/// </summary>
[DefaultExecutionOrder(11100)] // after FastSpringBoneService (11000)
[DisallowMultipleComponent]
public sealed class JacketHemCloth : MonoBehaviour
{
    [System.Serializable]
    public sealed class Chain
    {
        public Transform[] Joints; // root .. end; the unweighted end joint only gives the last segment its length
    }

    [System.Serializable]
    public struct Leg
    {
        public Transform Hip, Knee;
        public float HipRadius, KneeRadius;
    }

    [SerializeField] private Transform hips;
    [Tooltip("Character root. Inertia is measured relative to it.")]
    [SerializeField] private Transform space;
    [SerializeField] private Chain[] chains;
    [SerializeField] private Leg[] legs;
    [Tooltip("Renderer with the jacket and the skirt (the VRoid 'Body' mesh).")]
    [SerializeField] private SkinnedMeshRenderer body;
    [SerializeField] private int jacketSubmesh = 3;
    [Tooltip("The skirt's outside: its outer layer and the front band.")]
    [SerializeField] private int[] skirtSubmeshes = { 6, 7 };

    [Header("Motion (VRM spring units)")]
    [SerializeField] private float stiffness = 1.2f;
    [Tooltip("Share of the velocity lost per 1/60 s.")]
    [SerializeField, Range(0f, 1f)] private float drag = 0.3f;
    [SerializeField] private float gravity = 0.25f;
    [Tooltip("Share of the character's own movement the hem feels (0 = moves rigidly with her, 1 = full world-space inertia).")]
    [SerializeField, Range(0f, 1f)] private float inertia = 0.35f;
    [SerializeField, Range(0f, 90f)] private float maxSwing = 40f;
    [Tooltip("Limit on swinging sideways around the body: the cloth between neighbouring chains doesn't let a panel slide over the next one.")]
    [SerializeField, Range(0f, 90f)] private float maxSideways = 10f;

    [Header("Collision")]
    [SerializeField] private float clearance = 0.005f;

    private struct Segment
    {
        public Transform Joint;
        public Quaternion RestLocalRotation;
        public Vector3 Axis;              // rest direction to the child, in the joint's local space
        public float Length;
        public Vector3 Tail, PrevTail;    // world space
    }

    private struct Sample
    {
        public Vector3 Position;          // bind pose, mesh space
        public BoneWeight Weight;
        public float Allow;               // gap kept to the jacket: the clearance, or less where the rest pose is tighter
    }

    private const float MapAngleStep = 2.5f, MapHeightStep = 0.01f, BracketStep = 0.5f;

    private Segment[][] segments;
    private Vector3[][] restJoints;       // hips space
    private float[] chainAngle;
    private float[][] chainRestRadius;    // per chain: rest distance of its line from the hips axis, by map height
    private (int a, int b, float wb)[] brackets; // chains on either side of an angle, per BracketStep
    private Vector3 up, forward, right;   // hips space
    private float[] jacketMap;             // rest distance of the jacket from the hips axis [angle * mapHeights + height]; NaN = no jacket
    private int mapAngles, mapHeights;
    private float mapBottom;
    private Vector3[] legHip, legKnee;    // this frame, hips space
    private Sample[] samples;
    private Vector3[] skinned;            // this frame, hips space
    private float[] sampleHeight, sampleNeed; // this frame: height, and how far past the jacket's rest surface it reaches
    private List<int>[] chainSamples;     // this frame: the samples under each chain
    private Transform[] bones;
    private Matrix4x4[] bindposes, skin;
    private bool[] boneUsed;
    private Matrix4x4 lastSpace;
    private bool initialized;

    private void Awake()
    {
        if (hips == null || space == null || body == null || body.sharedMesh == null || chains == null || chains.Length == 0)
        {
            enabled = false;
            return;
        }
        BuildSegments();
        BuildSamples();
        legs ??= new Leg[0];
        legHip = new Vector3[legs.Length];
        legKnee = new Vector3[legs.Length];
    }

    private void OnDisable()
    {
        if (segments == null) return;
        foreach (var chain in segments)
            foreach (var s in chain)
                s.Joint.localRotation = s.RestLocalRotation;
        initialized = false;
    }

    // ------------------------------------------------------------------ setup

    private void BuildSegments()
    {
        segments = new Segment[chains.Length][];
        restJoints = new Vector3[chains.Length][];
        chainAngle = new float[chains.Length];
        for (int c = 0; c < chains.Length; c++)
        {
            var joints = chains[c].Joints;
            segments[c] = new Segment[joints.Length - 1];
            restJoints[c] = new Vector3[joints.Length];
            for (int k = 0; k < joints.Length; k++)
                restJoints[c][k] = hips.InverseTransformPoint(joints[k].position);
            for (int k = 0; k < joints.Length - 1; k++)
            {
                Vector3 local = joints[k + 1].localPosition;
                segments[c][k] = new Segment
                {
                    Joint = joints[k],
                    RestLocalRotation = joints[k].localRotation,
                    Axis = local.normalized,
                    Length = Vector3.Scale(local, joints[k].lossyScale).magnitude,
                };
            }
        }
    }

    private void BuildSamples()
    {
        Mesh mesh = body.sharedMesh;
        bones = body.bones;
        bindposes = mesh.bindposes;
        skin = new Matrix4x4[bones.Length];
        boneUsed = new bool[bones.Length];
        int hipsIndex = System.Array.IndexOf(bones, hips);
        Matrix4x4 meshToHips = hipsIndex >= 0 ? bindposes[hipsIndex] : hips.worldToLocalMatrix * body.transform.localToWorldMatrix;

        up = meshToHips.MultiplyVector(Vector3.up).normalized;
        forward = Vector3.ProjectOnPlane(meshToHips.MultiplyVector(Vector3.forward), up).normalized;
        right = Vector3.Cross(up, forward);
        for (int c = 0; c < chains.Length; c++)
        {
            Vector3 mid = Vector3.zero;
            for (int k = 1; k < restJoints[c].Length; k++) mid += restJoints[c][k];
            chainAngle[c] = Angle(mid);
        }
        var order = new int[chains.Length];
        for (int c = 0; c < order.Length; c++) order[c] = c;
        System.Array.Sort(order, (x, y) => chainAngle[x].CompareTo(chainAngle[y]));
        brackets = new (int, int, float)[Mathf.RoundToInt(360f / BracketStep)];
        for (int i = 0; i < brackets.Length; i++)
        {
            Bracket(order, -180f + i * BracketStep, out int a, out int b, out float wb);
            brackets[i] = (a, b, wb);
        }

        // Jacket surface around the chains, in hips space, bucketed by angle for the radial ray casts.
        var verts = mesh.vertices;
        var hipsVerts = new Vector3[verts.Length];
        for (int i = 0; i < verts.Length; i++) hipsVerts[i] = meshToHips.MultiplyPoint3x4(verts[i]);
        float top = float.MinValue, bottom = float.MaxValue;
        foreach (var chain in restJoints)
        {
            top = Mathf.Max(top, Vector3.Dot(chain[0], up));
            bottom = Mathf.Min(bottom, Vector3.Dot(chain[chain.Length - 1], up));
        }
        const int Buckets = 72;
        var bucket = new List<int>[Buckets];
        for (int b = 0; b < Buckets; b++) bucket[b] = new List<int>();
        var tris = mesh.GetTriangles(jacketSubmesh);
        for (int t = 0; t < tris.Length; t += 3)
        {
            Vector3 a = hipsVerts[tris[t]], b = hipsVerts[tris[t + 1]], d = hipsVerts[tris[t + 2]];
            float ha = Vector3.Dot(a, up), hb = Vector3.Dot(b, up), hd = Vector3.Dot(d, up);
            if (Mathf.Max(ha, Mathf.Max(hb, hd)) > top + 0.06f || Mathf.Min(ha, Mathf.Min(hb, hd)) < bottom - 0.06f) continue;
            float a0 = Angle(a), a1 = Angle(b), a2 = Angle(d);
            float lo = Mathf.Min(0f, Mathf.Min(Mathf.DeltaAngle(a0, a1), Mathf.DeltaAngle(a0, a2)));
            float hi = Mathf.Max(0f, Mathf.Max(Mathf.DeltaAngle(a0, a1), Mathf.DeltaAngle(a0, a2)));
            for (float x = a0 + lo - 5f; x <= a0 + hi + 5f; x += 360f / Buckets)
                bucket[Bucket(x, Buckets)].Add(t);
            bucket[Bucket(a0 + hi + 5f, Buckets)].Add(t);
        }

        // Map of the jacket's rest surface: nearest hit of a ray from the hips axis, per angle and height.
        mapBottom = bottom - 0.05f;
        int angles = Mathf.RoundToInt(360f / MapAngleStep), heights = Mathf.CeilToInt((top + 0.03f - mapBottom) / MapHeightStep) + 1;
        mapAngles = angles; mapHeights = heights;
        jacketMap = new float[angles * heights];
        for (int a = 0; a < angles; a++)
            for (int h = 0; h < heights; h++)
            {
                float angle = -180f + (a + 0.5f) * MapAngleStep;
                Vector3 o = up * (mapBottom + h * MapHeightStep), dir = Radial(angle);
                float r = float.MaxValue;
                foreach (int t in bucket[Bucket(angle, Buckets)])
                    if (RayTriangle(o, dir, hipsVerts[tris[t]], hipsVerts[tris[t + 1]], hipsVerts[tris[t + 2]], out float hit))
                        r = Mathf.Min(r, hit);
                jacketMap[a * heights + h] = r < float.MaxValue ? r : float.NaN;
            }
        chainRestRadius = new float[chains.Length][];
        for (int c = 0; c < chains.Length; c++)
        {
            chainRestRadius[c] = new float[heights];
            for (int h = 0; h < heights; h++) chainRestRadius[c][h] = RestRadiusAt(c, mapBottom + h * MapHeightStep);
        }

        // Every skirt point below the chain roots (above them the skirt and the jacket both follow the hips).
        var weights = mesh.boneWeights;
        var list = new List<Sample>();
        var seen = new HashSet<int>();
        foreach (int sub in skirtSubmeshes)
        {
            foreach (int i in mesh.GetIndices(sub))
            {
                if (!seen.Add(i)) continue;
                Vector3 q = hipsVerts[i];
                float h = Vector3.Dot(q, up);
                if (h > top) continue;
                float r = Vector3.ProjectOnPlane(q, up).magnitude;
                var w = weights[i];
                boneUsed[w.boneIndex0] = boneUsed[w.boneIndex1] = boneUsed[w.boneIndex2] = boneUsed[w.boneIndex3] = true;
                list.Add(new Sample
                {
                    Position = verts[i], Weight = w,
                    Allow = JacketAt(Angle(q), h, out float jacket) ? Mathf.Min(clearance, jacket - r) : clearance,
                });
            }
        }
        samples = list.ToArray();
        skinned = new Vector3[samples.Length];
        sampleHeight = new float[samples.Length];
        sampleNeed = new float[samples.Length];
        chainSamples = new List<int>[chains.Length];
        for (int c = 0; c < chains.Length; c++) chainSamples[c] = new List<int>();
    }

    /// <summary>The jacket's rest distance from the hips axis at an angle and height; false where there is no jacket.</summary>
    private bool JacketAt(float angle, float height, out float radius)
    {
        radius = 0f;
        int angles = mapAngles, heights = mapHeights;
        float fh = (height - mapBottom) / MapHeightStep;
        if (fh < -0.5f || fh >= heights - 0.5f) return false;
        float fa = Mathf.Repeat(angle + 180f, 360f) / MapAngleStep - 0.5f;
        int nh = (int)(fh + 0.5f);
        if (float.IsNaN(jacketMap[Wrap(Mathf.RoundToInt(fa), angles) * heights + nh])) return false;
        int a0 = Mathf.FloorToInt(fa), h0 = Mathf.FloorToInt(fh);
        float ta = fa - a0, th = fh - h0, sum = 0f, total = 0f;
        for (int da = 0; da < 2; da++)
            for (int dh = 0; dh < 2; dh++)
            {
                int h = h0 + dh;
                if (h < 0 || h >= heights) continue;
                float v = jacketMap[Wrap(a0 + da, angles) * heights + h];
                if (float.IsNaN(v)) continue;
                float w = (da == 0 ? 1f - ta : ta) * (dh == 0 ? 1f - th : th);
                sum += v * w;
                total += w;
            }
        if (total < 1e-5f) return false;
        radius = sum / total;
        return true;
    }

    private static int Wrap(int i, int n) => (i % n + n) % n;

    /// <summary>The two chains on either side of an angle; never across the front, where the jacket is open.</summary>
    private void Bracket(int[] order, float angle, out int a, out int b, out float wb)
    {
        a = b = -1; wb = 0f;
        for (int i = 0; i < order.Length; i++)
        {
            int c0 = order[i], c1 = order[(i + 1) % order.Length];
            float span = Mathf.Repeat(chainAngle[c1] - chainAngle[c0], 360f);
            float off = Mathf.Repeat(angle - chainAngle[c0], 360f);
            if (off > span) continue;
            if (Mathf.Repeat(-chainAngle[c0], 360f) <= span) a = off <= span * 0.5f ? c0 : c1; // the open front
            else { a = c0; b = c1; wb = off / span; }
            return;
        }
    }

    private static int Bucket(float angle, int buckets) => Mathf.FloorToInt(Mathf.Repeat(angle + 180f, 360f) / 360f * buckets) % buckets;

    private float Angle(Vector3 p) => Mathf.Atan2(Vector3.Dot(p, right), Vector3.Dot(p, forward)) * Mathf.Rad2Deg;

    private Vector3 Radial(float angle) => Quaternion.AngleAxis(angle, up) * forward;

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0f;
    }

    // ------------------------------------------------------------------ simulation

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;
        Matrix4x4 spaceNow = space.localToWorldMatrix;
        if (!initialized || (spaceNow.GetColumn(3) - lastSpace.GetColumn(3)).sqrMagnitude > 1f) Restart();

        // Carry the hem along with the character, except for the share felt as inertia.
        Matrix4x4 moved = spaceNow * lastSpace.inverse;
        lastSpace = spaceNow;
        float carry = 1f - inertia;
        foreach (var chain in segments)
            for (int k = 0; k < chain.Length; k++)
            {
                chain[k].Tail = Vector3.LerpUnclamped(chain[k].Tail, moved.MultiplyPoint3x4(chain[k].Tail), carry);
                chain[k].PrevTail = Vector3.LerpUnclamped(chain[k].PrevTail, moved.MultiplyPoint3x4(chain[k].PrevTail), carry);
            }

        SkinSamples();
        AssignSamples();
        for (int l = 0; l < legs.Length; l++)
        {
            legHip[l] = hips.InverseTransformPoint(legs[l].Hip.position);
            legKnee[l] = hips.InverseTransformPoint(legs[l].Knee.position);
        }
        float keep = Mathf.Pow(1f - drag, dt * 60f);
        for (int c = 0; c < segments.Length; c++)
        {
            var chain = segments[c];
            for (int k = 0; k < chain.Length; k++)
            {
                ref Segment s = ref chain[k];
                Vector3 head = s.Joint.position;
                Quaternion restRotation = s.Joint.parent.rotation * s.RestLocalRotation;
                Vector3 restDir = restRotation * s.Axis;

                Vector3 next = s.Tail + (s.Tail - s.PrevTail) * keep + restDir * (stiffness * dt) + Vector3.down * (gravity * dt);
                // Limits are relative to the body (hips), so they don't add up along the chain.
                Vector3 bodyRest = hips.TransformDirection(restJoints[c][k + 1] - restJoints[c][k]).normalized;
                Vector3 dir = LimitSwing(c, bodyRest, (next - head).normalized);
                next = head + dir * s.Length;
                next = Collide(c, k, head, next);

                s.PrevTail = s.Tail;
                s.Tail = next;
                s.Joint.rotation = Quaternion.FromToRotation(restDir, next - head) * restRotation;
            }
        }
    }

    /// <summary>Keeps a segment direction within maxSwing of its rest direction, and within maxSideways around the body.</summary>
    private Vector3 LimitSwing(int c, Vector3 restDir, Vector3 dir)
    {
        Vector3 radial = Vector3.ProjectOnPlane(hips.TransformDirection(Radial(chainAngle[c])), restDir).normalized;
        Vector3 side = Vector3.Cross(restDir, radial);
        float along = Vector3.Dot(dir, restDir), outward = Vector3.Dot(dir, radial), sideways = Vector3.Dot(dir, side);
        float maxSide = Mathf.Tan(maxSideways * Mathf.Deg2Rad) * new Vector2(along, outward).magnitude;
        if (Mathf.Abs(sideways) > maxSide) dir = (restDir * along + radial * outward + side * Mathf.Sign(sideways) * maxSide).normalized;
        float swing = Vector3.Angle(restDir, dir);
        return swing > maxSwing ? Vector3.Slerp(restDir, dir, maxSwing / swing) : dir;
    }

    private void Restart()
    {
        lastSpace = space.localToWorldMatrix;
        foreach (var chain in segments)
            for (int k = 0; k < chain.Length; k++)
            {
                chain[k].Joint.localRotation = chain[k].RestLocalRotation;
                Vector3 tail = chain[k].Joint.position + chain[k].Joint.rotation * chain[k].Axis * chain[k].Length;
                chain[k].Tail = chain[k].PrevTail = tail;
            }
        initialized = true;
    }

    private void SkinSamples()
    {
        Matrix4x4 toHips = hips.worldToLocalMatrix;
        for (int b = 0; b < bones.Length; b++)
            if (boneUsed[b]) skin[b] = toHips * bones[b].localToWorldMatrix * bindposes[b];
        for (int i = 0; i < samples.Length; i++)
        {
            var w = samples[i].Weight;
            Vector3 p = samples[i].Position;
            skinned[i] = skin[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0 + skin[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1
                       + skin[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2 + skin[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
        }
    }

    /// <summary>
    /// For every skirt point where it is now: how far it reaches past the jacket's rest surface there, and which
    /// chains hold the jacket over it (both neighbours, as the jacket between them blends the two).
    /// </summary>
    private void AssignSamples()
    {
        foreach (var list in chainSamples) list.Clear();
        for (int i = 0; i < samples.Length; i++)
        {
            Vector3 p = skinned[i];
            float h = Vector3.Dot(p, up), x = Vector3.Dot(p, right), z = Vector3.Dot(p, forward);
            float angle = Mathf.Atan2(x, z) * Mathf.Rad2Deg;
            if (!JacketAt(angle, h, out float jacket)) continue; // the open front, or below the hem
            float need = Mathf.Sqrt(x * x + z * z) + samples[i].Allow - jacket;
            if (need < -0.1f) continue; // far inside: no panel swings in that far
            sampleHeight[i] = h;
            sampleNeed[i] = need;
            var (a, b, wb) = brackets[Mathf.RoundToInt(Mathf.Repeat(angle + 180f, 360f) / BracketStep) % brackets.Length];
            if (a >= 0 && (b < 0 || wb < 0.9f)) chainSamples[a].Add(i);
            if (b >= 0 && wb > 0.1f) chainSamples[b].Add(i);
        }
    }

    /// <summary>
    /// Rotates the segment (head fixed) outward until the jacket is outside the skirt points and the thighs.
    /// The segment above already pushed out the region just below this head (its range reaches 30% past its
    /// tail); here that region only stops this segment from swinging back in, so its lever is capped at 30%.
    /// </summary>
    private Vector3 Collide(int c, int k, Vector3 headWorld, Vector3 tailWorld)
    {
        bool last = k == segments[c].Length - 1;
        Vector3 head = hips.InverseTransformPoint(headWorld), tail = hips.InverseTransformPoint(tailWorld);
        Vector3 seg = tail - head;
        float length = seg.magnitude;
        Vector3 outward = Vector3.ProjectOnPlane(Radial(chainAngle[c]), seg).normalized;
        float tMax = last ? 1f : 1.3f;
        float push = 0f; // radians

        // Skirt: how far a point reaches past the jacket's rest surface, against how far this chain has moved out.
        float hHead = Vector3.Dot(head, up), hTail = Vector3.Dot(tail, up);
        float radialGain = Mathf.Max(0.3f, Vector3.ProjectOnPlane(outward, up).magnitude);
        if (hHead - hTail > 0.01f)
        {
            float Moved(float t) => Vector3.ProjectOnPlane(head + seg * t, up).magnitude - RestRadius(c, hHead + (hTail - hHead) * t);
            float leastMoved = Mathf.Min(Moved(0f), Mathf.Min(Moved(1f), Moved(tMax))); // ~linear along the segment
            foreach (int i in chainSamples[c])
            {
                if (sampleNeed[i] <= leastMoved) continue; // inside wherever it is along this segment
                float h = sampleHeight[i];
                float t = (hHead - h) / (hHead - hTail);
                if (t < 0f || t > tMax) continue;
                float inside = sampleNeed[i] - Moved(t);
                if (inside > 0f) push = Mathf.Max(push, Mathf.Asin(Mathf.Min(1f, inside / (Mathf.Max(t, 0.3f) * length * radialGain))));
            }
        }

        // Thighs: tapered capsules from the hip joint to the knee.
        Vector3 middle = head + seg * (tMax * 0.5f);
        for (int l = 0; l < legs.Length; l++)
        {
            Vector3 a = legHip[l], ab = legKnee[l] - a;
            Vector3 nearest = a + ab * Mathf.Clamp01(Vector3.Dot(middle - a, ab) / ab.sqrMagnitude);
            float reach = Mathf.Max(legs[l].HipRadius, legs[l].KneeRadius) + clearance + length * tMax * 0.5f;
            if ((middle - nearest).sqrMagnitude > reach * reach) continue; // this leg is nowhere near the segment
            for (float t = 0.1f; t <= tMax + 0.01f; t += 0.15f)
            {
                Vector3 p = head + seg * t;
                float u = Mathf.Clamp01(Vector3.Dot(p - a, ab) / ab.sqrMagnitude);
                Vector3 off = p - (a + ab * u);
                float r = Mathf.Lerp(legs[l].HipRadius, legs[l].KneeRadius, u) + clearance;
                float c2 = off.sqrMagnitude - r * r;
                if (c2 >= 0f) continue;
                float bProj = Vector3.Dot(off, outward);
                float escape = -bProj + Mathf.Sqrt(bProj * bProj - c2); // move along 'outward' until outside
                push = Mathf.Max(push, Mathf.Asin(Mathf.Min(1f, escape / (Mathf.Max(t, 0.3f) * length))));
            }
        }

        if (push <= 0f) return tailWorld;
        Vector3 axis = Vector3.Cross(seg, outward);
        tail = head + Quaternion.AngleAxis(push * Mathf.Rad2Deg, axis) * seg;
        return hips.TransformPoint(tail);
    }

    /// <summary>Distance of the chain's rest line from the hips axis at a height (table lookup).</summary>
    private float RestRadius(int c, float height)
    {
        var table = chainRestRadius[c];
        float f = Mathf.Clamp((height - mapBottom) / MapHeightStep, 0f, table.Length - 1.001f);
        int i = (int)f;
        return Mathf.Lerp(table[i], table[i + 1], f - i);
    }

    /// <summary>Distance of the chain's rest line from the hips axis at a height (extrapolated past its ends).</summary>
    private float RestRadiusAt(int c, float height)
    {
        var p = restJoints[c];
        int k = 0;
        while (k < p.Length - 2 && Vector3.Dot(p[k + 1], up) > height) k++;
        float h0 = Vector3.Dot(p[k], up), h1 = Vector3.Dot(p[k + 1], up);
        float t = Mathf.Abs(h0 - h1) > 1e-4f ? (h0 - height) / (h0 - h1) : 0f;
        return Vector3.ProjectOnPlane(Vector3.LerpUnclamped(p[k], p[k + 1], t), up).magnitude;
    }
}
