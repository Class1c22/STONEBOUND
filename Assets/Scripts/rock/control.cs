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

    [Header("Керованість на швидкості")]
    [Tooltip("Наскільки різко (рад/с) камінь повертає напрямок руху на землі")]
    public float turnRate = 2.5f;
    [Tooltip("Гальмування, коли швидкість вища за ліміт")]
    public float overspeedDrag = 1.5f;

    [Header("Стрибок")]
    public float jumpSpeed = 7f;
    public float groundCheckExtra = 0.15f;

    Rigidbody rb;
    SphereCollider col;
    RockAbsorber absorber;   // необов'язково: стадія збільшує швидкість і силу
    bool jumpRequested;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<SphereCollider>();
        absorber = GetComponent<RockAbsorber>();

        // щоб камінь і камера не тремтіли
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // без цього на великій швидкості камінь перестає крутитися і ковзає
        rb.maxAngularVelocity = 100f;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

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

        float forceMul = absorber != null ? absorber.ForceMultiplier : 1f;
        float speedMul = absorber != null ? absorber.SpeedMultiplier : 1f;

        float accel = moveAcceleration * forceMul * (sprint ? sprintAccelerationMultiplier : 1f);
        float limit = maxSpeed * speedMul * (sprint ? sprintMaxSpeedMultiplier : 1f);

        Vector3 horizontalVelocity = rb.linearVelocity;
        horizontalVelocity.y = 0f;
        float hSpeed = horizontalVelocity.magnitude;

        Vector3 force = dir * accel;

        if (hSpeed >= limit)
        {
            // над лімітом: далі не розганяємось, але керувати можна
            Vector3 vDir = horizontalVelocity / hSpeed;
            float along = Vector3.Dot(force, vDir);
            if (along > 0f) force -= vDir * along;

            rb.AddForce(-vDir * (hSpeed - limit) * overspeedDrag, ForceMode.Acceleration);
        }

        rb.AddForce(force, ForceMode.Acceleration);

        // на землі плавно повертаємо напрямок руху до напрямку вводу (швидкість зберігається)
        if (dir.sqrMagnitude > 0.01f && hSpeed > 1f && IsGrounded())
        {
            Vector3 target = dir.normalized * hSpeed;
            Vector3 turned = Vector3.RotateTowards(horizontalVelocity, target, turnRate * Time.fixedDeltaTime, 0f);
            rb.linearVelocity = new Vector3(turned.x, rb.linearVelocity.y, turned.z);
        }

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