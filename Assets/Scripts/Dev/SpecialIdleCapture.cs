#if UNITY_EDITOR
using System.Collections;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

/// <summary>
/// Editor-only Play Mode check of the special idle (the Hmph) through the REAL Animator (controller transitions, the face layer
/// that hands the face over to the special, the VRM spring bones). Standing still, it fires the SpecialIdle trigger and records
/// 6 s at a locked 24 fps: a face close-up and a front 3/4 body view per frame (JPGs in outDir), and a log line per frame with the
/// animator state, the face layer weight and the blend shape weights that matter for the hmph.
///
/// Usage (Play Mode): SpecialIdleCapture.Begin("Captures/SpecialIdle"); poll Done / Report.
/// </summary>
public sealed class SpecialIdleCapture : MonoBehaviour
{
    public static bool Done { get; private set; }
    public static string Report { get; private set; } = "";

    private const int Fps = 24, CellW = 360, CellH = 480;
    private static readonly string[] Shapes =
    {
        "Fcl_EYE_Close", "Fcl_EYE_Fun", "Fcl_EYE_Angry", "Fcl_BRW_Angry", "Fcl_BRW_Fun",
        "Fcl_MTH_Small", "Fcl_MTH_Angry", "Nino_cheek_puff", "Nino_mouth_pout",
    };

    private string outDir;

    public static void Begin(string outputDir)
    {
        Done = false;
        Report = "";
        var cap = new GameObject("__SpecialIdleCapture").AddComponent<SpecialIdleCapture>();
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
        var max = new float[Shapes.Length];

        AnimeCharacterController.UseInputOverride = true;
        AnimeCharacterController.InputOverride = Vector2.zero;
        Time.captureFramerate = Fps;

        var cam = new GameObject("__SpecialCam").AddComponent<Camera>();
        cam.enabled = false;
        cam.nearClipPlane = 0.03f;
        var rt = new RenderTexture(CellW, CellH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var read = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);
        var log = new StringBuilder();
        log.AppendLine($"Face mesh blend shapes: {mesh.blendShapeCount}; indices of {string.Join(",", Shapes)} = {string.Join(",", idx)}; animator layers {animator.layerCount}");

        for (int i = 0; i < Fps; i++) yield return new WaitForEndOfFrame();     // 1 s of idle first
        animator.SetTrigger("SpecialIdle");

        Vector3 facing = player.transform.forward; facing.y = 0f; facing.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, facing);
        int total = 6 * Fps;
        int enteredAt = -1, leftAt = -1;
        for (int i = 0; i < total; i++)
        {
            yield return new WaitForEndOfFrame();
            var st = animator.GetCurrentAnimatorStateInfo(0);
            bool special = st.IsName("SpecialIdle");
            bool blending = animator.IsInTransition(0);
            if (special && enteredAt < 0) enteredAt = i;
            if (!special && enteredAt >= 0 && leftAt < 0 && !blending) leftAt = i;
            var sb = new StringBuilder();
            for (int k = 0; k < Shapes.Length; k++)
            {
                float w = idx[k] < 0 ? -1f : face.GetBlendShapeWeight(idx[k]);
                max[k] = Mathf.Max(max[k], w);
                sb.Append($" {Shapes[k].Replace("Fcl_", "")}={w:F0}");
            }
            log.AppendLine($"f{i:D3} state={(special ? "Special" : "Locomotion")}{(blending ? "(blend)" : "")} norm={st.normalizedTime:F3} faceLayerW={animator.GetLayerWeight(1):F2}{sb}");

            // face close-up and front 3/4 body view
            Vector3 p = player.transform.position;
            cam.fieldOfView = 24f;
            cam.transform.position = p + Vector3.up * 1.52f + facing * 0.9f + right * -0.05f;
            cam.transform.LookAt(p + Vector3.up * 1.50f);
            Shot(cam, rt, read, Path.Combine(outDir, $"face_{i:D3}.jpg"));
            cam.fieldOfView = 30f;
            Vector3 viewDir = (facing * 0.8f - right * 0.6f).normalized;
            cam.transform.position = p + Vector3.up * 0.95f + viewDir * 3.3f;
            cam.transform.LookAt(p + Vector3.up * 0.85f);
            Shot(cam, rt, read, Path.Combine(outDir, $"body_{i:D3}.jpg"));
        }

        log.AppendLine($"Special state entered at frame {enteredAt}, back in Locomotion at frame {leftAt}");
        log.AppendLine("max weights: " + string.Join(" ", Shapes.Select((n, k) => $"{n.Replace("Fcl_", "")}={max[k]:F0}")));
        Report = log.ToString();
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report);

        Time.captureFramerate = 0;
        AnimeCharacterController.UseInputOverride = false;
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
