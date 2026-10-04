using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Fixes Nino's cloth clipping (Tools/Character/Apply Cloth Clipping Fix):
/// 1. Jacket hem physics. In the VRoid model the jacket's lower part is skinned rigidly to the hips and thighs (its
///    "CoatSkirt" bones are unused template leftovers), so legs went straight through it and the spring-driven skirt
///    poked out of it. Twelve chains of joints are added around the hips (J_Sec_JacketHem_*), placed on the jacket's
///    surface, and the jacket's lower vertices are re-weighted onto them in a copy of the Body mesh
///    (Body_ClothFix.asset; the imported mesh is not modified). JacketHemCloth simulates them and keeps them outside
///    the skirt and the thighs.
/// 2. Covered skirt chains (sides/back) get their own, calmer spring settings; the visible front keeps its bounce.
/// 3. Hands in the jacket: adds ArmClothClearance to the character.
/// The skirt keeps its imported shape: an earlier version tucked its hidden part 3 cm inward, which pushed it inside
/// her hips, so her body showed through the skirt as soon as the jacket could move.
/// </summary>
public static class ClothClippingFix
{
    private const string FixedMeshPath = "Assets/Models/Nino Nakano/ClothFix/Body_ClothFix.asset";
    private const string JacketMaterial = "Tops_01_CLOTH";
    private const string CollarMaterial = "007_02";
    private const string SkirtMaterial = "Bottoms_01_CLOTH";
    private const string SkinMaterial = "Body_00_SKIN";

    // Jacket hem chains: angles from the front (degrees, + = character's right). The jacket is open at the front
    // (its edges are at +-32 degrees at the waist and +-40 at the hem), so the two front chains sit just inside the
    // edges and never blend across the opening.
    private static readonly float[] HemAngles = { -167f, -142f, -117f, -92f, -67f, -42f, 42f, 67f, 92f, 117f, 142f, 167f };
    // Joint heights in mesh space: pivot at the waist, two bends, and an unweighted end just below the hem (0.71).
    private static readonly float[] HemJointHeights = { 1.00f, 0.90f, 0.80f, 0.70f };
    private const float HemBlendTop = 1.00f;    // above: original skinning
    private const float HemBlendBottom = 0.975f; // below: fully on the hem chains (a half-driven band can't make way for the skirt)
    private const float JointBlend = 0.25f;     // share of a segment blended across each bend
    private const string HemPrefix = "J_Sec_JacketHem_";

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
        // Drop bones added by a previous run (they are appended after the imported ones).
        var originalBones = body.bones.Take(source.bindposes.Length).ToArray();

        log.AppendLine(RemoveJacketSpring(nino.transform));
        log.AppendLine(FitSkirtToBody(mesh, body, originalBones));
        var chains = RigJacketHem(mesh, body, originalBones, log);

        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FixedMeshPath));
        var existing = AssetDatabase.LoadAssetAtPath<Mesh>(FixedMeshPath);
        if (existing != null)
        {
            EditorUtility.CopySerialized(mesh, existing);
            EditorUtility.SetDirty(existing); // CopySerialized alone doesn't get the asset written to disk
            mesh = existing;
        }
        else AssetDatabase.CreateAsset(mesh, FixedMeshPath);
        AssetDatabase.SaveAssets();

        Undo.RecordObject(body, "Cloth fix mesh");
        body.sharedMesh = mesh;
        body.bones = originalBones.Concat(chains.SelectMany(c => c)).ToArray();

        log.AppendLine(SetupJacketCloth(nino, body, originalBones, chains));

        var clearance = nino.GetComponent<ArmClothClearance>() ?? Undo.AddComponent<ArmClothClearance>(nino.gameObject);
        var so = new SerializedObject(clearance);
        so.FindProperty("clothRenderer").objectReferenceValue = body;
        so.FindProperty("clothSubmesh").intValue = SubmeshIndex(body, JacketMaterial, CollarMaterial);
        so.ApplyModifiedProperties();
        log.AppendLine($"ArmClothClearance on {nino.name}, jacket submesh {so.FindProperty("clothSubmesh").intValue}");

        log.AppendLine(SplitCoveredSkirtSprings(nino.transform));
        log.AppendLine(FillSkirtHoles(body));
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(nino.gameObject.scene);
        return log.ToString();
    }

    /// <summary>
    /// The skirt's two layers have cut-outs in their textures: at the front waist, where the front band lies exactly
    /// on the skirt (cut so the two don't z-fight), and on the sides, under the jacket. The band doesn't cover its
    /// cut-out exactly and the cut-outs' pixels are nearly black, so a jagged black line showed across the front of
    /// the skirt, and dark spots on its sides now that the jacket moves. The band uses the skirt's texture layout, so
    /// this works per pixel: wherever the band doesn't cover the skirt, the skirt pixel becomes opaque in the
    /// surrounding skirt colour; the band's own soft edge gets the band's colour instead of black. Writes copies to
    /// ClothFix/ and points the materials at them; the imported textures are not modified.
    /// </summary>
    private static string FillSkirtHoles(SkinnedMeshRenderer body)
    {
        const byte BandCovers = 230; // skirt pixels stay cut out only where the band is (nearly) opaque: a little overlap, no gap
        var mats = body.sharedMaterials;
        Material band = mats.First(m => m.name.Contains(SkirtMaterial + "_02"));
        var bandPixels = ReadPixels(OriginalTexture(band.mainTexture), out int w, out int h);
        var log = new StringBuilder("Skirt texture cut-outs filled:");

        foreach (var layer in mats.Where(m => m.name.Contains(SkirtMaterial) && m != band).Distinct())
        {
            Texture original = OriginalTexture(layer.mainTexture);
            var px = ReadPixels(original, out int lw, out int lh);
            if (lw != w || lh != h) { log.Append($" {layer.name}: size differs from the band's, skipped;"); continue; }
            var fill = new bool[px.Length];
            int filled = 0;
            for (int i = 0; i < px.Length; i++)
                if (px[i].a < 255 && bandPixels[i].a < BandCovers) { fill[i] = true; filled++; }
            Inpaint(px, w, h, fill);
            for (int i = 0; i < px.Length; i++) if (fill[i]) px[i].a = 255;
            Retexture(layer, original, WriteFixedTexture(original, px, w, h));
            log.Append($" {original.name} {filled} px;");
        }

        // The band's soft edge: keep its alpha, give it the band's colour (it renders wherever alpha >= the cutoff).
        Texture bandOriginal = OriginalTexture(band.mainTexture);
        var edge = bandPixels.Select(p => p.a < 255).ToArray();
        Inpaint(bandPixels, w, h, edge);
        Retexture(band, bandOriginal, WriteFixedTexture(bandOriginal, bandPixels, w, h));
        return log.Append($" band edge recoloured ({bandOriginal.name})").ToString();
    }

    private const string TexturesFolder = "Assets/Models/Nino Nakano/5394265126170879566.Textures";
    private const string FixedSuffix = "_ClothFix";

    /// <summary>The imported texture behind a texture, even if a previous run already replaced it with a fixed copy.</summary>
    private static Texture OriginalTexture(Texture t)
    {
        string path = AssetDatabase.GetAssetPath(t);
        if (!System.IO.Path.GetFileNameWithoutExtension(path).EndsWith(FixedSuffix)) return t;
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        return AssetDatabase.LoadAssetAtPath<Texture>($"{TexturesFolder}/{name.Substring(0, name.Length - FixedSuffix.Length)}{System.IO.Path.GetExtension(path)}");
    }

    private static Color32[] ReadPixels(Texture t, out int w, out int h)
    {
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        tex.LoadImage(System.IO.File.ReadAllBytes(AssetDatabase.GetAssetPath(t)));
        w = tex.width; h = tex.height;
        var px = tex.GetPixels32();
        Object.DestroyImmediate(tex);
        return px;
    }

    /// <summary>Gives the marked pixels the colour of the nearest unmarked opaque pixels (grown outward ring by ring).</summary>
    private static void Inpaint(Color32[] px, int w, int h, bool[] marked)
    {
        var known = new bool[px.Length];
        var front = new List<int>();
        for (int i = 0; i < px.Length; i++)
            if (!marked[i] && px[i].a == 255) known[i] = true;
        for (int i = 0; i < px.Length; i++)
            if (!known[i] && HasKnownNeighbour(i)) front.Add(i);
        while (front.Count > 0)
        {
            var colours = new Color32[front.Count];
            for (int f = 0; f < front.Count; f++)
            {
                int i = front[f], x = i % w, y = i / w, n = 0;
                int r = 0, g = 0, b = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h || !known[ny * w + nx]) continue;
                        var c = px[ny * w + nx]; r += c.r; g += c.g; b += c.b; n++;
                    }
                colours[f] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), px[i].a);
            }
            var next = new HashSet<int>();
            for (int f = 0; f < front.Count; f++) { px[front[f]] = colours[f]; known[front[f]] = true; }
            foreach (int i in front)
            {
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int j = ny * w + nx;
                        if (!known[j]) next.Add(j);
                    }
            }
            front = next.ToList();
        }

        bool HasKnownNeighbour(int i)
        {
            int x = i % w, y = i / w;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < w && ny < h && known[ny * w + nx]) return true;
                }
            return false;
        }
    }

    /// <summary>Saves pixels as ClothFix/&lt;name&gt;_ClothFix.png with the original texture's import settings.</summary>
    private static Texture WriteFixedTexture(Texture original, Color32[] px, int w, int h)
    {
        string path = $"{System.IO.Path.GetDirectoryName(FixedMeshPath)}/{original.name}{FixedSuffix}.png".Replace('\\', '/');
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.SetPixels32(px);
        System.IO.File.WriteAllBytes(path, tex.EncodeToPNG());
        Object.DestroyImmediate(tex);
        AssetDatabase.ImportAsset(path);

        var src = (TextureImporter)AssetImporter.GetAtPath(AssetDatabase.GetAssetPath(original));
        var dst = (TextureImporter)AssetImporter.GetAtPath(path);
        var settings = new TextureImporterSettings();
        src.ReadTextureSettings(settings);
        dst.SetTextureSettings(settings);
        dst.SetPlatformTextureSettings(src.GetDefaultPlatformTextureSettings());
        foreach (string platform in new[] { "Standalone", "Android", "iPhone", "WebGL" })
        {
            var s = src.GetPlatformTextureSettings(platform);
            if (s.overridden) dst.SetPlatformTextureSettings(s);
        }
        dst.SaveAndReimport();
        return AssetDatabase.LoadAssetAtPath<Texture>(path);
    }

    /// <summary>Points every texture slot of a material that uses the original (or an older fixed copy) at the new texture.</summary>
    private static void Retexture(Material material, Texture original, Texture fixedTexture)
    {
        Undo.RecordObject(material, "Skirt texture fix");
        foreach (string prop in material.GetTexturePropertyNames())
        {
            Texture t = material.GetTexture(prop);
            if (t != null && OriginalTexture(t) == original) material.SetTexture(prop, fixedTexture);
        }
        EditorUtility.SetDirty(material);
        AssetDatabase.SaveAssets();
    }

    private static int SubmeshIndex(SkinnedMeshRenderer smr, string contains, string excludes = null)
    {
        var mats = smr.sharedMaterials;
        for (int i = 0; i < mats.Length; i++)
            if (mats[i].name.Contains(contains) && (excludes == null || !mats[i].name.Contains(excludes))) return i;
        return -1;
    }

    /// <summary>Bind-pose frame in mesh space: hips position, forward (towards the chest) and right.</summary>
    private static void BindFrame(Mesh mesh, Transform[] bones, out Vector3 hips, out Vector3 forward, out Vector3 right)
    {
        var bindposes = mesh.bindposes;
        Vector3 BindPos(string bone) => bindposes[System.Array.FindIndex(bones, b => b != null && b.name == bone)].inverse.MultiplyPoint3x4(Vector3.zero);
        hips = BindPos("J_Bip_C_Hips");
        // Midpoint of both bust bones: a single side would skew 'forward' by ~37 degrees.
        Vector3 bust = (BindPos("J_Sec_L_Bust1") + BindPos("J_Sec_R_Bust1")) * 0.5f;
        forward = bust - BindPos("J_Bip_C_UpperChest");
        forward.y = 0f;
        forward.Normalize();
        right = Vector3.Cross(Vector3.up, forward);
    }

    /// <summary>
    /// Makes the skirt move with the body under it, so her hips and bottom can't push through it:
    /// - The skirt's spring bones swing it about pivots 7 cm above the hem, but in the VRoid model points up to 15 cm
    ///   above a pivot carry up to 60% of that bone. Those points move the opposite way, into her bottom when the
    ///   skirt swings out behind a leg. Above its pivot a bone's share fades out within PivotFade.
    /// - Where the skirt fits tightly (waist, hips, bottom), its other weights are blended into the weights of the
    ///   skin under it: the VRoid skirt follows the pelvis only, the skin also follows the thighs.
    /// The lower skirt still swings on its springs. The layers (outer, inner, front band) use the closest
    /// outer-layer vertex's skin, so they move together.
    /// </summary>
    private static string FitSkirtToBody(Mesh mesh, SkinnedMeshRenderer body, Transform[] bones)
    {
        const float Tight = 0.02f, Loose = 0.045f; // distance to the skin: full transfer .. none
        const float PivotFade = 0.035f;
        var verts = mesh.vertices;
        var weights = mesh.boneWeights;
        var mats = body.sharedMaterials;
        var bindposes = mesh.bindposes;
        bool IsSkirtBone(int b) => bones[b].name.Contains("Skirt");
        float PivotHeight(int b) => bindposes[b].inverse.MultiplyPoint3x4(Vector3.zero).y;

        var skin = new HashSet<int>(mesh.GetIndices(SubmeshIndex(body, SkinMaterial))).Where(i => verts[i].y > 0.6f && verts[i].y < 1.2f).ToArray();
        int outerSub = System.Array.FindIndex(mats, m => m.name.Contains(SkirtMaterial + "_01"));
        var outer = new HashSet<int>(mesh.GetIndices(outerSub)).ToArray();
        var skirt = new HashSet<int>(Enumerable.Range(0, mats.Length).Where(s => mats[s].name.Contains(SkirtMaterial)).SelectMany(s => mesh.GetIndices(s)));

        // Per outer-layer vertex: how tight it fits and the skin's weights under it (inverse-distance blend of the 4 closest).
        var fit = new Dictionary<int, (float amount, Dictionary<int, float> skinWeights)>();
        foreach (int o in outer)
        {
            var near = skin.Select(i => (i, d: (verts[i] - verts[o]).magnitude)).OrderBy(x => x.d).Take(4).ToList();
            float amount = Mathf.InverseLerp(Loose, Tight, near[0].d);
            var sw = new Dictionary<int, float>();
            float total = 0f;
            foreach (var (i, d) in near)
            {
                float k = 1f / Mathf.Max(d, 0.002f);
                var w = weights[i];
                void Add(int b, float x) { if (x <= 0f) return; sw.TryGetValue(b, out float v); sw[b] = v + x * k; }
                Add(w.boneIndex0, w.weight0); Add(w.boneIndex1, w.weight1); Add(w.boneIndex2, w.weight2); Add(w.boneIndex3, w.weight3);
                total += k;
            }
            foreach (int b in sw.Keys.ToList()) sw[b] /= total;
            fit[o] = (amount, sw);
        }

        int changed = 0, released = 0;
        foreach (int i in skirt)
        {
            int o = outer.Contains(i) ? i : outer.OrderBy(x => (verts[x] - verts[i]).sqrMagnitude).First();
            var f = fit[o];
            var w = weights[i];
            var acc = new Dictionary<int, float>();
            void Add(int b, float x) { if (x <= 0f) return; acc.TryGetValue(b, out float v); acc[b] = v + x; }
            float toSkin = 0f; // share that follows the skin under the skirt
            foreach (var (b, x) in new[] { (w.boneIndex0, w.weight0), (w.boneIndex1, w.weight1), (w.boneIndex2, w.weight2), (w.boneIndex3, w.weight3) })
            {
                if (x <= 0f) continue;
                if (IsSkirtBone(b))
                {
                    float keep = Mathf.Clamp01(1f - (verts[i].y - PivotHeight(b)) / PivotFade);
                    Add(b, x * keep);
                    toSkin += x * (1f - keep);
                    if (keep < 1f) released++;
                }
                else { Add(b, x * (1f - f.amount)); toSkin += x * f.amount; }
            }
            if (toSkin <= 0f) continue;
            foreach (var kv in f.skinWeights) Add(kv.Key, kv.Value * toSkin);

            var top4 = acc.OrderByDescending(kv => kv.Value).Take(4).ToList();
            float sum = top4.Sum(kv => kv.Value);
            var nw = new BoneWeight();
            for (int t = 0; t < top4.Count; t++)
            {
                float x = top4[t].Value / sum;
                switch (t)
                {
                    case 0: nw.boneIndex0 = top4[t].Key; nw.weight0 = x; break;
                    case 1: nw.boneIndex1 = top4[t].Key; nw.weight1 = x; break;
                    case 2: nw.boneIndex2 = top4[t].Key; nw.weight2 = x; break;
                    case 3: nw.boneIndex3 = top4[t].Key; nw.weight3 = x; break;
                }
            }
            weights[i] = nw;
            changed++;
        }
        mesh.boneWeights = weights;
        return $"Skirt fitted to the body: {changed}/{skirt.Count} skirt vertices re-weighted ({released} spring-bone weights above their pivot faded out)";
    }

    /// <summary>Removes the previous version's world-space VRM spring for the jacket and its colliders.</summary>
    private static string RemoveJacketSpring(Transform root)
    {
        int removed = 0;
        foreach (var spring in root.GetComponentsInChildren<VRM.VRMSpringBone>(true).Where(s => s.m_comment == "JacketHem").ToList())
        {
            Undo.DestroyObjectImmediate(spring);
            removed++;
        }
        foreach (var t in root.GetComponentsInChildren<Transform>(true).Where(t => t.name == "JacketCollider").ToList())
        {
            Undo.DestroyObjectImmediate(t.gameObject);
            removed++;
        }
        return $"Removed {removed} leftovers of the old jacket spring";
    }

    private static List<Transform[]> RigJacketHem(Mesh mesh, SkinnedMeshRenderer body, Transform[] bones, StringBuilder log)
    {
        BindFrame(mesh, bones, out Vector3 hips, out Vector3 forward, out Vector3 right);
        var verts = mesh.vertices;
        var bindposes = mesh.bindposes.ToList();
        var weights = mesh.boneWeights;
        int hipsIdx = System.Array.FindIndex(bones, b => b.name == "J_Bip_C_Hips");
        var hipLike = new HashSet<int>(new[] { "J_Bip_C_Hips", "J_Bip_L_UpperLeg", "J_Bip_R_UpperLeg", "J_Bip_C_Spine" }
            .Select(n => System.Array.FindIndex(bones, b => b.name == n)));

        float Angle(Vector3 p) { Vector3 d = p - hips; return Mathf.Atan2(Vector3.Dot(d, right), Vector3.Dot(d, forward)) * Mathf.Rad2Deg; }
        Vector3 Dir(float angle) => Quaternion.AngleAxis(angle, Vector3.up) * forward;

        // Lower jacket vertices that are skinned to the hips/thighs (sleeves and chest are left alone).
        int jacket = SubmeshIndex(body, JacketMaterial, CollarMaterial);
        var hem = new HashSet<int>();
        foreach (int i in mesh.GetIndices(jacket))
        {
            if (verts[i].y >= HemBlendTop) continue;
            var w = weights[i];
            float hw = (hipLike.Contains(w.boneIndex0) ? w.weight0 : 0f) + (hipLike.Contains(w.boneIndex1) ? w.weight1 : 0f)
                     + (hipLike.Contains(w.boneIndex2) ? w.weight2 : 0f) + (hipLike.Contains(w.boneIndex3) ? w.weight3 : 0f);
            if (hw >= 0.5f) hem.Add(i);
        }

        // Joints sit on the jacket's surface (radial ray from the hips axis), so a chain is the jacket's own line.
        var tris = mesh.GetTriangles(jacket).ToArray();
        float? SurfaceRadius(float angle, float height)
        {
            Vector3 o = new Vector3(hips.x, height, hips.z), d = Dir(angle);
            float best = float.MaxValue;
            for (int t = 0; t < tris.Length; t += 3)
            {
                if (!hem.Contains(tris[t]) && !hem.Contains(tris[t + 1]) && !hem.Contains(tris[t + 2])) continue;
                if (RayTriangle(o, d, verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]], out float hit)) best = Mathf.Min(best, hit);
            }
            return best < float.MaxValue ? best : (float?)null;
        }

        // Recreate the joints under the scene's hips bone.
        Transform hipsBone = bones[hipsIdx];
        foreach (Transform old in hipsBone.Cast<Transform>().Where(t => t.name.StartsWith(HemPrefix)).ToList())
            Undo.DestroyObjectImmediate(old.gameObject);

        Matrix4x4 hipsBind = bindposes[hipsIdx].inverse;
        var chains = new List<Transform[]>();
        var jointIndex = new int[HemAngles.Length, HemJointHeights.Length];
        for (int c = 0; c < HemAngles.Length; c++)
        {
            var radii = new float[HemJointHeights.Length];
            for (int k = 0; k < radii.Length; k++)
            {
                float? r = SurfaceRadius(HemAngles[c], HemJointHeights[k]);
                // Below the hem there is no surface: continue the last segment's slope.
                if (r == null && k < radii.Length - 1) log.AppendLine($"WARNING: no jacket surface at {HemAngles[c]} deg, height {HemJointHeights[k]}");
                radii[k] = r ?? (k >= 2 ? 2f * radii[k - 1] - radii[k - 2] : 0.15f);
            }

            var joints = new Transform[HemJointHeights.Length];
            Transform parent = hipsBone;
            Matrix4x4 parentBind = hipsBind;
            for (int k = 0; k < joints.Length; k++)
            {
                Vector3 p = new Vector3(hips.x, HemJointHeights[k], hips.z) + Dir(HemAngles[c]) * radii[k];
                Matrix4x4 bind = Matrix4x4.TRS(p, Quaternion.identity, Vector3.one);
                var go = new GameObject($"{HemPrefix}{c}_{k}");
                Undo.RegisterCreatedObjectUndo(go, "Jacket hem joint");
                go.transform.SetParent(parent, false);
                Matrix4x4 local = parentBind.inverse * bind;
                go.transform.localPosition = local.GetColumn(3);
                go.transform.localRotation = local.rotation;
                jointIndex[c, k] = bones.Length + c * joints.Length + k;
                joints[k] = go.transform;
                bindposes.Add(bind.inverse);
                parent = go.transform;
                parentBind = bind;
            }
            chains.Add(joints);
        }

        // Re-weight. Along a chain: each segment moves with its upper joint, blended across the bends. Around the
        // hips: between the two neighbouring chains. Towards the waist: back into the original skinning.
        int lastBone = HemJointHeights.Length - 2; // the end joint is unweighted
        foreach (int i in hem)
        {
            Vector3 p = verts[i];
            float a = Angle(p);
            int c0, c1; float ta;
            int front = System.Array.FindIndex(HemAngles, x => x > 0f); // first chain right of the opening
            if (a > HemAngles[front - 1] && a < HemAngles[front]) { c0 = c1 = a < 0f ? front - 1 : front; ta = 0f; }
            else
            {
                c0 = HemAngles.Length - 1; c1 = 0; ta = 0f;
                for (int c = 0; c < HemAngles.Length; c++)
                {
                    int n = (c + 1) % HemAngles.Length;
                    float span = Mathf.Repeat(HemAngles[n] - HemAngles[c], 360f);
                    float off = Mathf.Repeat(a - HemAngles[c], 360f);
                    if (off <= span) { c0 = c; c1 = n; ta = off / span; break; }
                }
            }

            // Position along the chain in segments (0 at the pivot): a vertex moves with the joint at the top of its
            // segment, blended with the next one within +-JointBlend of each bend.
            float u = HemJointHeights.Length - 1;
            for (int k = 0; k < HemJointHeights.Length - 1; k++)
                if (p.y >= HemJointHeights[k + 1])
                {
                    u = k + Mathf.Clamp01((HemJointHeights[k] - p.y) / (HemJointHeights[k] - HemJointHeights[k + 1]));
                    break;
                }
            int bend = Mathf.RoundToInt(u);
            int kA, kB; float tBend; // weight (1 - tBend) on joint kA, tBend on joint kB
            if (bend >= 1 && bend <= lastBone && Mathf.Abs(u - bend) < JointBlend)
            {
                kA = bend - 1; kB = bend;
                tBend = (u - (bend - JointBlend)) / (2f * JointBlend);
            }
            else
            {
                kA = kB = Mathf.Min(Mathf.FloorToInt(u), lastBone);
                tBend = 1f;
            }

            var acc = new Dictionary<int, float>();
            void Add(int bone, float w) { if (w <= 0f) return; acc.TryGetValue(bone, out float v); acc[bone] = v + w; }
            float s = Mathf.InverseLerp(HemBlendTop, HemBlendBottom, p.y);
            var ow = weights[i];
            Add(ow.boneIndex0, ow.weight0 * (1f - s)); Add(ow.boneIndex1, ow.weight1 * (1f - s));
            Add(ow.boneIndex2, ow.weight2 * (1f - s)); Add(ow.boneIndex3, ow.weight3 * (1f - s));
            Add(jointIndex[c0, kA], s * (1f - ta) * (1f - tBend)); Add(jointIndex[c0, kB], s * (1f - ta) * tBend);
            Add(jointIndex[c1, kA], s * ta * (1f - tBend));        Add(jointIndex[c1, kB], s * ta * tBend);

            var top4 = acc.OrderByDescending(kv => kv.Value).Take(4).ToList();
            float sum = top4.Sum(kv => kv.Value);
            var nw = new BoneWeight();
            for (int t = 0; t < top4.Count; t++)
            {
                float w = top4[t].Value / sum;
                switch (t)
                {
                    case 0: nw.boneIndex0 = top4[t].Key; nw.weight0 = w; break;
                    case 1: nw.boneIndex1 = top4[t].Key; nw.weight1 = w; break;
                    case 2: nw.boneIndex2 = top4[t].Key; nw.weight2 = w; break;
                    case 3: nw.boneIndex3 = top4[t].Key; nw.weight3 = w; break;
                }
            }
            weights[i] = nw;
        }
        mesh.boneWeights = weights;
        mesh.bindposes = bindposes.ToArray();
        log.AppendLine($"Jacket hem: {HemAngles.Length} chains x {HemJointHeights.Length - 1} segments on the jacket surface, {hem.Count} vertices re-weighted");
        return chains;
    }

    private static bool RayTriangle(Vector3 o, Vector3 d, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 e1 = b - a, e2 = c - a, p = Vector3.Cross(d, e2);
        float det = Vector3.Dot(e1, p);
        if (Mathf.Abs(det) < 1e-9f) return false;
        float inv = 1f / det;
        Vector3 s = o - a;
        float u = Vector3.Dot(s, p) * inv;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, e1);
        float v = Vector3.Dot(d, q) * inv;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0f;
    }

    private static string SetupJacketCloth(Animator nino, SkinnedMeshRenderer body, Transform[] bones, List<Transform[]> chains)
    {
        var cloth = nino.GetComponent<JacketHemCloth>() ?? Undo.AddComponent<JacketHemCloth>(nino.gameObject);
        var so = new SerializedObject(cloth);
        so.FindProperty("hips").objectReferenceValue = nino.GetBoneTransform(HumanBodyBones.Hips);
        so.FindProperty("space").objectReferenceValue = body.rootBone != null ? body.rootBone : nino.transform;
        so.FindProperty("body").objectReferenceValue = body;
        so.FindProperty("jacketSubmesh").intValue = SubmeshIndex(body, JacketMaterial, CollarMaterial);

        // The skirt's outside: its outer layer and the front band (the inner layer lies a few mm behind the outer one).
        var skirt = Enumerable.Range(0, body.sharedMaterials.Length)
            .Where(i => body.sharedMaterials[i].name.Contains(SkirtMaterial + "_01") || body.sharedMaterials[i].name.Contains(SkirtMaterial + "_02")).ToArray();
        var skirtProp = so.FindProperty("skirtSubmeshes");
        skirtProp.arraySize = skirt.Length;
        for (int i = 0; i < skirt.Length; i++) skirtProp.GetArrayElementAtIndex(i).intValue = skirt[i];

        var chainsProp = so.FindProperty("chains");
        chainsProp.arraySize = chains.Count;
        for (int c = 0; c < chains.Count; c++)
        {
            var joints = chainsProp.GetArrayElementAtIndex(c).FindPropertyRelative("Joints");
            joints.arraySize = chains[c].Length;
            for (int k = 0; k < chains[c].Length; k++) joints.GetArrayElementAtIndex(k).objectReferenceValue = chains[c][k];
        }

        var legs = new[] { (HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg), (HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg) };
        var legsProp = so.FindProperty("legs");
        legsProp.arraySize = legs.Length;
        var sb = new StringBuilder();
        for (int l = 0; l < legs.Length; l++)
        {
            Transform hip = nino.GetBoneTransform(legs[l].Item1), knee = nino.GetBoneTransform(legs[l].Item2);
            ThighRadii(body, bones, hip, knee, out float hipRadius, out float kneeRadius);
            var leg = legsProp.GetArrayElementAtIndex(l);
            leg.FindPropertyRelative("Hip").objectReferenceValue = hip;
            leg.FindPropertyRelative("Knee").objectReferenceValue = knee;
            leg.FindPropertyRelative("HipRadius").floatValue = hipRadius;
            leg.FindPropertyRelative("KneeRadius").floatValue = kneeRadius;
            sb.Append($" {hip.name} {hipRadius * 100f:F1}->{kneeRadius * 100f:F1} cm");
        }
        so.ApplyModifiedProperties();
        return $"JacketHemCloth on {nino.name}: {chains.Count} chains, skirt submeshes {string.Join(",", skirt)}, thigh capsules{sb}";
    }

    /// <summary>
    /// Tapered capsule around the thigh (hip joint to knee) that contains the skin skinned to the upper leg in the
    /// bind pose: the knee radius is the thickest point near the knee, and the hip radius is the smallest that
    /// keeps the straight taper outside every tenth of the thigh (the upper thigh bulges).
    /// </summary>
    private static void ThighRadii(SkinnedMeshRenderer body, Transform[] bones, Transform hip, Transform knee, out float hipRadius, out float kneeRadius)
    {
        Mesh mesh = PrefabUtility.GetCorrespondingObjectFromSource(body).sharedMesh;
        var verts = mesh.vertices;
        var weights = mesh.boneWeights;
        var bindposes = mesh.bindposes;
        int hi = System.Array.IndexOf(bones, hip), ki = System.Array.IndexOf(bones, knee);
        Vector3 a = bindposes[hi].inverse.MultiplyPoint3x4(Vector3.zero), b = bindposes[ki].inverse.MultiplyPoint3x4(Vector3.zero);
        var thickest = new float[10];
        foreach (int i in new HashSet<int>(mesh.GetIndices(SubmeshIndex(body, SkinMaterial))))
        {
            var w = weights[i];
            float onLeg = (w.boneIndex0 == hi ? w.weight0 : 0f) + (w.boneIndex1 == hi ? w.weight1 : 0f) + (w.boneIndex2 == hi ? w.weight2 : 0f) + (w.boneIndex3 == hi ? w.weight3 : 0f);
            if (onLeg < 0.5f) continue;
            float u = Vector3.Dot(verts[i] - a, b - a) / (b - a).sqrMagnitude;
            if (u < 0f || u >= 1f) continue;
            int bin = (int)(u * thickest.Length);
            thickest[bin] = Mathf.Max(thickest[bin], Vector3.Cross(verts[i] - a, (b - a).normalized).magnitude);
        }
        kneeRadius = Mathf.Max(thickest[thickest.Length - 1], thickest[thickest.Length - 2]);
        hipRadius = kneeRadius;
        for (int bin = 0; bin < thickest.Length; bin++)
        {
            float u = (bin + 0.5f) / thickest.Length;
            hipRadius = Mathf.Max(hipRadius, (thickest[bin] - kneeRadius * u) / (1f - u));
        }
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
