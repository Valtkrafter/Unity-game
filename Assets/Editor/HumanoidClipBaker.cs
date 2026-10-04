using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes pose streams written by Tools/AnimConvert (vmd2clip.py, vrma2clip.py) into humanoid AnimationClips.
///
/// A pose stream holds, per frame, the world rotation of each humanoid bone relative to the T-pose and the hips
/// position. The stream is applied to a hidden copy of Nino (whose bind pose is the T-pose), read back as
/// muscles with HumanPoseHandler and written as RootT/RootQ + muscle curves, so the clip plays on any humanoid.
/// </summary>
public static class HumanoidClipBaker
{
    private const string NinoPrefab = "Assets/Models/Nino Nakano/5394265126170879566.prefab";
    private const string StreamDir = "Tools/AnimConvert/out";
    private const string OutDir = "Assets/Animations/Candidates";
    // In the scene the Player's CharacterController hovers skinWidth (5 cm) above the floor and Nino_Model sits at
    // local y -2 cm, so the model origin is 3 cm above the ground (the Mixamo idle/jump clips are tuned to that).
    // Streams are solved with the soles on y = 0, so they are lowered by the same 3 cm.
    private const float FloorOffset = -0.03f;

    [System.Serializable]
    private class Frame { public float[] p; public float[][] q; }

    [System.Serializable]
    private class Stream
    {
        public string name; public string source; public float fps; public bool loop;
        public float speed;           // ground speed the in-place cycle was authored for (m/s), 0 for gestures
        public string outDir;         // asset folder, default Assets/Animations/Candidates
        public string[] bones; public List<Frame> frames;
    }

    [MenuItem("Tools/Animation/Bake Pose Streams To Humanoid Clips")]
    public static void BakeAll()
    {
        if (!Directory.Exists(StreamDir)) { Debug.LogError($"No {StreamDir}"); return; }
        foreach (var f in Directory.GetFiles(StreamDir, "*.json")) Bake(f);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }

    public static string Bake(string jsonPath)
    {
        var s = Newtonsoft.Json.JsonConvert.DeserializeObject<Stream>(File.ReadAllText(jsonPath));
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(NinoPrefab);
        var go = Object.Instantiate(prefab);
        go.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            go.transform.SetPositionAndRotation(Vector3.zero, Quaternion.identity);
            go.transform.localScale = Vector3.one;
            var animator = go.GetComponent<Animator>();
            animator.enabled = false;
            var avatar = animator.avatar;

            // Bones in parent-first order, with their bind (T-pose) world rotations and local rotations.
            var all = new Dictionary<HumanBodyBones, Transform>();
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = animator.GetBoneTransform((HumanBodyBones)i);
                if (t != null) all[(HumanBodyBones)i] = t;
            }
            var restWorld = all.ToDictionary(kv => kv.Key, kv => kv.Value.rotation);
            var restLocal = all.ToDictionary(kv => kv.Key, kv => kv.Value.localRotation);
            var ordered = all.Keys.OrderBy(b => Depth(all[b])).ToList();

            var streamIndex = new Dictionary<HumanBodyBones, int>();
            for (int i = 0; i < s.bones.Length; i++)
                if (System.Enum.TryParse(s.bones[i], out HumanBodyBones hb) && all.ContainsKey(hb)) streamIndex[hb] = i;

            var handler = new HumanPoseHandler(avatar, go.transform);
            var pose = new HumanPose();
            int n = s.frames.Count;
            int muscleCount = HumanTrait.MuscleCount;
            var rootT = new AnimationCurve[3].Select(_ => new AnimationCurve()).ToArray();
            var rootQ = new AnimationCurve[4].Select(_ => new AnimationCurve()).ToArray();
            var muscles = new AnimationCurve[muscleCount].Select(_ => new AnimationCurve()).ToArray();
            Quaternion lastQ = Quaternion.identity;
            Transform hips = all[HumanBodyBones.Hips];

            // IK goal curves (LeftFootT/Q ...): the Animator's foot IK pulls the feet to these, so a humanoid clip
            // without them collapses the legs whenever foot IK is on (playables default to it).
            animator.enabled = true;
            animator.Rebind();
            float humanScale = animator.humanScale;
            animator.enabled = false;
            var goals = new[] { ("LeftFoot", HumanBodyBones.LeftFoot), ("RightFoot", HumanBodyBones.RightFoot),
                                ("LeftHand", HumanBodyBones.LeftHand), ("RightHand", HumanBodyBones.RightHand) };
            var goalT = goals.Select(_ => new AnimationCurve[3].Select(__ => new AnimationCurve()).ToArray()).ToArray();
            var goalQ = goals.Select(_ => new AnimationCurve[4].Select(__ => new AnimationCurve()).ToArray()).ToArray();
            var lastGoalQ = new Quaternion[goals.Length];
            var postRotation = typeof(Avatar).GetMethod("GetPostRotation", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var axisLength = typeof(Avatar).GetMethod("GetAxisLength", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            bool withGoals = postRotation != null && axisLength != null;
            if (!withGoals) Debug.LogWarning("[HumanoidClipBaker] Avatar.GetPostRotation/GetAxisLength not found: no IK goal curves; keep foot IK off for these clips.");

            for (int fi = 0; fi < n; fi++)
            {
                var fr = s.frames[fi];
                foreach (var b in ordered) all[b].localRotation = restLocal[b];
                hips.position = new Vector3(fr.p[0], fr.p[1] + FloorOffset, fr.p[2]);
                foreach (var b in ordered)
                {
                    if (!streamIndex.TryGetValue(b, out int k)) continue;
                    var q = fr.q[k];
                    all[b].rotation = new Quaternion(q[0], q[1], q[2], q[3]) * restWorld[b];
                }
                handler.GetHumanPose(ref pose);
                float t = fi / s.fps;
                if (fi > 0 && Quaternion.Dot(lastQ, pose.bodyRotation) < 0f)
                    pose.bodyRotation = new Quaternion(-pose.bodyRotation.x, -pose.bodyRotation.y, -pose.bodyRotation.z, -pose.bodyRotation.w);
                lastQ = pose.bodyRotation;
                for (int a = 0; a < 3; a++) rootT[a].AddKey(new Keyframe(t, pose.bodyPosition[a]));
                for (int a = 0; a < 4; a++) rootQ[a].AddKey(new Keyframe(t, pose.bodyRotation[a]));
                for (int m = 0; m < muscleCount; m++) muscles[m].AddKey(new Keyframe(t, pose.muscles[m]));
                if (!withGoals) continue;
                // Goals live in body space, normalised by humanScale; feet are measured at the sole (axis length).
                Vector3 bodyT = pose.bodyPosition * humanScale;
                Quaternion invBodyQ = Quaternion.Inverse(pose.bodyRotation);
                for (int g = 0; g < goals.Length; g++)
                {
                    int id = (int)goals[g].Item2;
                    Transform bone = all[goals[g].Item2];
                    Quaternion q = bone.rotation * (Quaternion)postRotation.Invoke(avatar, new object[] { id });
                    Vector3 p = bone.position;
                    if (g < 2) p += q * new Vector3((float)axisLength.Invoke(avatar, new object[] { id }), 0f, 0f);
                    Vector3 gt = invBodyQ * (p - bodyT) / humanScale;
                    Quaternion gq = invBodyQ * q;
                    if (fi > 0 && Quaternion.Dot(lastGoalQ[g], gq) < 0f) gq = new Quaternion(-gq.x, -gq.y, -gq.z, -gq.w);
                    lastGoalQ[g] = gq;
                    for (int a = 0; a < 3; a++) goalT[g][a].AddKey(new Keyframe(t, gt[a]));
                    for (int a = 0; a < 4; a++) goalQ[g][a].AddKey(new Keyframe(t, gq[a]));
                }
            }
            handler.Dispose();

            var clip = new AnimationClip { name = s.name, frameRate = s.fps };
            string[] tn = { "RootT.x", "RootT.y", "RootT.z" }, qn = { "RootQ.x", "RootQ.y", "RootQ.z", "RootQ.w" };
            for (int a = 0; a < 3; a++) clip.SetCurve("", typeof(Animator), tn[a], Reduce(rootT[a], 0.0005f));
            for (int a = 0; a < 4; a++) clip.SetCurve("", typeof(Animator), qn[a], Reduce(rootQ[a], 0.0003f));
            for (int m = 0; m < muscleCount; m++)
            {
                clip.SetCurve("", typeof(Animator), MuscleProperty(HumanTrait.MuscleName[m]), Reduce(muscles[m], 0.002f));
            }
            if (withGoals)
                for (int g = 0; g < goals.Length; g++)
                {
                    string gn = goals[g].Item1;
                    for (int a = 0; a < 3; a++) clip.SetCurve("", typeof(Animator), $"{gn}T.{"xyz"[a]}", Reduce(goalT[g][a], 0.001f));
                    for (int a = 0; a < 4; a++) clip.SetCurve("", typeof(Animator), $"{gn}Q.{"xyzw"[a]}", Reduce(goalQ[g][a], 0.001f));
                }

            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = s.loop;
            // Streams are in place and already face +Z: keep everything exactly as authored.
            settings.loopBlendOrientation = true;
            settings.keepOriginalOrientation = true;
            settings.loopBlendPositionY = true;
            settings.keepOriginalPositionY = true;
            settings.loopBlendPositionXZ = true;
            settings.keepOriginalPositionXZ = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            string dir = string.IsNullOrEmpty(s.outDir) ? OutDir : s.outDir;
            Directory.CreateDirectory(dir);
            string path = $"{dir}/{s.name}.anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null) { EditorUtility.CopySerialized(clip, existing); EditorUtility.SetDirty(existing); }
            else AssetDatabase.CreateAsset(clip, path);
            Debug.Log($"[HumanoidClipBaker] {path}: {n} frames @ {s.fps} fps from {s.source}, speed {s.speed:F3} m/s, humanScale {humanScale:F3}");
            return path;
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    private static int Depth(Transform t) { int d = 0; while (t.parent != null) { t = t.parent; d++; } return d; }

    /// <summary>
    /// Keyframe reduction: keeps the keys a Douglas-Peucker pass needs to stay within eps of the per-frame samples,
    /// with smooth tangents, then re-adds any sample the smoothed curve still misses by more than eps. Muscle values
    /// span about -1..1, so eps 0.002 is ~0.1 degree. Cuts the clips (and the repo) by an order of magnitude.
    /// </summary>
    private static AnimationCurve Reduce(AnimationCurve dense, float eps)
    {
        var src = dense.keys;
        int n = src.Length;
        if (n <= 2) return dense;
        var keep = new bool[n];
        keep[0] = keep[n - 1] = true;
        var stack = new Stack<(int, int)>();
        stack.Push((0, n - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            int worst = -1; float worstErr = eps;
            for (int i = a + 1; i < b; i++)
            {
                float u = (src[i].time - src[a].time) / (src[b].time - src[a].time);
                float err = Mathf.Abs(src[i].value - Mathf.Lerp(src[a].value, src[b].value, u));
                if (err > worstErr) { worstErr = err; worst = i; }
            }
            if (worst < 0) continue;
            keep[worst] = true;
            stack.Push((a, worst));
            stack.Push((worst, b));
        }
        AnimationCurve curve = null;
        for (int pass = 0; pass < 4; pass++)
        {
            curve = new AnimationCurve(src.Where((k, i) => keep[i]).Select(k => new Keyframe(k.time, k.value)).ToArray());
            for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
            bool added = false;
            for (int i = 0; i < n; i++)
                if (!keep[i] && Mathf.Abs(curve.Evaluate(src[i].time) - src[i].value) > eps) { keep[i] = true; added = true; }
            if (!added) break;
        }
        return curve;
    }

    /// <summary>HumanTrait muscle name -> animation clip property ("Left Thumb 1 Stretched" -> "LeftHand.Thumb.1 Stretched").</summary>
    private static string MuscleProperty(string muscle)
    {
        string[] fingers = { "Thumb", "Index", "Middle", "Ring", "Little" };
        foreach (var side in new[] { "Left", "Right" })
            foreach (var f in fingers)
            {
                string prefix = $"{side} {f} ";
                if (muscle.StartsWith(prefix)) return $"{side}Hand.{f}.{muscle.Substring(prefix.Length)}";
            }
        return muscle;
    }
}
