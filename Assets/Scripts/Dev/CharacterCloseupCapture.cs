#if UNITY_EDITOR
using System.IO;
using UnityEngine;

/// <summary>
/// Editor-only: renders close-ups of the player (front, 3/4, side, back at hip and full-body framing) into one
/// contact sheet, for checking cloth/limb clipping and edge quality. Optionally drives the player
/// (AnimeCharacterController input override) and waits before shooting so spring bones are settled/in motion.
/// Usage (Play Mode): CharacterCloseupCapture.Shoot(path, input, settleSeconds).
/// </summary>
public sealed class CharacterCloseupCapture : MonoBehaviour
{
    private const int CellW = 400, CellH = 500;
    private static readonly float[] Yaws = { 0f, 45f, 90f, 180f, 270f };

    public static bool Done { get; private set; }

    private string path;
    private float shootAt;
    private bool hipCloseup;

    public static void Shoot(string outputPath, Vector2 input, float settleSeconds, bool hipCloseup)
    {
        Done = false;
        AnimeCharacterController.UseInputOverride = true;
        AnimeCharacterController.InputOverride = input;
        var c = new GameObject("__CloseupCapture").AddComponent<CharacterCloseupCapture>();
        c.path = outputPath;
        c.shootAt = Time.time + settleSeconds;
        c.hipCloseup = hipCloseup;
    }

    private void LateUpdate()
    {
        if (Time.time < shootAt) return;
        var player = GameObject.Find("Player").transform;
        var cam = new GameObject("__CloseupCam").AddComponent<Camera>();
        cam.enabled = false;
        cam.fieldOfView = 30f;
        cam.nearClipPlane = 0.05f;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0.22f, 0.23f, 0.26f);
        var rt = new RenderTexture(CellW, CellH, 24) { antiAliasing = 8 };
        var tex = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);
        var sheet = new Texture2D(CellW * Yaws.Length, CellH * 2, TextureFormat.RGB24, false);

        for (int row = 0; row < 2; row++)
        {
            bool close = row == 0 && hipCloseup;
            float height = close ? 0.95f : 0.85f;
            float dist = close ? 1.3f : 3.6f;
            for (int i = 0; i < Yaws.Length; i++)
            {
                Vector3 dir = Quaternion.Euler(0f, player.eulerAngles.y + Yaws[i], 0f) * Vector3.forward;
                Vector3 center = player.position + Vector3.up * height;
                cam.transform.SetPositionAndRotation(center + dir * dist, Quaternion.LookRotation(-dir));
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, CellW, CellH), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                sheet.SetPixels(i * CellW, (1 - row) * CellH, CellW, CellH, tex.GetPixels());
            }
        }
        sheet.Apply();
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, sheet.EncodeToPNG());
        rt.Release();
        Destroy(cam.gameObject);
        AnimeCharacterController.UseInputOverride = false;
        Done = true;
        Destroy(gameObject);
    }
}
#endif
