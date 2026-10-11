using System.Collections.Generic;
using UnityEngine;

// Повісь на камінь. Коли камінь вдаряється у воксельний світ достатньо сильно,
// у місці удару видаляється куля вокселів, а замість них розлітаються уламки.
[RequireComponent(typeof(Rigidbody))]
public class VoxelDestroyer : MonoBehaviour
{
    public VoxelWorld world;

    [Header("Удар")]
    [Tooltip("Мінімальна швидкість удару, при якій щось руйнується")]
    public float minImpactSpeed = 5f;
    public float baseRadius = 0.8f;
    [Tooltip("Скільки радіуса додає кожен м/с швидкості")]
    public float radiusPerSpeed = 0.06f;
    public float maxRadius = 4f;
    [Tooltip("Яку частку швидкості камінь зберігає після пробиття (1 = взагалі не гальмує)")]
    [Range(0f, 1f)] public float speedKeep = 0.85f;
    public float hitCooldown = 0.05f;

    [Header("Уламки")]
    public int maxDebris = 300;
    public int debrisPerHit = 40;
    public float debrisScale = 0.9f;
    public float debrisSpread = 4f;

    Rigidbody rb;
    Vector3 lastVelocity;
    float lastHitTime;

    Rigidbody[] pool;
    int poolIndex;
    Material debrisMaterial;
    MaterialPropertyBlock block;
    static readonly int BaseColor = Shader.PropertyToID("_BaseColor");
    readonly List<VoxelWorld.RemovedVoxel> removed = new List<VoxelWorld.RemovedVoxel>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        pool = new Rigidbody[maxDebris];
        block = new MaterialPropertyBlock();
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

        float speed = c.relativeVelocity.magnitude;
        if (speed < minImpactSpeed) return;
        lastHitTime = Time.time;

        // сила удару залежить від швидкості й маси
        float radius = (baseRadius + speed * radiusPerSpeed) * Mathf.Pow(rb.mass, 1f / 3f);
        radius = Mathf.Min(radius, maxRadius);

        ContactPoint cp = c.GetContact(0);
        Vector3 center = cp.point - cp.normal * (radius * 0.5f);   // трохи углиб стіни

        removed.Clear();
        int n = world.DestroySphere(center, radius, removed);
        if (n == 0) return;

        rb.linearVelocity = lastVelocity * speedKeep;   // камінь пробиває, а не зупиняється
        SpawnDebris(center);
    }

    void SpawnDebris(Vector3 center)
    {
        if (debrisMaterial == null)
        {
            debrisMaterial = new Material(world.RuntimeMaterial);
            debrisMaterial.SetTexture("_BaseMap", Texture2D.whiteTexture);
        }

        int step = Mathf.Max(1, removed.Count / debrisPerHit);
        for (int i = 0; i < removed.Count; i += step)
        {
            var v = removed[i];
            Rigidbody d = GetDebris();

            d.transform.position = v.position;
            d.transform.rotation = Random.rotation;
            d.linearVelocity = (v.position - center).normalized * Random.Range(1f, debrisSpread)
                               + lastVelocity * 0.3f + Random.insideUnitSphere;
            d.angularVelocity = Random.insideUnitSphere * 5f;

            block.SetColor(BaseColor, world.GetColor(v.id));
            d.GetComponent<Renderer>().SetPropertyBlock(block);
        }
    }

    Rigidbody GetDebris()
    {
        Rigidbody d = pool[poolIndex];
        if (d == null)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "Debris";
            cube.transform.localScale = Vector3.one * world.voxelSize * debrisScale;
            cube.GetComponent<Renderer>().sharedMaterial = debrisMaterial;
            d = cube.AddComponent<Rigidbody>();
            d.mass = 0.05f;
            pool[poolIndex] = d;
        }
        poolIndex = (poolIndex + 1) % pool.Length;   // найстаріший уламок використовується повторно
        return d;
    }
}
