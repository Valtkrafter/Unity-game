using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Nino's animation setup from the clips made in Blender (Tools/AnimConvert/blender_export_streams.py -> HumanoidClipBaker.BakeAll):
///   idle    = Nino_Idle            5 s loop, the standing pose
///   walk    = Nino_Walk            1 s loop - the STANDARD movement (natural ground speed measured from the planted foot)
///   run     = Nino_Walk played faster (RunPlaybackRate) while Shift is held, until there is a real run clip
///   special = Nino_Special_Hmph    one-shot "hmph", starts and ends on the idle pose; played after she has stood still for a while
///   face    = its own layer: standing = Nino_Idle_Face (12 s of blinks + a closed-mouth smile, the mouth NEVER opens), moving = Nino_Face
///             (the 3 s happy walk smile); AnimeCharacterController hands the face over to the special's own face curves while the
///             special plays (layer weight -> 0), so the two never fight.
/// Menu: Tools/Locomotion/1. Build Animator From Blender Clips (applies to the scene)  |  2. Measure Natural Clip Speeds  |  3. Capture 24fps Sequence.
/// </summary>
public static class NinoLocomotionSetup
{
    private const string ClipDir = "Assets/Animations/Nino/";
    private const string ControllerPath = "Assets/Animations/Nino_LocomotionController.controller";
    private const string FaceMaskPath = "Assets/Animations/Nino/Nino_FaceOnly.mask";
    private const string NinoPrefabPath = "Assets/Models/Nino Nakano/5394265126170879566.prefab";

    /// <summary>The walk cycle played this much faster is the "run" (a walk cycle looks frantic beyond ~1.8x; a Blender run clip is the real fix).</summary>
    public const float RunPlaybackRate = 1.7f;
    public const int FaceLayerIndex = 1;

    [MenuItem("Tools/Locomotion/1. Build Animator From Blender Clips")]
    public static void ApplyMenu() => Debug.Log(Apply());

    [MenuItem("Tools/Locomotion/2. Measure Natural Clip Speeds")]
    public static void MeasureMenu() => Debug.Log(MeasureSpeeds(out _));

    public static AnimationClip Clip(string name) => AssetDatabase.LoadAssetAtPath<AnimationClip>(ClipDir + name + ".anim");

    public static string Apply()
    {
        var log = new StringBuilder();
        var idle = Clip("Nino_Idle"); var walk = Clip("Nino_Walk"); var hmph = Clip("Nino_Special_Hmph"); var face = Clip("Nino_Face");
        var idleFace = Clip("Nino_Idle_Face");
        if (idle == null || walk == null || hmph == null || face == null || idleFace == null)
            return "Missing clips in " + ClipDir + " (run Tools/Animation/Bake Pose Streams To Humanoid Clips first).";

        log.Append(MeasureSpeeds(out float measured));
        float walkSpeed = Mathf.Round(measured * 100f) / 100f;
        float runSpeed = Mathf.Round(walkSpeed * RunPlaybackRate * 100f) / 100f;

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        if (controller == null) controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
        Reset(controller);

        // ---- parameters (the movement script drives all of them; the jump ones wait for a Blender jump set)
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        controller.AddParameter("Jump", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("SpecialIdle", AnimatorControllerParameterType.Trigger);
        controller.AddParameter("VerticalVelocity", AnimatorControllerParameterType.Float);
        controller.AddParameter("IsGrounded", AnimatorControllerParameterType.Bool);
        controller.AddParameter("AirProgress", AnimatorControllerParameterType.Float);

        // ---- base layer: Locomotion blend tree (idle | walk | run = walk x RunPlaybackRate) and the special
        var sm = controller.layers[0].stateMachine;
        AnimatorState locomotion = controller.CreateBlendTreeInController("Locomotion", out BlendTree tree, 0);
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.children = new[]
        {
            Child(idle, 0f, 1f),
            Child(walk, walkSpeed, 1f),
            Child(walk, runSpeed, RunPlaybackRate),
        };
        locomotion.speed = 1f;
        locomotion.iKOnFeet = false;
        locomotion.writeDefaultValues = true;
        sm.defaultState = locomotion;

        var special = sm.AddState("SpecialIdle", new Vector3(300f, 160f));
        special.motion = hmph;
        special.tag = NinoLocomotionTags.Special;
        special.iKOnFeet = false;
        special.writeDefaultValues = true;

        // the special starts and ends on the idle pose, so short blends are enough
        var enter = Link(locomotion, special, 0.3f);
        enter.AddCondition(AnimatorConditionMode.If, 0f, "SpecialIdle");
        enter.AddCondition(AnimatorConditionMode.Less, 0.05f, "Speed");
        Link(special, locomotion, 0.45f, exitTime: 0.88f);                                           // the last ~0.6 s of the clip is idle
        Link(special, locomotion, 0.25f).AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");  // moving cuts it short

        // ---- face layer, masked so it cannot touch the body: standing = the idle face (mouth always closed), moving = the happy walk smile
        controller.AddLayer("Face");
        var layers = controller.layers;
        layers[FaceLayerIndex].defaultWeight = 1f;
        layers[FaceLayerIndex].blendingMode = AnimatorLayerBlendingMode.Override;
        layers[FaceLayerIndex].avatarMask = FaceMask();
        var fsm = layers[FaceLayerIndex].stateMachine;
        var faceIdle = fsm.AddState("FaceIdle", new Vector3(300f, 0f));
        faceIdle.motion = idleFace;
        faceIdle.writeDefaultValues = true;
        var faceMove = fsm.AddState("FaceMove", new Vector3(300f, 90f));
        faceMove.motion = face;
        faceMove.writeDefaultValues = true;
        fsm.defaultState = faceIdle;
        Link(faceIdle, faceMove, 0.3f).AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        Link(faceMove, faceIdle, 0.3f).AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");
        controller.layers = layers;

        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        log.AppendLine($"Blend tree: idle 0 | walk {walkSpeed} | run {runSpeed} (walk x{RunPlaybackRate}); special = {hmph.name}; face layer = {idleFace.name} (standing) | {face.name} (moving)");

        // ---- the scene
        var player = GameObject.Find("Player");
        if (player != null)
        {
            var anim = player.GetComponentInChildren<Animator>();
            Undo.RecordObject(anim, "Assign locomotion controller");
            anim.runtimeAnimatorController = controller;
            anim.applyRootMotion = false;
            if (anim.GetComponent<DiscardRootMotion>() == null) Undo.AddComponent<DiscardRootMotion>(anim.gameObject);

            var cc = player.GetComponent<AnimeCharacterController>();
            var so = new SerializedObject(cc);
            so.FindProperty("walkSpeed").floatValue = walkSpeed;
            so.FindProperty("runSpeed").floatValue = runSpeed;
            so.ApplyModifiedProperties();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
            UnityEditor.SceneManagement.EditorSceneManager.SaveScene(player.scene);
            log.AppendLine($"Scene saved: Animator -> {controller.name}, walkSpeed {walkSpeed}, runSpeed {runSpeed}");
        }
        else log.AppendLine("No 'Player' in the open scene: controller built, scene not touched.");
        return log.ToString();
    }

    /// <summary>Empties the controller in place (keeps its GUID, so the scene reference survives).</summary>
    private static void Reset(AnimatorController controller)
    {
        for (int i = controller.layers.Length - 1; i >= 1; i--) controller.RemoveLayer(i);
        var sm = controller.layers[0].stateMachine;
        foreach (var s in sm.states.ToArray()) sm.RemoveState(s.state);
        foreach (var t in sm.anyStateTransitions.ToArray()) sm.RemoveAnyStateTransition(t);
        foreach (var t in sm.entryTransitions.ToArray()) sm.RemoveEntryTransition(t);
        for (int i = controller.parameters.Length - 1; i >= 0; i--) controller.RemoveParameter(i);
    }

    private static AvatarMask FaceMask()
    {
        var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(FaceMaskPath);
        if (mask == null) { mask = new AvatarMask(); AssetDatabase.CreateAsset(mask, FaceMaskPath); }
        foreach (AvatarMaskBodyPart part in System.Enum.GetValues(typeof(AvatarMaskBodyPart)))
            if (part != AvatarMaskBodyPart.LastBodyPart) mask.SetHumanoidBodyPartActive(part, false);
        mask.transformCount = 1;                       // only the Face mesh object (the blend shape curves live on it)
        mask.SetTransformPath(0, "Face");
        mask.SetTransformActive(0, true);
        EditorUtility.SetDirty(mask);
        return mask;
    }

    private static ChildMotion Child(Motion motion, float threshold, float timeScale) =>
        new ChildMotion { motion = motion, threshold = threshold, timeScale = timeScale, directBlendParameter = "Speed" };

    private static AnimatorStateTransition Link(AnimatorState from, AnimatorState to, float duration, float? exitTime = null)
    {
        var t = from.AddTransition(to);
        t.hasFixedDuration = true;
        t.duration = duration;
        t.hasExitTime = exitTime.HasValue;
        t.exitTime = exitTime ?? 0f;
        return t;
    }

    // ------------------------------------------------------------------ speed measurement (planted foot)
    /// <summary>
    /// Plays each loop on a hidden copy of Nino and measures how fast the planted foot travels backwards relative to the body:
    /// the speed the character must move for the foot to stay locked to the ground (stride length x cadence).
    /// </summary>
    public static string MeasureSpeeds(out float walkSpeed)
    {
        var log = new StringBuilder();
        walkSpeed = 0f;
        var nino = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NinoPrefabPath));
        nino.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var animator = nino.GetComponent<Animator>();
            animator.applyRootMotion = false;
            float ankleRestY = animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
            Transform toes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
            float toeRestY = toes != null ? toes.position.y : 0f;
            log.AppendLine($"Nino rest: ankleY={ankleRestY:F3} toeY={toeRestY:F3} (contact heights below are relative to these; 0 = touching ground)");
            walkSpeed = Measure(animator, Clip("Nino_Walk"), ankleRestY, toeRestY, log);
        }
        finally
        {
            Object.DestroyImmediate(nino);
        }
        return log.ToString();
    }

    private static float Measure(Animator animator, AnimationClip clip, float ankleRestY, float toeRestY, StringBuilder log)
    {
        const int samplesPerSecond = 240;
        int n = Mathf.RoundToInt(clip.length * samplesPerSecond);
        var lf = new Vector3[n + 1];
        var rf = new Vector3[n + 1];
        Transform la = animator.GetBoneTransform(HumanBodyBones.LeftFoot), lt = animator.GetBoneTransform(HumanBodyBones.LeftToes);
        Transform ra = animator.GetBoneTransform(HumanBodyBones.RightFoot), rt = animator.GetBoneTransform(HumanBodyBones.RightToes);
        // Contact point = whichever of ankle / ball of the foot is closer to the ground (heel vs forefoot strike).
        Vector3 Contact(Transform ankle, Transform toe, float ankleRest, float toeRest) =>
            toe == null || ankle.position.y - ankleRest < toe.position.y - toeRest ? ankle.position - Vector3.up * ankleRest : toe.position - Vector3.up * toeRest;
        for (int i = 0; i <= n; i++)
        {
            clip.SampleAnimation(animator.gameObject, clip.length * i / n);
            lf[i] = Contact(la, lt, ankleRestY, toeRestY);
            rf[i] = Contact(ra, rt, ankleRestY, toeRestY);
        }

        float minY = Mathf.Min(lf.Min(p => p.y), rf.Min(p => p.y));
        var speeds = new List<float>();
        float dt = 1f / samplesPerSecond;
        for (int i = 1; i <= n; i++)
        {
            foreach (var f in new[] { lf, rf })
            {
                if (f[i].y > minY + 0.03f) continue;
                if (Mathf.Abs(f[i].y - f[i - 1].y) / dt > 0.15f) continue; // landing / lifting
                float vz = -(f[i].z - f[i - 1].z) / dt; // body faces +Z, planted foot moves -Z
                if (vz > 0f) speeds.Add(vz);
            }
        }
        speeds.Sort();
        float median = speeds.Count > 0 ? speeds[speeds.Count / 2] : 0f;
        log.AppendLine($"{clip.name}: length={clip.length:F3}s cycle={1f / clip.length:F2}Hz stanceSamples={speeds.Count} " +
                       $"footGroundSpeed median={median:F2} m/s (p25={Pct(speeds, 0.25f):F2} p75={Pct(speeds, 0.75f):F2}) lowestContact={minY:F3} m");
        return median;
    }

    private static float Pct(List<float> sorted, float p) => sorted.Count == 0 ? 0f : sorted[Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

    // ------------------------------------------------------------------ 24 fps capture (Play Mode)
    public const string CaptureDir = "Captures/Locomotion_24fps";
    private const string CapturePendingKey = "NinoLocomotionSetup.CapturePendingKind";

    /// <summary>Enters Play Mode and records idle / walk / run / start / stop at 24 fps into Captures/Locomotion_24fps.</summary>
    [MenuItem("Tools/Locomotion/3. Capture 24fps Sequence (Play Mode)")]
    public static void CaptureMenu() => RequestCapture("loco");

    /// <summary>Enters Play Mode, stands still, plays the special idle through the real Animator and records face + body frames (Captures/SpecialIdle).</summary>
    [MenuItem("Tools/Locomotion/4. Capture Special Idle (Play Mode)")]
    public static void CaptureSpecialMenu() => RequestCapture("special");

    /// <summary>Enters Play Mode and checks the idle face through the real Animator: 7.5 s standing (mouth must stay closed), 2.5 s walking, then standing (Captures/IdleFace).</summary>
    [MenuItem("Tools/Locomotion/5. Capture Idle Face (Play Mode)")]
    public static void CaptureIdleFaceMenu() => RequestCapture("idleface");

    private static void RequestCapture(string kind)
    {
        if (EditorApplication.isPlaying) { StartCapture(kind); return; }
        // Entering Play Mode reloads the domain, so the request has to survive in SessionState.
        SessionState.SetString(CapturePendingKey, kind);
        EditorApplication.isPlaying = true;
    }

    [InitializeOnLoadMethod]
    private static void RegisterPlayModeHook() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        string kind = SessionState.GetString(CapturePendingKey, "");
        if (state != PlayModeStateChange.EnteredPlayMode || kind == "") return;
        SessionState.EraseString(CapturePendingKey);
        StartCapture(kind);
    }

    private static void StartCapture(string kind)
    {
        if (kind == "special")
        {
            SpecialIdleCapture.Begin("Captures/SpecialIdle");
            EditorApplication.update += WaitForSpecialCapture;
            return;
        }
        if (kind == "idleface")
        {
            IdleFaceCapture.Begin("Captures/IdleFace");
            EditorApplication.update += WaitForIdleFaceCapture;
            return;
        }
        StartCapture();
    }

    private static void WaitForIdleFaceCapture()
    {
        if (!IdleFaceCapture.Done && EditorApplication.isPlaying) return;
        EditorApplication.update -= WaitForIdleFaceCapture;
        Debug.Log($"[Idle face capture] {System.IO.Path.GetFullPath("Captures/IdleFace")}\n{IdleFaceCapture.Report}");
    }

    private static void WaitForSpecialCapture()
    {
        if (!SpecialIdleCapture.Done && EditorApplication.isPlaying) return;
        EditorApplication.update -= WaitForSpecialCapture;
        Debug.Log($"[Special idle capture] {System.IO.Path.GetFullPath("Captures/SpecialIdle")}\n{SpecialIdleCapture.Report}");
    }

    private static void StartCapture()
    {
        var nino = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NinoPrefabPath));
        var animator = nino.GetComponent<Animator>();
        float ankleRestY = animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
        Transform toes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
        float toeRestY = toes != null ? toes.position.y : 0f;
        Object.DestroyImmediate(nino);

        // Keep the player loop ticking while the editor window is in the background.
        Application.runInBackground = true;
        EditorApplication.update -= EditorApplication.QueuePlayerLoopUpdate;
        EditorApplication.update += EditorApplication.QueuePlayerLoopUpdate;
        LocomotionFrameCapture.Begin(System.IO.Path.GetFullPath(CaptureDir), ankleRestY, toeRestY);
        EditorApplication.update += WaitForCapture;
    }

    private static void WaitForCapture()
    {
        if (!LocomotionFrameCapture.Done && EditorApplication.isPlaying) return;
        EditorApplication.update -= WaitForCapture;
        EditorApplication.update -= EditorApplication.QueuePlayerLoopUpdate;
        Debug.Log($"[Locomotion capture] {System.IO.Path.GetFullPath(CaptureDir)}\n{LocomotionFrameCapture.Report}");
    }
}
