#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

/// <summary>
/// Editor-only Play Mode capture for comparing animation clips on the real in-game Nino (spring bones, jacket cloth,
/// toon shading). Each clip is played directly through a PlayableGraph while the model is moved at the clip's
/// authored ground speed, so the feet don't slide and the motion looks exactly as authored. Three tracking views (front 3/4,
/// side, and the gameplay view from behind) are written per frame as
/// JPGs into outDir/clipName/, ready to be turned into GIFs (Tools/AnimConvert/make_gifs.py).
///
/// Usage (Play Mode): AnimShowcaseCapture.Begin(outDir, "Assets/...anim|seconds|fps|groundSpeed", ...); poll Done.
/// </summary>
public sealed class AnimShowcaseCapture : MonoBehaviour
{
    public static bool Done { get; private set; }
    public static string Report { get; private set; } = "";

    private const int CellW = 360, CellH = 480;
    private const float SettleSeconds = 1.0f;
    private static readonly string[] Views = { "front", "side", "game" };

    private struct Entry { public string Path; public float Seconds; public int Fps; public float Speed; }

    private List<Entry> entries;
    private string outDir;

    public static void Begin(string outputDir, params string[] specs)
    {
        Done = false;
        Report = "";
        var cap = new GameObject("__AnimShowcaseCapture").AddComponent<AnimShowcaseCapture>();
        cap.outDir = outputDir;
        cap.entries = specs.Select(s =>
        {
            var p = s.Split('|');
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            return new Entry { Path = p[0], Seconds = float.Parse(p[1], ci), Fps = int.Parse(p[2]), Speed = p.Length > 3 ? float.Parse(p[3], ci) : 0f };
        }).ToList();
    }

    private IEnumerator Start()
    {
        Application.runInBackground = true;
        var player = GameObject.Find("Player");
        var model = player.GetComponentInChildren<Animator>();
        var modelT = model.transform;
        Vector3 localPos = modelT.localPosition;
        Quaternion localRot = modelT.localRotation;
        var disabled = new List<Behaviour>();
        foreach (var b in player.GetComponentsInChildren<MonoBehaviour>())
        {
            string n = b.GetType().Name;
            if ((n == "AnimeCharacterController" || n == "DiscardRootMotion") && b.enabled) { b.enabled = false; disabled.Add(b); }
        }
        bool rootMotion = model.applyRootMotion;
        var controller = model.runtimeAnimatorController;
        model.runtimeAnimatorController = null;
        model.applyRootMotion = false; // clips are in place; the capture moves the model at the clip's ground speed

        var cam = new GameObject("__ShowcaseCam").AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 28f;
        cam.nearClipPlane = 0.05f;
        var rt = new RenderTexture(CellW, CellH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var read = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);

        foreach (var e in entries)
        {
            var clip = UnityEditor.AssetDatabase.LoadAllAssetsAtPath(e.Path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
            string name = Path.GetFileNameWithoutExtension(e.Path).Replace("X Bot@", "Mixamo_").Replace(" ", "");
            string dir = Path.Combine(outDir, name);
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.jpg")) File.Delete(f);

            modelT.localPosition = localPos;
            modelT.localRotation = localRot;
            Vector3 facing = modelT.forward; facing.y = 0f; facing.Normalize();
            var graph = PlayableGraph.Create("Showcase");
            graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            var output = AnimationPlayableOutput.Create(graph, "Anim", model);
            var playable = AnimationClipPlayable.Create(graph, clip);
            output.SetSourcePlayable(playable);
            graph.Play();

            Time.captureFramerate = e.Fps;
            int settle = Mathf.RoundToInt(SettleSeconds * e.Fps);
            int frames = Mathf.RoundToInt(e.Seconds * e.Fps);
            Vector3 smoothFocus = Vector3.zero;
            Vector3 start = Vector3.zero;
            for (int i = -settle; i < frames; i++)
            {
                if (i == -settle) playable.SetTime(0);
                if (i == 0) playable.SetTime(0);
                modelT.localPosition = localPos;
                modelT.position += facing * e.Speed * ((i + settle) / (float)e.Fps);
                yield return new WaitForEndOfFrame();
                Vector3 hips = model.GetBoneTransform(HumanBodyBones.Hips).position;
                Vector3 focus = new Vector3(hips.x, modelT.position.y + 0.82f, hips.z);
                smoothFocus = i <= -settle + 1 ? focus : Vector3.Lerp(smoothFocus, focus, 0.5f);
                if (i < 0) continue;
                if (i == 0) start = modelT.position;
                for (int view = 0; view < Views.Length; view++)
                {
                    Vector3 right = Vector3.Cross(Vector3.up, facing);
                    if (view == 2)
                    {
                        // Gameplay view: behind and above like ThirdPersonOrbitCamera (12 deg pitch, ~4.2 m).
                        cam.transform.position = smoothFocus - facing * 4.1f + Vector3.up * 1.05f;
                        cam.transform.LookAt(smoothFocus + Vector3.up * 0.25f);
                    }
                    else
                    {
                        Vector3 viewDir = view == 0 ? (facing * 0.8f - right * 0.6f).normalized : right;
                        cam.transform.position = smoothFocus + viewDir * 3.6f + Vector3.up * 0.15f;
                        cam.transform.LookAt(smoothFocus);
                    }
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = null;
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, CellW, CellH), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    File.WriteAllBytes(Path.Combine(dir, $"{Views[view]}_{i:D4}.jpg"), read.EncodeToJPG(92));
                }
            }
            Vector3 travel = modelT.position - start; travel.y = 0f;
            Report += $"{name}: {frames} frames @ {e.Fps} fps, ground speed {travel.magnitude / ((frames - 1) / (float)e.Fps):F2} m/s, clip {clip.length:F2}s\n";
            graph.Destroy();
        }

        Time.captureFramerate = 0;
        modelT.localPosition = localPos;
        modelT.localRotation = localRot;
        model.applyRootMotion = rootMotion;
        model.runtimeAnimatorController = controller;
        model.Rebind();
        foreach (var b in disabled) b.enabled = true;
        rt.Release();
        Destroy(cam.gameObject);
        File.WriteAllText(Path.Combine(outDir, "report.txt"), Report);
        Done = true;
        Destroy(gameObject);
    }
}
#endif
