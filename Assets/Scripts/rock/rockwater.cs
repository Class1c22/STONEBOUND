using System.Collections.Generic;
using UnityEngine;

// Повісь на камінь (Rigidbody + SphereCollider). Працює з будь-якою масою й розміром каменя.
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class FloatingRock : MonoBehaviour
{
    [Header("Плавучість")]
    [Range(0.1f, 0.9f)] public float floatLevel = 0.5f;   // яка частка кулі у воді в спокої
    public float maxBuoyancyMultiplier = 3f;
    [Tooltip("Макс. швидкість підйому у воді, м/с. Не дає викинути камінь у повітря")]
    public float maxRiseSpeed = 4f;

    [Header("Опір води (сповільнення)")]
    public float waterDrag = 3f;
    public float quadraticDrag = 0.6f;
    public float waterAngularDrag = 3f;

    [Header("Хвилі від каменя")]
    public float wakeStrength = 0.35f;
    public float splashStrength = 1f;

    [Header("Хвилі на камінь")]
    public float waveDrift = 0.4f;

    [Header("Гребок")]
    public float paddle = 1.5f;

    [Header("Уламки у воді")]
    [Tooltip("У воді камінь не колайдиться з уламками (VoxelPiece), які лежать під ним, тож вони не піднімають його")]
    public bool ignoreDebrisBelow = true;
    [Tooltip("Ігнорувати у воді взагалі всі уламки. Може зламати втягування на E, якщо воно через колізії")]
    public bool ignoreAllDebris = false;
    [Tooltip("Наскільки нижче центру (частка радіуса) має бути уламок, щоб вважатись нижнім")]
    [Range(-0.5f, 1f)] public float belowThreshold = 0.2f;

    Rigidbody rb;
    SphereCollider sc;
    float prevFraction;

    readonly Collider[] buf = new Collider[256];
    readonly HashSet<int> ignored = new HashSet<int>();
    float clearTimer;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        sc = GetComponent<SphereCollider>();
    }

    void FixedUpdate()
    {
        WaterSurface water = WaterSurface.Instance;
        if (water == null) return;

        Transform t = sc.transform;
        Vector3 c = t.TransformPoint(sc.center);
        Vector3 s = t.lossyScale;
        float r = sc.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z));

        float waterY = water.GetHeight(c.x, c.z);
        float h = Mathf.Clamp(waterY - (c.y - r), 0f, 2f * r);   // глибина занурення від низу кулі
        if (h <= 0f)
        {
            prevFraction = 0f;
            return;
        }

        // частка об'єму кулі під водою (сферичний сегмент)
        float fraction = h * h * (3f * r - h) / (4f * r * r * r);
        float dt = Time.fixedDeltaTime;

        // уламки не піднімають камінь
        if (ignoreDebrisBelow || ignoreAllDebris) IgnoreDebris(c, r);

        // плавучість: сила = вага * (fraction / floatLevel), рівновага при fraction == floatLevel
        float g = Physics.gravity.magnitude;
        float mult = Mathf.Min(fraction / floatLevel, maxBuoyancyMultiplier);
        rb.AddForce(Vector3.up * (g * rb.mass * mult), ForceMode.Force);

#if UNITY_6000_0_OR_NEWER
        Vector3 vel = rb.linearVelocity;
#else
        Vector3 vel = rb.velocity;
#endif
        Vector3 ang = rb.angularVelocity;

        // обмеження підйому: у воді камінь не вилітає вгору
        if (maxRiseSpeed > 0f && vel.y > maxRiseSpeed)
        {
            vel.y = maxRiseSpeed;
#if UNITY_6000_0_OR_NEWER
            rb.linearVelocity = vel;
#else
            rb.velocity = vel;
#endif
        }

        // опір води: лінійний + квадратичний
        Vector3 drag = -vel * (waterDrag * fraction)
                       - vel * (vel.magnitude * quadraticDrag * fraction / Mathf.Max(0.1f, r));
        rb.AddForce(drag, ForceMode.Acceleration);
        rb.AddTorque(-ang * (waterAngularDrag * fraction), ForceMode.Acceleration);

        // хвилі зносять камінь вниз по схилу
        Vector2 slope = water.GetSlope(c.x, c.z);
        rb.AddForce(new Vector3(-slope.x, 0f, -slope.y) * (waveDrift * g * fraction), ForceMode.Acceleration);

        // гребок: швидкість кочення v = ω × up * r
        if (paddle > 0f)
        {
            Vector3 desired = Vector3.Cross(ang, Vector3.up) * r;
            Vector3 horizVel = new Vector3(vel.x, 0f, vel.z);
            rb.AddForce((desired - horizVel) * (paddle * fraction), ForceMode.Acceleration);
        }

        // ───── хвилі від каменя ─────
        float submerged = Mathf.Clamp01(fraction / floatLevel);

        Vector3 hv = new Vector3(vel.x, 0f, vel.z);
        float hs = hv.magnitude;
        if (hs > 0.1f)
        {
            Vector3 dir = hv / hs;
            float a = hs * dt * wakeStrength * submerged;
            float rad = r * 0.8f;
            water.AddDisturbance(c + dir * (r * 0.9f), rad, a);
            water.AddDisturbance(c - dir * (r * 0.9f), rad, -a);
        }

        float splash = vel.y * dt * Mathf.Max(fraction, prevFraction) * splashStrength;
        if (Mathf.Abs(splash) > 0.0005f)
            water.AddDisturbance(c, r * 1.1f, splash);

        prevFraction = fraction;
    }

    // Вимикає колізію каменя з уламками поруч (нижніми або всіма)
    void IgnoreDebris(Vector3 c, float r)
    {
        clearTimer -= Time.fixedDeltaTime;
        if (clearTimer <= 0f)
        {
            clearTimer = 10f;
            ignored.Clear();   // знищені уламки не копляться
        }

        int count = Physics.OverlapSphereNonAlloc(c, r * 1.6f + 0.5f, buf, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            Collider col = buf[i];
            buf[i] = null;
            if (col == null || col == sc) continue;

            int id = col.GetInstanceID();
            if (ignored.Contains(id)) continue;
            if (!IsPiece(col)) continue;

            if (!ignoreAllDebris)
            {
                // нижній уламок: центр нижче центру каменя
                if (col.bounds.center.y > c.y - r * belowThreshold) continue;
            }

            Physics.IgnoreCollision(sc, col, true);
            ignored.Add(id);
        }
    }

    static bool IsPiece(Collider col)
    {
        if (col.TryGetComponent<VoxelPiece>(out _)) return true;
        Rigidbody body = col.attachedRigidbody;
        return body != null && body.TryGetComponent<VoxelPiece>(out _);
    }
}