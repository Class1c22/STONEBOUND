using System.Collections.Generic;
using UnityEngine;

// Повісь на камінь. Коли камінь вдаряється у воксельний світ достатньо сильно,
// у місці удару видаляється куля вокселів, а замість них розлітаються уламки.
// Якщо на камені є RockAbsorber, радіус руйнування росте разом зі стадією,
// а матеріали міцніші за поточну силу каменя не ламаються (див. VoxelWorld → Міцність матеріалів).
// Налаштування уламків і падіння шматків у VoxelWorld.
[RequireComponent(typeof(Rigidbody))]
public class VoxelDestroyer : MonoBehaviour
{
    public VoxelWorld world;

    [Header("Удар")]
    [Tooltip("Мінімальна швидкість У стіну (м/с), при якій щось руйнується. Менше = ламається від легких торкань")]
    public float minImpactSpeed = 5f;
    public float baseRadius = 0.8f;
    [Tooltip("Скільки радіуса додає кожен м/с швидкості")]
    public float radiusPerSpeed = 0.06f;
    public float maxRadius = 4f;
    [Tooltip("Яку частку швидкості камінь зберігає після пробиття (1 = взагалі не гальмує)")]
    [Range(0f, 1f)] public float speedKeep = 0.85f;
    public float hitCooldown = 0.05f;

    [Header("Дебаг")]
    [Tooltip("Писати в Console, коли матеріал надто міцний для поточної стадії")]
    public bool debugLog = true;

    Rigidbody rb;
    RockAbsorber absorber;
    Vector3 lastVelocity;
    float lastHitTime, lastLogTime;
    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        absorber = GetComponent<RockAbsorber>();   // необов'язково
    }

    void OnEnable()
    {
        if (world != null) world.HitBlocked += OnHitBlocked;
    }

    void OnDisable()
    {
        if (world != null) world.HitBlocked -= OnHitBlocked;
    }

    void OnHitBlocked(Vector3 point, int blocked, int removedCount)
    {
        if (!debugLog || Time.time - lastLogTime < 0.5f) return;
        lastLogTime = Time.time;
        int power = absorber != null ? absorber.Power : int.MaxValue;
        Debug.Log($"[Rock] Надто міцно: не зламано {blocked} вокселів, зламано {removedCount} (сила каменя {power})", this);
    }

    void FixedUpdate()
    {
        // швидкість на початку фізичного кроку, тобто до зіткнення
        lastVelocity = rb.linearVelocity;
    }

    void OnCollisionEnter(Collision c) => Handle(c);
    void OnCollisionStay(Collision c) => Handle(c);

    void Handle(Collision c)
    {
        if (world == null) return;
        if (c.collider.GetComponentInParent<VoxelWorld>() != world) return;
        if (Time.time - lastHitTime < hitCooldown) return;

        ContactPoint cp = c.GetContact(0);

        // враховуємо тільки швидкість У стіну (по нормалі). Дотик збоку чи ковзання вздовж стіни
        // нічого не ламає, навіть якщо камінь їде швидко
        float impact = Mathf.Abs(Vector3.Dot(lastVelocity, cp.normal));
        if (impact < minImpactSpeed) return;
        lastHitTime = Time.time;

        // сила удару залежить від швидкості, маси і стадії
        float radiusMul = absorber != null ? absorber.DestroyRadiusMultiplier : 1f;
        int power = absorber != null ? absorber.Power : int.MaxValue;

        float radius = (baseRadius + impact * radiusPerSpeed) * Mathf.Pow(rb.mass, 1f / 3f);
        radius = Mathf.Min(radius, maxRadius) * radiusMul;

        Vector3 center = cp.point - cp.normal * (radius * 0.5f);   // трохи углиб стіни

        removed.Clear();
        int n = world.DestroySphere(center, radius, removed, power);
        if (n == 0) return;   // надто міцне для цієї стадії: камінь просто відскакує

        rb.linearVelocity = lastVelocity * speedKeep;   // камінь пробиває, а не зупиняється
        world.SpawnDebris(removed, center, lastVelocity);
    }
}