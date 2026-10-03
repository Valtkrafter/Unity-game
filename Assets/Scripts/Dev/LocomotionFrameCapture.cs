#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Editor-only diagnostic: drives AnimeCharacterController through idle/walk/run/sprint, start/stop and
/// standing/running jumps, recording each phase at a locked 24 fps (Time.captureFramerate), so every cell is
/// 1/24 s apart. Writes three contact sheets per phase (game camera, side view, front view) and a text report
/// with foot sliding, shoe-sole height, knee flexion, ground speed and animator state.
///
/// Usage (Play Mode): LocomotionFrameCapture.Begin(dir, ankleRestY, toeRestY); poll Done / Report.
/// </summary>
[DefaultExecutionOrder(10000)]
public sealed class LocomotionFrameCapture : MonoBehaviour
{
    private const int Fps = 24;
    private const int Cols = 6;
    private const int CellW = 256;
    private const int CellH = 320;

    public static bool Done { get; private set; }
    public static string Report { get; private set; } = "";

    private struct Phase
    {
        public string Name;
        public Vector2 SettleInput; // held while settling, before the first recorded frame
        public Vector2 Input;       // held while recording
        public bool Walk, Sprint, Jump;
        public int Frames;
    }

    private static Phase Steady(string name, Vector2 input, bool walk = false, bool sprint = false) =>
        new Phase { Name = name, SettleInput = input, Input = input, Walk = walk, Sprint = sprint, Frames = 24 };

    private static readonly Phase[] Phases =
    {
        Steady("idle", Vector2.zero),
        Steady("walk", Vector2.up, walk: true),
        Steady("run", Vector2.up),
        Steady("sprint", Vector2.up, sprint: true),
        new Phase { Name = "start", SettleInput = Vector2.zero, Input = Vector2.up, Frames = 24 },
        new Phase { Name = "stop", SettleInput = Vector2.up, Input = Vector2.zero, Frames = 24 },
        new Phase { Name = "jump", SettleInput = Vector2.zero, Input = Vector2.zero, Jump = true, Frames = 36 },
        new Phase { Name = "run_jump", SettleInput = Vector2.up, Input = Vector2.up, Jump = true, Frames = 36 },
        // Camera check: run to the camera's right; the camera should swing round behind the character.
        new Phase { Name = "camera_strafe", SettleInput = Vector2.zero, Input = Vector2.right, Frames = 36 },
    };

    private static readonly string[] StateNames = { "Locomotion", "Jump_Takeoff", "Jump_Air", "Jump_Land" };

    private const int SettleFrames = Fps * 2; // 2 s to reach steady state before recording

    private string outDir;
    private Transform player;
    private Vector3 startPosition;
    private Animator animator;
    private Camera gameCam, sideCam;
    private RenderTexture rt;
    private Texture2D readback, gameSheet, sideSheet, frontSheet;
    private int phaseIndex, frameInPhase;
    private StringBuilder log;
    private Vector3 lastPlayerPos;
    private Foot lastLFoot, lastRFoot;
    private float slideSum, slideMax, speedSum;
    private int slideCount, speedCount;
    private float minKnee = 999f, maxKnee;

    private float ankleRestY, toeRestY;
    private float contactSum; private int contactCount;
    private SkinnedMeshRenderer[] skins;
    private Mesh bakeMesh;
    private readonly List<Vector3> bakeVerts = new List<Vector3>();
    private readonly List<float> soleHeights = new List<float>();

    /// <param name="ankleRestY">Ankle height above the ground with the foot flat (bind pose).</param>
    /// <param name="toeRestY">Ball-of-foot (toe bone) height above the ground with the foot flat.</param>
    public static void Begin(string outputDir, float ankleRestY, float toeRestY)
    {
        Done = false;
        Report = "";
        var cap = new GameObject("__LocomotionFrameCapture").AddComponent<LocomotionFrameCapture>();
        cap.outDir = outputDir;
        cap.ankleRestY = ankleRestY;
        cap.toeRestY = toeRestY;
    }

    private void Start()
    {
        Directory.CreateDirectory(outDir);
        var pgo = GameObject.Find("Player");
        player = pgo.transform;
        startPosition = player.position;
        animator = pgo.GetComponentInChildren<Animator>();
        skins = pgo.GetComponentsInChildren<SkinnedMeshRenderer>();
        bakeMesh = new Mesh();
        gameCam = Camera.main;

        sideCam = new GameObject("__SideCam").AddComponent<Camera>();
        sideCam.enabled = false;
        sideCam.orthographic = true;
        sideCam.orthographicSize = 1.0f;
        sideCam.clearFlags = CameraClearFlags.SolidColor;
        sideCam.backgroundColor = new Color(0.20f, 0.21f, 0.24f);

        rt = new RenderTexture(CellW, CellH, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
        readback = new Texture2D(CellW, CellH, TextureFormat.RGB24, false);

        log = new StringBuilder();
        log.AppendLine($"humanScale={animator.humanScale:F3} ankleRestY={ankleRestY:F3} toeRestY={toeRestY:F3}");
        Time.captureFramerate = Fps;
        AnimeCharacterController.UseInputOverride = true;
        StartPhase(0);
    }

    private void StartPhase(int i)
    {
        phaseIndex = i;
        frameInPhase = -SettleFrames;
        var p = Phases[i];
        // Back to the arena centre each phase so long runs never leave the floor.
        var cc = player.GetComponent<CharacterController>();
        cc.enabled = false;
        player.position = startPosition;
        cc.enabled = true;
        AnimeCharacterController.InputOverride = p.SettleInput;
        AnimeCharacterController.WalkOverride = p.Walk;
        AnimeCharacterController.SprintOverride = p.Sprint;
        int rows = Mathf.CeilToInt(p.Frames / (float)Cols);
        gameSheet = new Texture2D(CellW * Cols, CellH * rows, TextureFormat.RGB24, false);
        sideSheet = new Texture2D(CellW * Cols, CellH * rows, TextureFormat.RGB24, false);
        frontSheet = new Texture2D(CellW * Cols, CellH * rows, TextureFormat.RGB24, false);
        slideSum = slideMax = speedSum = contactSum = 0f;
        slideCount = speedCount = contactCount = 0;
        soleHeights.Clear();
        minKnee = 999f; maxKnee = 0f;
        Clear(gameSheet);
        Clear(sideSheet);
        Clear(frontSheet);
        log.AppendLine($"--- {p.Name} ---");
    }

    private void LateUpdate()
    {
        if (Done || player == null) return;

        float groundY = Physics.Raycast(player.position + Vector3.up, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore)
            ? hit.point.y : player.position.y;
        Foot lf = Contact(HumanBodyBones.LeftFoot, HumanBodyBones.LeftToes, groundY);
        Foot rf = Contact(HumanBodyBones.RightFoot, HumanBodyBones.RightToes, groundY);
        Vector3 pos = player.position;

        if (frameInPhase >= 0)
        {
            float dt = 1f / Fps;
            // Only the support foot (the lower one, unchanged since last frame) is checked: in a low-clearance
            // walk the swinging foot also passes within a few cm of the floor.
            bool leftLower = lf.Height <= rf.Height;
            Foot support = leftLower ? lf : rf, lastSupport = leftLower ? lastLFoot : lastRFoot;
            if (leftLower == lastLFoot.Height <= lastRFoot.Height)
                Measure(support, lastSupport, dt);
            Vector3 d = pos - lastPlayerPos; d.y = 0f;
            speedSum += d.magnitude / dt; speedCount++;
            float lk = KneeAngle(HumanBodyBones.LeftUpperLeg, HumanBodyBones.LeftLowerLeg, HumanBodyBones.LeftFoot);
            float rk = KneeAngle(HumanBodyBones.RightUpperLeg, HumanBodyBones.RightLowerLeg, HumanBodyBones.RightFoot);
            minKnee = Mathf.Min(minKnee, lk, rk); maxKnee = Mathf.Max(maxKnee, lk, rk);

            float sole = LowestVertexY() - groundY;
            soleHeights.Add(sole);

            var st = animator.GetCurrentAnimatorStateInfo(0);
            string state = System.Array.Find(StateNames, n => st.IsName(n)) ?? "?";
            log.AppendLine($"f{frameInPhase:D2} t={Time.time:F3} state={state} speedParam={animator.GetFloat("Speed"):F2} norm={st.normalizedTime:F3} " +
                           $"hipsY={animator.GetBoneTransform(HumanBodyBones.Hips).position.y - groundY:F3} sole={sole * 100f:F1}cm " +
                           $"lContact={lf.Height:F3} rContact={rf.Height:F3} support={(leftLower ? "L" : "R")} " +
                           $"ankleY={support.Ankle.y:F3} toeY={support.Toe.y:F3} ankleV={PlanarSpeed(support.Ankle, lastSupport.Ankle, dt):F2} toeV={PlanarSpeed(support.Toe, lastSupport.Toe, dt):F2} " +
                           $"kneeFlexL={lk:F0} kneeFlexR={rk:F0}");

            CaptureCell(gameCam, gameSheet);
            PlaceViewCam(sideways: true);
            CaptureCell(sideCam, sideSheet);
            PlaceViewCam(sideways: false);
            CaptureCell(sideCam, frontSheet);
        }

        lastLFoot = lf; lastRFoot = rf; lastPlayerPos = pos;
        frameInPhase++;

        if (frameInPhase == 0)
        {
            // Recording starts next frame: switch to the phase's action input now so frame 0 shows its first response.
            var p = Phases[phaseIndex];
            AnimeCharacterController.InputOverride = p.Input;
            AnimeCharacterController.JumpOverride = p.Jump;
        }
        if (frameInPhase >= Phases[phaseIndex].Frames) FinishPhase();
    }

    /// <summary>World Y of the lowest skinned vertex (the shoe sole), i.e. what actually touches the floor.</summary>
    private float LowestVertexY()
    {
        float min = float.MaxValue;
        foreach (var smr in skins)
        {
            if (!smr.enabled || !smr.gameObject.activeInHierarchy) continue;
            smr.BakeMesh(bakeMesh, true);
            bakeMesh.GetVertices(bakeVerts);
            Matrix4x4 m = smr.transform.localToWorldMatrix;
            foreach (var v in bakeVerts) min = Mathf.Min(min, m.MultiplyPoint3x4(v).y);
        }
        return min;
    }

    /// <summary>Heel/ankle and ball-of-foot points, with y = height above their flat-foot rest height (0 = touching).</summary>
    private struct Foot
    {
        public Vector3 Ankle, Toe;
        public float Height => Mathf.Min(Ankle.y, Toe.y);
    }

    private Foot Contact(HumanBodyBones ankleBone, HumanBodyBones toeBone, float groundY)
    {
        Vector3 ankle = animator.GetBoneTransform(ankleBone).position - Vector3.up * (groundY + ankleRestY);
        Transform toeT = animator.GetBoneTransform(toeBone);
        Vector3 toe = toeT != null ? toeT.position - Vector3.up * (groundY + toeRestY) : ankle;
        return new Foot { Ankle = ankle, Toe = toe };
    }

    private void Measure(Foot foot, Foot last, float dt)
    {
        // Within 2 cm of the ground (both frames) counts as planted. A planted foot rolls over its heel or
        // its ball, so one of those two points must stay still: its speed is the slide.
        if (foot.Height > 0.02f || last.Height > 0.02f) return;
        float v = Mathf.Min(PlanarSpeed(foot.Ankle, last.Ankle, dt), PlanarSpeed(foot.Toe, last.Toe, dt));
        slideSum += v; slideCount++;
        slideMax = Mathf.Max(slideMax, v);
        contactSum += foot.Height; contactCount++;
    }

    private static float PlanarSpeed(Vector3 a, Vector3 b, float dt)
    {
        Vector3 d = a - b; d.y = 0f;
        return d.magnitude / dt;
    }

    private float KneeAngle(HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones foot)
    {
        Vector3 a = animator.GetBoneTransform(upper).position;
        Vector3 b = animator.GetBoneTransform(lower).position;
        Vector3 c = animator.GetBoneTransform(foot).position;
        return 180f - Vector3.Angle(a - b, c - b); // 0 = straight leg
    }

    /// <summary>Orthographic tracking view from the character's right side, or from straight in front.</summary>
    private void PlaceViewCam(bool sideways)
    {
        Vector3 fwd = player.forward;
        Vector3 dir = sideways ? Vector3.Cross(Vector3.up, fwd).normalized : fwd;
        Vector3 center = player.position + Vector3.up * 0.85f;
        sideCam.transform.position = center + dir * 4f;
        sideCam.transform.rotation = Quaternion.LookRotation(-dir, Vector3.up);
    }

    private void CaptureCell(Camera cam, Texture2D sheet)
    {
        var prevTarget = cam.targetTexture;
        float prevAspect = cam.aspect;
        cam.targetTexture = rt;
        cam.aspect = (float)CellW / CellH;
        cam.Render();
        cam.targetTexture = prevTarget;
        cam.aspect = prevAspect;
        if (prevTarget == null) cam.ResetAspect();

        var prevActive = RenderTexture.active;
        RenderTexture.active = rt;
        readback.ReadPixels(new Rect(0, 0, CellW, CellH), 0, 0);
        readback.Apply();
        RenderTexture.active = prevActive;

        int rows = sheet.height / CellH;
        int col = frameInPhase % Cols;
        int row = rows - 1 - frameInPhase / Cols; // first frame top-left
        sheet.SetPixels(col * CellW, row * CellH, CellW, CellH, readback.GetPixels());
    }

    private void FinishPhase()
    {
        var p = Phases[phaseIndex];
        gameSheet.Apply(); sideSheet.Apply(); frontSheet.Apply();
        File.WriteAllBytes(Path.Combine(outDir, $"{p.Name}_game.png"), gameSheet.EncodeToPNG());
        File.WriteAllBytes(Path.Combine(outDir, $"{p.Name}_side.png"), sideSheet.EncodeToPNG());
        File.WriteAllBytes(Path.Combine(outDir, $"{p.Name}_front.png"), frontSheet.EncodeToPNG());
        float avgSpeed = speedCount > 0 ? speedSum / speedCount : 0f;
        float avgSlide = slideCount > 0 ? slideSum / slideCount : 0f;
        float avgContact = contactCount > 0 ? contactSum / contactCount : 0f;
        soleHeights.Sort();
        string summary = $"[{p.Name}] groundSpeed={avgSpeed:F2} m/s  plantedFootSlide avg={avgSlide:F2} max={slideMax:F2} m/s (n={slideCount})  " +
                         $"plantedHeight avg={avgContact * 100f:F1} cm  " +
                         $"shoeSole min={soleHeights[0] * 100f:F1} median={soleHeights[soleHeights.Count / 2] * 100f:F1} max={soleHeights[soleHeights.Count - 1] * 100f:F1} cm  " +
                         $"kneeFlex {minKnee:F0}..{maxKnee:F0} deg";
        log.AppendLine(summary);
        Report += summary + "\n";

        if (phaseIndex + 1 < Phases.Length)
        {
            StartPhase(phaseIndex + 1);
            return;
        }

        File.WriteAllText(Path.Combine(outDir, "report.txt"), log.ToString());
        Time.captureFramerate = 0;
        AnimeCharacterController.UseInputOverride = false;
        AnimeCharacterController.WalkOverride = false;
        AnimeCharacterController.SprintOverride = false;
        Destroy(sideCam.gameObject);
        rt.Release();
        Done = true;
        Destroy(gameObject);
    }

    private static void Clear(Texture2D t)
    {
        var px = new Color[t.width * t.height];
        for (int i = 0; i < px.Length; i++) px[i] = Color.black;
        t.SetPixels(px);
    }
}
#endif
