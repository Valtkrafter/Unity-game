using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class AnimeCharacterController : MonoBehaviour
{
    // Speeds match the stride of the Mixamo clips on Nino (measured with Tools/Locomotion/2. Measure Natural Clip Speeds)
    // so the planted foot stays locked to the ground. Change them together with the blend tree thresholds.
    [Header("Locomotion Speeds (m/s)")]
    [SerializeField] private float walkSpeed = 0.95f;
    [SerializeField] private float runSpeed = 3.4f;
    [SerializeField] private float sprintSpeed = 4.6f;
    // Movement speed and the Animator Speed parameter share one ramped value, so feet stay in sync on starts/stops.
    [SerializeField] private float acceleration = 9.0f;  // m/s^2 (idle -> run in ~0.4 s)
    [SerializeField] private float deceleration = 7.0f;  // m/s^2 (run -> idle in ~0.5 s: one or two slowing steps)

    [Header("Rotation Tuning")]
    [SerializeField] private float rotationSmoothTime = 0.08f;
    private float currentTurnVelocity;

    [Header("Anime Physics & Jump")]
    [SerializeField] private float gravity = -25.0f;
    [SerializeField] private float jumpHeight = 1.8f;
    [Tooltip("Delay between the jump press and leaving the ground, matching the push-off of the jump animation.")]
    [SerializeField] private float jumpTakeoffDelay = 0.07f;
    [SerializeField] private float terminalVelocity = -40.0f;
    [SerializeField] private float groundedStickiness = -3.0f;
    [SerializeField] private LayerMask groundLayer = 1; // Default to layer 1

    [Header("Camera Reference")]
    [SerializeField] private ThirdPersonOrbitCamera orbitCamera;

    [Header("Animation Hook")]
    [SerializeField] private Animator animator;

    private CharacterController controller;
    private Vector3 verticalVelocity;
    private float currentSpeedMagnitude;
    private Vector3 lastMoveDirection;
    private float takeoffTimer = -1f; // >= 0 while the push-off animation plays before leaving the ground

    public float CurrentSpeed => currentSpeedMagnitude;
    public bool IsSprinting => currentSpeedMagnitude > runSpeed + 0.1f;
    private float JumpVelocity => Mathf.Sqrt(jumpHeight * -2.0f * gravity); // v = sqrt(h * -2 * g)
    public bool IsGrounded => controller.isGrounded || Physics.CheckSphere(transform.position + Vector3.up * 0.1f, 0.2f, groundLayer, QueryTriggerInteraction.Ignore);

    // Automation / Testing overrides
    public static Vector2 InputOverride = Vector2.zero;
    public static bool UseInputOverride = false;
    public static bool SprintOverride = false;
    public static bool WalkOverride = false;
    public static bool JumpOverride = false; // consumed by the next jump check

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        animator = GetComponentInChildren<Animator>();
        FindOrbitCamera();
    }

    private void Start()
    {
        FindOrbitCamera();
    }

    public void SetOrbitCamera(ThirdPersonOrbitCamera cam)
    {
        orbitCamera = cam;
    }

    private void FindOrbitCamera()
    {
        if (orbitCamera == null && Camera.main != null)
        {
            orbitCamera = Camera.main.GetComponent<ThirdPersonOrbitCamera>();
        }
        
        if (orbitCamera != null)
        {
            orbitCamera.SetTarget(transform);
        }
    }

    private void Update()
    {
        HandleMovement();
        ApplyGravityAndJump();
        UpdateAnimator();
    }

    private void HandleMovement()
    {
        Vector2 input = ReadMoveInput();
        float inputMagnitude = input.sqrMagnitude;

        Vector3 moveDirection = Vector3.zero;

        if (inputMagnitude > 0.01f)
        {
            if (orbitCamera != null)
            {
                Vector3 camForward = orbitCamera.PlanarForward;
                Vector3 camRight = orbitCamera.PlanarRight;
                moveDirection = (camForward * input.y + camRight * input.x).normalized;
            }
            else
            {
                moveDirection = new Vector3(input.x, 0f, input.y).normalized;
            }

            float targetAngle = Mathf.Atan2(moveDirection.x, moveDirection.z) * Mathf.Rad2Deg;
            float smoothedAngle = Mathf.SmoothDampAngle(transform.eulerAngles.y, targetAngle, ref currentTurnVelocity, rotationSmoothTime);
            transform.rotation = Quaternion.Euler(0f, smoothedAngle, 0f);
        }

        float targetSpeed = 0f;
        if (inputMagnitude > 0.01f)
        {
            if (IsSprintPressed())
            {
                targetSpeed = sprintSpeed;
            }
            else if (IsWalkPressed())
            {
                targetSpeed = walkSpeed;
            }
            else
            {
                targetSpeed = runSpeed;
            }
        }

        // Accelerate/decelerate instead of snapping, and keep moving along the last direction while
        // slowing down, so the feet stay in sync with the blend tree during starts and stops.
        if (moveDirection != Vector3.zero) lastMoveDirection = moveDirection;
        float rate = targetSpeed > currentSpeedMagnitude ? acceleration : deceleration;
        currentSpeedMagnitude = Mathf.MoveTowards(currentSpeedMagnitude, targetSpeed, rate * Time.deltaTime);
        Vector3 horizontalVelocity = lastMoveDirection * currentSpeedMagnitude;
        controller.Move(horizontalVelocity * Time.deltaTime);
    }

    private void ApplyGravityAndJump()
    {
        if (takeoffTimer >= 0f)
        {
            // The takeoff animation pushes off first; leave the ground when its feet do.
            takeoffTimer -= Time.deltaTime;
            if (takeoffTimer < 0f) verticalVelocity.y = JumpVelocity;
        }

        if (IsGrounded && verticalVelocity.y <= 0f)
        {
            verticalVelocity.y = groundedStickiness;

            if (takeoffTimer < 0f && IsJumpTriggered())
            {
                takeoffTimer = jumpTakeoffDelay;
                if (animator != null)
                {
                    animator.SetTrigger(JumpHash);
                }
            }
        }
        else
        {
            verticalVelocity.y += gravity * Time.deltaTime;
            if (verticalVelocity.y < terminalVelocity)
            {
                verticalVelocity.y = terminalVelocity;
            }
        }

        controller.Move(verticalVelocity * Time.deltaTime);
    }

    private static readonly int SpeedHash = Animator.StringToHash("Speed");
    private static readonly int IsGroundedHash = Animator.StringToHash("IsGrounded");
    private static readonly int VerticalVelocityHash = Animator.StringToHash("VerticalVelocity");
    private static readonly int AirProgressHash = Animator.StringToHash("AirProgress");
    private static readonly int JumpHash = Animator.StringToHash("Jump");

    private void UpdateAnimator()
    {
        if (animator == null) return;
        animator.SetFloat(SpeedHash, currentSpeedMagnitude); // already smoothed in HandleMovement
        animator.SetBool(IsGroundedHash, IsGrounded);
        animator.SetFloat(VerticalVelocityHash, verticalVelocity.y);
        // Air pose follows the physics arc: 0 = just left the ground, 0.5 = apex, 1 = about to land.
        animator.SetFloat(AirProgressHash, Mathf.InverseLerp(JumpVelocity, -JumpVelocity, verticalVelocity.y));
    }

    private Vector2 ReadMoveInput()
    {
        if (UseInputOverride) return InputOverride.sqrMagnitude > 0.001f ? InputOverride.normalized : Vector2.zero;

        float x = 0f;
        float y = 0f;

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null)
        {
            if (Keyboard.current.wKey.isPressed || Keyboard.current.upArrowKey.isPressed) y += 1f;
            if (Keyboard.current.sKey.isPressed || Keyboard.current.downArrowKey.isPressed) y -= 1f;
            if (Keyboard.current.aKey.isPressed || Keyboard.current.leftArrowKey.isPressed) x -= 1f;
            if (Keyboard.current.dKey.isPressed || Keyboard.current.rightArrowKey.isPressed) x += 1f;
            return new Vector2(x, y).normalized;
        }
#endif

#if ENABLE_LEGACY_INPUT_MANAGER
        try
        {
            x = Input.GetAxisRaw("Horizontal");
            y = Input.GetAxisRaw("Vertical");
            return new Vector2(x, y).normalized;
        }
        catch { }
#endif
        return Vector2.zero;
    }

    private bool IsJumpTriggered()
    {
        if (UseInputOverride)
        {
            bool jump = JumpOverride;
            JumpOverride = false;
            return jump;
        }

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        try { if (Input.GetKeyDown(KeyCode.Space)) return true; } catch { }
#endif
        return false;
    }

    private bool IsSprintPressed()
    {
        if (UseInputOverride) return SprintOverride;

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.leftShiftKey.isPressed) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        try { if (Input.GetKey(KeyCode.LeftShift)) return true; } catch { }
#endif
        return false;
    }

    private bool IsWalkPressed()
    {
        if (UseInputOverride) return WalkOverride;

#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        try { if (Input.GetKey(KeyCode.LeftControl)) return true; } catch { }
#endif
        return false;
    }
}
