using UnityEngine;

// Повісь на камінь (Rigidbody + SphereCollider). Працює з будь-якою масою й розміром каменя.
[RequireComponent(typeof(Rigidbody), typeof(SphereCollider))]
public class FloatingRock : MonoBehaviour
{
    [Header("Плавучість")]
    [Range(0.1f, 0.9f)] public float floatLevel = 0.5f;   // яка частка кулі у воді в спокої
    public float maxBuoyancyMultiplier = 3f;

    [Header("Опір води (сповільнення)")]
    [Tooltip("Лінійний опір: гальмує завжди")]
    public float waterDrag = 3f;
    [Tooltip("Квадратичний опір: чим швидше камінь, тим сильніше гальмує")]
    public float quadraticDrag = 0.6f;
    [Tooltip("Гасіння обертання: у воді камінь важче розкрутити")]
    public float waterAngularDrag = 3f;

    [Header("Хвилі від каменя")]
    [Tooltip("Наскільки сильно рух вперед піднімає хвилю спереду й опускає воду ззаду")]
    public float wakeStrength = 0.35f;
    [Tooltip("Наскільки вертикальний рух каменя (падіння, хитання) б'є по воді")]
    public float splashStrength = 1f;

    [Header("Хвилі на камінь")]
    public float waveDrift = 0.4f;

    [Header("Гребок")]
    [Tooltip("У воді немає тертя об землю, тож цей параметр перетворює обертання на рух. 0 = вимкнути")]
    public float paddle = 1.5f;

    Rigidbody rb;
    SphereCollider sc;
    float prevFraction;

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

        // опір води: лінійний + квадратичний (більший камінь гальмує слабше відносно своєї маси)
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

        // слід: спереду вода піднімається, ззаду опускається (хвилі розходяться за напрямком руху)
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

        // бризки: падіння й хитання б'ють по воді вертикально
        float splash = vel.y * dt * Mathf.Max(fraction, prevFraction) * splashStrength;
        if (Mathf.Abs(splash) > 0.0005f)
            water.AddDisturbance(c, r * 1.1f, splash);

        prevFraction = fraction;
    }
}