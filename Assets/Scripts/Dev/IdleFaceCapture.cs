#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Editor-only Play Mode check of the idle face through the REAL Animator (face layer states FaceIdle / FaceMove).
/// Locked 24 fps: stands still for 7.5 s (the idle face: blinks and one closed-mouth "^_^"; the special idle only fires after 8 s),
/// walks for 2.5 s (the face crossfades to the happy walk smile), then stands again for 1.5 s.
/// Per frame it logs the face layer state, the blend shape weights and the REAL mouth height measured on the skinned mesh
/// (vertical extent of the FaceMouth submesh after skinning + blend shapes), and saves a face close-up JPG every 3rd frame.
///
/// Usage (Play Mode): IdleFaceCapture.Begin("Captures/IdleFace"); poll Done / Report.
/// </summary>
public sealed class IdleFaceCapture : MonoBehaviour
{
    public static bool Done { get; private set; }
    public static string Report { get; private set; } = "";

    private const int Fps = 24, CellW = 360, CellH = 480, StandFrames = 180, WalkFrames = 60, StopFrames = 36;
    private static readonly string[] Shapes = { "Fcl_EYE_Close", "Fcl_EYE_Joy", "Fcl_EYE_Fun", "Fcl_MTH_Fun", "Fcl_MTH_Joy", "Fcl_BRW_Fun" };

    private string outDir;

    public static void Begin(string outputDir)
    {
        Done = false;
        Report = "";
        var cap = new GameObject("__IdleFaceCapture").AddComponent<IdleFaceCapture>();
        cap.outDir = Path.GetFullPath(outputDir);
    }

    private IEnumerator Start()
    {
        Application.runInBackground = true;
        UnityEditor.EditorApplication.update += UnityEditor.EditorApplication.QueuePlayerLoopUpdate;   // keep ticking while the editor is in the background
        Directory.CreateDirectory(outDir);

        var player = GameObject.Find("Player");
        var animator = player.GetComponentInChildren<Animator>();
        var face = animator.GetComponentsInChildren<SkinnedMeshRenderer>().First(s => s.name == "Face");
        var mesh = face.sharedMesh;
        int[] idx = Shapes.Select(n => mesh.GetBlendShapeIndex(n)).ToArray();

        // the vertices of the mouth material (lips, inside of the mouth, teeth): their height is how open the mouth is
        int mouthSub = Enumerable.Range(0, face.sharedMaterials.Length).First(i => face.sharedMaterials[i] != null && face.sharedMaterials[i].name.Contains("FaceMouth"));
        var mouthVerts = new HashSet<int>(mesh.GetIndices(mouthSub)).ToArray();
        var baked = new Mesh();

        AnimeCharacterController.UseInputOverride = true;
        AnimeCharacterController.InputOverride = Vector2.zero;
        Time.captureFramerate = Fps;

        var cam = new GameObject("__IdleFaceCam").AddComponent<Camera>();
        cam.enabled = false;
        cam.nearClipPlane = 0.03f;
        var rt = new RenderTexture(CellW, CellH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var read = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);
        var log = new StringBuilder();
        log.AppendLine($"Face mesh blend shapes: {mesh.blendShapeCount}; mouth submesh {mouthSub} ({mouthVerts.Length} vertices); animator layers {animator.layerCount}");

        int total = StandFrames + WalkFrames + StopFrames;
        var maxStand = new float[Shapes.Length];
        var maxWalk = new float[Shapes.Length];
        float standMouthMin = float.MaxValue, standMouthMax = 0f, walkMouthMax = 0f;
        string lastState = "";
        var switches = new List<string>();
        var closeFrames = new List<int>();
        var joyFrames = new List<int>();

        for (int i = 0; i < total; i++)
        {
            AnimeCharacterController.InputOverride = (i >= StandFrames && i < StandFrames + WalkFrames) ? Vector2.up : Vector2.zero;
            yield return new WaitForEndOfFrame();

            var st = animator.GetCurrentAnimatorStateInfo(1);
            var nx = animator.GetNextAnimatorStateInfo(1);
            bool blending = animator.IsInTransition(1);
            string state = st.IsName("FaceIdle") ? "FaceIdle" : st.IsName("FaceMove") ? "FaceMove" : "?";
            if (blending) state += ">" + (nx.IsName("FaceIdle") ? "FaceIdle" : nx.IsName("FaceMove") ? "FaceMove" : "?");
            if (state != lastState) { switches.Add($"f{i} ({i / (float)Fps:F2}s) {state}"); lastState = state; }

            var w = new float[Shapes.Length];
            var sb = new StringBuilder();
            for (int k = 0; k < Shapes.Length; k++)
            {
                w[k] = idx[k] < 0 ? -1f : face.GetBlendShapeWeight(idx[k]);
                sb.Append($" {Shapes[k].Replace("Fcl_", "")}={w[k]:F0}");
            }

            // real mouth height on the skinned mesh, in mm (the Face renderer sits at scale 1)
            face.BakeMesh(baked, false);
            var vs = baked.vertices;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (int v in mouthVerts) { lo = Mathf.Min(lo, vs[v].y); hi = Mathf.Max(hi, vs[v].y); }
            float mouthH = (hi - lo) * 1000f;

            bool standing = i < StandFrames || i >= StandFrames + WalkFrames + 12;       // the last 12 walk-to-idle frames are still the crossfade
            if (i < StandFrames)
            {
                for (int k = 0; k < Shapes.Length; k++) maxStand[k] = Mathf.Max(maxStand[k], w[k]);
                standMouthMin = Mathf.Min(standMouthMin, mouthH);
                standMouthMax = Mathf.Max(standMouthMax, mouthH);
                if (w[0] > 50f) closeFrames.Add(i);
                if (w[1] > 50f) joyFrames.Add(i);
            }
            else if (i < StandFrames + WalkFrames)
            {
                for (int k = 0; k < Shapes.Length; k++) maxWalk[k] = Mathf.Max(maxWalk[k], w[k]);
                walkMouthMax = Mathf.Max(walkMouthMax, mouthH);
            }
            log.AppendLine($"f{i:D3} {(standing ? "stand" : "walk ")} faceState={state} faceLayerW={animator.GetLayerWeight(1):F2} speed={animator.GetFloat("Speed"):F2} mouthH={mouthH:F1}mm{sb}");

            if (i % 3 == 0)
            {
                Vector3 facing = player.transform.forward; facing.y = 0f; facing.Normalize();
                Vector3 right = Vector3.Cross(Vector3.up, facing);
                Vector3 p = player.transform.position;
                cam.fieldOfView = 24f;
                cam.transform.position = p + Vector3.up * 1.52f + facing * 0.9f + right * -0.05f;
                cam.transform.LookAt(p + Vector3.up * 1.50f);
                Shot(cam, rt, read, Path.Combine(outDir, $"face_{i:D3}.jpg"));
            }
        }

        string Ranges(float[] m) => string.Join(" ", Shapes.Select((n, k) => $"{n.Replace("Fcl_", "")}={m[k]:F0}"));
        log.AppendLine("face layer switches: " + string.Join(" | ", switches));
        log.AppendLine("STANDING 0..7.5 s  max weights: " + Ranges(maxStand));
        log.AppendLine($"STANDING mouth height {standMouthMin:F1} .. {standMouthMax:F1} mm (closed = about 25 mm; 30+ mm = open)");
        log.AppendLine("STANDING EYE_Close > 50 on frames: " + (closeFrames.Count == 0 ? "-" : string.Join(",", closeFrames)));
        log.AppendLine("STANDING EYE_Joy (^_^) > 50 on frames: " + (joyFrames.Count == 0 ? "-" : string.Join(",", joyFrames)));
        log.AppendLine("WALKING max weights: " + Ranges(maxWalk) + $" | max mouth height {walkMouthMax:F1} mm");
        Report = log.ToString();
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report);

        Time.captureFramerate = 0;
        AnimeCharacterController.UseInputOverride = false;
        AnimeCharacterController.InputOverride = Vector2.zero;
        UnityEditor.EditorApplication.update -= UnityEditor.EditorApplication.QueuePlayerLoopUpdate;
        rt.Release();
        Destroy(cam.gameObject);
        Done = true;
        Destroy(gameObject);
    }

    private static void Shot(Camera cam, RenderTexture rt, Texture2D read, string path)
    {
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = null;
        RenderTexture.active = rt;
        read.ReadPixels(new Rect(0, 0, CellW, CellH), 0, 0);
        read.Apply();
        RenderTexture.active = null;
        File.WriteAllBytes(path, read.EncodeToJPG(92));
    }
}
#endif
