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

    float yaw;
    float pitch = 25f;
    SphereCollider targetCollider;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;

        if (target != null)
            targetCollider = target.GetComponent<SphereCollider>();
    }

    // справжній центр кулі (інтерпольований), а не pivot моделі
    Vector3 GetFocusPoint()
    {
        return targetCollider != null
            ? target.TransformPoint(targetCollider.center)
            : target.position;
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

        // нахил, нижче якого камера опустилась би під землю; камінь при цьому лишається по центру
        float groundPitch = Mathf.Asin(Mathf.Clamp((minCameraHeight - focus.y) / distance, -1f, 1f)) * Mathf.Rad2Deg;
        float usedPitch = Mathf.Max(pitch, groundPitch);
        Quaternion rot = Quaternion.Euler(usedPitch, yaw, 0f);

        // камера завжди дивиться точно в центр каменя
        transform.rotation = rot;
        transform.position = focus - rot * Vector3.forward * distance;
    }
}