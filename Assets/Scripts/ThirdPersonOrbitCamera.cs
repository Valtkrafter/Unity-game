using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Wuthering Waves style third-person camera:
/// - mouse orbit (frame-rate independent), scroll zoom, pitch-dependent distance (closer when looking up)
/// - tight horizontal follow with a softer vertical follow, so jumps and steps don't jolt the view
/// - lazy auto-recentering behind the character while it moves sideways, paused while the player steers
/// - slight FOV widening while sprinting
/// - collision pull-in that snaps in and eases back out
/// - cursor locked in play; hold Left Alt to free it (Esc unlocks, click relocks)
/// </summary>
[DisallowMultipleComponent]
public sealed class ThirdPersonOrbitCamera : MonoBehaviour
{
    [Header("Target Tracking")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 targetOffset = new Vector3(0f, 1.35f, 0f);
    [SerializeField] private float followSmoothTimeHorizontal = 0.05f;
    [SerializeField] private float followSmoothTimeVertical = 0.18f;

    [Header("Distance & Zoom")]
    [SerializeField] private float defaultDistance = 4.2f;
    [SerializeField] private float minDistance = 1.6f;
    [SerializeField] private float maxDistance = 8.0f;
    [SerializeField] private float zoomStep = 0.6f;
    [SerializeField] private float distanceSmoothTime = 0.12f;
    [Tooltip("Distance multiplier when looking fully up (at minPitch); keeps the camera off the ground.")]
    [SerializeField] private float lookUpDistanceScale = 0.55f;

    [Header("Look")]
    [SerializeField] private float mouseSensitivity = 0.12f; // degrees per mouse pixel
    [SerializeField] private bool invertY;
    [SerializeField] private float minPitch = -35f;
    [SerializeField] private float maxPitch = 70f;
    [SerializeField] private float defaultPitch = 12f;
    [SerializeField] private float rotationSmoothTime = 0.03f;

    [Header("Auto Recenter")]
    [SerializeField] private float autoRecenterDelay = 0.6f;    // seconds after the last mouse input
    [SerializeField] private float autoRecenterYawSpeed = 70f;  // deg/s at full sideways run
    [SerializeField] private float autoRecenterPitchSpeed = 12f;

    [Header("Field Of View")]
    [SerializeField] private float fieldOfView = 50f;
    [SerializeField] private float sprintFovBoost = 5f;
    [SerializeField] private float fovSmoothTime = 0.35f;

    [Header("Collision Avoidance")]
    [SerializeField] private LayerMask collisionLayers = 1;
    [SerializeField] private float collisionRadius = 0.25f;
    [SerializeField] private float collisionRecoverTime = 0.25f;

    private Camera cam;
    private AnimeCharacterController targetController;

    private float targetYaw, targetPitch, currentYaw, currentPitch;
    private float yawVelocity, pitchVelocity;
    private float zoomDistance, currentDistance, distanceVelocity;
    private float fovVelocity;
    private Vector3 focusPoint, focusVelocity;
    private Vector3 lastTargetPosition;
    private float timeSinceLook = 999f;
    private bool initialized;
    private bool cursorReleased;

    public Vector3 PlanarForward
    {
        get
        {
            Vector3 forward = transform.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 0.001f ? forward.normalized : Vector3.forward;
        }
    }

    public Vector3 PlanarRight
    {
        get
        {
            Vector3 right = transform.right;
            right.y = 0f;
            return right.sqrMagnitude > 0.001f ? right.normalized : Vector3.right;
        }
    }

    private void Awake()
    {
        cam = GetComponent<Camera>();
        zoomDistance = currentDistance = defaultDistance;
    }

    private void Start()
    {
        LockCursor(true);
    }

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
        targetController = target != null ? target.GetComponent<AnimeCharacterController>() : null;
        initialized = false;
    }

    private void SnapBehindTarget()
    {
        targetYaw = currentYaw = target.eulerAngles.y;
        targetPitch = currentPitch = defaultPitch;
        focusPoint = target.position + targetOffset;
        focusVelocity = Vector3.zero;
        lastTargetPosition = target.position;
        initialized = true;
    }

    private void LateUpdate()
    {
        if (target == null) return;
        if (targetController == null) targetController = target.GetComponent<AnimeCharacterController>();
        if (!initialized) SnapBehindTarget();

        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        UpdateCursor();
        ReadLookAndZoom(out Vector2 look, out float scroll);

        // --- Orbit input ---
        if (look.sqrMagnitude > 0.0001f)
        {
            targetYaw += look.x * mouseSensitivity;
            targetPitch += (invertY ? look.y : -look.y) * mouseSensitivity;
            timeSinceLook = 0f;
        }
        else
        {
            timeSinceLook += dt;
        }

        // --- Auto recenter while moving (yaw follows sideways motion, pitch eases to default) ---
        Vector3 planarVelocity = (target.position - lastTargetPosition) / dt;
        planarVelocity.y = 0f;
        lastTargetPosition = target.position;
        float speed = planarVelocity.magnitude;
        if (timeSinceLook > autoRecenterDelay && speed > 0.5f)
        {
            Vector3 moveDir = planarVelocity / speed;
            Vector3 camForward = Quaternion.Euler(0f, targetYaw, 0f) * Vector3.forward;
            float forwardDot = Vector3.Dot(moveDir, camForward);
            if (forwardDot > -0.6f) // running towards the camera: leave it alone
            {
                float sideways = Vector3.Cross(camForward, moveDir).y; // -1..1, sign = turn direction
                float speedFactor = Mathf.Clamp01(speed / 3.4f);
                targetYaw += sideways * autoRecenterYawSpeed * speedFactor * dt;
                targetPitch = Mathf.MoveTowards(targetPitch, defaultPitch, autoRecenterPitchSpeed * speedFactor * dt);
            }
        }

        targetPitch = Mathf.Clamp(targetPitch, minPitch, maxPitch);
        currentYaw = Mathf.SmoothDampAngle(currentYaw, targetYaw, ref yawVelocity, rotationSmoothTime);
        currentPitch = Mathf.SmoothDamp(currentPitch, targetPitch, ref pitchVelocity, rotationSmoothTime);
        Quaternion rotation = Quaternion.Euler(currentPitch, currentYaw, 0f);

        // --- Follow: tight on the ground plane, softer vertically ---
        Vector3 desiredFocus = target.position + targetOffset;
        focusPoint.x = Mathf.SmoothDamp(focusPoint.x, desiredFocus.x, ref focusVelocity.x, followSmoothTimeHorizontal);
        focusPoint.z = Mathf.SmoothDamp(focusPoint.z, desiredFocus.z, ref focusVelocity.z, followSmoothTimeHorizontal);
        focusPoint.y = Mathf.SmoothDamp(focusPoint.y, desiredFocus.y, ref focusVelocity.y, followSmoothTimeVertical);

        // --- Distance: zoom, look-up shortening, collision ---
        if (Mathf.Abs(scroll) > 0.01f)
            zoomDistance = Mathf.Clamp(zoomDistance - scroll * zoomStep, minDistance, maxDistance);
        float lookUp = Mathf.InverseLerp(0f, minPitch, currentPitch); // 0 at level, 1 fully up
        float wantedDistance = zoomDistance * Mathf.Lerp(1f, lookUpDistanceScale, lookUp);

        Vector3 back = rotation * Vector3.back;
        if (Physics.SphereCast(focusPoint, collisionRadius, back, out RaycastHit hit, wantedDistance, collisionLayers, QueryTriggerInteraction.Ignore))
            wantedDistance = Mathf.Max(hit.distance, 0.3f);

        if (wantedDistance < currentDistance)
        {
            currentDistance = wantedDistance; // snap in: never show the inside of a wall
            distanceVelocity = 0f;
        }
        else
        {
            currentDistance = Mathf.SmoothDamp(currentDistance, wantedDistance, ref distanceVelocity,
                Mathf.Max(distanceSmoothTime, collisionRecoverTime));
        }

        transform.SetPositionAndRotation(focusPoint + back * currentDistance, rotation);

        // --- FOV ---
        if (cam != null)
        {
            bool sprinting = targetController != null && targetController.IsSprinting;
            float wantedFov = fieldOfView + (sprinting ? sprintFovBoost : 0f);
            cam.fieldOfView = Mathf.SmoothDamp(cam.fieldOfView, wantedFov, ref fovVelocity, fovSmoothTime);
        }
    }

    private void ReadLookAndZoom(out Vector2 look, out float scroll)
    {
        look = Vector2.zero;
        scroll = 0f;
        bool locked = Cursor.lockState == CursorLockMode.Locked; // orbit only while locked; zoom always

#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            if (locked) look = Mouse.current.delta.ReadValue(); // pixels this frame
            // Input System 1.8+ reports one notch as 1 (ScrollDeltaBehavior.UniformAcrossAllPlatforms);
            // older versions / KeepPlatformSpecificInputRange report 120 per notch on Windows.
            float raw = Mouse.current.scroll.ReadValue().y;
            scroll = Mathf.Abs(raw) >= 20f ? raw / 120f : raw;
            return;
        }
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        try
        {
            // Legacy axes are already scaled by the input manager's sensitivity (~0.1 per pixel).
            if (locked) look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
            scroll = Input.mouseScrollDelta.y;
        }
        catch { }
#endif
    }

    private void UpdateCursor()
    {
        bool altHeld = false, escPressed = false, clicked = false;
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            altHeld = Keyboard.current.leftAltKey.isPressed;
            escPressed = Keyboard.current.escapeKey.wasPressedThisFrame;
        }
        if (Mouse.current != null) clicked = Mouse.current.leftButton.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        altHeld = Input.GetKey(KeyCode.LeftAlt);
        escPressed = Input.GetKeyDown(KeyCode.Escape);
        clicked = Input.GetMouseButtonDown(0);
#endif
        if (escPressed) cursorReleased = true;
        else if (clicked && !altHeld) cursorReleased = false;
        LockCursor(!altHeld && !cursorReleased);
    }

    private static void LockCursor(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }
}
