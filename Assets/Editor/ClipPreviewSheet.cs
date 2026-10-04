using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Edit-mode preview of a humanoid clip on a fresh copy of Nino: samples N evenly spaced frames and renders a side
/// view row and a front-3/4 view row into one PNG. Spring bones don't run in edit mode, so hair and skirt stay at rest;
/// it is meant for judging the body motion quickly. Use the Play Mode capture for the final look.
/// </summary>
public static class ClipPreviewSheet
{
    private const string NinoPrefab = "Assets/Models/Nino Nakano/5394265126170879566.prefab";
    private const int PreviewLayer = 30;

    public static void Render(string clipPath, string outPng, int frames = 8, int cellW = 200, int cellH = 300, float start01 = 0f, float end01 = 1f)
    {
        var clip = LoadClip(clipPath);
        var go = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NinoPrefab));
        go.hideFlags = HideFlags.DontSave;
        var origin = new Vector3(200f, 0f, 200f);
        var cam = new GameObject("__PreviewCam").AddComponent<Camera>();
        cam.gameObject.hideFlags = HideFlags.DontSave;
        cam.enabled = false;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.82f, 0.84f, 0.88f);
        cam.fieldOfView = 30f;
        var rt = new RenderTexture(cellW, cellH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        var read = new Texture2D(cellW, cellH, TextureFormat.RGB24, false);
        var sheet = new Texture2D(cellW * frames, cellH * 2, TextureFormat.RGB24, false);
        try
        {
            var animator = go.GetComponent<Animator>();
            animator.applyRootMotion = false;
            // Isolate the preview on its own layer and re-skin on every render (edit mode otherwise shows stale poses).
            foreach (var tr in go.GetComponentsInChildren<Transform>(true)) tr.gameObject.layer = PreviewLayer;
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true)) smr.forceMatrixRecalculationPerRender = true;
            cam.cullingMask = 1 << PreviewLayer;
            for (int i = 0; i < frames; i++)
            {
                float t = Mathf.Lerp(start01, end01, frames == 1 ? 0f : i / (float)frames) * clip.length;
                go.transform.SetPositionAndRotation(origin, Quaternion.identity);
                clip.SampleAnimation(go, t);
                var hips = animator.GetBoneTransform(HumanBodyBones.Hips).position;
                var focus = new Vector3(hips.x, origin.y + 0.8f, hips.z);
                for (int row = 0; row < 2; row++)
                {
                    Vector3 dir = row == 0 ? Vector3.right : new Vector3(0.6f, 0f, 1f).normalized;
                    cam.transform.position = focus + dir * 4.2f + Vector3.up * 0.1f;
                    cam.transform.LookAt(focus);
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.Render();
                    RenderTexture.active = rt;
                    read.ReadPixels(new Rect(0, 0, cellW, cellH), 0, 0);
                    read.Apply();
                    RenderTexture.active = null;
                    sheet.SetPixels(i * cellW, (1 - row) * cellH, cellW, cellH, read.GetPixels());
                }
            }
            sheet.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(outPng));
            File.WriteAllBytes(outPng, sheet.EncodeToPNG());
        }
        finally
        {
            cam.targetTexture = null;
            Object.DestroyImmediate(cam.gameObject);
            Object.DestroyImmediate(go);
            rt.Release();
        }
    }

    /// <summary>A .anim clip, or the (non-preview) clip inside a model file.</summary>
    public static AnimationClip LoadClip(string path) =>
        AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));
}
