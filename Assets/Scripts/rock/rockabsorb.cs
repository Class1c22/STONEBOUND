using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Повісь на камінь. Тримай E: камінь притягує до себе уламки й шматки, що менші за нього.
// Всмоктані фігури прилипають до поверхні як новий шар, а камінь (і його колайдер) росте.
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class RockAbsorber : MonoBehaviour
{
    [Header("Всмоктування (кнопка E)")]
    [Tooltip("Дальність притягання (для початкового розміру каменя)")]
    public float suckRadius = 6f;
    public float pullSpeed = 14f;
    public float pullAcceleration = 60f;
    [Tooltip("Найбільша фігура відносно об'єму каменя (0.35 = до 35%)")]
    public float maxSizeRatio = 0.35f;

    [Header("Ріст")]
    [Tooltip("Візуальна модель каменя (дочірній об'єкт). Росте разом із колайдером")]
    public Transform coreVisual;
    [Tooltip("Множник приросту об'єму від поглинутих фігур (більше = камінь росте швидше)")]
    public float volumeGain = 1.5f;
    [Tooltip("Швидкість плавного росту, метрів за секунду")]
    public float growSpeed = 0.6f;
    [Tooltip("Скільки фігур одночасно лишається видимими на поверхні (старі зникають, об'єм лишається)")]
    public int maxAttached = 300;

    [Header("Камера (необов'язково)")]
    public FollowCamera followCamera;
    [Range(0f, 1f)] public float cameraFollowsGrowth = 0.8f;

    public float Radius => currentRadius;
    public float Volume => volume;

    Rigidbody rb;
    SphereCollider col;
    float startRadius, currentRadius, targetRadius;
    float startMass, volume;
    Vector3 coreBaseScale, coreBaseLocalPos;
    float baseCameraDistance;

    readonly Collider[] buffer = new Collider[256];
    readonly List<Transform> attached = new List<Transform>();

    Vector3 Center => col.transform.TransformPoint(col.center);

    float MaxScale()
    {
        Vector3 s = col.transform.lossyScale;
        return Mathf.Max(s.x, s.y, s.z);
    }

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        col = GetComponent<SphereCollider>();

        startRadius = col.radius * MaxScale();
        currentRadius = targetRadius = startRadius;
        startMass = rb.mass;
        volume = 4f / 3f * Mathf.PI * startRadius * startRadius * startRadius;

        if (coreVisual != null)
        {
            coreBaseScale = coreVisual.localScale;
            coreBaseLocalPos = coreVisual.localPosition;
        }
        if (followCamera != null) baseCameraDistance = followCamera.distance;
    }

    void Update()
    {
        // плавний ріст колайдера
        if (!Mathf.Approximately(currentRadius, targetRadius))
        {
            currentRadius = Mathf.MoveTowards(currentRadius, targetRadius, growSpeed * Time.deltaTime);
            ApplySize();
        }
    }

    void FixedUpdate()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.eKey.isPressed) Suck();
    }

    void ApplySize()
    {
        float ratio = currentRadius / startRadius;

        col.radius = currentRadius / MaxScale();
        rb.mass = startMass * ratio * ratio * ratio;   // маса росте разом з об'ємом

        if (coreVisual != null)
        {
            coreVisual.localScale = coreBaseScale * ratio;
            // тримаємо центр моделі в центрі колайдера, навіть якщо pivot моделі зміщений
            coreVisual.localPosition = col.center - ratio * (col.center - coreBaseLocalPos);
        }

        if (followCamera != null)
            followCamera.distance = baseCameraDistance * Mathf.Lerp(1f, ratio, cameraFollowsGrowth);
    }

    void Suck()
    {
        Vector3 center = Center;
        float range = suckRadius * (currentRadius / startRadius);
        int n = Physics.OverlapSphereNonAlloc(center, range, buffer, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < n; i++)
        {
            Collider c = buffer[i];
            var a = c.GetComponentInParent<Absorbable>();
            if (a == null || a.attached) continue;
            if (a.volume > volume * maxSizeRatio) continue;   // надто велика фігура поки що

            Rigidbody b = a.GetComponent<Rigidbody>();
            if (b == null) continue;

            Vector3 to = center - b.worldCenterOfMass;
            float dist = to.magnitude;
            float reach = currentRadius + c.bounds.extents.magnitude * 0.6f;

            if (dist <= reach)
            {
                Attach(a, b, center);
                continue;
            }

            Vector3 wanted = to / dist * pullSpeed;
            b.linearVelocity = Vector3.MoveTowards(b.linearVelocity, wanted, pullAcceleration * Time.fixedDeltaTime);
        }
    }

    void Attach(Absorbable a, Rigidbody b, Vector3 center)
    {
        a.attached = true;
        Transform t = a.transform;

        // фігура стає на поточну поверхню каменя
        Vector3 dir = t.position - center;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Random.onUnitSphere;
        t.position = center + dir * currentRadius;
        t.SetParent(transform, true);

        var piece = a.GetComponent<VoxelPiece>();
        if (piece != null) piece.OnAbsorbed();

        foreach (var c in a.GetComponents<Collider>()) Destroy(c);
        Destroy(b);

        attached.Add(t);
        while (attached.Count > maxAttached)
        {
            if (attached[0] != null) Destroy(attached[0].gameObject);
            attached.RemoveAt(0);
        }

        // камінь росте
        volume += a.volume * volumeGain;
        targetRadius = Mathf.Pow(volume * 3f / (4f * Mathf.PI), 1f / 3f);
    }
}