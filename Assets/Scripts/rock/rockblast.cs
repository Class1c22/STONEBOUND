using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

// Повісь на камінь (разом з RockAbsorber). Кнопка R: камінь віддає половину того, що з'їв.
// Він меншає, а відповідна частка шматків з поверхні розлітається в усі боки
// і ламає воксельний світ (кожен шматок при ударі вибиває кулю вокселів, як і сам камінь).
[RequireComponent(typeof(Rigidbody), typeof(RockAbsorber))]
public class RockBlast : MonoBehaviour
{
    [Header("Викид (кнопка R)")]
    [Tooltip("Яку частку набраного віддавати (0.5 = половину)")]
    [Range(0.05f, 1f)] public float shedFraction = 0.5f;
    public float cooldown = 0.4f;
    public float minSpeed = 12f;
    public float maxSpeed = 26f;
    [Tooltip("Розкид напрямку від радіального (0 = строго від центру каменя)")]
    [Range(0f, 1f)] public float scatter = 0.35f;
    [Tooltip("Додатковий підкид угору, щоб шматки розліталися й дугою")]
    [Range(0f, 1f)] public float upBias = 0.25f;
    [Tooltip("Яку частку швидкості каменя успадковують шматки")]
    [Range(0f, 1f)] public float inheritVelocity = 0.5f;
    public float pieceMass = 1.5f;
    public float pieceLifetime = 12f;
    [Tooltip("Скільки секунд шматок не можна знову всмоктати (щоб не влетів назад одразу)")]
    public float pickupDelay = 1.5f;

    [Header("Руйнування шматками")]
    [Tooltip("Якщо порожньо, береться з VoxelDestroyer на цьому ж камені")]
    public VoxelWorld world;
    [Tooltip("Мінімальна швидкість У стіну, при якій шматок щось ламає")]
    public float minImpactSpeed = 4f;
    public float baseRadius = 0.6f;
    [Tooltip("Скільки радіуса додає кожен м/с швидкості")]
    public float radiusPerSpeed = 0.04f;
    public float maxRadius = 2.5f;
    [Tooltip("Скільки разів шматок може пробити стіну, перш ніж зникне")]
    public int maxHits = 3;
    [Range(0f, 1f)] public float speedKeep = 0.8f;

    Rigidbody rb;
    RockAbsorber absorber;
    Collider rockCollider;
    float nextTime;
    readonly List<Transform> released = new List<Transform>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        absorber = GetComponent<RockAbsorber>();
        rockCollider = GetComponent<Collider>();

        if (world == null)
        {
            var destroyer = GetComponent<VoxelDestroyer>();
            if (destroyer != null) world = destroyer.world;
        }
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.rKey.wasPressedThisFrame && Time.time >= nextTime)
            Blast();
    }

    void Blast()
    {
        released.Clear();
        Vector3 center = absorber.WorldCenter;

        if (!absorber.ShedFraction(shedFraction, released)) return;   // нічого віддавати
        nextTime = Time.time + cooldown;

        foreach (Transform t in released)
            if (t != null) Launch(t, center);
        released.Clear();
    }

    void Launch(Transform t, Vector3 center)
    {
        t.SetParent(null, true);

        // напрямок: від центру каменя + випадковий розкид + трохи вгору
        Vector3 dir = t.position - center;
        dir = dir.sqrMagnitude > 1e-6f ? dir.normalized : Random.onUnitSphere;
        dir = (dir + Random.onUnitSphere * scatter + Vector3.up * upBias).normalized;

        // колайдер по межах моделі шматка
        var box = t.gameObject.AddComponent<BoxCollider>();
        var mf = t.GetComponent<MeshFilter>();
        if (mf != null && mf.sharedMesh != null)
        {
            box.center = mf.sharedMesh.bounds.center;
            box.size = Vector3.Max(mf.sharedMesh.bounds.size, Vector3.one * 0.05f);
        }
        if (rockCollider != null) Physics.IgnoreCollision(box, rockCollider, true);

        var body = t.gameObject.AddComponent<Rigidbody>();
        body.mass = pieceMass;   // >= 1, тож шматки ламають і падаючі шматки конструкцій
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.linearVelocity = dir * Random.Range(minSpeed, maxSpeed) + rb.linearVelocity * inheritVelocity;
        body.angularVelocity = Random.insideUnitSphere * 8f;

        var p = t.gameObject.AddComponent<ShedProjectile>();
        p.world = world;
        p.minImpactSpeed = minImpactSpeed;
        p.baseRadius = baseRadius;
        p.radiusPerSpeed = radiusPerSpeed;
        p.maxRadius = maxRadius;
        p.hitsLeft = maxHits;
        p.power = absorber.Power;                                   // шматки ламають не більше, ніж сам камінь
        p.radiusMultiplier = absorber.DestroyRadiusMultiplier;
        p.speedKeep = speedKeep;
        p.lifetime = pieceLifetime;
        p.pickupDelay = pickupDelay;
    }
}

// Додається до кожного викинутого шматка автоматично.
public class ShedProjectile : MonoBehaviour
{
    [HideInInspector] public VoxelWorld world;
    [HideInInspector] public float minImpactSpeed, baseRadius, radiusPerSpeed, maxRadius, speedKeep;
    [HideInInspector] public int hitsLeft;
    [HideInInspector] public int power = int.MaxValue;
    [HideInInspector] public float radiusMultiplier = 1f;
    [HideInInspector] public float lifetime, pickupDelay;

    Rigidbody rb;
    Absorbable absorbable;
    Vector3 lastVelocity;
    float spawnTime, lastHitTime;
    bool pickable;
    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        absorbable = GetComponent<Absorbable>();
        spawnTime = Time.time;
        lastVelocity = rb != null ? rb.linearVelocity : Vector3.zero;

        // поки летить, камінь не може його знову всмоктати
        if (absorbable != null) absorbable.attached = true;
    }

    void FixedUpdate()
    {
        if (rb != null) lastVelocity = rb.linearVelocity;   // швидкість до зіткнення
    }

    void Update()
    {
        if (!pickable && Time.time - spawnTime >= pickupDelay)
        {
            pickable = true;
            if (absorbable != null) absorbable.attached = false;
        }

        // камінь знову всмоктав шматок: він став дитиною каменя, більше не снаряд
        if (pickable && transform.parent != null)
        {
            Destroy(this);
            return;
        }

        if (Time.time - spawnTime > lifetime) Destroy(gameObject);
    }

    void OnCollisionEnter(Collision c)
    {
        if (world == null || hitsLeft <= 0 || rb == null) return;
        if (Time.time - lastHitTime < 0.05f) return;
        if (c.collider.GetComponentInParent<VoxelWorld>() != world) return;

        ContactPoint cp = c.GetContact(0);

        // тільки швидкість У стіну: ковзання вздовж стіни нічого не ламає
        float impact = Mathf.Abs(Vector3.Dot(lastVelocity, cp.normal));
        if (impact < minImpactSpeed) return;

        float radius = Mathf.Min(maxRadius, baseRadius + impact * radiusPerSpeed) * radiusMultiplier;
        Vector3 center = cp.point - cp.normal * (radius * 0.5f);   // трохи углиб стіни

        removed.Clear();
        int n = world.DestroySphere(center, radius, removed, power);
        if (n == 0) return;

        lastHitTime = Time.time;
        world.SpawnDebris(removed, center, lastVelocity);
        rb.linearVelocity = lastVelocity * speedKeep;   // пробиває, а не зупиняється

        hitsLeft--;
        if (hitsLeft <= 0) Destroy(gameObject);
    }
}