#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Editor-only cloth audit for the player. Drives AnimeCharacterController through idle, walk, run (Shift),
/// start, stop and a 180 turn with the game simulated at a locked 60 fps, and on every
/// frame, after the animation and the spring bones, measures on the skinned Body mesh how the clothing layers
/// overlap (all three should stay at 0):
/// - skin through skirt   (butt/thighs showing through the skirt)
/// - skirt through jacket
/// - skin through jacket  (legs through the jacket)
/// An inner vertex counts when it is outside the outer layer where that layer covered it in the rest pose
/// (points beside an open edge, like the jacket's front opening, don't count). The report also says where:
/// vertex-frames by rest-pose height and angle around the hips.
/// Every 3rd frame (20 fps) it renders one contact-sheet column per phase: the game camera's view moved close
/// to the hips, the same view with the jacket hidden (does the skirt cover her?), from behind/below and from
/// the side; or, with closeUp, hip-height close-ups from the game direction, front-left, front-right and behind.
/// Renders use a baked copy of the meshes, so pictures and numbers are the same frame.
/// Usage (Play Mode): ClothLayerAudit.Begin(dir); poll Done / Report.
/// </summary>
[DefaultExecutionOrder(20000)] // after the Animator, ArmClothClearance and FastSpringBoneService (11000)
public sealed class ClothLayerAudit : MonoBehaviour
{
    private const int SimFps = 60;
    private const int Stride = 3;              // render every 3rd simulated frame (20 fps)
    private const int CellW = 200, CellH = 280;
    private const float SettleSeconds = 2f;
    private const float PenetrationTolerance = 0.002f;

    public static bool Done { get; private set; }
    public static string Report { get; private set; } = "";

    private struct Phase
    {
        public string Name;
        public Vector2 SettleInput, Input;
        public bool Walk, Sprint, Jump;
        public float Seconds;
    }

    private static readonly Phase[] AllPhases =
    {
        new Phase { Name = "idle", Seconds = 1.2f },
        new Phase { Name = "walk", SettleInput = Vector2.up, Input = Vector2.up, Seconds = 1.2f },                  // walking is the default
        new Phase { Name = "run", SettleInput = Vector2.up, Input = Vector2.up, Sprint = true, Seconds = 1.2f },   // Shift held = run
        new Phase { Name = "start", Input = Vector2.up, Seconds = 1.2f },
        new Phase { Name = "stop", SettleInput = Vector2.up, Seconds = 1.2f },
        new Phase { Name = "turn", SettleInput = Vector2.up, Input = Vector2.down, Seconds = 1.2f },
        // jump phases come back with a Blender jump set (AnimeCharacterController.jumpAnimationAvailable)
    };

    // ---- views ----
    private enum View { Game, GameNoJacket, BackLow, Side, FrontLeft, FrontRight, BackHip }
    private static readonly View[] WideViews = { View.Game, View.GameNoJacket, View.BackLow, View.Side };
    // Close-up mode: hip height, 1.2 m away.
    private static readonly View[] CloseViews = { View.Game, View.FrontLeft, View.FrontRight, View.BackHip };
    private View[] views;

    private string outDir;
    private Phase[] phases;
    private bool images, runInBackground;
    private int phaseIndex, frame, recordFrames;
    private Transform player;
    private Vector3 startPosition;
    private Camera gameCam, cam;
    private RenderTexture rt;
    private Texture2D readback, sheet;
    private readonly StringBuilder log = new StringBuilder();

    // ---- meshes ----
    private SkinnedMeshRenderer body;
    private SkinnedMeshRenderer[] skins;
    private GameObject[] snaps;
    private Mesh[] snapMeshes;
    private int jacketSubmesh;
    private readonly List<Vector3> bakedV = new List<Vector3>(), bakedN = new List<Vector3>();
    private Vector3[] worldV, worldN;

    // Layer pairs: inner vertices that the outer layer covers in the bind pose, and the outer layer's vertices.
    private sealed class Pair
    {
        public string Name;
        public int[] Inner, Outer;
        public float Radius;
        public int FramesHit, MaxVerts; public float MaxDepth; public long SumVerts; public int Frames;
        public int FrameVerts; public float FrameDepth;
        public int[] Hits; // per inner vertex, this phase
    }
    private Pair[] pairs;
    private Vector3[] bindV;     // imported mesh, mesh space
    private Vector3 bindHips;
    private bool[] edge;         // vertex on an open edge of a cloth layer

    public static void Begin(string outputDir, bool renderImages = true, string onlyPhases = null, bool closeUp = false)
    {
        Done = false;
        Report = "";
        var a = new GameObject("__ClothLayerAudit").AddComponent<ClothLayerAudit>();
        a.outDir = outputDir;
        a.images = renderImages;
        a.views = closeUp ? CloseViews : WideViews;
        a.phases = onlyPhases == null ? AllPhases
            : System.Array.FindAll(AllPhases, p => System.Array.IndexOf(onlyPhases.Split(','), p.Name) >= 0);
    }

    private void Start()
    {
        Directory.CreateDirectory(outDir);
        player = GameObject.Find("Player").transform;
        startPosition = player.position;
        gameCam = Camera.main;
        skins = player.GetComponentsInChildren<SkinnedMeshRenderer>();
        body = System.Array.Find(skins, s => s.name == "Body");
        BuildPairs();

        cam = new GameObject("__AuditCam").AddComponent<Camera>();
        cam.enabled = false;
        cam.nearClipPlane = 0.03f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.22f, 0.23f, 0.26f);
        rt = new RenderTexture(CellW, CellH, 24) { antiAliasing = 4 };
        readback = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);

        snaps = new GameObject[skins.Length];
        snapMeshes = new Mesh[skins.Length];
        for (int i = 0; i < skins.Length; i++)
        {
            snaps[i] = new GameObject("__Snap_" + skins[i].name, typeof(MeshFilter), typeof(MeshRenderer));
            snaps[i].layer = skins[i].gameObject.layer;
            snapMeshes[i] = new Mesh();
            snaps[i].GetComponent<MeshFilter>().sharedMesh = snapMeshes[i];
            var mr = snaps[i].GetComponent<MeshRenderer>();
            mr.sharedMaterials = skins[i].sharedMaterials;
            mr.shadowCastingMode = skins[i].shadowCastingMode;
            mr.receiveShadows = skins[i].receiveShadows;
            snaps[i].SetActive(false);
        }

        Time.captureFramerate = SimFps;
        runInBackground = Application.runInBackground;
        Application.runInBackground = true; // keep the editor's play mode ticking while it isn't focused
        AnimeCharacterController.UseInputOverride = true;
        StartPhase(0);
    }

    // ------------------------------------------------------------------ phases

    private void StartPhase(int i)
    {
        phaseIndex = i;
        var p = phases[i];
        frame = -Mathf.RoundToInt(SettleSeconds * SimFps);
        recordFrames = Mathf.RoundToInt(p.Seconds * SimFps);
        var cc = player.GetComponent<CharacterController>();
        cc.enabled = false;
        player.position = startPosition;
        cc.enabled = true;
        AnimeCharacterController.InputOverride = p.SettleInput;
        AnimeCharacterController.WalkOverride = p.Walk;
        AnimeCharacterController.SprintOverride = p.Sprint;
        foreach (var pair in pairs)
        {
            pair.FramesHit = pair.MaxVerts = pair.Frames = 0; pair.SumVerts = 0; pair.MaxDepth = 0f;
            pair.Hits = new int[pair.Inner.Length];
        }
        int cols = (recordFrames + Stride - 1) / Stride;
        if (images) sheet = new Texture2D(CellW * cols, CellH * views.Length, TextureFormat.RGB24, false);
        log.AppendLine($"--- {p.Name} ---");
    }

    private void LateUpdate()
    {
        if (Done || player == null) return;
        if (frame >= 0)
        {
            Bake();
            Measure();
            if (images && frame % Stride == 0) RenderColumn(frame / Stride);
            log.AppendLine($"f{frame:D3} " + string.Join("  ", System.Array.ConvertAll(pairs, q => $"{q.Name}={q.FrameVerts}/{q.FrameDepth * 100f:F1}cm")));
        }

        frame++;
        if (frame == 0)
        {
            var p = phases[phaseIndex];
            AnimeCharacterController.InputOverride = p.Input;
            AnimeCharacterController.JumpOverride = p.Jump;
        }
        if (frame >= recordFrames) FinishPhase();
    }

    private void FinishPhase()
    {
        var p = phases[phaseIndex];
        if (images)
        {
            sheet.Apply();
            File.WriteAllBytes(Path.Combine(outDir, p.Name + ".png"), sheet.EncodeToPNG());
            Destroy(sheet);
        }
        var sb = new StringBuilder($"[{p.Name}]");
        foreach (var q in pairs)
            sb.Append($"  {q.Name}: frames {q.FramesHit}/{q.Frames} avg {(q.Frames > 0 ? q.SumVerts / (float)q.Frames : 0f):F1} max {q.MaxVerts} verts, depth {q.MaxDepth * 100f:F1} cm");
        log.AppendLine(sb.ToString());
        Report += sb + "\n";
        foreach (var q in pairs) Report += Where(q);

        if (phaseIndex + 1 < phases.Length) { StartPhase(phaseIndex + 1); return; }

        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report + "\n" + log);
        Time.captureFramerate = 0;
        Application.runInBackground = runInBackground;
        AnimeCharacterController.UseInputOverride = false;
        AnimeCharacterController.InputOverride = Vector2.zero;
        AnimeCharacterController.WalkOverride = AnimeCharacterController.SprintOverride = false;
        foreach (var s in snaps) Destroy(s);
        Destroy(cam.gameObject);
        rt.Release();
        Done = true;
        Destroy(gameObject);
    }

    // ------------------------------------------------------------------ measuring

    private void BuildPairs()
    {
        Mesh mesh = body.sharedMesh;
        // Coverage comes from the imported mesh (same vertex order as any fixed copy), so a fix that moves cloth
        // can't hide its own clipping by changing what counts as covered.
        var source = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(body);
        Mesh original = source != null && source.sharedMesh != null && source.sharedMesh.vertexCount == mesh.vertexCount ? source.sharedMesh : mesh;
        var v = original.vertices;
        var n = original.normals;
        var mats = body.sharedMaterials;
        int Sub(string key) => System.Array.FindIndex(mats, m => m.name.Contains(key));
        jacketSubmesh = Sub("007_01_Tops");
        int[] Verts(int sub, float minY, float maxY)
        {
            var set = new HashSet<int>(mesh.GetIndices(sub));
            set.RemoveWhere(i => v[i].y < minY || v[i].y > maxY);
            var a = new int[set.Count]; set.CopyTo(a); return a;
        }
        int[] skin = Verts(Sub("Body_00_SKIN"), 0.55f, 1.10f);
        int[] skirt = Concat(Verts(Sub("Bottoms_01_CLOTH_01"), 0f, 2f), Verts(Sub("Bottoms_01_CLOTH_03"), 0f, 2f));
        int[] jacket = Verts(jacketSubmesh, 0f, 1.10f);
        edge = new bool[v.Length];
        foreach (string layer in new[] { "Bottoms_01_CLOTH_01", "Bottoms_01_CLOTH_03", "007_01_Tops" }) MarkOpenEdges(mesh.GetTriangles(Sub(layer)), v);

        // In the bind pose, an inner vertex is covered if the outer layer passes over it (closest outer points
        // within the radius, inner point on their inner side).
        int[] Covered(int[] inner, int[] outer, float radius)
        {
            var list = new List<int>();
            foreach (int i in inner)
                if (Signed(v[i], outer, v, n, radius, out _) < 0f) list.Add(i);
            return list.ToArray();
        }
        pairs = new[]
        {
            new Pair { Name = "skin>skirt", Inner = Covered(skin, skirt, 0.05f), Outer = skirt, Radius = 0.05f },
            new Pair { Name = "skirt>jacket", Inner = Covered(skirt, jacket, 0.05f), Outer = jacket, Radius = 0.05f },
            new Pair { Name = "skin>jacket", Inner = Covered(skin, jacket, 0.06f), Outer = jacket, Radius = 0.06f },
        };
        log.AppendLine($"mesh {mesh.name}, coverage from {original.name}");
        foreach (var q in pairs) log.AppendLine($"{q.Name}: {q.Inner.Length} covered vertices, {q.Outer.Length} outer");
        worldV = new Vector3[v.Length];
        worldN = new Vector3[v.Length];
        bindV = v;
        int hipsBone = System.Array.FindIndex(body.bones, b => b.name == "J_Bip_C_Hips");
        bindHips = hipsBone >= 0 ? mesh.bindposes[hipsBone].inverse.MultiplyPoint3x4(Vector3.zero) : new Vector3(0f, 0.95f, 0f);
    }

    /// <summary>Where a pair clipped this phase: vertex-frames by bind-pose height (3 cm) and angle (30 deg, 0 = front, + = her right).</summary>
    private string Where(Pair q)
    {
        var bins = new Dictionary<(int, int), int>();
        for (int i = 0; i < q.Inner.Length; i++)
        {
            if (q.Hits[i] == 0) continue;
            Vector3 d = bindV[q.Inner[i]] - bindHips;
            int h = Mathf.FloorToInt(bindV[q.Inner[i]].y / 0.03f);
            int a = Mathf.RoundToInt(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg / 30f);
            bins.TryGetValue((h, a), out int c);
            bins[(h, a)] = c + q.Hits[i];
        }
        if (bins.Count == 0) return "";
        var top = new List<KeyValuePair<(int, int), int>>(bins);
        top.Sort((x, y) => y.Value.CompareTo(x.Value));
        var sb = new StringBuilder($"    {q.Name} where:");
        for (int i = 0; i < Mathf.Min(6, top.Count); i++)
            sb.Append($"  y{top[i].Key.Item1 * 0.03f:F2} {top[i].Key.Item2 * 30}deg x{top[i].Value}");
        return sb.Append('\n').ToString();
    }

    private static int[] Concat(int[] a, int[] b) { var r = new int[a.Length + b.Length]; a.CopyTo(r, 0); b.CopyTo(r, a.Length); return r; }

    /// <summary>
    /// Flags the vertices on a cloth layer's open edges (hem, front opening). A point next to an edge isn't
    /// covered by that layer, so it can't clip through it. Vertices are welded by position first, so UV seams
    /// don't count as edges.
    /// </summary>
    private void MarkOpenEdges(int[] tris, Vector3[] v)
    {
        var weld = new Dictionary<Vector3Int, int>();
        int Id(int i)
        {
            var key = Vector3Int.RoundToInt(v[i] * 10000f);
            if (!weld.TryGetValue(key, out int id)) weld[key] = id = weld.Count;
            return id;
        }
        var count = new Dictionary<(int, int), int>();
        var verts = new Dictionary<(int, int), (int, int)>();
        for (int t = 0; t < tris.Length; t += 3)
            for (int e = 0; e < 3; e++)
            {
                int a = tris[t + e], b = tris[t + (e + 1) % 3];
                int ia = Id(a), ib = Id(b);
                var key = ia < ib ? (ia, ib) : (ib, ia);
                count.TryGetValue(key, out int c);
                count[key] = c + 1;
                verts[key] = (a, b);
            }
        var open = new HashSet<int>();
        foreach (var kv in count)
            if (kv.Value == 1) { open.Add(Id(verts[kv.Key].Item1)); open.Add(Id(verts[kv.Key].Item2)); }
        foreach (int i in tris) if (open.Contains(Id(i))) edge[i] = true;
    }

    /// <summary>
    /// Signed distance of p from the outer layer: the plane through the average of its 4 closest vertices
    /// (within radius), oriented by their average normal. Positive = outside. +inf when nothing is close.
    /// </summary>
    private const int K = 4;
    private static readonly int[] best = new int[K];
    private static readonly float[] bestD = new float[K];

    private static float Signed(Vector3 p, int[] outer, Vector3[] v, Vector3[] n, float radius, out bool near)
    {
        for (int k = 0; k < K; k++) { best[k] = -1; bestD[k] = radius * radius; }
        foreach (int o in outer)
        {
            float d = (v[o] - p).sqrMagnitude;
            if (d >= bestD[K - 1]) continue;
            int k = K - 1;
            while (k > 0 && d < bestD[k - 1]) { bestD[k] = bestD[k - 1]; best[k] = best[k - 1]; k--; }
            bestD[k] = d; best[k] = o;
        }
        near = best[0] >= 0;
        if (!near) return float.PositiveInfinity;
        Vector3 c = Vector3.zero, nn = Vector3.zero; int cnt = 0;
        for (int k = 0; k < K; k++) if (best[k] >= 0) { c += v[best[k]]; nn += n[best[k]]; cnt++; }
        c /= cnt;
        return Vector3.Dot(p - c, nn.normalized);
    }

    private void Bake()
    {
        for (int i = 0; i < skins.Length; i++)
        {
            skins[i].BakeMesh(snapMeshes[i], true);
            snaps[i].transform.SetPositionAndRotation(skins[i].transform.position, skins[i].transform.rotation);
        }
        int b = System.Array.IndexOf(skins, body);
        snapMeshes[b].GetVertices(bakedV);
        snapMeshes[b].GetNormals(bakedN);
        Matrix4x4 m = Matrix4x4.TRS(body.transform.position, body.transform.rotation, Vector3.one);
        for (int i = 0; i < bakedV.Count; i++) { worldV[i] = m.MultiplyPoint3x4(bakedV[i]); worldN[i] = m.MultiplyVector(bakedN[i]); }
    }

    private void Measure()
    {
        foreach (var q in pairs)
        {
            int count = 0; float depth = 0f;
            for (int j = 0; j < q.Inner.Length; j++)
            {
                float s = Signed(worldV[q.Inner[j]], q.Outer, worldV, worldN, q.Radius, out bool near);
                // Closest to an open edge: the point is beside the outer layer (hem, front opening), not under it.
                if (near && s > PenetrationTolerance && !edge[best[0]]) { count++; depth = Mathf.Max(depth, s); q.Hits[j]++; }
            }
            q.FrameVerts = count; q.FrameDepth = depth;
            q.Frames++;
            q.SumVerts += count;
            if (count > 0) q.FramesHit++;
            q.MaxVerts = Mathf.Max(q.MaxVerts, count);
            q.MaxDepth = Mathf.Max(q.MaxDepth, depth);
        }
    }

    // ------------------------------------------------------------------ rendering

    private void RenderColumn(int col)
    {
        foreach (var s in skins) s.forceRenderingOff = true;
        foreach (var s in snaps) s.SetActive(true);
        Vector3 hips = player.position + Vector3.up * 0.9f;
        Vector3 fwd = player.forward;
        int b = System.Array.IndexOf(skins, body);
        int[] jacketTris = null;

        bool close = views == CloseViews;
        for (int r = 0; r < views.Length; r++)
        {
            Vector3 dir; float dist = close ? 1.2f : 2.3f, fov = 40f;
            switch (views[r])
            {
                case View.Game:
                case View.GameNoJacket:
                    dir = gameCam != null ? gameCam.transform.forward : fwd; break;
                case View.BackLow:
                    dir = Quaternion.AngleAxis(-28f, Vector3.Cross(Vector3.up, fwd)) * fwd; dist = 1.9f; break;
                case View.FrontLeft:
                    dir = -(Quaternion.AngleAxis(-50f, Vector3.up) * fwd); break;
                case View.FrontRight:
                    dir = -(Quaternion.AngleAxis(50f, Vector3.up) * fwd); break;
                case View.BackHip:
                    dir = fwd; break;
                default:
                    dir = -Vector3.Cross(Vector3.up, fwd); break;
            }
            if (views[r] == View.GameNoJacket)
            {
                jacketTris = snapMeshes[b].GetTriangles(jacketSubmesh);
                snapMeshes[b].SetTriangles(new int[0], jacketSubmesh);
            }
            cam.fieldOfView = fov;
            cam.transform.SetPositionAndRotation(hips - dir.normalized * dist, Quaternion.LookRotation(dir));
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            readback.ReadPixels(new Rect(0, 0, CellW, CellH), 0, 0);
            readback.Apply();
            RenderTexture.active = null;
            sheet.SetPixels(col * CellW, (views.Length - 1 - r) * CellH, CellW, CellH, readback.GetPixels());
            if (jacketTris != null) { snapMeshes[b].SetTriangles(jacketTris, jacketSubmesh); jacketTris = null; }
        }

        foreach (var s in snaps) s.SetActive(false);
        foreach (var s in skins) s.forceRenderingOff = false;
    }
}
#endif
