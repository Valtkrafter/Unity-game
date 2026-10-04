using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Clean, reproducible setup for the Mixamo X Bot locomotion clips on Nino.
///
/// 1. Reference T-pose: Mixamo FBX files downloaded "without skin" have no bind pose, so Unity builds the
///    humanoid reference pose from the first animation frame. For the idle that frame is a weight-shifted
///    stance (legs ~7 deg off vertical, feet splayed), and every retargeted pose inherits that error.
///    This tool rebuilds a true T-pose (level hips, straight vertical spine and legs, horizontal arms,
///    feet pointing forward) and writes it into each clip's humanoid description.
/// 2. Clip import: original Mixamo motion, no level offsets, no curve edits; root motion baked into pose.
/// 3. Speeds: measures each clip's natural ground speed from the planted foot (stride length x cadence)
///    so the controller moves the character exactly as fast as the feet push - no sliding, no fast-forward.
/// </summary>
public static class MixamoLocomotionSetup
{
    private const string IdlePath = "Assets/Animations/X Bot@Female Standing Pose.fbx";
    private const string BreathingIdlePath = "Assets/Animations/Female Locomotion Pack/idle.fbx";
    private const string WalkPath = "Assets/Animations/X Bot@Female Walk.fbx";
    private const string RunPath = "Assets/Animations/X Bot@Running.fbx";
    // Clips the blend tree actually uses: Unity-chan's walk/run (Unity-Chan! Model 1.2.2, humanoid FBX, no conversion).
    private const string LocoWalkPath = "Assets/ThirdParty/UnityChan/Animations/unitychan_WALK00_F.fbx";
    private const string LocoRunPath = "Assets/ThirdParty/UnityChan/Animations/unitychan_RUN00_F.fbx";
    // Root height offset per clip (importer "Offset", positive = lower), measured in Play Mode with
    // Tools/Locomotion/4 so the shoe soles touch the floor: with 0 the walk floated ~3 cm and the run sank ~1.6 cm
    // (the model origin sits 3 cm above the floor in the scene and the clips carry Unity-chan's own foot height).
    private static readonly Dictionary<string, float> LocoHeightOffset = new Dictionary<string, float>
    {
        { LocoWalkPath, 0.035f },
        { LocoRunPath, -0.01f },
    };
    // Idle flourish (SpecialIdle): VRoid "Model pose" - hand on hip, touches her hair.
    private const string IdleFlourishPath = "Assets/Animations/Nino/Gestures/VRoid_ModelPose.anim";
    private const string JumpPath = "Assets/Animations/Female Locomotion Pack/jump.fbx";
    private const string ReferenceRigPath = "Assets/Animations/Female Locomotion Pack/idle.fbx"; // bone mapping source
    private const string ControllerPath = "Assets/Animations/Nino_LocomotionController.controller";
    private const string NinoPrefabPath = "Assets/Models/Nino Nakano/5394265126170879566.prefab";

    [MenuItem("Tools/Locomotion/1. Reimport Mixamo Clips With Clean T-Pose")]
    public static void ReimportMenu() => Debug.Log(ReimportClips());

    [MenuItem("Tools/Locomotion/2. Measure Natural Clip Speeds")]
    public static void MeasureMenu() => Debug.Log(MeasureSpeeds(out _, out _));

    [MenuItem("Tools/Locomotion/3. Apply Speeds To Blend Tree And Scene")]
    public static void ApplyMenu() => Debug.Log(ApplyToControllerAndScene());

    public const string CaptureDir = "Captures/Locomotion_24fps";

    /// <summary>Enters Play Mode and records idle/walk/run/sprint at 24 fps into Captures/Locomotion_24fps.</summary>
    [MenuItem("Tools/Locomotion/4. Capture 24fps Sequence (Play Mode)")]
    public static void CaptureMenu()
    {
        if (EditorApplication.isPlaying) { StartCapture(); return; }
        // Entering Play Mode reloads the domain, so the request has to survive in SessionState.
        SessionState.SetBool(CapturePendingKey, true);
        EditorApplication.isPlaying = true;
    }

    private const string CapturePendingKey = "MixamoLocomotionSetup.CapturePending";

    [InitializeOnLoadMethod]
    private static void RegisterPlayModeHook() => EditorApplication.playModeStateChanged += OnPlayModeChanged;

    private static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state != PlayModeStateChange.EnteredPlayMode || !SessionState.GetBool(CapturePendingKey, false)) return;
        SessionState.EraseBool(CapturePendingKey);
        StartCapture();
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

    // Sprint reuses the run clip played faster; beyond ~1.35x a run cycle starts to look frantic.
    // A dedicated Mixamo sprint clip is needed for anything faster.
    private const float SprintPlaybackRate = 1.35f;

    public static string ApplyToControllerAndScene()
    {
        var log = new StringBuilder();
        ConfigureLocomotionClips(log);
        log.Append(MeasureSpeeds(out float walk, out float run));
        walk = Mathf.Round(walk * 100f) / 100f;
        run = Mathf.Round(run * 10f) / 10f;
        float sprint = Mathf.Round(run * SprintPlaybackRate * 10f) / 10f;

        var controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
        var locomotion = controller.layers[0].stateMachine.states.Select(s => s.state).First(s => s.name == "Locomotion");
        var tree = (BlendTree)locomotion.motion;
        tree.blendType = BlendTreeType.Simple1D;
        tree.blendParameter = "Speed";
        tree.useAutomaticThresholds = false;
        tree.children = new[]
        {
            Child(Clip(BreathingIdlePath), 0f, 1f), // same pose as the standing pose, but alive (breathing sway)
            Child(Clip(LocoWalkPath), walk, 1f),
            Child(Clip(LocoRunPath), run, 1f),
            Child(Clip(LocoRunPath), sprint, SprintPlaybackRate),
        };
        locomotion.speed = 1f;
        locomotion.iKOnFeet = false;
        BuildJumpStates(controller, locomotion);
        BuildSpecialIdle(controller, locomotion);
        EditorUtility.SetDirty(tree);
        EditorUtility.SetDirty(controller);
        AssetDatabase.SaveAssets();
        log.AppendLine($"Blend tree: idle 0 | walk {walk} | run {run} | sprint {sprint} (run x{SprintPlaybackRate})");
        log.AppendLine("Jump: Locomotion -> Jump_Takeoff -> Jump_Air (time = AirProgress) -> Jump_Land -> Locomotion");
        log.AppendLine("SpecialIdle: Locomotion -> SpecialIdle (VRoid_ModelPose) -> Locomotion");

        var player = GameObject.Find("Player");
        if (player != null)
        {
            var anim = player.GetComponentInChildren<Animator>();
            Undo.RecordObject(anim, "Assign locomotion controller");
            anim.runtimeAnimatorController = controller; // replaces the stale scene-embedded override controller
            anim.applyRootMotion = false;
            if (anim.GetComponent<DiscardRootMotion>() == null) Undo.AddComponent<DiscardRootMotion>(anim.gameObject);

            var cc = player.GetComponent<AnimeCharacterController>();
            var so = new SerializedObject(cc);
            so.FindProperty("walkSpeed").floatValue = walk;
            so.FindProperty("runSpeed").floatValue = run;
            so.FindProperty("sprintSpeed").floatValue = sprint;
            so.ApplyModifiedProperties();
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(player.scene);
            log.AppendLine($"Scene: Animator -> {controller.name}, controller speeds walk {walk} run {run} sprint {sprint}");
        }
        return log.ToString();
    }

    /// <summary>
    /// Takeoff plays on the Jump trigger; the air pose is scrubbed by AirProgress (set from the vertical velocity),
    /// so it matches the physics arc for any jump height or fall; landing plays when the ground is reached.
    /// </summary>
    private static void BuildJumpStates(AnimatorController controller, AnimatorState locomotion)
    {
        if (controller.parameters.All(p => p.name != "AirProgress"))
            controller.AddParameter("AirProgress", AnimatorControllerParameterType.Float);

        var sm = controller.layers[0].stateMachine;
        foreach (var child in sm.states)
            if (child.state != locomotion) sm.RemoveState(child.state);
        foreach (var t in locomotion.transitions) locomotion.RemoveTransition(t);
        foreach (var t in sm.anyStateTransitions) sm.RemoveAnyStateTransition(t);
        sm.defaultState = locomotion;

        AnimatorState State(string name, string clip, Vector3 pos)
        {
            var s = sm.AddState(name, pos);
            s.motion = Clip(JumpPath, clip);
            s.writeDefaultValues = true;
            s.iKOnFeet = false;
            return s;
        }
        var takeoff = State("Jump_Takeoff", JumpTakeoffClip, new Vector3(520f, -40f));
        var air = State("Jump_Air", JumpAirClip, new Vector3(760f, 60f));
        var land = State("Jump_Land", JumpLandClip, new Vector3(520f, 160f));
        air.timeParameterActive = true;
        air.timeParameter = "AirProgress";

        AnimatorStateTransition Link(AnimatorState from, AnimatorState to, float duration, float? exitTime = null)
        {
            var t = from.AddTransition(to);
            t.hasFixedDuration = true;
            t.duration = duration;
            t.hasExitTime = exitTime.HasValue;
            t.exitTime = exitTime ?? 0f;
            return t;
        }

        Link(locomotion, takeoff, 0.08f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
        Link(land, takeoff, 0.05f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
        Link(takeoff, air, 0.05f, exitTime: 1f);

        var fall = Link(locomotion, air, 0.2f); // walked off a ledge
        fall.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsGrounded");
        fall.AddCondition(AnimatorConditionMode.Less, -2f, "VerticalVelocity");

        var touchDown = Link(air, land, 0.06f);
        touchDown.AddCondition(AnimatorConditionMode.If, 0f, "IsGrounded");
        touchDown.AddCondition(AnimatorConditionMode.Less, 0.1f, "VerticalVelocity");

        // Landed while moving: run out of the landing early. Standing: let the crouch recover first.
        Link(land, locomotion, 0.2f, exitTime: 0.2f).AddCondition(AnimatorConditionMode.Greater, 0.5f, "Speed");
        Link(land, locomotion, 0.25f, exitTime: 0.6f);
    }

    /// <summary>
    /// SpecialIdle trigger (set by AnimeCharacterController after standing still) plays the idle flourish once;
    /// moving or jumping cuts it short.
    /// </summary>
    private static void BuildSpecialIdle(AnimatorController controller, AnimatorState locomotion)
    {
        var sm = controller.layers[0].stateMachine;
        var takeoff = sm.states.Select(s => s.state).First(s => s.name == "Jump_Takeoff");
        var flourish = sm.AddState("SpecialIdle", new Vector3(260f, 220f));
        flourish.motion = Clip(IdleFlourishPath);
        flourish.writeDefaultValues = true;
        flourish.iKOnFeet = false;

        AnimatorStateTransition Link(AnimatorState from, AnimatorState to, float duration, float? exitTime = null)
        {
            var t = from.AddTransition(to);
            t.hasFixedDuration = true;
            t.duration = duration;
            t.hasExitTime = exitTime.HasValue;
            t.exitTime = exitTime ?? 0f;
            return t;
        }

        var enter = Link(locomotion, flourish, 0.45f);
        enter.AddCondition(AnimatorConditionMode.If, 0f, "SpecialIdle");
        enter.AddCondition(AnimatorConditionMode.Less, 0.05f, "Speed");
        Link(flourish, locomotion, 0.6f, exitTime: 0.92f);
        Link(flourish, locomotion, 0.25f).AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        Link(flourish, takeoff, 0.08f).AddCondition(AnimatorConditionMode.If, 0f, "Jump");
    }

    /// <summary>
    /// Walk/run import settings: looping, everything baked into the pose relative to the original root (the clips are
    /// in place), lowered by LocoHeightOffset so the soles touch the floor in the scene.
    /// </summary>
    private static void ConfigureLocomotionClips(StringBuilder log)
    {
        foreach (var path in new[] { LocoWalkPath, LocoRunPath })
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);
            var clips = importer.clipAnimations.Length > 0 ? importer.clipAnimations : importer.defaultClipAnimations;
            foreach (var c in clips)
            {
                c.loopTime = true;
                c.lockRootRotation = true;
                c.keepOriginalOrientation = true;
                c.lockRootHeightY = true;
                c.keepOriginalPositionY = true;
                c.lockRootPositionXZ = true;
                c.keepOriginalPositionXZ = true;
                c.heightFromFeet = false;
                c.heightOffset = LocoHeightOffset[path];
            }
            importer.clipAnimations = clips;
            importer.SaveAndReimport();
            log.AppendLine($"{System.IO.Path.GetFileName(path)}: loop, root baked (Original), height offset {LocoHeightOffset[path]}");
        }
    }

    private static ChildMotion Child(Motion motion, float threshold, float timeScale) =>
        new ChildMotion { motion = motion, threshold = threshold, timeScale = timeScale, directBlendParameter = "Speed" };

    public static string ReimportClips()
    {
        var log = new StringBuilder();
        var refImporter = (ModelImporter)AssetImporter.GetAtPath(ReferenceRigPath);
        HumanBone[] humanBones = refImporter.humanDescription.human;
        // One shared T-pose for every clip: each file's own first frame (e.g. the crouched run) would give
        // every clip a different hip height, i.e. a different body scale, and the run would float.
        SkeletonBone[] tPose = BuildTPoseSkeleton(ReferenceRigPath);

        foreach (string path in new[] { IdlePath, BreathingIdlePath, WalkPath, RunPath, JumpPath })
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(path);

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;

            var skeleton = (SkeletonBone[])tPose.Clone();
            skeleton[0].name = System.IO.Path.GetFileNameWithoutExtension(path) + "(Clone)";
            var hd = refImporter.humanDescription;
            hd.human = humanBones;
            hd.skeleton = skeleton;
            importer.humanDescription = hd;

            ModelImporterClipAnimation[] clips;
            if (path == JumpPath)
            {
                // The Mixamo jump is one standing jump: crouch, push-off, air, landing. The CharacterController
                // owns the height, so the clip is split and the phases are driven by the physics state.
                // Takeoff and landing keep their crouch in the pose (feet stay on the floor). The air phase does
                // not bake its vertical motion: the clip's own 0.27 m hop is extracted as root motion and dropped
                // (applyRootMotion is off), so it does not stack on top of the physics jump.
                var src = importer.defaultClipAnimations[0];
                clips = JumpPhases.Select(p =>
                {
                    var c = CopyClip(src, p.Name, p.First, p.Last);
                    Configure(c, loop: false);
                    c.heightOffset = 0.04f; // the jump's standing frames sit 4 cm lower than the idle (measured in Play Mode)
                    if (p.Name == JumpAirClip) c.lockRootHeightY = false;
                    return c;
                }).ToArray();
            }
            else
            {
                clips = importer.defaultClipAnimations;
                foreach (var c in clips) Configure(c, loop: true);
            }
            importer.clipAnimations = clips;
            importer.SaveAndReimport();

            Avatar av = AssetDatabase.LoadAllAssetsAtPath(path).OfType<Avatar>().FirstOrDefault();
            log.AppendLine($"{path}: avatar valid={av != null && av.isValid} clips={string.Join(", ", clips.Select(c => c.name))}");
        }
        return log.ToString();
    }

    // Frames at 60 fps in jump.fbx: crouch 7-24, push-off 24-37 (feet leave ~34-38), air 37-63, landing 63-100.
    // Takeoff starts after the anticipation crouch so the jump responds immediately.
    private static readonly (string Name, float First, float Last)[] JumpPhases =
    {
        (JumpTakeoffClip, 29f, 37f),
        (JumpAirClip, 37f, 63f),
        (JumpLandClip, 63f, 100f),
    };

    public const string JumpTakeoffClip = "Jump_Takeoff";
    public const string JumpAirClip = "Jump_Air";
    public const string JumpLandClip = "Jump_Land";

    private static void Configure(ModelImporterClipAnimation c, bool loop)
    {
        c.loopTime = loop;
        c.loopPose = loop;
        c.cycleOffset = 0f;
        c.mirror = false;
        c.heightOffset = 0f;            // no artificial sinking of the hips
        c.rotationOffset = 0f;
        c.lockRootRotation = true;      // root rotation baked: no drifting yaw
        c.keepOriginalOrientation = false;
        c.lockRootHeightY = true;       // vertical motion stays in the pose
        c.keepOriginalPositionY = true;
        c.heightFromFeet = false;
        c.lockRootPositionXZ = true;    // character controller owns translation
        c.keepOriginalPositionXZ = false;
        c.curves = new ClipAnimationInfoCurve[0];
        c.events = new AnimationEvent[0];
    }

    private static ModelImporterClipAnimation CopyClip(ModelImporterClipAnimation src, string name, float first, float last) =>
        new ModelImporterClipAnimation
        {
            name = name,
            takeName = src.takeName,
            firstFrame = first,
            lastFrame = last,
            maskType = src.maskType,
            wrapMode = src.wrapMode,
        };

    /// <summary>Poses the file's rig into a strict T-pose and returns it as a humanoid skeleton description.</summary>
    private static SkeletonBone[] BuildTPoseSkeleton(string path)
    {
        var go = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(path));
        try
        {
            Transform B(string n) => go.GetComponentsInChildren<Transform>().First(t => t.name == "mixamorig:" + n);
            void Aim(Transform bone, Transform child, Vector3 dir)
            {
                Vector3 cur = child.position - bone.position;
                bone.rotation = Quaternion.FromToRotation(cur, dir) * bone.rotation;
            }

            // The reference file is a flat-footed standing frame: remember its ankle height above the ground.
            float ankleHeight = Mathf.Min(B("LeftFoot").position.y, B("RightFoot").position.y);

            // Hips: upright, hip line along X, facing +Z.
            Transform hips = B("Hips");
            hips.rotation = Quaternion.identity;
            Vector3 hipLine = B("RightUpLeg").position - B("LeftUpLeg").position; hipLine.y = 0f;
            hips.rotation = Quaternion.FromToRotation(hipLine, Vector3.right * Mathf.Sign(hipLine.x)) * hips.rotation;

            // Spine chain straight up.
            Aim(B("Spine"), B("Spine1"), Vector3.up);
            Aim(B("Spine1"), B("Spine2"), Vector3.up);
            Aim(B("Spine2"), B("Neck"), Vector3.up);
            Aim(B("Neck"), B("Head"), Vector3.up);
            Aim(B("Head"), B("HeadTop_End"), Vector3.up);

            foreach (string side in new[] { "Left", "Right" })
            {
                // Legs straight down, feet pointing forward and flat.
                Aim(B(side + "UpLeg"), B(side + "Leg"), Vector3.down);
                Aim(B(side + "Leg"), B(side + "Foot"), Vector3.down);
                Transform foot = B(side + "Foot"), toe = B(side + "ToeBase");
                Vector3 toeDir = toe.position - foot.position;
                Vector3 flatDir = new Vector3(0f, toeDir.y, Mathf.Sqrt(toeDir.x * toeDir.x + toeDir.z * toeDir.z));
                Aim(foot, toe, flatDir); // remove toe-in/out but keep the natural ankle pitch

                // Arms horizontal (shoulders and arm twist kept from the file).
                float sx = Mathf.Sign(B(side + "Arm").position.x - B(side + "Shoulder").position.x);
                Vector3 outward = new Vector3(sx, 0f, 0f);
                Aim(B(side + "Arm"), B(side + "ForeArm"), outward);
                Aim(B(side + "ForeArm"), B(side + "Hand"), outward);
            }

            // Stand the straightened legs on the ground: hip height = ankle height + leg length + pelvis.
            float lowestAnkle = Mathf.Min(B("LeftFoot").position.y, B("RightFoot").position.y);
            Vector3 hp = hips.position;
            hips.position = new Vector3(0f, hp.y + ankleHeight - lowestAnkle, 0f);

            var skeleton = new List<SkeletonBone>();
            foreach (Transform t in go.GetComponentsInChildren<Transform>())
            {
                skeleton.Add(new SkeletonBone
                {
                    name = t == go.transform ? go.name : t.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                });
            }
            return skeleton.ToArray();
        }
        finally
        {
            Object.DestroyImmediate(go);
        }
    }

    /// <summary>
    /// Plays each loop on a hidden copy of Nino and measures how fast the planted foot travels backwards
    /// relative to the body. That is the speed the character must move for the foot to stay locked to the ground.
    /// </summary>
    public static string MeasureSpeeds(out float walkSpeed, out float runSpeed)
    {
        var log = new StringBuilder();
        var nino = (GameObject)Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(NinoPrefabPath));
        nino.hideFlags = HideFlags.HideAndDontSave;
        try
        {
            var animator = nino.GetComponent<Animator>();
            animator.applyRootMotion = false;
            // Rest heights of ankle and ball of the foot in Nino's bind pose (foot flat on the ground).
            float ankleRestY = animator.GetBoneTransform(HumanBodyBones.LeftFoot).position.y;
            Transform toes = animator.GetBoneTransform(HumanBodyBones.LeftToes);
            float toeRestY = toes != null ? toes.position.y : 0f;
            log.AppendLine($"Nino rest: ankleY={ankleRestY:F3} toeY={toeRestY:F3} (contact heights below are relative to these; 0 = touching ground)");
            Measure(animator, Clip(IdlePath), ankleRestY, toeRestY, log, out _);
            walkSpeed = Measure(animator, Clip(LocoWalkPath), ankleRestY, toeRestY, log, out _);
            runSpeed = Measure(animator, Clip(LocoRunPath), ankleRestY, toeRestY, log, out _);
        }
        finally
        {
            Object.DestroyImmediate(nino);
        }
        return log.ToString();
    }

    private static float Measure(Animator animator, AnimationClip clip, float ankleRestY, float toeRestY, StringBuilder log, out int stanceSamples)
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

        // Planted foot = contact point near the ground. Its backward speed is the required ground speed.
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
        stanceSamples = speeds.Count;
        float median = speeds.Count > 0 ? speeds[speeds.Count / 2] : 0f;
        log.AppendLine($"{clip.name}: length={clip.length:F3}s cycle={1f / clip.length:F2}Hz stanceSamples={speeds.Count} " +
                       $"footGroundSpeed median={median:F2} m/s (p25={Pct(speeds, 0.25f):F2} p75={Pct(speeds, 0.75f):F2}) lowestContact={minY:F3} m");
        return median;
    }

    private static float Pct(List<float> sorted, float p) => sorted.Count == 0 ? 0f : sorted[Mathf.Clamp((int)(sorted.Count * p), 0, sorted.Count - 1)];

    public static AnimationClip Clip(string path) =>
        AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => !c.name.StartsWith("__preview__"));

    public static AnimationClip Clip(string path, string name) =>
        AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>().First(c => c.name == name);
}
