using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Вибух планети. Планета розколюється на великі цілі шматки (клини від поверхні до розжареного ядра),
// які мають справжній рельєф і кольори планети (трава, пісок, сніг, гори, океан). Шматки НЕ зникають:
// вони летять далі в невагомості й повільно обертаються. Також: ударна хвиля, вогняна куля, спалах світла,
// розжарене ядро в центрі, трохи дрібних уламків.
//
// ПОТРІБЕН метод PlanetGenerator.SampleForExplosion (див. PlanetGenerator_patch.txt).
// Шматки будуються заздалегідь у фоні, коли планета згенерована, тож у момент вибуху лагу немає.
// Викликається з PlanetEndTrigger, або вручну: ПКМ по компоненту -> Explode (тест).
public class PlanetExplosion : MonoBehaviour
{
    [Header("Зв'язки (порожньо = знайдеться саме)")]
    public PlanetGenerator planet;

    [Header("Шматки планети")]
    [Tooltip("На скільки великих шматків розколюється планета")]
    [Range(4, 120)] public int chunkCount = 30;
    [Tooltip("Нерівність тріщин між шматками (0 = рівні сектори)")]
    [Range(0f, 0.6f)] public float crackJitter = 0.28f;
    [Tooltip("Роздільність рельєфу шматків: по довготі й широті (більше = точніше, але довше будується)")]
    public int gridLon = 144;
    public int gridLat = 72;
    [Tooltip("Радіус розжареного ядра (частка радіуса планети)")]
    [Range(0.05f, 0.3f)] public float coreRadius01 = 0.14f;
    [Tooltip("Швидкість розльоту шматків у радіусах планети за секунду")]
    public float chunkSpeedRadii = 0.22f;
    public Vector2 chunkSpeedRandom = new Vector2(0.6f, 1.3f);
    [Tooltip("Максимальне обертання шматків, градусів за секунду (повільне, як у невагомості)")]
    public float chunkSpin = 12f;
    [Tooltip("Яскравість розжареного нутрощів шматків")]
    public float lavaGlow = 2f;
    [Tooltip("Будувати шматки заздалегідь, коли планета згенерована (без лагу під час вибуху)")]
    public bool prebuild = true;

    [Header("Дрібні уламки")]
    public int rubbleCount = 220;
    public Vector2 rubbleVoxels = new Vector2(1.5f, 4f);
    public float rubbleSpeedRadii = 0.5f;
    public float rubbleSpin = 200f;

    [Header("Очищення далеких шматків")]
    [Tooltip("Шматки й уламки далі за стільки радіусів планети видаляються (економія). 0 = не видаляти ніколи")]
    public float cullDistanceRadii = 120f;

    [Header("Вогняна куля й ударна хвиля")]
    public bool fireball = true;
    public float fireballMaxRadii = 1.3f;
    public float fireballTime = 1.1f;
    public bool shockwave = true;
    public float shockwaveMaxRadii = 5f;
    public float shockwaveTime = 2.2f;

    [Header("Ядро")]
    [Tooltip("Розжарена куля в центрі, що повільно остигає")]
    public bool lavaCore = true;
    public float coreCoolTime = 40f;

    [Header("Світло")]
    public float flashIntensity = 40f;
    public float flashTime = 1.5f;
    public Color flashColor = new Color(1f, 0.65f, 0.25f);

    [Header("Що прибрати під час вибуху")]
    public bool hideWorld = true;
    public bool hideBedrock = true;
    public bool hideSea = true;

    public bool Exploded { get; private set; }

    class Body { public Transform t; public Vector3 vel, axis; public float spin; }
    class Mb
    {
        public List<Vector3> v = new List<Vector3>();
        public List<Vector3> n = new List<Vector3>();
        public List<Vector2> uv = new List<Vector2>();
        public List<int> tri = new List<int>();
    }

    const int PalW = 256;
    static readonly Color darkLava = new Color(0.55f, 0.10f, 0.02f);
    static readonly Color brightLava = new Color(1f, 0.50f, 0.08f);
    static readonly Color capLava = new Color(1f, 0.80f, 0.25f);

    readonly List<Body> bodies = new List<Body>();
    readonly List<GameObject> chunkObjs = new List<GameObject>();
    readonly List<Vector3> chunkDirs = new List<Vector3>();
    readonly Color32[] palAlb = new Color32[PalW];
    readonly Color32[] palEm = new Color32[PalW];
    readonly Dictionary<int, int> palMap = new Dictionary<int, int>();
    int palCount;

    Transform chunkRoot;
    Material chunkMat;
    Mesh rubbleMesh;
    bool built, building;
    Coroutine buildCo;
    float R, vs, rin;
    Shader baseShader, unlitShader;

    void Awake()
    {
        baseShader = Shader.Find("Universal Render Pipeline/Lit");
        if (baseShader == null) baseShader = Shader.Find("Standard");
        unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
        if (unlitShader == null) unlitShader = Shader.Find("Sprites/Default");
    }

    void Start()
    {
        if (prebuild) StartCoroutine(PrebuildRoutine());
    }

    IEnumerator PrebuildRoutine()
    {
        while (planet == null || !planet.Ready)
        {
            if (Exploded) yield break;
            if (planet == null) planet = FindFirstObjectByType<PlanetGenerator>();
            yield return null;
        }
        if (Exploded || built || building) yield break;
        buildCo = StartCoroutine(BuildRoutine(true));
    }

    // ---------- вибух ----------

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

        // якщо шматки ще не добудувались, добудовуємо одразу (може бути коротка пауза)
        if (!built)
        {
            if (building && buildCo != null) { StopCoroutine(buildCo); ClearBuild(); }
            var e = BuildRoutine(false);
            while (e.MoveNext()) { }
        }

        Vector3 center = planet.CenterWorld;

        if (hideBedrock) { var b = planet.transform.Find("Bedrock"); if (b != null) b.gameObject.SetActive(false); }
        if (hideSea) { var s = planet.transform.Find("Sea"); if (s != null) s.gameObject.SetActive(false); }
        if (hideWorld && planet.world != null) planet.world.gameObject.SetActive(false);

        // шматки планети: стають видимими на місці планети й розлітаються
        if (chunkRoot != null)
        {
            chunkRoot.position = center;
            chunkRoot.gameObject.SetActive(true);
            for (int i = 0; i < chunkObjs.Count; i++)
            {
                if (chunkObjs[i] == null) continue;
                float sp = R * chunkSpeedRadii * Random.Range(chunkSpeedRandom.x, chunkSpeedRandom.y);
                bodies.Add(new Body
                {
                    t = chunkObjs[i].transform,
                    vel = chunkDirs[i] * sp,
                    axis = Random.onUnitSphere,
                    spin = Random.Range(-1f, 1f) * chunkSpin
                });
            }
        }

        SpawnRubble(center);

        if (fireball) StartCoroutine(Blast("Fireball", center, R * 0.15f, R * fireballMaxRadii, fireballTime,
            new Color(1f, 0.95f, 0.7f, 0.55f), new Color(1f, 0.45f, 0.1f, 0.4f), new Color(0.3f, 0.05f, 0f, 0f)));
        if (shockwave) StartCoroutine(Blast("Shockwave", center, R, R * shockwaveMaxRadii, shockwaveTime,
            new Color(1f, 0.92f, 0.75f, 0.45f), new Color(1f, 0.8f, 0.6f, 0.18f), new Color(1f, 0.8f, 0.6f, 0f)));
        if (lavaCore) StartCoroutine(CoreRoutine(center));
        StartCoroutine(Flash(center));
    }

    void Update()
    {
        if (bodies.Count == 0) return;
        float dt = Time.deltaTime;
        Vector3 center = planet != null ? planet.CenterWorld : Vector3.zero;
        float cull = cullDistanceRadii > 0f ? cullDistanceRadii * R : 0f;
        float cull2 = cull * cull;

        for (int i = bodies.Count - 1; i >= 0; i--)
        {
            Body b = bodies[i];
            if (b.t == null) { bodies.RemoveAt(i); continue; }
            b.t.position += b.vel * dt;
            b.t.rotation = Quaternion.AngleAxis(b.spin * dt, b.axis) * b.t.rotation;

            if (cull > 0f && (b.t.position - center).sqrMagnitude > cull2)
            {
                Destroy(b.t.gameObject);
                bodies.RemoveAt(i);
            }
        }
    }

    // ---------- побудова шматків ----------

    void InitMetrics()
    {
        R = 50f;
        Transform bedrock = planet.transform.Find("Bedrock");
        if (bedrock != null)
        {
            var sc = bedrock.GetComponent<SphereCollider>();
            if (sc != null) R = sc.radius * bedrock.lossyScale.x;
        }
        vs = planet.world != null ? planet.world.voxelSize : 1f;
        rin = R * Mathf.Clamp(coreRadius01, 0.05f, 0.3f);
    }

    void ClearBuild()
    {
        if (chunkRoot != null) Destroy(chunkRoot.gameObject);
        chunkRoot = null;
        chunkObjs.Clear();
        chunkDirs.Clear();
        built = false;
        building = false;
    }

    int Tex(Color c, bool emissive)
    {
        Color32 a = c;
        a.a = 255;
        int key = (a.r << 24) | (a.g << 16) | (a.b << 8) | (emissive ? 1 : 0);
        int idx;
        if (palMap.TryGetValue(key, out idx)) return idx;
        if (palCount >= PalW) return PalW - 1;
        idx = palCount++;
        palAlb[idx] = a;
        palEm[idx] = emissive ? a : new Color32(0, 0, 0, 255);
        palMap[key] = idx;
        return idx;
    }

    static float Pn(float a, float b) { return Mathf.PerlinNoise(a + 20f, b + 20f) * 2f - 1f; }

    static Vector3 Jitter(Vector3 d)
    {
        return new Vector3(
            Pn(d.x * 3f + 17.3f, d.y * 3f + 5.1f) + 0.5f * Pn(d.x * 9f + 3.7f, d.z * 9f + 8.9f),
            Pn(d.y * 3f + 9.7f, d.z * 3f + 2.3f) + 0.5f * Pn(d.y * 9f + 1.3f, d.x * 9f + 6.1f),
            Pn(d.z * 3f + 31.1f, d.x * 3f + 7.7f) + 0.5f * Pn(d.z * 9f + 4.1f, d.y * 9f + 2.2f));
    }

    void AddQuad(Mb m, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 outRef, int pal)
    {
        Vector3 n = Vector3.Cross(p1 - p0, p2 - p0);
        if (n.sqrMagnitude < 1e-12f) n = Vector3.Cross(p2 - p0, p3 - p0);
        if (n.sqrMagnitude < 1e-12f) return;   // вироджений квад (біля полюса)
        n.Normalize();
        bool flip = Vector3.Dot(n, outRef) < 0f;
        if (flip) n = -n;

        int b = m.v.Count;
        Vector2 uv = new Vector2((pal + 0.5f) / PalW, 0.5f);
        m.v.Add(p0); m.v.Add(p1); m.v.Add(p2); m.v.Add(p3);
        for (int k = 0; k < 4; k++) { m.n.Add(n); m.uv.Add(uv); }
        if (!flip) { m.tri.Add(b); m.tri.Add(b + 1); m.tri.Add(b + 2); m.tri.Add(b); m.tri.Add(b + 2); m.tri.Add(b + 3); }
        else { m.tri.Add(b); m.tri.Add(b + 2); m.tri.Add(b + 1); m.tri.Add(b); m.tri.Add(b + 3); m.tri.Add(b + 2); }
    }

    // Стінка зламу між двома шматками: ґрунт, камінь, темна лава, яскрава лава
    void AddWall(Mb mb, int a, int b, Vector3[] dirs, float[] rad, Vector3 cellDir, Color soil, Color stone)
    {
        Vector3 dA = dirs[a], dB = dirs[b];
        Vector3 mid = (dA + dB).normalized;
        Vector3 outRef = mid - cellDir;
        if (outRef.sqrMagnitude < 1e-10f) outRef = mid;

        float rA = rad[a], rB = rad[b];
        float[] la = { rA, Mathf.Min(rA, R), R * 0.62f, R * 0.34f, rin };
        float[] lb = { rB, Mathf.Min(rB, R), R * 0.62f, R * 0.34f, rin };
        int[] col = { Tex(soil, false), Tex(stone, false), Tex(darkLava, true), Tex(brightLava, true) };
        for (int k = 0; k < 4; k++)
            AddQuad(mb, dA * la[k], dB * lb[k], dB * lb[k + 1], dA * la[k + 1], outRef, col[k]);
    }

    IEnumerator BuildRoutine(bool slow)
    {
        building = true;
        InitMetrics();
        System.Array.Clear(palAlb, 0, PalW);
        System.Array.Clear(palEm, 0, PalW);
        palMap.Clear();
        palCount = 0;
        Tex(Color.white, false);   // текселя 0 = білий (для дрібних уламків)

        int W = Mathf.Max(24, gridLon), H = Mathf.Max(12, gridLat);
        int K = Mathf.Clamp(chunkCount, 4, 120);
        var rng = new System.Random(planet.seed + 991);

        // центри шматків: рівномірно по кулі з розкидом
        var seeds = new Vector3[K];
        for (int i = 0; i < K; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / K;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float phi = i * 2.39996f + (float)rng.NextDouble() * 0.8f;
            Vector3 p = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
            p += new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.3f;
            seeds[i] = p.normalized;
        }

        // сітка напрямків і радіуси поверхні (реальний рельєф планети)
        var dirs = new Vector3[(H + 1) * W];
        var rad = new float[(H + 1) * W];
        for (int i = 0; i <= H; i++)
        {
            float th = Mathf.PI * i / H;
            for (int j = 0; j < W; j++)
            {
                float ph = 2f * Mathf.PI * j / W;
                Vector3 d = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                dirs[i * W + j] = d;
                float thick;
                Color t0, s0, st0;
                planet.SampleForExplosion(d, 0, out thick, out t0, out s0, out st0);
                rad[i * W + j] = R + thick * vs;
            }
            if (slow && (i & 7) == 0) yield return null;
        }

        // який клітинці який шматок (Вороного з нерівними краями)
        var chunkOf = new int[H * W];
        for (int i = 0; i < H; i++)
        for (int j = 0; j < W; j++)
        {
            int j1 = (j + 1) % W;
            Vector3 d = (dirs[i * W + j] + dirs[i * W + j1] + dirs[(i + 1) * W + j] + dirs[(i + 1) * W + j1]).normalized;
            Vector3 jd = (d + Jitter(d) * crackJitter).normalized;
            int best = 0;
            float bd = -2f;
            for (int k = 0; k < K; k++)
            {
                float dd = Vector3.Dot(jd, seeds[k]);
                if (dd > bd) { bd = dd; best = k; }
            }
            chunkOf[i * W + j] = best;
        }
        if (slow) yield return null;

        var mbs = new Mb[K];
        for (int k = 0; k < K; k++) mbs[k] = new Mb();

        for (int i = 0; i < H; i++)
        {
            for (int j = 0; j < W; j++)
            {
                int j1 = (j + 1) % W;
                int ia = i * W + j, ib = i * W + j1, ic = (i + 1) * W + j1, idd = (i + 1) * W + j;
                int ch = chunkOf[i * W + j];
                Mb mb = mbs[ch];

                Vector3 dc = (dirs[ia] + dirs[ib] + dirs[ic] + dirs[idd]).normalized;
                int h = unchecked(i * 73856093 ^ j * 19349663 ^ 5381);
                float thick;
                Color top, soil, stone;
                planet.SampleForExplosion(dc, h, out thick, out top, out soil, out stone);

                // поверхня
                AddQuad(mb, dirs[ia] * rad[ia], dirs[ib] * rad[ib], dirs[ic] * rad[ic], dirs[idd] * rad[idd], dc, Tex(top, false));
                // розжарена основа клина (біля ядра)
                AddQuad(mb, dirs[ia] * rin, dirs[ib] * rin, dirs[ic] * rin, dirs[idd] * rin, -dc, Tex(capLava, true));

                // стінки там, де сусідня клітинка належить іншому шматку
                if (i > 0 && chunkOf[(i - 1) * W + j] != ch) AddWall(mb, ia, ib, dirs, rad, dc, soil, stone);
                if (i < H - 1 && chunkOf[(i + 1) * W + j] != ch) AddWall(mb, idd, ic, dirs, rad, dc, soil, stone);
                if (chunkOf[i * W + (j + W - 1) % W] != ch) AddWall(mb, ia, idd, dirs, rad, dc, soil, stone);
                if (chunkOf[i * W + j1] != ch) AddWall(mb, ib, ic, dirs, rad, dc, soil, stone);
            }
            if (slow && (i % 6) == 0) yield return null;
        }

        // матеріал із палітрою й обʼєкти шматків
        chunkMat = MakeChunkMaterial();
        chunkRoot = new GameObject("PlanetChunks").transform;
        chunkRoot.position = planet.CenterWorld;

        for (int k = 0; k < K; k++)
        {
            Mb mb = mbs[k];
            if (mb.v.Count == 0) continue;

            Vector3 cen = Vector3.zero;
            for (int q = 0; q < mb.v.Count; q++) cen += mb.v[q];
            cen /= mb.v.Count;
            var verts = mb.v.ToArray();
            for (int q = 0; q < verts.Length; q++) verts[q] -= cen;

            var mesh = new Mesh { name = "PlanetChunk" + k };
            mesh.indexFormat = IndexFormat.UInt32;
            mesh.vertices = verts;
            mesh.normals = mb.n.ToArray();
            mesh.uv = mb.uv.ToArray();
            mesh.triangles = mb.tri.ToArray();
            mesh.RecalculateBounds();

            var go = new GameObject("chunk" + k);
            go.transform.SetParent(chunkRoot, false);
            go.transform.localPosition = cen;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = chunkMat;

            chunkObjs.Add(go);
            chunkDirs.Add(cen.sqrMagnitude > 1e-6f ? cen.normalized : seeds[k]);
        }

        chunkRoot.gameObject.SetActive(false);
        built = true;
        building = false;
    }

    Material MakeChunkMaterial()
    {
        var alb = new Texture2D(PalW, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        alb.SetPixels32(palAlb);
        alb.Apply(false);
        var em = new Texture2D(PalW, 1, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        em.SetPixels32(palEm);
        em.Apply(false);

        var m = new Material(baseShader) { name = "PlanetChunkMat" };
        if (m.HasProperty("_BaseMap")) m.SetTexture("_BaseMap", alb);
        if (m.HasProperty("_MainTex")) m.SetTexture("_MainTex", alb);
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", Color.white);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.05f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        if (m.HasProperty("_Cull")) m.SetFloat("_Cull", 0f);   // двобічний (на випадок помилки обходу)

        m.EnableKeyword("_EMISSION");
        m.SetTexture("_EmissionMap", em);
        m.SetColor("_EmissionColor", Color.white * Mathf.Max(0f, lavaGlow));
        m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
        return m;
    }

    // ---------- дрібні уламки ----------

    void SpawnRubble(Vector3 center)
    {
        if (rubbleCount <= 0 || chunkMat == null) return;

        if (rubbleMesh == null)
        {
            var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
            rubbleMesh = Instantiate(tmp.GetComponent<MeshFilter>().sharedMesh);
            Destroy(tmp);
            var uv = new Vector2[rubbleMesh.vertexCount];
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(0.5f / PalW, 0.5f);   // білий тексель
            rubbleMesh.uv = uv;
        }

        var root = new GameObject("PlanetRubble").transform;
        root.position = center;

        for (int i = 0; i < rubbleCount; i++)
        {
            Vector3 dir = Random.onUnitSphere;
            float thick;
            Color top, soil, stone;
            planet.SampleForExplosion(dir, Random.Range(0, 1 << 30), out thick, out top, out soil, out stone);

            var go = new GameObject("rubble");
            go.transform.SetParent(root, false);
            go.transform.position = center + dir * (R + thick * vs * Random.value);
            go.transform.rotation = Random.rotation;
            go.transform.localScale = Vector3.one * vs * Random.Range(rubbleVoxels.x, rubbleVoxels.y);
            go.AddComponent<MeshFilter>().sharedMesh = rubbleMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = chunkMat;
            mr.shadowCastingMode = ShadowCastingMode.Off;
            var mpb = new MaterialPropertyBlock();
            Color col = Random.value < 0.5f ? top : (Random.value < 0.5f ? soil : stone);
            mpb.SetColor("_BaseColor", col);
            mpb.SetColor("_Color", col);
            mr.SetPropertyBlock(mpb);

            Vector3 d2 = (dir + Random.insideUnitSphere * 0.5f).normalized;
            bodies.Add(new Body
            {
                t = go.transform,
                vel = d2 * R * rubbleSpeedRadii * Random.Range(0.5f, 1.5f),
                axis = Random.onUnitSphere,
                spin = Random.Range(-1f, 1f) * rubbleSpin
            });
        }
    }

    // ---------- куля, хвиля, ядро, світло ----------

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
            float e = 1f - (1f - k) * (1f - k);
            go.transform.localScale = Vector3.one * Mathf.Lerp(r0, r1, e) * 2f;   // Sphere має діаметр 1
            SetColor(mat, k < 0.5f ? Color.Lerp(c0, c1, k * 2f) : Color.Lerp(c1, cEnd, (k - 0.5f) * 2f));
            yield return null;
        }
        Destroy(mat);
        Destroy(go);
    }

    IEnumerator CoreRoutine(Vector3 center)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        go.name = "LavaCore";
        Destroy(go.GetComponent<Collider>());
        go.transform.position = center;
        go.transform.localScale = Vector3.one * rin * 2.3f;
        var rend = go.GetComponent<MeshRenderer>();
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;
        var mat = new Material(unlitShader) { name = "LavaCoreMat" };
        rend.sharedMaterial = mat;

        Color hot = new Color(1f, 0.72f, 0.28f, 1f), cold = new Color(0.32f, 0.06f, 0.02f, 1f);
        float t = 0f, time = Mathf.Max(1f, coreCoolTime);
        while (t < time)
        {
            t += Time.deltaTime;
            SetColor(mat, Color.Lerp(hot, cold, Mathf.Clamp01(t / time)));
            yield return null;
        }
    }

    IEnumerator Flash(Vector3 center)
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