using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
[RequireComponent(typeof(CharacterController))]
public sealed class AnimeCharacterController : MonoBehaviour
{
    [Header("Locomotion Speeds (m/s)")]
    [SerializeField] private float walkSpeed = 2.2f;
    [SerializeField] private float runSpeed = 5.0f;
    [SerializeField] private float sprintSpeed = 10.0f;

    [Header("Rotation Tuning")]
    [SerializeField] private float rotationSmoothTime = 0.08f;
    private float currentTurnVelocity;

    [Header("Anime Physics & Jump")]
    [SerializeField] private float gravity = -25.0f;
    [SerializeField] private float jumpHeight = 1.8f;
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

    public float CurrentSpeed => currentSpeedMagnitude;
    public bool IsGrounded => controller.isGrounded || Physics.CheckSphere(transform.position + Vector3.up * 0.1f, 0.2f, groundLayer, QueryTriggerInteraction.Ignore);

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

        currentSpeedMagnitude = targetSpeed;
        Vector3 horizontalVelocity = moveDirection * targetSpeed;
        controller.Move(horizontalVelocity * Time.deltaTime);
    }

    private void ApplyGravityAndJump()
    {
        if (IsGrounded)
        {
            if (verticalVelocity.y < 0f)
            {
                verticalVelocity.y = groundedStickiness;
            }

            if (IsJumpTriggered())
            {
                // v = sqrt(h * -2 * g)
                verticalVelocity.y = Mathf.Sqrt(jumpHeight * -2.0f * gravity);
                if (animator != null)
                {
                    animator.SetTrigger("Jump");
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

    private void UpdateAnimator()
    {
        if (animator == null) return;
        animator.SetFloat(SpeedHash, currentSpeedMagnitude, 0.15f, Time.deltaTime);
        animator.SetBool(IsGroundedHash, IsGrounded);
        animator.SetFloat(VerticalVelocityHash, verticalVelocity.y);
    }

    private Vector2 ReadMoveInput()
    {
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
#if ENABLE_INPUT_SYSTEM
        if (Keyboard.current != null && Keyboard.current.leftCtrlKey.isPressed) return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        try { if (Input.GetKey(KeyCode.LeftControl)) return true; } catch { }
#endif
        return false;
    }
}
