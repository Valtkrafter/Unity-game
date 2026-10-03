using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// Raises character fidelity and removes aliasing without lowering any quality setting, and claws back
/// the cost with optimisations that don't change the image.
///
/// Quality:
/// - Character textures had no mipmaps: they shimmered/sparkled as soon as Nino was a few metres away.
///   Now: mipmaps (Kaiser filter keeps line art crisp), trilinear + 8x anisotropic, BC7 instead of DXT1/DXT5
///   (no block artefacts on anime gradients), alpha coverage preserved for alpha-clipped hair.
/// - MSAA 4x for geometry and inverted-hull outline edges (SMAA High stays on for cel-shading steps).
/// Performance (image-neutral):
/// - Mipmaps cut texture bandwidth/cache misses whenever the character isn't filling the screen.
/// - Skinned meshes no longer recompute bounds every frame (updateWhenOffscreen off, fixed generous bounds).
/// - Hair/skirt/bust spring bones run as one Burst job instead of 23 MonoBehaviour updates.
/// </summary>
public static class RenderQualityUpgrade
{
    private const string CharacterTextures = "Assets/Models/Nino Nakano";
    private const string PcPipelineAsset = "Assets/Settings/PC_RPAsset.asset";

    [MenuItem("Tools/Character/Upgrade Render Quality")]
    public static void ApplyMenu() => Debug.Log(Apply());

    public static string Apply()
    {
        var log = new StringBuilder();
        log.AppendLine(UpgradeTextures());
        log.AppendLine(EnableMsaa());
        log.AppendLine(FixSkinnedBounds());
        log.AppendLine(UseFastSpringBones());
        return log.ToString();
    }

    private static string UseFastSpringBones()
    {
        var animator = GameObject.Find("Player")?.GetComponentInChildren<Animator>();
        if (animator == null) return "Spring bones: no character";
        if (animator.GetComponent<FastSpringBoneActivator>() == null) Undo.AddComponent<FastSpringBoneActivator>(animator.gameObject);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(animator.gameObject.scene);
        return "Spring bones: FastSpringBoneActivator (Burst job runtime) on " + animator.name;
    }

    private static string UpgradeTextures()
    {
        int count = 0;
        foreach (string guid in AssetDatabase.FindAssets("t:Texture2D", new[] { CharacterTextures }))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (!(AssetImporter.GetAtPath(path) is TextureImporter ti)) continue;

            ti.mipmapEnabled = true;
            ti.mipmapFilter = TextureImporterMipFilter.KaiserFilter;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 8;
            ti.textureCompression = TextureImporterCompression.CompressedHQ;
            if (ti.DoesSourceTextureHaveAlpha())
            {
                // Alpha-clipped hair strands keep their thickness in the smaller mips.
                ti.mipMapsPreserveCoverage = true;
                ti.alphaTestReferenceValue = 0.5f;
            }
            var standalone = ti.GetPlatformTextureSettings("Standalone");
            standalone.overridden = true;
            standalone.format = TextureImporterFormat.BC7;
            standalone.compressionQuality = 100;
            standalone.maxTextureSize = Mathf.Max(ti.maxTextureSize, 2048);
            ti.SetPlatformTextureSettings(standalone);
            ti.SaveAndReimport();
            count++;
        }
        return $"Textures: {count} upgraded (mipmaps Kaiser, trilinear, aniso 8, BC7, alpha coverage preserved)";
    }

    private static string EnableMsaa()
    {
        var urp = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(PcPipelineAsset);
        urp.msaaSampleCount = 4;
        EditorUtility.SetDirty(urp);
        AssetDatabase.SaveAssets();

        var cam = Camera.main;
        if (cam != null)
        {
            Undo.RecordObject(cam, "MSAA");
            cam.allowMSAA = true;
            var data = cam.GetComponent<UniversalAdditionalCameraData>();
            Undo.RecordObject(data, "SMAA");
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
            data.antialiasingQuality = AntialiasingQuality.High;
        }
        return $"Anti-aliasing: MSAA {urp.msaaSampleCount}x on {urp.name} + SMAA High on the camera";
    }

    private static string FixSkinnedBounds()
    {
        var player = GameObject.Find("Player");
        if (player == null) return "Skinned bounds: no Player";
        var sb = new StringBuilder("Skinned bounds:");
        foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            Undo.RecordObject(smr, "Skinned bounds");
            // Box around the whole character incl. raised arms, flying hair and jumps (local space of the root bone).
            Transform root = smr.rootBone != null ? smr.rootBone : smr.transform;
            Vector3 centerWorld = player.transform.position + Vector3.up * 0.9f;
            smr.localBounds = new Bounds(root.InverseTransformPoint(centerWorld), Vector3.one * 2.6f);
            smr.updateWhenOffscreen = false;
            sb.Append($" {smr.name}");
        }
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
        return sb + " -> fixed 2.6 m bounds, updateWhenOffscreen off";
    }
}
