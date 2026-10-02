using UnityEngine;
using UnityEditor;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// One-shot editor tool:
///   1. Purges all M_HuTao materials and the HuoTao model folder.
///   2. Migrates every Nino material from UniGLTF/UniUnlit → VRM/MToon
///      with proper cel-shading, alpha, and render-queue settings.
/// </summary>
public static class PurgeHuTaoAndUpgradeNino
{
    // ── MToon blend-mode constants (from MToonDefinition) ──
    const float BlendMode_Opaque       = 0f;
    const float BlendMode_Cutout       = 1f;
    const float BlendMode_Transparent  = 2f;
    const float BlendMode_TransparentZ = 3f; // Transparent + ZWrite

    [MenuItem("Tools/Purge HuTao + Upgrade Nino to MToon")]
    public static void Execute()
    {
        PurgeHuTao();
        UpgradeNinoMaterials();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[NinoUpgrade] ★ All done.");
    }

    // ────────────────────────────────────────────────────────
    //  STEP 1 — Purge Hu Tao
    // ────────────────────────────────────────────────────────
    static void PurgeHuTao()
    {
        // Delete M_HuTao* materials from Assets/Materials/
        string matDir = "Assets/Materials";
        if (AssetDatabase.IsValidFolder(matDir))
        {
            var guids = AssetDatabase.FindAssets("M_HuTao", new[] { matDir });
            foreach (var g in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(g);
                if (AssetDatabase.DeleteAsset(path))
                    Debug.Log("[PurgeHuTao] Deleted material: " + path);
                else
                    Debug.LogWarning("[PurgeHuTao] Failed to delete: " + path);
            }
        }

        // Delete HuoTao model folder
        string huTaoDir = "Assets/Models/HuoTao";
        if (AssetDatabase.IsValidFolder(huTaoDir))
        {
            if (AssetDatabase.DeleteAsset(huTaoDir))
                Debug.Log("[PurgeHuTao] Deleted folder: " + huTaoDir);
            else
                Debug.LogWarning("[PurgeHuTao] Failed to delete folder: " + huTaoDir);
        }
        else
        {
            Debug.Log("[PurgeHuTao] HuoTao folder not found — already clean.");
        }
    }

    // ────────────────────────────────────────────────────────
    //  STEP 2 — Upgrade Nino to MToon
    // ────────────────────────────────────────────────────────
    static void UpgradeNinoMaterials()
    {
        Shader mtoon = Shader.Find("VRM/MToon");
        if (mtoon == null)
        {
            Debug.LogError("[NinoUpgrade] VRM/MToon shader not found! Is UniVRM installed?");
            return;
        }

        string[] searchFolders = new[] { "Assets/Models/Nino Nakano" };
        var guids = AssetDatabase.FindAssets("t:Material", searchFolders);

        if (guids.Length == 0)
        {
            Debug.LogWarning("[NinoUpgrade] No materials found in Nino Nakano folder.");
            return;
        }

        int count = 0;
        foreach (var g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            Material mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            // Preserve original texture before shader swap
            Texture mainTex = null;
            if (mat.HasProperty("_MainTex")) mainTex = mat.GetTexture("_MainTex");
            if (mainTex == null && mat.HasProperty("_BaseMap")) mainTex = mat.GetTexture("_BaseMap");

            Color baseColor = Color.white;
            if (mat.HasProperty("_Color")) baseColor = mat.GetColor("_Color");
            else if (mat.HasProperty("_BaseColor")) baseColor = mat.GetColor("_BaseColor");

            // ── Switch shader ──
            mat.shader = mtoon;

            // ── Re-apply base texture and color ──
            mat.SetTexture("_MainTex", mainTex);
            mat.SetColor("_Color", baseColor);

            // ── Cel-shading ramp — crisp anime shadows ──
            mat.SetFloat("_ShadeToony", 0.95f);   // Sharp step between lit and shaded
            mat.SetFloat("_ShadeShift", -0.1f);    // Slight offset for the shadow boundary
            mat.SetColor("_ShadeColor", new Color(0.65f, 0.65f, 0.75f, 1f)); // Cool-toned shadow

            // Indirect lighting so shadows don't go full black
            mat.SetFloat("_IndirectLightIntensity", 0.4f);

            // ── Per-material blend mode, alpha, and render queue ──
            string matName = mat.name.ToLowerInvariant();
            ConfigureBlendMode(mat, matName);

            // MToon version tag (required by the shader)
            mat.SetFloat("_MToonVersion", 38f);
            mat.SetFloat("_DebugMode", 0f);

            // Outline defaults (subtle)
            mat.SetFloat("_OutlineWidthMode", 1f); // WorldCoordinates
            mat.SetFloat("_OutlineWidth", 0.0f);   // Off for now; user can tune later
            mat.SetFloat("_OutlineColorMode", 0f);  // FixedColor
            mat.SetColor("_OutlineColor", Color.black);

            EditorUtility.SetDirty(mat);
            count++;
            Debug.Log("[NinoUpgrade] ✓ " + mat.name + " → MToon | queue=" + mat.renderQueue);
        }

        Debug.Log("[NinoUpgrade] Upgraded " + count + " materials to VRM/MToon.");
    }

    /// <summary>
    /// Determines the correct blend mode based on material name semantics.
    /// </summary>
    static void ConfigureBlendMode(Material mat, string matName)
    {
        bool isTransparentOverlay =
            matName.Contains("eyehighlight") ||
            matName.Contains("facebrow") ||
            matName.Contains("faceeyelash") ||
            matName.Contains("faceeyeline");

        bool isCutout =
            matName.Contains("facemouth");

        bool isEyeIris =
            matName.Contains("eyeiris");

        if (isTransparentOverlay)
        {
            // Transparent overlays: eye highlights, brows, eyelashes, eye lines
            SetMToonBlendMode(mat, BlendMode_Transparent);
            mat.renderQueue = 3000;
        }
        else if (isEyeIris)
        {
            // Eye iris: transparent + ZWrite so it doesn't z-fight with whites
            SetMToonBlendMode(mat, BlendMode_TransparentZ);
            mat.renderQueue = 2950;
        }
        else if (isCutout)
        {
            // Mouth interior: use cutout for clean edges
            SetMToonBlendMode(mat, BlendMode_Cutout);
            mat.SetFloat("_Cutoff", 0.5f);
            mat.renderQueue = 2450;
        }
        else
        {
            // Everything else (body, face, hair, clothes): opaque
            SetMToonBlendMode(mat, BlendMode_Opaque);
            mat.renderQueue = 2000;
        }
    }

    /// <summary>
    /// Sets MToon blend mode keywords and properties correctly.
    /// </summary>
    static void SetMToonBlendMode(Material mat, float mode)
    {
        mat.SetFloat("_BlendMode", mode);

        if (mode == BlendMode_Opaque)
        {
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetFloat("_ZWrite", 1f);
            mat.SetFloat("_AlphaToMask", 0f);
            SetKeywords(mat, "_ALPHATEST_ON", false, "_ALPHABLEND_ON", false, "_ALPHAPREMULTIPLY_ON", false);
            mat.SetOverrideTag("RenderType", "Opaque");
        }
        else if (mode == BlendMode_Cutout)
        {
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
            mat.SetFloat("_ZWrite", 1f);
            mat.SetFloat("_AlphaToMask", 1f);
            SetKeywords(mat, "_ALPHATEST_ON", true, "_ALPHABLEND_ON", false, "_ALPHAPREMULTIPLY_ON", false);
            mat.SetOverrideTag("RenderType", "TransparentCutout");
        }
        else if (mode == BlendMode_Transparent)
        {
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetFloat("_AlphaToMask", 0f);
            SetKeywords(mat, "_ALPHATEST_ON", false, "_ALPHABLEND_ON", true, "_ALPHAPREMULTIPLY_ON", false);
            mat.SetOverrideTag("RenderType", "Transparent");
        }
        else if (mode == BlendMode_TransparentZ)
        {
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 1f);
            mat.SetFloat("_AlphaToMask", 0f);
            SetKeywords(mat, "_ALPHATEST_ON", false, "_ALPHABLEND_ON", true, "_ALPHAPREMULTIPLY_ON", false);
            mat.SetOverrideTag("RenderType", "Transparent");
        }
    }

    static void SetKeywords(Material mat, string k1, bool v1, string k2, bool v2, string k3, bool v3)
    {
        if (v1) mat.EnableKeyword(k1); else mat.DisableKeyword(k1);
        if (v2) mat.EnableKeyword(k2); else mat.DisableKeyword(k2);
        if (v3) mat.EnableKeyword(k3); else mat.DisableKeyword(k3);
    }
}
