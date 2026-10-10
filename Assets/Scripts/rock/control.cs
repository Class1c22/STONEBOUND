using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody))]
[RequireComponent(typeof(SphereCollider))]
public class RockController : MonoBehaviour
{
    [Header("Рух")]
    public Transform cameraTransform;
    public float moveAcceleration = 30f;
    public float maxSpeed = 20f;

    [Header("Шифт (прискорення)")]
    public float sprintAccelerationMultiplier = 2f;
    public float sprintMaxSpeedMultiplier = 1.7f;

    [Header("Стрибок")]
    public float jumpSpeed = 7f;
    public float groundCheckExtra = 0.15f;

    Rigidbody rb;
    SphereCollider col;
    bool jumpRequested;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<SphereCollider>();

        // щоб камінь і камера не тремтіли
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.spaceKey.wasPressedThisFrame)
            jumpRequested = true;
    }

    void FixedUpdate()
    {
        var kb = Keyboard.current;
        if (kb == null || cameraTransform == null) return;

        float x = (kb.dKey.isPressed ? 1f : 0f) - (kb.aKey.isPressed ? 1f : 0f);
        float z = (kb.wKey.isPressed ? 1f : 0f) - (kb.sKey.isPressed ? 1f : 0f);
        bool sprint = kb.leftShiftKey.isPressed;

        // напрямок руху відносно камери
        Vector3 forward = cameraTransform.forward;
        forward.y = 0f;
        forward.Normalize();
        Vector3 right = cameraTransform.right;
        right.y = 0f;
        right.Normalize();

        Vector3 dir = forward * z + right * x;
        if (dir.sqrMagnitude > 1f) dir.Normalize();

        float accel = moveAcceleration * (sprint ? sprintAccelerationMultiplier : 1f);
        float limit = maxSpeed * (sprint ? sprintMaxSpeedMultiplier : 1f);

        // розгін, поки не досягли ліміту горизонтальної швидкості
        Vector3 horizontalVelocity = rb.linearVelocity;
        horizontalVelocity.y = 0f;
        if (horizontalVelocity.magnitude < limit)
            rb.AddForce(dir * accel, ForceMode.Acceleration);

        if (jumpRequested && IsGrounded())
            rb.AddForce(Vector3.up * jumpSpeed, ForceMode.VelocityChange);

        jumpRequested = false;
    }

    bool IsGrounded()
    {
        Vector3 s = transform.lossyScale;
        float radius = col.radius * Mathf.Max(s.x, s.y, s.z);
        return Physics.Raycast(col.bounds.center, Vector3.down, radius + groundCheckExtra);
    }
}