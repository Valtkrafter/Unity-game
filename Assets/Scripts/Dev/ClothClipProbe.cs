#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Editor-only: measures cloth clipping on the baked Body mesh.
/// - Skirt poke-through: skirt vertices lying outside the jacket surface where the jacket covers them.
/// - Hand clipping: hand/finger points lying inside the jacket surface.
/// Usage (Play Mode): ClothClipProbe.Measure() -> string.
/// </summary>
public static class ClothClipProbe
{
    private const string JacketMaterial = "Tops_01_CLOTH";
    private const string SkirtMaterial = "Bottoms_01_CLOTH";

    public static string Measure(out int skirtOutside, out float handMaxPenetration)
    {
        var player = GameObject.Find("Player");
        var animator = player.GetComponentInChildren<Animator>();
        SkinnedMeshRenderer body = null;
        foreach (var smr in player.GetComponentsInChildren<SkinnedMeshRenderer>()) if (smr.name == "Body") body = smr;

        var baked = new Mesh();
        body.BakeMesh(baked, true);
        var verts = new List<Vector3>(); baked.GetVertices(verts);
        var normals = new List<Vector3>(); baked.GetNormals(normals);
        Matrix4x4 m = body.transform.localToWorldMatrix;

        var jacketP = new List<Vector3>(); var jacketN = new List<Vector3>();
        var skirt = new List<Vector3>();
        var mats = body.sharedMaterials;
        for (int s = 0; s < baked.subMeshCount; s++)
        {
            string mat = mats[s].name;
            bool jacket = mat.Contains(JacketMaterial) && !mat.Contains("007_02"); // 007_02 is the collar
            bool isSkirt = mat.Contains(SkirtMaterial);
            if (!jacket && !isSkirt) continue;
            var seen = new HashSet<int>(baked.GetIndices(s));
            foreach (int i in seen)
            {
                Vector3 p = m.MultiplyPoint3x4(verts[i]);
                if (jacket) { jacketP.Add(p); jacketN.Add(m.MultiplyVector(normals[i]).normalized); }
                else skirt.Add(p);
            }
        }

        // Skirt vertex is "poking through" if the nearest jacket vertex (within 4 cm, i.e. the jacket covers
        // that spot) has the skirt vertex on its outer side by more than 3 mm.
        skirtOutside = 0;
        float worst = 0f;
        foreach (var p in skirt)
        {
            int best = Nearest(jacketP, p, 0.04f);
            if (best < 0) continue;
            float outside = Vector3.Dot(p - jacketP[best], jacketN[best]);
            if (outside > 0.003f) { skirtOutside++; worst = Mathf.Max(worst, outside); }
        }

        // Hand: wrist + finger joints; penetration = how far inside the jacket surface (nearest vertex within 6 cm).
        handMaxPenetration = 0f;
        string worstPoint = "-";
        var handBones = new[]
        {
            HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexDistal, HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftLittleDistal, HumanBodyBones.LeftThumbDistal,
            HumanBodyBones.RightHand, HumanBodyBones.RightIndexDistal, HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightLittleDistal, HumanBodyBones.RightThumbDistal,
        };
        foreach (var hb in handBones)
        {
            Transform t = animator.GetBoneTransform(hb);
            if (t == null) continue;
            int best = Nearest(jacketP, t.position, 0.06f);
            if (best < 0) continue;
            float inside = -Vector3.Dot(t.position - jacketP[best], jacketN[best]);
            if (inside > handMaxPenetration) { handMaxPenetration = inside; worstPoint = hb.ToString(); }
        }

        Object.Destroy(baked);
        return $"skirtVertsOutsideJacket={skirtOutside}/{skirt.Count} worst={worst * 100f:F1}cm  handInsideJacket max={handMaxPenetration * 100f:F1}cm ({worstPoint})";
    }

    /// <summary>Drives the player with the given input and aggregates Measure() over a number of frames.</summary>
    public static void Sample(Vector2 input, bool sprint, float settleSeconds, int frames)
    {
        SampleReport = null;
        AnimeCharacterController.UseInputOverride = true;
        AnimeCharacterController.InputOverride = input;
        AnimeCharacterController.SprintOverride = sprint;
        var runner = new GameObject("__ClothClipSampler").AddComponent<Sampler>();
        runner.startAt = Time.time + settleSeconds;
        runner.frames = frames;
    }

    public static string SampleReport { get; private set; }

    [DefaultExecutionOrder(20000)] // after the animator and the VRM spring bones
    private sealed class Sampler : MonoBehaviour
    {
        public float startAt;
        public int frames;
        private int done, framesWithSkirtClip, framesWithHandClip;
        private int skirtMax;
        private float handMax, skirtSum;

        private void LateUpdate()
        {
            if (Time.time < startAt) return;
            Measure(out int skirt, out float hand);
            skirtSum += skirt;
            skirtMax = Mathf.Max(skirtMax, skirt);
            handMax = Mathf.Max(handMax, hand);
            if (skirt > 0) framesWithSkirtClip++;
            if (hand > 0.005f) framesWithHandClip++;
            if (++done < frames) return;
            SampleReport = $"frames={frames} skirt poke-through: frames={framesWithSkirtClip} avgVerts={skirtSum / frames:F1} maxVerts={skirtMax} | " +
                           $"hand inside jacket (>5mm): frames={framesWithHandClip} max={handMax * 100f:F1}cm";
            AnimeCharacterController.UseInputOverride = false;
            AnimeCharacterController.SprintOverride = false;
            Destroy(gameObject);
        }
    }

    private static int Nearest(List<Vector3> pts, Vector3 p, float maxDist)
    {
        int best = -1;
        float bestSq = maxDist * maxDist;
        for (int i = 0; i < pts.Count; i++)
        {
            float d = (pts[i] - p).sqrMagnitude;
            if (d < bestSq) { bestSq = d; best = i; }
        }
        return best;
    }
}
#endif
