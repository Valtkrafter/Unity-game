using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public sealed class ThirdPersonOrbitCamera : MonoBehaviour
{
    [Header("Target Tracking")]
    [SerializeField] private Transform target;
    [SerializeField] private Vector3 targetOffset = new Vector3(0f, 1.4f, 0f);

    [Header("Orbit Distance")]
    [SerializeField] private float defaultDistance = 3.5f;
    [SerializeField] private float minDistance = 1.0f;
    [SerializeField] private float maxDistance = 6.0f;
    [SerializeField] private float distanceSmoothTime = 0.1f;

    [Header("Sensitivity & Limits")]
    [SerializeField] private float mouseSensitivityX = 180f;
    [SerializeField] private float mouseSensitivityY = 120f;
    [SerializeField] private float minPitch = -25f;
    [SerializeField] private float maxPitch = 65f;
    [SerializeField] private float rotationSmoothTime = 0.05f;

    [Header("Collision Avoidance")]
    [SerializeField] private LayerMask collisionLayers;
    [SerializeField] private float collisionRadius = 0.2f;

    private float currentYaw;
    private float currentPitch;
    private float targetYaw;
    private float targetPitch;
    private float currentDistance;
    private float targetDistance;

    private float yawVelocity;
    private float pitchVelocity;
    private float distanceVelocity;

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
        Vector3 angles = transform.eulerAngles;
        targetYaw = currentYaw = angles.y;
        targetPitch = currentPitch = angles.x;
        targetDistance = currentDistance = defaultDistance;
    }

    private void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    private void LateUpdate()
    {
        if (target == null) return;

        float mouseX = 0f;
        float mouseY = 0f;

#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
        {
            Vector2 delta = Mouse.current.delta.ReadValue();
            mouseX = delta.x * 0.15f;
            mouseY = delta.y * 0.15f;
        }
        else
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        {
            try
            {
                mouseX = Input.GetAxisRaw("Mouse X");
                mouseY = Input.GetAxisRaw("Mouse Y");
            }
            catch { }
        }
#else
        {}
#endif

        targetYaw += mouseX * mouseSensitivityX * Time.deltaTime;
        targetPitch -= mouseY * mouseSensitivityY * Time.deltaTime;
        targetPitch = Mathf.Clamp(targetPitch, minPitch, maxPitch);

        currentYaw = Mathf.SmoothDampAngle(currentYaw, targetYaw, ref yawVelocity, rotationSmoothTime);
        currentPitch = Mathf.SmoothDampAngle(currentPitch, targetPitch, ref pitchVelocity, rotationSmoothTime);

        Quaternion rotation = Quaternion.Euler(currentPitch, currentYaw, 0f);
        Vector3 focusPoint = target.position + targetOffset;

        targetDistance = defaultDistance;
        Vector3 desiredCameraPos = focusPoint - (rotation * Vector3.forward * targetDistance);
        Vector3 rayDirection = desiredCameraPos - focusPoint;
        float rayDistance = rayDirection.magnitude;

        if (Physics.SphereCast(focusPoint, collisionRadius, rayDirection.normalized, out RaycastHit hit, rayDistance, collisionLayers))
        {
            targetDistance = Mathf.Clamp(hit.distance - collisionRadius, minDistance, maxDistance);
        }

        currentDistance = Mathf.SmoothDamp(currentDistance, targetDistance, ref distanceVelocity, distanceSmoothTime);
        Vector3 finalPosition = focusPoint - (rotation * Vector3.forward * currentDistance);

        transform.position = finalPosition;
        transform.rotation = rotation;
    }
}
