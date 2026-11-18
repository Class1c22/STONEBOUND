using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Вибух планети: основа, море і вокселна кора зникають, замість них розлітаються уламки,
// росте вогняна куля, йде ударна хвиля, спалахує світло.
// Викликається з PlanetEndTrigger, або вручну: ПКМ по компоненту -> Explode (тест).
// Не потребує налаштування: PlanetGenerator знаходиться сам.
public class PlanetExplosion : MonoBehaviour
{
    [Header("Зв'язки (порожньо = знайдеться саме)")]
    public PlanetGenerator planet;

    [Header("Уламки")]
    [Tooltip("Скільки шматків розлітається. Більше = ефектніше, але важче")]
    public int debrisCount = 700;
    [Tooltip("Розмір шматка у вокселях (від і до)")]
    public Vector2 chunkVoxels = new Vector2(3f, 9f);
    [Tooltip("Яка частка шматків із надр планети (розжарені, помаранчеві)")]
    [Range(0f, 1f)] public float coreFraction = 0.3f;
    [Tooltip("Швидкість розльоту в радіусах планети за секунду")]
    public float speedRadii = 0.7f;
    [Tooltip("Скільки секунд живуть уламки")]
    public float lifetime = 7f;
    [Tooltip("Макс. обертання, градусів за секунду")]
    public float spin = 240f;
    [Tooltip("Гальмування в польоті (частка швидкості за секунду)")]
    public float drag = 0.15f;

    [Header("Вогняна куля й ударна хвиля")]
    public bool fireball = true;
    public float fireballMaxRadii = 1.6f;
    public float fireballTime = 1.4f;
    public bool shockwave = true;
    public float shockwaveMaxRadii = 3.5f;
    public float shockwaveTime = 1.8f;

    [Header("Світло")]
    public float flashIntensity = 40f;
    public float flashTime = 1.5f;
    public Color flashColor = new Color(1f, 0.65f, 0.25f);

    [Header("Що прибрати під час вибуху")]
    [Tooltip("Вимкнути VoxelWorld (уся вокселна кора, дерева)")]
    public bool hideWorld = true;
    public bool hideBedrock = true;
    public bool hideSea = true;

    public bool Exploded { get; private set; }

    class Chunk
    {
        public Transform t;
        public Vector3 vel, axis;
        public float spin, scale;
    }

    readonly List<Chunk> chunks = new List<Chunk>();
    float age;
    Mesh cubeMesh;
    Material chunkMat;
    Shader baseShader, unlitShader;

    void Awake()
    {
        baseShader = Shader.Find("Universal Render Pipeline/Lit");
        if (baseShader == null) baseShader = Shader.Find("Standard");
        unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");
    }

    [ContextMenu("Explode (тест)")]
    public void Explode()
    {
        if (Exploded) return;
        if (planet == null) planet = FindFirstObjectByType<PlanetGenerator>();
        if (planet == null)
        {
            Debug.LogWarning("[PlanetExplosion] Не знайдено PlanetGenerator", this);
            return;
        }
        Exploded = true;

        Vector3 center = planet.CenterWorld;
        Transform bedrock = planet.transform.Find("Bedrock");
        Transform sea = planet.transform.Find("Sea");

        // радіус основи у світі
        float R = 50f;
        if (bedrock != null)
        {
            var sc = bedrock.GetComponent<SphereCollider>();
            if (sc != null) R = sc.radius * bedrock.lossyScale.x;
        }
        float vs = planet.world != null ? planet.world.voxelSize : 1f;
        float crust = planet.crustMax * vs;

        // прибираємо цілу планету, замість неї будуть уламки
        if (hideBedrock && bedrock != null) bedrock.gameObject.SetActive(false);
        if (hideSea && sea != null) sea.gameObject.SetActive(false);
        if (hideWorld && planet.world != null) planet.world.gameObject.SetActive(false);

        SpawnDebris(center, R, crust, vs);
        if (fireball) StartCoroutine(Blast("Fireball", center, R * 0.2f, R * fireballMaxRadii, fireballTime,
            new Color(1f, 0.95f, 0.7f, 0.95f), new Color(1f, 0.45f, 0.1f, 0.7f), new Color(0.3f, 0.05f, 0f, 0f)));
        if (shockwave) StartCoroutine(Blast("Shockwave", center, R, R * shockwaveMaxRadii, shockwaveTime,
            new Color(1f, 0.9f, 0.7f, 0.35f), new Color(1f, 0.8f, 0.6f, 0.15f), new Color(1f, 0.8f, 0.6f, 0f)));
        StartCoroutine(Flash(center, R));
    }

    // ---------- уламки ----------

    void SpawnDebris(Vector3 center, float R, float crust, float vs)
    {
        if (cubeMesh == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cubeMesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            Destroy(tmp);
        }
        if (chunkMat == null)
        {
            chunkMat = new Material(baseShader) { name = "PlanetDebris" };
            if (chunkMat.HasProperty("_Smoothness")) chunkMat.SetFloat("_Smoothness", 0f);
            if (chunkMat.HasProperty("_Metallic")) chunkMat.SetFloat("_Metallic", 0f);
        }

        var root = new GameObject("PlanetDebris").transform;
        root.position = center;
        Destroy(root.gameObject, lifetime + 1f);

        for (int i = 0; i < debrisCount; i++)
        {
            Vector3 n = Random.onUnitSphere;
            bool core = Random.value < coreFraction;

            // кора: від основи до верху кори; надра: усередині кулі
            float r = core ? R * Random.Range(0.15f, 0.95f) : R + Random.Range(-0.05f, 1f) * crust;
            Color col;
            if (core)
            {
                float k = Random.value;
                col = Color.Lerp(new Color(1f, 0.35f, 0.05f), new Color(1f, 0.85f, 0.25f), k);
                col *= Random.Range(0.8f, 1.1f);
            }
            else
            {
                col = planet.RandomCrustColor();
            }
            col.a = 1f;

            float size = vs * Random.Range(chunkVoxels.x, chunkVoxels.y);

            var go = new GameObject("chunk");
            go.transform.SetParent(root, false);
            go.transform.position = center + n * r;
            go.transform.rotation = Random.rotation;
            go.transform.localScale = Vector3.one * size;
            go.AddComponent<MeshFilter>().sharedMesh = cubeMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = chunkMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;

            var mpb = new MaterialPropertyBlock();
            mpb.SetColor("_BaseColor", col);
            mpb.SetColor("_Color", col);
            if (core)
            {
                mpb.SetColor("_EmissionColor", col * 1.5f);
            }
            mr.SetPropertyBlock(mpb);

            Vector3 dir = (n + Random.insideUnitSphere * 0.35f).normalized;
            float speed = R * speedRadii * Random.Range(0.4f, 1.2f) * (core ? 1.15f : 1f);

            chunks.Add(new Chunk
            {
                t = go.transform,
                vel = dir * speed,
                axis = Random.onUnitSphere,
                spin = Random.Range(-1f, 1f) * spin,
                scale = size
            });
        }
        age = 0f;
    }

    void Update()
    {
        if (chunks.Count == 0) return;
        float dt = Time.deltaTime;
        age += dt;
        float life = Mathf.Max(0.5f, lifetime);
        float fade = Mathf.Clamp01((age - life * 0.7f) / (life * 0.3f));   // останні 30% життя зменшуються

        for (int i = chunks.Count - 1; i >= 0; i--)
        {
            Chunk c = chunks[i];
            if (c.t == null) { chunks.RemoveAt(i); continue; }
            c.vel *= Mathf.Max(0f, 1f - drag * dt);
            c.t.position += c.vel * dt;
            c.t.Rotate(c.axis, c.spin * dt, Space.World);
            c.t.localScale = Vector3.one * c.scale * (1f - fade);
        }
        if (age > life) chunks.Clear();
    }

    // ---------- куля й ударна хвиля ----------

    Material MakeTransparent(Color c)
    {
        var m = new Material(unlitShader) { name = "ExplosionFX" };
        if (unlitShader.name.Contains("Universal"))
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
        }
        SetColor(m, c);
        return m;
    }

    static void SetColor(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    IEnumerator Blast(string label, Vector3 center, float r0, float r1, float time, Color c0, Color c1, Color cEnd)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = label;
        Destroy(go.GetComponent<Collider>());
        go.transform.position = center;
        var rend = go.GetComponent<MeshRenderer>();
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;
        var mat = MakeTransparent(c0);
        rend.sharedMaterial = mat;

        float t = 0f;
        time = Mathf.Max(0.1f, time);
        while (t < time)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / time);
            float e = 1f - (1f - k) * (1f - k);          // швидко на початку, повільніше далі
            go.transform.localScale = Vector3.one * Mathf.Lerp(r0, r1, e) * 2f;   // Sphere має діаметр 1
            SetColor(mat, k < 0.5f ? Color.Lerp(c0, c1, k * 2f) : Color.Lerp(c1, cEnd, (k - 0.5f) * 2f));
            yield return null;
        }
        Destroy(mat);
        Destroy(go);
    }

    IEnumerator Flash(Vector3 center, float R)
    {
        var go = new GameObject("ExplosionLight");
        go.transform.position = center;
        var l = go.AddComponent<Light>();
        l.type = LightType.Point;
        l.color = flashColor;
        l.range = R * 6f;
        l.shadows = LightShadows.None;
        l.intensity = flashIntensity;

        float t = 0f, time = Mathf.Max(0.1f, flashTime);
        while (t < time)
        {
            t += Time.deltaTime;
            l.intensity = flashIntensity * (1f - Mathf.Clamp01(t / time));
            yield return null;
        }
        Destroy(go);
    }
}