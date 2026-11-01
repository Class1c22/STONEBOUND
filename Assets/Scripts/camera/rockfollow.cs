using UnityEngine;
using UnityEngine.InputSystem;

public class FollowCamera : MonoBehaviour
{
    public Transform target;
    public float distance = 8f;
    public float sensitivity = 0.15f;
    public float minPitch = -10f;
    public float maxPitch = 70f;
    [Tooltip("Камера ніколи не опускається нижче цієї висоти (світова координата Y)")]
    public float minCameraHeight = 0.3f;

    [Header("Тряска камери (тільки на великій швидкості)")]
    public bool shakeEnabled = true;
    [Tooltip("Швидкість, з якої починається тряска")]
    public float shakeStartSpeed = 20f;
    [Tooltip("Швидкість, при якій тряска максимальна")]
    public float shakeMaxSpeed = 45f;
    [Tooltip("Максимальне зміщення камери при тряски (метри)")]
    public float maxShakeOffset = 0.25f;
    [Tooltip("Максимальний нахил камери при тряски (градуси)")]
    public float maxShakeRoll = 2.5f;
    public float shakeFrequency = 25f;
    [Tooltip("Як швидко тряска наростає й затухає")]
    public float shakeSmoothing = 8f;

    float yaw;
    float pitch = 25f;
    SphereCollider targetCollider;

    Vector3 prevFocus;
    bool hasPrevFocus;
    float smoothedSpeed;
    float shakeIntensity;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (target != null)
            targetCollider = target.GetComponent<SphereCollider>();
    }

    // Перепризначити ціль у рантаймі (наприклад, камінь, що перейшов у нову сцену)
    public void SetTarget(Transform t)
    {
        target = t;
        targetCollider = t != null ? t.GetComponent<SphereCollider>() : null;
        hasPrevFocus = false;
        smoothedSpeed = 0f;
        shakeIntensity = 0f;
    }

    // справжній центр кулі (інтерпольований), а не pivot моделі
    Vector3 GetFocusPoint()
    {
        return targetCollider != null
            ? target.TransformPoint(targetCollider.center)
            : target.position;
    }

    void UpdateShake(Vector3 focus)
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        float speed = 0f;
        if (hasPrevFocus)
            speed = (focus - prevFocus).magnitude / dt;
        prevFocus = focus;
        hasPrevFocus = true;

        smoothedSpeed = Mathf.Lerp(smoothedSpeed, speed, 1f - Mathf.Exp(-15f * dt));

        float targetIntensity = shakeEnabled
            ? Mathf.InverseLerp(shakeStartSpeed, shakeMaxSpeed, smoothedSpeed)
            : 0f;

        shakeIntensity = Mathf.Lerp(shakeIntensity, targetIntensity, 1f - Mathf.Exp(-shakeSmoothing * dt));
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        var mouse = Mouse.current;
        if (mouse != null && Cursor.lockState != CursorLockMode.Locked && mouse.leftButton.wasPressedThisFrame)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        if (mouse != null && Cursor.lockState == CursorLockMode.Locked)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * sensitivity;
            pitch -= delta.y * sensitivity;
            pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
        }
    }

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 focus = GetFocusPoint();
        UpdateShake(focus);

        // нахил, нижче якого камера опустилась би під землю; камінь при цьому лишається по центру
        float groundPitch = Mathf.Asin(Mathf.Clamp((minCameraHeight - focus.y) / distance, -1f, 1f)) * Mathf.Rad2Deg;
        float usedPitch = Mathf.Max(pitch, groundPitch);
        Quaternion rot = Quaternion.Euler(usedPitch, yaw, 0f);

        Vector3 position = focus - rot * Vector3.forward * distance;

        if (shakeIntensity > 0.001f)
        {
            float t = Time.time * shakeFrequency;
            float nx = Mathf.PerlinNoise(t, 0.1f) * 2f - 1f;
            float ny = Mathf.PerlinNoise(0.3f, t) * 2f - 1f;
            float nr = Mathf.PerlinNoise(t, 7.7f) * 2f - 1f;

            Vector3 localOffset = new Vector3(nx, ny, 0f) * (maxShakeOffset * shakeIntensity);
            position += rot * localOffset;
            position.y = Mathf.Max(position.y, minCameraHeight);

            rot *= Quaternion.Euler(0f, 0f, nr * maxShakeRoll * shakeIntensity);
        }

        // камера завжди дивиться точно в центр каменя
        transform.rotation = rot;
        transform.position = position;
    }
}