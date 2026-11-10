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
    [Tooltip("Мінімальна швидкість У стіну (м/с), при якій щось руйнується (для каменя стартового розміру). Менше = ламається від легких торкань")]
    public float minImpactSpeed = 3f;
    [Tooltip("Чим більший камінь, тим легше він ламає: ефективна швидкість удару = швидкість × (розмір каменя)^цей степінь. " +
             "0 = розмір не враховується, 0.5 = стандарт, 1 = дуже крихко для великих")]
    [Range(0f, 1.5f)] public float sizeBreakExponent = 0.6f;
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

    [Header("Таран (пробиття наскрізь)")]
    [Tooltip("Сильний удар пробиває тунель уздовж руху каменя, а не лише ямку навколо нього")]
    public bool ramThrough = true;
    [Tooltip("Довжина тунелю в радіусах каменя на кожну одиницю (швидкість / Min Impact Speed - 1). Слабкий удар ламає лише ямку")]
    public float ramPerSpeed = 1f;
    [Tooltip("Максимальна довжина тунелю в радіусах каменя")]
    public float maxRamRadii = 2.5f;
    [Tooltip("Скільки секунд після удару камінь проходить крізь старі колайдери зачеплених чанків (вони ще не перебудовані). " +
             "Без цього камінь відскакує від уже зламаної стіни. Занадто багато = камінь пролітає крізь ціле")]
    public float ghostTime = 0.12f;

    [Header("Продуктивність")]
    [Tooltip("Мінімальна пауза між руйнуваннями, секунд. Не дає ламати кулю щокадру, поки камінь їде вздовж стіни")]
    public float minDestroyInterval = 0f;
    [Tooltip("Не руйнувати знову, поки камінь не зрушив від місця попереднього руйнування хоча б на цю частку свого радіуса")]
    [Range(0f, 1f)] public float minMoveFraction = 0f;

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
    bool hasLastHit;
    Vector3 lastHitPos;
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

    // ---- проходження крізь старі колайдери зламаних чанків ----
    readonly Collider[] ghostBuf = new Collider[128];
    readonly Dictionary<Collider, float> ghosts = new Dictionary<Collider, float>();
    readonly List<Collider> ghostTmp = new List<Collider>();
    Collider[] myCols;

    void GhostThrough(Vector3 center, float radius)
    {
        if (ghostTime <= 0f) return;
        if (myCols == null) myCols = GetComponentsInChildren<Collider>();

        int cnt = Physics.OverlapSphereNonAlloc(center, radius, ghostBuf, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < cnt; i++)
        {
            Collider other = ghostBuf[i];
            if (other == null || other.GetComponentInParent<VoxelWorld>() != world) continue;
            foreach (var my in myCols)
                if (my != null) Physics.IgnoreCollision(my, other, true);
            ghosts[other] = Time.time + ghostTime;
        }
    }

    void SetGhost(Collider other, bool ignore)
    {
        if (other == null || myCols == null) return;
        foreach (var my in myCols)
            if (my != null) Physics.IgnoreCollision(my, other, ignore);
    }

    void Update()
    {
        if (ghosts.Count == 0) return;
        ghostTmp.Clear();
        foreach (var kv in ghosts)
            if (kv.Key == null || Time.time >= kv.Value) ghostTmp.Add(kv.Key);
        foreach (var other in ghostTmp)
        {
            SetGhost(other, false);
            ghosts.Remove(other);
        }
    }

    void OnDisable()
    {
        foreach (var kv in ghosts) SetGhost(kv.Key, false);
        ghosts.Clear();
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
        float cd = Mathf.Max(hitCooldown, minDestroyInterval);
        if (Time.time - lastHitTime < cd) return;

        // камінь майже не зрушив від попереднього руйнування: нового там ламати нічого
        if (hasLastHit && minMoveFraction > 0f)
        {
            float minMove = RockRadius() * minMoveFraction;
            if ((RockCenter() - lastHitPos).sqrMagnitude < minMove * minMove) return;
        }

        ContactPoint cp = c.GetContact(0);

        // враховуємо тільки швидкість У стіну (по нормалі). Дотик збоку чи ковзання вздовж стіни
        // нічого не ламає, навіть якщо камінь їде швидко
        float impact = Mathf.Abs(Vector3.Dot(lastVelocity, cp.normal));
        float sizeRatio = absorber != null ? Mathf.Max(1f, absorber.SizeRatio) : 1f;
        impact *= Mathf.Pow(sizeRatio, sizeBreakExponent);   // великий камінь ламає і з меншою швидкістю
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

        // таран: сильніший удар прорізає тунель уздовж руху
        float tunnel = 0f;
        if (strictRockRadius && ramThrough)
        {
            float R = RockRadius();
            tunnel = Mathf.Clamp((impact / Mathf.Max(0.01f, minImpactSpeed) - 1f) * ramPerSpeed, 0f, maxRamRadii) * R;
            Vector3 dir = lastVelocity.sqrMagnitude > 1e-4f ? lastVelocity.normalized : -cp.normal;
            float stepLen = Mathf.Max(0.01f, R * 0.9f);
            for (float d = stepLen; d <= tunnel + 0.001f; d += stepLen)
                world.DestroySphere(center + dir * d, radius, removed);
        }
        GhostThrough(center, radius + tunnel + RockRadius());

        hasLastHit = true;
        lastHitPos = RockCenter();

        rb.linearVelocity = lastVelocity * speedKeep;   // камінь пробиває, а не зупиняється

        world.SpawnDebris(removed, center, lastVelocity);   // кількість уламків обмежує VoxelWorld (Max Debris Per Hit)
    }
}