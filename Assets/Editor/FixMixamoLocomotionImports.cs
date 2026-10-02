using UnityEditor;
using UnityEngine;
using System.IO;

public static class FixMixamoLocomotionImports
{
    [MenuItem("Tools/Fix Mixamo Locomotion Imports")]
    public static void Fix()
    {
        string folder = "Assets/Animations/Female Locomotion Pack";
        string[] fbxGuids = AssetDatabase.FindAssets("t:Model", new[] { folder });

        if (fbxGuids.Length == 0)
        {
            Debug.LogWarning("[FixMixamo] No FBX files found in " + folder);
            return;
        }

        int count = 0;

        foreach (string guid in fbxGuids)
        {
            string assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (!assetPath.EndsWith(".fbx", System.StringComparison.OrdinalIgnoreCase))
                continue;

            ModelImporter importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
            {
                Debug.LogWarning("[FixMixamo] Could not get ModelImporter for: " + assetPath);
                continue;
            }

            // --- Rig tab ---
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            // --- Animation tab: configure clip settings ---
            // Retrieve the default clip that Unity auto-creates from the FBX
            ModelImporterClipAnimation[] srcClips = importer.defaultClipAnimations;

            if (srcClips.Length == 0)
            {
                Debug.Log("[FixMixamo] No clips in: " + assetPath + " — skipping clip config.");
            }
            else
            {
                string fileName = Path.GetFileNameWithoutExtension(assetPath).ToLowerInvariant();
                bool isJump = fileName.Contains("jump");

                // For every clip in this FBX (usually just one from Mixamo)
                for (int i = 0; i < srcClips.Length; i++)
                {
                    // Locomotion clips loop; jump does not
                    srcClips[i].loopTime = !isJump;

                    // Bake root motion into pose so the Animator drives movement
                    srcClips[i].lockRootRotation      = true;  // Bake Into Pose: Rotation
                    srcClips[i].keepOriginalOrientation = true;
                    srcClips[i].rotationOffset          = 0f;

                    srcClips[i].lockRootHeightY       = true;  // Bake Into Pose: Position (Y)
                    srcClips[i].keepOriginalPositionY  = true;
                    srcClips[i].heightOffset            = 0f;

                    srcClips[i].lockRootPositionXZ     = true;  // Bake Into Pose: Position (XZ)
                    srcClips[i].keepOriginalPositionXZ = true;

                    // Loop pose blending for smooth cycles
                    srcClips[i].loopPose = !isJump;
                }

                importer.clipAnimations = srcClips;
            }

            importer.SaveAndReimport();
            count++;
            Debug.Log("[FixMixamo] ✓ Fixed: " + assetPath);
        }

        Debug.Log($"[FixMixamo] Done — fixed {count} FBX file(s).");
    }
}
