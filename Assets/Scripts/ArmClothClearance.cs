using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps the hands out of the jacket. The jacket hem is skinned rigidly to the hips/thighs, so hanging arms
/// (and the hand-on-hip idle) sink into it. Each frame, after the Animator and before the VRM spring bones,
/// this skins a sample of the jacket's lower surface on the CPU (a few hundred vertices), finds how deep the
/// hand and finger tips are inside it, and rotates the upper arm outward just enough to clear it.
/// The correction is applied instantly when needed (no visible clipping) and eased out when no longer needed.
/// </summary>
[DefaultExecutionOrder(-500)]
[RequireComponent(typeof(Animator))]
public sealed class ArmClothClearance : MonoBehaviour
{
    [SerializeField] private SkinnedMeshRenderer clothRenderer;
    [SerializeField] private int clothSubmesh = 3;            // Tops_01_CLOTH (the jacket)
    [SerializeField] private float minHeight = 0.55f;          // mesh-space band of the jacket used as the surface
    [SerializeField] private float maxHeight = 1.10f;
    [SerializeField] private float clearance = 0.012f;         // gap kept between finger tips and the cloth
    [SerializeField] private float maxCorrectionDegrees = 25f;
    [SerializeField] private float releaseDegreesPerSecond = 90f;

    private struct Sample { public Vector3 Position, Normal; public BoneWeight Weight; }

    private sealed class Arm
    {
        public Transform UpperArm;
        public Transform[] Probes;
        public float Angle;
        public Vector3 LocalAxis = Vector3.forward; // rotation axis in the upper arm's parent space
    }

    private Sample[] samples;
    private Vector3[] skinnedP, skinnedN;
    private Transform[] bones;
    private Matrix4x4[] bindposes, skin;
    private bool[] boneUsed;
    private Arm[] arms;

    private void Awake()
    {
        var animator = GetComponent<Animator>();
        if (clothRenderer == null || clothRenderer.sharedMesh == null || !animator.isHuman) { enabled = false; return; }

        Mesh mesh = clothRenderer.sharedMesh;
        bones = clothRenderer.bones;
        bindposes = mesh.bindposes;
        skin = new Matrix4x4[bones.Length];
        boneUsed = new bool[bones.Length];

        var verts = mesh.vertices;
        var normals = mesh.normals;
        var weights = mesh.boneWeights;
        var list = new List<Sample>();
        foreach (int i in new HashSet<int>(mesh.GetIndices(clothSubmesh)))
        {
            if (verts[i].y < minHeight || verts[i].y > maxHeight) continue;
            var w = weights[i];
            list.Add(new Sample { Position = verts[i], Normal = normals[i], Weight = w });
            boneUsed[w.boneIndex0] = boneUsed[w.boneIndex1] = boneUsed[w.boneIndex2] = boneUsed[w.boneIndex3] = true;
        }
        samples = list.ToArray();
        skinnedP = new Vector3[samples.Length];
        skinnedN = new Vector3[samples.Length];

        arms = new[]
        {
            MakeArm(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftHand, HumanBodyBones.LeftIndexDistal,
                HumanBodyBones.LeftMiddleDistal, HumanBodyBones.LeftRingDistal, HumanBodyBones.LeftLittleDistal, HumanBodyBones.LeftThumbDistal),
            MakeArm(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightHand, HumanBodyBones.RightIndexDistal,
                HumanBodyBones.RightMiddleDistal, HumanBodyBones.RightRingDistal, HumanBodyBones.RightLittleDistal, HumanBodyBones.RightThumbDistal),
        };
    }

    private static Arm MakeArm(Animator animator, HumanBodyBones upper, params HumanBodyBones[] probes)
    {
        var list = new List<Transform>();
        foreach (var b in probes)
        {
            Transform t = animator.GetBoneTransform(b);
            if (t != null) list.Add(t);
        }
        return new Arm { UpperArm = animator.GetBoneTransform(upper), Probes = list.ToArray() };
    }

    private void LateUpdate()
    {
        SkinSamples();
        foreach (var arm in arms) Solve(arm, Time.deltaTime);
    }

    private void SkinSamples()
    {
        for (int b = 0; b < bones.Length; b++)
            if (boneUsed[b]) skin[b] = bones[b].localToWorldMatrix * bindposes[b];

        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            var w = s.Weight;
            Vector3 p = skin[w.boneIndex0].MultiplyPoint3x4(s.Position) * w.weight0
                      + skin[w.boneIndex1].MultiplyPoint3x4(s.Position) * w.weight1
                      + skin[w.boneIndex2].MultiplyPoint3x4(s.Position) * w.weight2
                      + skin[w.boneIndex3].MultiplyPoint3x4(s.Position) * w.weight3;
            Vector3 n = skin[w.boneIndex0].MultiplyVector(s.Normal) * w.weight0
                      + skin[w.boneIndex1].MultiplyVector(s.Normal) * w.weight1
                      + skin[w.boneIndex2].MultiplyVector(s.Normal) * w.weight2
                      + skin[w.boneIndex3].MultiplyVector(s.Normal) * w.weight3;
            skinnedP[i] = p;
            skinnedN[i] = n.normalized;
        }
    }

    private void Solve(Arm arm, float dt)
    {
        // Deepest penetration of any finger probe into the cloth, measured on the animated pose.
        float depth = 0f;
        Vector3 pushDir = Vector3.zero, deepestProbe = Vector3.zero;
        foreach (var probe in arm.Probes)
        {
            Vector3 p = probe.position;
            int nearest = -1;
            float bestSq = 0.08f * 0.08f;
            for (int i = 0; i < skinnedP.Length; i++)
            {
                float d = (skinnedP[i] - p).sqrMagnitude;
                if (d < bestSq) { bestSq = d; nearest = i; }
            }
            if (nearest < 0) continue;
            float inside = clearance - Vector3.Dot(p - skinnedP[nearest], skinnedN[nearest]);
            if (inside > depth) { depth = inside; pushDir = skinnedN[nearest]; deepestProbe = p; }
        }

        Transform parent = arm.UpperArm.parent;
        float target = 0f;
        if (depth > 0f)
        {
            // Rotate about the shoulder so the deepest probe moves along the cloth normal by 'depth'.
            Vector3 lever = deepestProbe - arm.UpperArm.position;
            Vector3 move = Vector3.ProjectOnPlane(pushDir, lever.normalized);
            if (move.sqrMagnitude > 1e-6f && lever.magnitude > 0.05f)
            {
                Vector3 axis = Vector3.Cross(lever, move).normalized;
                arm.LocalAxis = parent.InverseTransformDirection(axis);
                float travel = depth / move.magnitude;
                target = Mathf.Min(Mathf.Asin(Mathf.Clamp01(travel / lever.magnitude)) * Mathf.Rad2Deg, maxCorrectionDegrees);
            }
        }

        arm.Angle = target >= arm.Angle ? target : Mathf.MoveTowards(arm.Angle, target, releaseDegreesPerSecond * dt);
        if (arm.Angle > 0.01f)
            arm.UpperArm.rotation = Quaternion.AngleAxis(arm.Angle, parent.TransformDirection(arm.LocalAxis)) * arm.UpperArm.rotation;
    }
}
