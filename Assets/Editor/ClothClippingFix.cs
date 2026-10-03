using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fixes Nino's cloth clipping:
/// 1. Skirt through jacket: the jacket hem is skinned rigidly to hips/thighs (no cloth bones), while the skirt is
///    spring-driven, so the skirt's sides/back poke through the jacket. The part of the skirt that the jacket
///    always covers is tucked inward in a copy of the Body mesh (Body_ClothFix.asset). The visible front of the
///    skirt is untouched; the original imported mesh is not modified.
/// 2. Covered skirt chains (sides/back) get their own, calmer spring settings; the visible front keeps its bounce.
/// 3. Hands in the jacket: adds ArmClothClearance to the character.
/// </summary>
public static class ClothClippingFix
{
    private const string FixedMeshPath = "Assets/Models/Nino Nakano/ClothFix/Body_ClothFix.asset";
    private const string JacketMaterial = "Tops_01_CLOTH";
    private const string CollarMaterial = "007_02";
    private const string SkirtMaterial = "Bottoms_01_CLOTH";

    private const float TuckDistance = 0.03f;   // how far the hidden skirt is pulled in (worst measured poke: 3.6 cm)
    private const float TuckFullBelow = 0.98f;  // mesh-space height: full tuck below, none above the waist
    private const float TuckNoneAbove = 1.04f;

    [MenuItem("Tools/Character/Apply Cloth Clipping Fix")]
    public static void ApplyMenu() => Debug.Log(Apply());

    public static string Apply()
    {
        var log = new StringBuilder();
        var nino = GameObject.Find("Player").GetComponentInChildren<Animator>();
        var body = nino.GetComponentsInChildren<SkinnedMeshRenderer>().First(s => s.name == "Body");

        // Always rebuild from the original imported mesh (the prefab's), never from a previous fix.
        Mesh source = PrefabUtility.GetCorrespondingObjectFromSource(body).sharedMesh;

        Mesh mesh = Object.Instantiate(source);
        mesh.name = source.name + "_ClothFix";
        log.AppendLine(TuckHiddenSkirt(mesh, body, out int tucked));

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FixedMeshPath));
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(FixedMeshPath);
        if (existing != null) { EditorUtility.CopySerialized(mesh, existing); mesh = existing; }
        else AssetDatabase.CreateAsset(mesh, FixedMeshPath);
        AssetDatabase.SaveAssets();

        Undo.RecordObject(body, "Cloth fix mesh");
        body.sharedMesh = mesh;

        var clearance = nino.GetComponent<ArmClothClearance>() ?? Undo.AddComponent<ArmClothClearance>(nino.gameObject);
        var so = new SerializedObject(clearance);
        so.FindProperty("clothRenderer").objectReferenceValue = body;
        so.FindProperty("clothSubmesh").intValue = SubmeshIndex(body, JacketMaterial, CollarMaterial);
        so.ApplyModifiedProperties();
        log.AppendLine($"ArmClothClearance on {nino.name}, jacket submesh {so.FindProperty("clothSubmesh").intValue}");

        log.AppendLine(SplitCoveredSkirtSprings(nino.transform));
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(nino.gameObject.scene);
        return log.ToString();
    }

    private static int SubmeshIndex(SkinnedMeshRenderer smr, string contains, string excludes)
    {
        var mats = smr.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
            if (mats[i].name.Contains(contains) && !mats[i].name.Contains(excludes)) return i;
        return -1;
    }

    private static string TuckHiddenSkirt(Mesh mesh, SkinnedMeshRenderer body, out int tucked)
    {
        var verts = mesh.vertices;
        var mats = body.sharedMaterials;
        var bindposes = mesh.bindposes;
        var bones = body.bones;

        // Bind-pose frame in mesh space: hips axis and the character's forward (towards the bust).
        Vector3 BindPos(string bone) => bindposes[System.Array.FindIndex(bones, b => b.name == bone)].inverse.MultiplyPoint3x4(Vector3.zero);
        Vector3 hips = BindPos("J_Bip_C_Hips");
        Vector3 forward = BindPos("J_Sec_L_Bust1") - BindPos("J_Bip_C_UpperChest");
        forward.y = 0f; forward.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, forward);

        float Angle(Vector3 p) { Vector3 d = p - hips; return Mathf.Atan2(Vector3.Dot(d, right), Vector3.Dot(d, forward)) * Mathf.Rad2Deg; }
        float Radius(Vector3 p) { Vector3 d = p - hips; d.y = 0f; return d.magnitude; }

        var jacket = new List<(float a, float h, float r)>();
        var skirtIdx = new HashSet<int>();
        for (int s = 0; s < mesh.subMeshCount; s++)
        {
            string m = mats[s].name;
            if (m.Contains(JacketMaterial) && !m.Contains(CollarMaterial))
                foreach (int i in mesh.GetIndices(s)) jacket.Add((Angle(verts[i]), verts[i].y, Radius(verts[i])));
            else if (m.Contains(SkirtMaterial))
                foreach (int i in mesh.GetIndices(s)) skirtIdx.Add(i);
        }

        // A skirt point is hidden if the jacket passes outside it at the same angle and height.
        bool Covered(float a, float h, float r) => jacket.Any(j => Mathf.Abs(Mathf.DeltaAngle(j.a, a)) < 5f && Mathf.Abs(j.h - h) < 0.04f && j.r > r - 0.01f);

        tucked = 0;
        foreach (int i in skirtIdx)
        {
            Vector3 p = verts[i];
            float a = Angle(p), h = p.y, r = Radius(p);
            // Fraction of the neighbourhood (+-12 deg) that is covered: fades the tuck out towards the jacket's opening.
            int covered = 0;
            for (int k = -2; k <= 2; k++) if (Covered(a + k * 6f, h, r)) covered++;
            float weight = (covered / 5f) * Mathf.InverseLerp(TuckNoneAbove, TuckFullBelow, h);
            if (weight <= 0f) continue;
            Vector3 inward = hips - p; inward.y = 0f;
            float amount = Mathf.Min(TuckDistance * weight, inward.magnitude * 0.5f);
            verts[i] = p + inward.normalized * amount;
            tucked++;
        }
        mesh.vertices = verts;
        mesh.RecalculateBounds();
        return $"Tucked {tucked}/{skirtIdx.Count} hidden skirt vertices inward by up to {TuckDistance * 100f:F0} cm";
    }

    /// <summary>Moves the side/back skirt chains (always under the jacket) into their own calmer spring.</summary>
    private static string SplitCoveredSkirtSprings(Transform root)
    {
        var springType = System.AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("VRM.VRMSpringBone")).FirstOrDefault(t => t != null);
        if (springType == null) return "VRMSpringBone type not found";
        var springs = root.GetComponentsInChildren(springType, true).Cast<MonoBehaviour>().ToList();
        var skirt = springs.Where(s => new SerializedObject(s).FindProperty("m_comment").stringValue == "Skirt").ToList();
        if (skirt.Count == 0) return "No 'Skirt' springs found";

        var covered = springs.FirstOrDefault(s => new SerializedObject(s).FindProperty("m_comment").stringValue == "SkirtCovered");
        var host = skirt[0].gameObject;
        if (covered == null) covered = (MonoBehaviour)Undo.AddComponent(host, springType);
        var dst = new SerializedObject(covered);
        var template = new SerializedObject(skirt[0]);

        var movedRoots = new List<Object>();
        foreach (var s in skirt)
        {
            var so = new SerializedObject(s);
            var roots = so.FindProperty("RootBones");
            for (int i = roots.arraySize - 1; i >= 0; i--)
            {
                var t = roots.GetArrayElementAtIndex(i).objectReferenceValue;
                if (t != null && (t.name.Contains("SkirtSide") || t.name.Contains("SkirtBack")))
                {
                    movedRoots.Add(t);
                    roots.DeleteArrayElementAtIndex(i);
                }
            }
            so.ApplyModifiedProperties();
        }

        var existingRoots = dst.FindProperty("RootBones");
        for (int i = 0; i < existingRoots.arraySize; i++) movedRoots.Add(existingRoots.GetArrayElementAtIndex(i).objectReferenceValue);
        movedRoots = movedRoots.Where(t => t != null).Distinct().ToList();

        dst.FindProperty("m_comment").stringValue = "SkirtCovered";
        dst.FindProperty("m_stiffnessForce").floatValue = 0.8f;  // front: 0.25 - hidden part follows the hips closely
        dst.FindProperty("m_gravityPower").floatValue = 0.1f;
        dst.FindProperty("m_gravityDir").vector3Value = template.FindProperty("m_gravityDir").vector3Value;
        dst.FindProperty("m_dragForce").floatValue = 0.7f;
        dst.FindProperty("m_hitRadius").floatValue = template.FindProperty("m_hitRadius").floatValue;
        dst.FindProperty("m_center").objectReferenceValue = template.FindProperty("m_center").objectReferenceValue;
        existingRoots.arraySize = movedRoots.Count;
        for (int i = 0; i < movedRoots.Count; i++) existingRoots.GetArrayElementAtIndex(i).objectReferenceValue = movedRoots[i];
        var srcCols = template.FindProperty("ColliderGroups");
        var dstCols = dst.FindProperty("ColliderGroups");
        dstCols.arraySize = srcCols.arraySize;
        for (int i = 0; i < srcCols.arraySize; i++)
            dstCols.GetArrayElementAtIndex(i).objectReferenceValue = srcCols.GetArrayElementAtIndex(i).objectReferenceValue;
        dst.ApplyModifiedProperties();
        return $"SkirtCovered spring: {movedRoots.Count} side/back chains (stiffness 0.8, drag 0.7); front chains keep 0.25/0.4";
    }
}
