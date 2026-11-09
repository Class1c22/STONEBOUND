using System.Collections.Generic;
using UnityEngine;

// Повісь на камінь. Коли камінь вдаряється у воксельний світ достатньо сильно,
// у місці удару видаляється куля вокселів, а замість них розлітаються уламки.
// За замовчуванням (Strict Rock Radius) куля руйнування дорівнює самому каменю:
// центр у центрі каменя, радіус = поточний радіус каменя. Ламається рівно те, що камінь займає.
// Налаштування уламків і падіння шматків у VoxelWorld.
[RequireComponent(typeof(Rigidbody))]
public class VoxelDestroyer : MonoBehaviour
{
    public VoxelWorld world;

    [Header("Удар")]
    [Tooltip("Мінімальна швидкість У стіну (м/с), при якій щось руйнується. Менше = ламається від легких торкань")]
    public float minImpactSpeed = 5f;
    [Tooltip("Яку частку швидкості камінь зберігає після пробиття (1 = взагалі не гальмує)")]
    [Range(0f, 1f)] public float speedKeep = 0.85f;
    public float hitCooldown = 0.05f;

    [Header("Радіус руйнування")]
    [Tooltip("Увімкнено: руйнується рівно куля розміру каменя (центр = центр каменя, радіус = радіус каменя). " +
             "Швидкість, маса і множник стадії на радіус не впливають")]
    public bool strictRockRadius = true;
    [Tooltip("Невеликий запас до радіуса каменя (1 = рівно по радіусу). Потрібен, щоб вокселі, на які камінь трохи наліз, теж прибиралися. " +
             "Більше 1.1 не став, інакше ламатиметься більше за камінь")]
    [Range(1f, 1.25f)] public float radiusScale = 1.03f;

    [Tooltip("Запас у вокселях світу додатково до радіуса каменя. Потрібен, бо камінь стоїть на поверхні вокселів і не заходить у них: " +
             "без запасу куля не дістає до центрів вокселів. 1 = рівно один шар. Якщо не ламається, збільш; якщо ламає зайве, зменш до 0.6")]
    [Range(0f, 3f)] public float voxelPadding = 1f;

    [Header("Старий режим (лише коли Strict Rock Radius вимкнено)")]
    public float baseRadius = 0.8f;
    [Tooltip("Скільки радіуса додає кожен м/с швидкості")]
    public float radiusPerSpeed = 0.06f;
    public float maxRadius = 4f;

    Rigidbody rb;
    RockAbsorber absorber;
    SphereCollider sphere;
    Vector3 lastVelocity;
    float lastHitTime;
    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        absorber = GetComponent<RockAbsorber>();   // необов'язково
        sphere = GetComponent<SphereCollider>();   // запасний варіант, якщо немає RockAbsorber
    }

    void FixedUpdate()
    {
        // швидкість на початку фізичного кроку, тобто до зіткнення
        lastVelocity = rb.linearVelocity;
    }

    void OnCollisionEnter(Collision c) => Handle(c);
    void OnCollisionStay(Collision c) => Handle(c);

    // Справжній радіус каменя у світових метрах
    float RockRadius()
    {
        if (absorber != null) return absorber.Radius;
        if (sphere != null)
        {
            Vector3 s = sphere.transform.lossyScale;
            return sphere.radius * Mathf.Max(s.x, s.y, s.z);
        }
        return baseRadius;
    }

    Vector3 RockCenter()
    {
        if (absorber != null) return absorber.WorldCenter;
        if (sphere != null) return sphere.transform.TransformPoint(sphere.center);
        return rb.worldCenterOfMass;
    }

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

        float radius;
        Vector3 center;
        if (strictRockRadius)
        {
            // Фізика не дає каменю влізти у воксель: центри найближчих вокселів лежать на відстані
            // радіус + ~пів вокселя від центра каменя. Без запасу в 1 воксель куля їх не зачіпає.
            radius = RockRadius() * radiusScale + world.voxelSize * voxelPadding;
            center = RockCenter();
        }
        else
        {
            float radiusMul = absorber != null ? absorber.DestroyRadiusMultiplier : 1f;
            radius = (baseRadius + impact * radiusPerSpeed) * Mathf.Pow(rb.mass, 1f / 3f);
            radius = Mathf.Min(radius, maxRadius) * radiusMul;
            center = cp.point - cp.normal * (radius * 0.5f);   // трохи углиб стіни
        }

        removed.Clear();
        int n = world.DestroySphere(center, radius, removed);
        if (n == 0) return;   // нічого не зруйновано: камінь просто відскакує

        rb.linearVelocity = lastVelocity * speedKeep;   // камінь пробиває, а не зупиняється
        world.SpawnDebris(removed, center, lastVelocity);
    }
}