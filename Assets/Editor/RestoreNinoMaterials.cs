using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

/// <summary>
/// Editor utility to restore Nino's materials and SkinnedMeshRenderers
/// to their pristine VRM specification using Universal Render Pipeline/Unlit.
/// </summary>
public static class RestoreNinoMaterials
{
    private static readonly Dictionary<string, int> TransparentMaterialQueues = new Dictionary<string, int>()
    {
        { "N00_000_00_EyeIris_00_EYE (Instance)", 3000 },
        { "N00_000_00_FaceEyeline_00_FACE (Instance)", 3000 },
        { "N00_000_00_EyeHighlight_00_EYE (Instance)", 3500 },
        { "N00_000_00_FaceBrow_00_FACE (Instance)", 4000 },
        { "N00_000_00_FaceEyelash_00_FACE (Instance)", 4500 }
    };

    [MenuItem("Tools/Restore Nino Original VRM Materials (URP)")]
    public static void RestoreAll()
    {
        RestoreMaterials();
        ConfigureShadows();
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("[RestoreNinoMaterials] ★ Nino materials and shadow settings successfully restored.");
    }

    public static void RestoreMaterials()
    {
        var unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null)
        {
            Debug.LogError("[RestoreNinoMaterials] 'Universal Render Pipeline/Unlit' shader not found!");
            return;
        }

        string matFolder = "Assets/Models/Nino Nakano/5394265126170879566.Materials";
        var guids = AssetDatabase.FindAssets("t:Material", new[] { matFolder });

        foreach (var g in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(g);
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null) continue;

            Texture tex = mat.mainTexture;
            if (tex == null && mat.HasProperty("_BaseMap")) tex = mat.GetTexture("_BaseMap");
            if (tex == null && mat.HasProperty("_MainTex")) tex = mat.GetTexture("_MainTex");

            mat.shader = unlitShader;
            mat.SetColor("_BaseColor", Color.white);
            mat.SetColor("_Color", Color.white);
            if (tex != null)
            {
                mat.SetTexture("_BaseMap", tex);
                mat.SetTexture("_MainTex", tex);
            }

            // Clear legacy/incompatible keywords
            foreach (var kw in mat.shaderKeywords)
            {
                mat.DisableKeyword(kw);
            }

            if (TransparentMaterialQueues.ContainsKey(mat.name))
            {
                int queue = TransparentMaterialQueues[mat.name];
                mat.SetFloat("_Surface", 1f); // Transparent
                mat.SetFloat("_Blend", 0f);   // Alpha
                mat.SetFloat("_AlphaClip", 0f);
                mat.SetFloat("_Cull", 0f);    // Two-sided
                mat.SetFloat("_ZWrite", 0f);
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                mat.SetOverrideTag("RenderType", "Transparent");
                mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                mat.renderQueue = queue;
            }
            else
            {
                // TransparentCutout (hair cards, clothing, skin, mouth)
                mat.SetFloat("_Surface", 0f); // Opaque
                mat.SetFloat("_Blend", 0f);
                mat.SetFloat("_AlphaClip", 1f); // Alpha testing
                mat.SetFloat("_Cutoff", 0.5f);
                mat.SetFloat("_Cull", 0f);    // Two-sided to avoid clipping
                mat.SetFloat("_ZWrite", 1f);
                mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.One);
                mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.Zero);
                mat.SetOverrideTag("RenderType", "TransparentCutout");
                mat.EnableKeyword("_ALPHATEST_ON");
                mat.renderQueue = 2450;
            }

            EditorUtility.SetDirty(mat);
        }
    }

    public static void ConfigureShadows()
    {
        // 1. Scene instance
        var nino = GameObject.Find("Nino_Model");
        if (nino != null)
        {
            var smrs = nino.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                string name = smr.name.ToLowerInvariant();
                if (name.Contains("face"))
                {
                    smr.receiveShadows = false;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                else if (name.Contains("hair") || name.Contains("body"))
                {
                    smr.receiveShadows = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                EditorUtility.SetDirty(smr);
            }
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(nino.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(nino.scene);
        }

        // 2. Prefab asset
        string prefabPath = "Assets/Models/Nino Nakano/5394265126170879566.prefab";
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
        if (prefab != null)
        {
            var smrs = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            foreach (var smr in smrs)
            {
                string name = smr.name.ToLowerInvariant();
                if (name.Contains("face"))
                {
                    smr.receiveShadows = false;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                else if (name.Contains("hair") || name.Contains("body"))
                {
                    smr.receiveShadows = true;
                    smr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                }
                EditorUtility.SetDirty(smr);
            }
            PrefabUtility.SavePrefabAsset(prefab);
        }
    }
}
