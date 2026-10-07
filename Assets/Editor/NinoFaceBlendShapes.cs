using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Adds the custom Face shape keys that were made in Blender (Nino_cheek_puff, Nino_mouth_pout) to the imported VRM Face mesh
/// as blend shapes, so the Hmph's puffed cheeks and pouting lips play in Unity. VRoid itself has neither shape.
///
/// Source: Tools/AnimConvert/blendshapes/nino_face_custom.json (written by blender_export_streams.py: export_custom_shapes).
/// The deltas are in the Face mesh's local space (Blender local space with Y and Z swapped). Vertices are matched by POSITION
/// (the Blender basis against the Unity mesh), so a different vertex order does not matter. New shapes are appended after the 57
/// VRoid shapes, so the VRM blend shape clips (which refer to shape indices) keep working. Safe to run twice (existing names are skipped).
/// Re-importing / re-extracting the VRM resets the mesh; run this again afterwards.
/// </summary>
public static class NinoFaceBlendShapes
{
    private const string FaceMeshPath = "Assets/Models/Nino Nakano/5394265126170879566.Meshes/Face (merged).baked.asset";
    private const string JsonPath = "Tools/AnimConvert/blendshapes/nino_face_custom.json";
    private const float Cell = 0.001f;          // spatial hash cell (1 mm)
    private const float MatchTolerance = 0.00005f;  // 0.05 mm

    [System.Serializable] private class Shape { public int[] idx; public float[] d; }
    [System.Serializable] private class Data { public string mesh; public int vertexCount; public float[] basis; public Dictionary<string, Shape> shapes; }

    [MenuItem("Tools/Character/Add Custom Face Blend Shapes (from Blender)")]
    public static void AddMenu() => Debug.Log(Add());

    public static string Add()
    {
        var log = new StringBuilder();
        if (!File.Exists(JsonPath)) return $"Missing {JsonPath}";
        var data = Newtonsoft.Json.JsonConvert.DeserializeObject<Data>(File.ReadAllText(JsonPath));
        var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(FaceMeshPath);
        if (mesh == null) return $"Missing mesh {FaceMeshPath}";

        // spatial hash of the Blender basis (already in Unity mesh-local axes)
        var grid = new Dictionary<long, List<int>>();
        for (int i = 0; i < data.vertexCount; i++)
        {
            long k = CellKey(data.basis[3 * i], data.basis[3 * i + 1], data.basis[3 * i + 2]);
            if (!grid.TryGetValue(k, out var list)) grid[k] = list = new List<int>(2);
            list.Add(i);
        }

        var verts = mesh.vertices;
        var match = new int[verts.Length];
        int unmatched = 0;
        float worst = 0f;
        for (int v = 0; v < verts.Length; v++)
        {
            match[v] = -1;
            float best = MatchTolerance;
            int cx = Mathf.FloorToInt(verts[v].x / Cell), cy = Mathf.FloorToInt(verts[v].y / Cell), cz = Mathf.FloorToInt(verts[v].z / Cell);
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (!grid.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz, true), out var cand)) continue;
                foreach (int i in cand)
                {
                    var b = new Vector3(data.basis[3 * i], data.basis[3 * i + 1], data.basis[3 * i + 2]);
                    float dist = (b - verts[v]).magnitude;
                    if (dist <= best) { best = dist; match[v] = i; }
                }
            }
            if (match[v] < 0) unmatched++; else worst = Mathf.Max(worst, best);
        }
        log.AppendLine($"Face mesh '{mesh.name}': {verts.Length} vertices (Blender {data.vertexCount}), matched {verts.Length - unmatched}, unmatched {unmatched}, worst match {worst * 1000f:F3} mm");
        if (unmatched > verts.Length / 50) return log + "Too many unmatched vertices: the axis mapping is wrong, nothing was changed.";

        var existing = new HashSet<string>();
        for (int i = 0; i < mesh.blendShapeCount; i++) existing.Add(mesh.GetBlendShapeName(i));

        foreach (var kv in data.shapes)
        {
            if (existing.Contains(kv.Key)) { log.AppendLine($"  {kv.Key}: already on the mesh, skipped"); continue; }
            var byBlender = new Dictionary<int, Vector3>();
            for (int j = 0; j < kv.Value.idx.Length; j++)
                byBlender[kv.Value.idx[j]] = new Vector3(kv.Value.d[3 * j], kv.Value.d[3 * j + 1], kv.Value.d[3 * j + 2]);
            var delta = new Vector3[verts.Length];
            int moved = 0;
            float max = 0f;
            for (int v = 0; v < verts.Length; v++)
            {
                if (match[v] < 0 || !byBlender.TryGetValue(match[v], out var d)) continue;
                delta[v] = d;
                moved++;
                max = Mathf.Max(max, d.magnitude);
            }
            mesh.AddBlendShapeFrame(kv.Key, 100f, delta, null, null);
            log.AppendLine($"  {kv.Key}: added, {moved} vertices move, max {max * 1000f:F1} mm");
        }
        EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        log.AppendLine($"Face mesh now has {mesh.blendShapeCount} blend shapes.");
        return log.ToString();
    }

    private static long CellKey(float x, float y, float z) =>
        CellKey(Mathf.FloorToInt(x / Cell), Mathf.FloorToInt(y / Cell), Mathf.FloorToInt(z / Cell), true);

    private static long CellKey(int x, int y, int z, bool _) =>
        ((long)(x + 1048576) << 42) ^ ((long)(y + 1048576) << 21) ^ (long)(z + 1048576);
}
