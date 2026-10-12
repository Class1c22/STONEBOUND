using System.Collections.Generic;
using UnityEngine;

// Повісь на камінь. Коли камінь вдаряється у воксельний світ достатньо сильно,
// у місці удару видаляється куля вокселів, а замість них розлітаються уламки.
// Налаштування уламків і падіння шматків тепер у VoxelWorld.
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

    Rigidbody rb;
    Vector3 lastVelocity;
    float lastHitTime;
    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
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

        // сила удару залежить від швидкості й маси
        float radius = (baseRadius + impact * radiusPerSpeed) * Mathf.Pow(rb.mass, 1f / 3f);
        radius = Mathf.Min(radius, maxRadius);

        Vector3 center = cp.point - cp.normal * (radius * 0.5f);   // трохи углиб стіни

        removed.Clear();
        int n = world.DestroySphere(center, radius, removed);
        if (n == 0) return;

        rb.linearVelocity = lastVelocity * speedKeep;   // камінь пробиває, а не зупиняється
        world.SpawnDebris(removed, center, lastVelocity);
    }
}