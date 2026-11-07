using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Рівень-планета "як реальна Земля": материки, моря, клімат за широтою, гори, дерева.
// Повісь на порожній GameObject, VoxelWorld і камінь знайдуться самі.
// VoxelWorld має стояти на (0,0,0) з масштабом 1. FloorGenerator і VillageGenerator у цій сцені вимкни.
//
//  * Уся кора генерується ОДИН РАЗ на старті (від полюса, де стартує камінь, і далі).
//    Поки йде генерація, камінь заморожений, на екрані відсотки. Потім камінь може котитись будь-де.
//  * ОСНОВА (Bedrock): гладка куля з SphereCollider і земляною текстурою. Каменем не ламається.
//  * КОРА: шар справжніх вокселів. Зверху шар біому, під ним земля (бруд), глибше земля з камінням.
//  * МОРЕ: прозора водяна куля. Біля берегів піщане мілководдя. У воді камінь сповільнюється.
//  * КЛІМАТ: за широтою + шум вологості: сніг, тундра, ліс, трава, савана, пустеля, джунглі, гори.
//  * КРАЇНИ: діаграма Вороного, на межах гравійна смуга.
//  * ДЕРЕВА: вокселні, ростуть радіально від центру планети.
//  * ГРАВІТАЦІЯ радіальна: до центру планети.
//
// Усі розміри у ВОКСЕЛЯХ. Висота світу потрібна приблизно 2 * (Bedrock + Crust + TreeMargin).
// Планета з Bedrock 200 це кілька мільйонів вокселів: якщо важко, зменш bedrockRadius.
public class PlanetGenerator : MonoBehaviour
{
    [Header("Зв'язки")]
    public VoxelWorld world;
    public int seed = 4004;

    [Header("Планета (у вокселях)")]
    [Tooltip("Радіус незламної основи")]
    public int bedrockRadius = 200;
    [Tooltip("Максимальна товщина вокселної кори над основою")]
    public int crustMax = 10;
    [Tooltip("Запас висоти над корою для дерев")]
    public int treeMargin = 9;
    [Tooltip("Висота води над основою (у вокселях). Суша має товщину більшу за це")]
    public float seaHeight = 2.2f;
    [Tooltip("Більше = більше моря (Земля ~ 0.54)")]
    [Range(0.3f, 0.7f)] public float seaLevel = 0.54f;
    [Tooltip("Розмір материків (більше = дрібніші)")]
    public float continentFreq = 1.7f;
    [Tooltip("Частота пагорбів і гір")]
    public float hillFreq = 8f;
    public int countryCount = 14;
    [Tooltip("Ширина смуги кордону (на одиничній кулі)")]
    public float borderWidth = 0.025f;

    [Header("Дерева")]
    public bool trees = true;
    [Range(0f, 3f)] public float treeDensity = 1f;

    [Header("Гравітація")]
    [Tooltip("Прискорення до центру планети, м/с²")]
    public float gravity = 30f;
    [Tooltip("Опір води для каменя")]
    public float waterDrag = 1.5f;
    [Range(0f, 2f)] public float friction = 0.6f;

    [Header("Генерація")]
    public int blockSize = 24;
    [Tooltip("Скільки мс на кадр можна витрачати на генерацію")]
    public float frameBudgetMs = 30f;
    public bool freezeRockWhileLoading = true;
    public bool showProgress = true;

    [Header("Вигляд")]
    public Color waterColor = new Color(0.15f, 0.45f, 0.85f, 0.72f);
    public int textureResolution = 32;

    // ---------- біоми ----------
    const int GRASS = 0, DESERT = 1, SNOW = 2, FOREST = 3, SAVANNA = 4, TUNDRA = 5, JUNGLE = 6, MOUNTAIN = 7;
    const int BIOMES = 8;

    // ---------- стан ----------

    struct VoxelOp { public Vector3Int pos; public byte id; }
    struct Col { public bool sea, shallow, border, beach; public int biome; public float thick, treeP; }

    readonly List<VoxelOp> ops = new List<VoxelOp>();

    float vs;
    int Rb, B;
    Vector3 C;                       // центр планети у вокселях
    Vector3 off1, off2;
    Vector3[] seeds;

    RockAbsorber rock;
    Rigidbody rb;
    bool rockWasKinematic;
    bool frozen;

    byte[][] topSets, soilSets, leafSets;
    byte[] stoneSet, sandSet, borderSet, trunkSet;

    // ---------- публічне ----------

    public bool Ready { get; private set; }
    public float Progress { get; private set; }

    public Vector3 CenterWorld
    {
        get { return world != null ? world.transform.TransformPoint(C * vs) : transform.position; }
    }

    // Куди "вгору" у цій точці світу (від центру планети). Потрібно камері й керуванню каменя
    public Vector3 UpAt(Vector3 worldPos)
    {
        return (worldPos - CenterWorld).normalized;
    }

    // Північний полюс: сюди став камінь на старті (там завжди суша першої країни)
    public Vector3 StartPoint
    {
        get
        {
            if (world == null || vs <= 0f) return transform.position;
            return world.transform.TransformPoint((C + Vector3.up * (Rb + crustMax + 1)) * vs);
        }
    }

    // ---------- старт ----------

    void Start()
    {
        if (world == null) world = FindFirstObjectByType<VoxelWorld>();
        if (world == null)
        {
            Debug.LogWarning("[PlanetGenerator] Не знайдено VoxelWorld", this);
            enabled = false;
            return;
        }
        world.voxelGround = false;

        vs = world.voxelSize;
        Rb = Mathf.Max(40, bedrockRadius);
        B = Mathf.Max(8, blockSize);
        C = new Vector3(0.5f, Rb + crustMax + Mathf.Max(0, treeMargin) + 2.5f, 0.5f);

        var g = new System.Random(seed);
        off1 = new Vector3((float)g.NextDouble(), (float)g.NextDouble(), (float)g.NextDouble()) * 100f;
        off2 = new Vector3((float)g.NextDouble(), (float)g.NextDouble(), (float)g.NextDouble()) * 100f;

        // країни: рівномірні точки на кулі (спіраль Фібоначчі) з легким зсувом; країна 0 біля північного полюса
        int n = Mathf.Max(3, countryCount);
        seeds = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / n;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float phi = i * 2.39996f + (float)g.NextDouble() * 0.5f;
            Vector3 p = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
            p += new Vector3((float)g.NextDouble() - 0.5f, (float)g.NextDouble() - 0.5f, (float)g.NextDouble() - 0.5f) * 0.25f;
            seeds[i] = p.normalized;
        }

        InitColors();
        BuildBedrock();
        BuildWater();
        StartCoroutine(Generate());
    }

    byte Cl(int r, int g, int b)
    {
        return world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));
    }

    void InitColors()
    {
        topSets = new byte[BIOMES][];
        soilSets = new byte[BIOMES][];
        leafSets = new byte[BIOMES][];

        // верхній шар
        topSets[GRASS]    = new[] { Cl(95, 160, 70), Cl(110, 175, 80), Cl(80, 140, 62) };
        topSets[DESERT]   = new[] { Cl(222, 196, 130), Cl(212, 184, 118), Cl(230, 206, 142) };
        topSets[SNOW]     = new[] { Cl(245, 247, 250), Cl(232, 238, 244), Cl(250, 251, 252) };
        topSets[FOREST]   = new[] { Cl(62, 125, 55), Cl(55, 110, 48), Cl(70, 135, 60) };
        topSets[SAVANNA]  = new[] { Cl(170, 170, 80), Cl(158, 160, 72), Cl(180, 176, 92) };
        topSets[TUNDRA]   = new[] { Cl(130, 140, 110), Cl(120, 128, 100), Cl(140, 148, 120) };
        topSets[JUNGLE]   = new[] { Cl(40, 130, 50), Cl(34, 115, 44), Cl(50, 145, 58) };
        topSets[MOUNTAIN] = new[] { Cl(140, 138, 134), Cl(120, 118, 116), Cl(155, 152, 148) };

        // земля (бруд) під верхнім шаром
        soilSets[GRASS]    = new[] { Cl(128, 100, 70), Cl(120, 94, 66), Cl(112, 86, 60) };
        soilSets[DESERT]   = new[] { Cl(196, 164, 106), Cl(184, 152, 96) };
        soilSets[SNOW]     = new[] { Cl(120, 100, 84), Cl(110, 92, 78) };
        soilSets[FOREST]   = new[] { Cl(104, 78, 54), Cl(96, 72, 50), Cl(112, 86, 60) };
        soilSets[SAVANNA]  = new[] { Cl(140, 106, 70), Cl(130, 98, 64) };
        soilSets[TUNDRA]   = new[] { Cl(110, 96, 80), Cl(102, 88, 74) };
        soilSets[JUNGLE]   = new[] { Cl(92, 66, 44), Cl(84, 60, 40), Cl(100, 72, 48) };
        soilSets[MOUNTAIN] = new[] { Cl(112, 110, 106), Cl(100, 98, 96) };

        // листя дерев
        leafSets[GRASS]   = new[] { Cl(70, 140, 60), Cl(60, 125, 52) };
        leafSets[FOREST]  = new[] { Cl(40, 105, 45), Cl(34, 92, 40), Cl(190, 130, 40) };
        leafSets[SAVANNA] = new[] { Cl(120, 140, 56), Cl(108, 128, 50) };
        leafSets[TUNDRA]  = new[] { Cl(36, 86, 60), Cl(30, 76, 52) };
        leafSets[JUNGLE]  = new[] { Cl(28, 120, 44), Cl(22, 104, 38), Cl(36, 134, 50) };

        stoneSet = new[] { Cl(150, 150, 146), Cl(124, 124, 120), Cl(104, 104, 102) };
        sandSet = new[] { Cl(226, 206, 150), Cl(214, 192, 134) };
        borderSet = new[] { Cl(110, 96, 78), Cl(120, 106, 88) };
        trunkSet = new[] { Cl(98, 70, 42), Cl(88, 62, 38) };
    }

    // ---------- основа й море ----------

    Mesh SphereMesh(float radius, int seg, int rings)
    {
        var verts = new Vector3[(seg + 1) * (rings + 1)];
        var norms = new Vector3[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (int i = 0; i <= rings; i++)
        {
            float th = Mathf.PI * i / rings;
            for (int j = 0; j <= seg; j++)
            {
                float ph = 2f * Mathf.PI * j / seg;
                Vector3 v = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                int k = i * (seg + 1) + j;
                verts[k] = v * radius;
                norms[k] = v;
                uvs[k] = new Vector2((float)j / seg, (float)i / rings);
            }
        }
        var tris = new int[seg * rings * 6];
        int t = 0;
        for (int i = 0; i < rings; i++)
        for (int j = 0; j < seg; j++)
        {
            int a = i * (seg + 1) + j, b = a + seg + 1;
            // передня грань дивиться НАЗОВНІ
            tris[t++] = a; tris[t++] = a + 1; tris[t++] = b;
            tris[t++] = a + 1; tris[t++] = b + 1; tris[t++] = b;
        }
        var m = new Mesh { name = "PlanetSphere" };
        m.indexFormat = IndexFormat.UInt32;
        m.vertices = verts;
        m.normals = norms;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateBounds();
        return m;
    }

    void BuildBedrock()
    {
        float R = Rb * vs;
        var go = new GameObject("Bedrock");
        go.transform.SetParent(transform, false);
        go.transform.position = CenterWorld;

        go.AddComponent<MeshFilter>().sharedMesh = SphereMesh(R * 0.9995f, 128, 64);
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = BedrockMaterial();
        rend.shadowCastingMode = ShadowCastingMode.Off;

        // гладкий колайдер: ідеальна куля, камінь по ній котиться без рифлення
        var sc = go.AddComponent<SphereCollider>();
        sc.radius = R;
        sc.sharedMaterial = new PhysicsMaterial("Bedrock")
        {
            dynamicFriction = friction,
            staticFriction = friction,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
    }

    Material BedrockMaterial()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) lit = Shader.Find("Standard");
        var m = new Material(lit) { name = "BedrockMat" };

        // піксельна текстура: переважно земля, трохи каміння (видно, коли кору зламано)
        int n = Mathf.Max(8, textureResolution);
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
        var rng = new System.Random(seed + 5);
        var px = new Color[n * n];
        Color[] pal =
        {
            new Color32(128, 100, 70, 255), new Color32(120, 94, 66, 255), new Color32(110, 84, 58, 255),
            new Color32(100, 76, 52, 255), new Color32(136, 108, 76, 255), new Color32(124, 124, 120, 255),
            new Color32(104, 104, 102, 255)
        };
        for (int i = 0; i < px.Length; i++) px[i] = pal[rng.Next(pal.Length)];
        tex.SetPixels(px);
        tex.Apply(true);

        if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(140f, 70f)); }
        if (m.HasProperty("_MainTex")) { m.SetTexture("_MainTex", tex); m.SetTextureScale("_MainTex", new Vector2(140f, 70f)); }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        return m;
    }

    void BuildWater()
    {
        float R = (Rb + seaHeight) * vs;
        var go = new GameObject("Sea");
        go.transform.SetParent(transform, false);
        go.transform.position = CenterWorld;
        go.AddComponent<MeshFilter>().sharedMesh = SphereMesh(R, 128, 64);
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = WaterMaterial();
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;
        // колайдера немає: камінь заходить у воду
    }

    Material WaterMaterial()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        var m = new Material(lit != null ? lit : Shader.Find("Standard")) { name = "SeaMat" };
        if (lit != null)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", waterColor);
            m.SetFloat("_Smoothness", 0.6f);
        }
        return m;
    }

    // ---------- шум ----------

    float N3(Vector3 p)
    {
        int xi = Mathf.FloorToInt(p.x), yi = Mathf.FloorToInt(p.y), zi = Mathf.FloorToInt(p.z);
        float fx = p.x - xi, fy = p.y - yi, fz = p.z - zi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
        float c000 = H(xi, yi, zi), c100 = H(xi + 1, yi, zi), c010 = H(xi, yi + 1, zi), c110 = H(xi + 1, yi + 1, zi);
        float c001 = H(xi, yi, zi + 1), c101 = H(xi + 1, yi, zi + 1), c011 = H(xi, yi + 1, zi + 1), c111 = H(xi + 1, yi + 1, zi + 1);
        float x00 = Mathf.Lerp(c000, c100, fx), x10 = Mathf.Lerp(c010, c110, fx);
        float x01 = Mathf.Lerp(c001, c101, fx), x11 = Mathf.Lerp(c011, c111, fx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
    }

    float H(int x, int y, int z) { return Hash01(x + seed, y, z); }

    float Fbm(Vector3 p) { return N3(p) * 0.55f + N3(p * 2.03f) * 0.30f + N3(p * 4.1f) * 0.15f; }

    // ---------- карта планети ----------

    // Що лежить у напрямку n (одиничний вектор від центру)
    Col Sample(Vector3 n)
    {
        var c = new Col();
        float land = Fbm(n * continentFreq + off1);
        land += Mathf.Clamp01((n.y - 0.93f) / 0.05f) * 0.6f;      // біля північного полюса завжди суша
        if (land < seaLevel)
        {
            c.sea = true;
            if (land > seaLevel - 0.04f)                            // піщане мілководдя біля берега
            {
                c.shallow = true;
                c.beach = true;
                c.biome = DESERT;
                c.thick = 1.6f;
            }
            return c;
        }

        // країни (для кордонів)
        float d1 = 9f, d2 = 9f;
        for (int i = 0; i < seeds.Length; i++)
        {
            float d = (n - seeds[i]).magnitude;
            if (d < d1) { d2 = d1; d1 = d; }
            else if (d < d2) d2 = d;
        }
        c.border = (d2 - d1) < borderWidth;

        // рельєф
        float coast = Mathf.Clamp01((land - seaLevel) / 0.05f);
        float hills = Fbm(n * hillFreq + off2);
        float rel = Mathf.Clamp01((hills - 0.35f) / 0.4f);
        c.thick = 2.6f + coast * (1.2f + rel * (crustMax - 3.8f));
        c.beach = c.thick < 3.4f;

        // клімат: широта + шум температури й вологості
        float lat = Mathf.Abs(n.y);
        float tn = Fbm(n * 2.3f + off1 * 0.7f) - 0.5f;
        float m = Fbm(n * 3.1f + off2 * 0.5f);
        float latN = Mathf.Clamp01(lat + tn * 0.25f);

        int biome;
        if (latN > 0.82f) biome = SNOW;
        else if (latN > 0.65f) biome = m > 0.47f ? FOREST : TUNDRA;
        else if (latN > 0.40f) biome = m > 0.50f ? FOREST : GRASS;
        else if (latN > 0.18f) biome = m < 0.45f ? DESERT : (m < 0.55f ? SAVANNA : GRASS);
        else biome = m > 0.50f ? JUNGLE : SAVANNA;

        if (rel > 0.88f) biome = SNOW;                              // засніжені піки
        else if (rel > 0.6f && biome != SNOW) biome = MOUNTAIN;     // кам'яні гори
        if (n.y < -0.85f) biome = SNOW;                             // південний полюс
        if (n.y > 0.93f) biome = GRASS;                             // стартова країна зелена
        c.biome = biome;

        // густота дерев
        float tp = 0f;
        switch (biome)
        {
            case FOREST:  tp = 0.030f; break;
            case JUNGLE:  tp = 0.045f; break;
            case GRASS:   tp = 0.004f; break;
            case SAVANNA: tp = 0.003f; break;
            case TUNDRA:  tp = 0.006f; break;
        }
        if (n.y > 0.90f) tp = 0f;
        c.treeP = tp * treeDensity;
        return c;
    }

    // ---------- повна генерація один раз ----------

    IEnumerator Generate()
    {
        for (int i = 0; i < 4; i++) yield return null;

        FindRock();
        Freeze(true);

        // усі блоки, що зачіпають кору
        float outer = Rb + crustMax + B;
        float inner = Rb - B;
        int lo = Mathf.FloorToInt((-outer) / B) - 1;
        int hi = Mathf.CeilToInt(outer / B) + 1;
        int cx = Mathf.FloorToInt(C.x / B), cy = Mathf.FloorToInt(C.y / B), cz = Mathf.FloorToInt(C.z / B);

        var list = new List<Vector3Int>();
        var dist = new Dictionary<Vector3Int, float>();
        Vector3 startV = C + Vector3.up * (Rb + crustMax + 1);
        for (int dz = lo; dz <= hi; dz++)
        for (int dy = lo; dy <= hi; dy++)
        for (int dx = lo; dx <= hi; dx++)
        {
            var b = new Vector3Int(cx + dx, cy + dy, cz + dz);
            Vector3 bc = new Vector3(b.x + 0.5f, b.y + 0.5f, b.z + 0.5f) * B;
            float dr = (bc - C).magnitude;
            if (dr > outer || dr < inner) continue;
            list.Add(b);
            dist[b] = (bc - startV).sqrMagnitude;
        }
        // спершу блоки біля старту, далі назовні
        list.Sort((a, b) => dist[a].CompareTo(dist[b]));

        var sw = new System.Diagnostics.Stopwatch();
        sw.Start();
        int total = Mathf.Max(1, list.Count);

        for (int bi = 0; bi < list.Count; bi++)
        {
            BuildBlock(list[bi]);

            int cnt = 0;
            for (int idx = 0; idx < ops.Count; idx++)
            {
                VoxelOp op = ops[idx];
                if (world.GetVoxel(op.pos) == 0) world.SetVoxel(op.pos, op.id);
                if ((++cnt & 1023) == 0 && sw.ElapsedMilliseconds > frameBudgetMs)
                {
                    yield return null;
                    sw.Restart();
                }
            }
            ops.Clear();

            Progress = (bi + 1f) / total;
            if (sw.ElapsedMilliseconds > frameBudgetMs)
            {
                yield return null;
                sw.Restart();
            }
        }

        Progress = 1f;
        Ready = true;
        Freeze(false);
    }

    void FindRock()
    {
        if (rock == null)
        {
            rock = FindFirstObjectByType<RockAbsorber>();
            if (rock == null) return;
        }
        if (rb == null)
        {
            rb = rock.GetComponentInParent<Rigidbody>();
            if (rb != null) rb.useGravity = false;
        }
    }

    // на час генерації камінь стоїть на місці
    void Freeze(bool on)
    {
        if (!freezeRockWhileLoading || rb == null) return;
        if (on && !frozen)
        {
            rockWasKinematic = rb.isKinematic;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.isKinematic = true;
            frozen = true;
        }
        else if (!on && frozen)
        {
            rb.isKinematic = rockWasKinematic;
            frozen = false;
        }
    }

    void BuildBlock(Vector3Int b)
    {
        int x0 = b.x * B, y0 = b.y * B, z0 = b.z * B;
        for (int z = z0; z < z0 + B; z++)
        for (int y = y0; y < y0 + B; y++)
        for (int x = x0; x < x0 + B; x++)
        {
            Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - C;
            float d = p.magnitude;
            float h = d - Rb;
            if (h < -0.5f || h > crustMax) continue;

            Vector3 n = p / d;
            Col c = Sample(n);
            if (c.sea && !c.shallow) continue;    // глибокий океан: лише дно (основа) і вода
            if (h >= c.thick) continue;

            int depth = h < 0f ? 99 : Mathf.FloorToInt(c.thick - h);
            byte id;
            if (depth == 0)
            {
                if (c.border) id = Pick(borderSet, Hash(x, y, z));
                else if (c.beach) id = Pick(sandSet, Hash(x, y, z));
                else id = Pick(topSets[c.biome], Hash(x, y, z));

                if (trees && !c.border && !c.beach && c.treeP > 0f && Hash01(x, y + 7, z) < c.treeP)
                    PlaceTree(n, d, c.biome, Hash(x, 5, z));
            }
            else if (c.shallow)
            {
                id = Pick(sandSet, Hash(x, 3, z));
            }
            else if (depth <= 3)
            {
                // шари землі (бруд) одразу під поверхнею
                id = Pick(soilSets[c.biome], Hash(x, 2, z));
            }
            else
            {
                // глибше: земля з камінцями
                id = Hash01(x, y, z) < 0.35f ? Pick(stoneSet, Hash(x, 1, z)) : Pick(soilSets[c.biome], Hash(x, 2, z));
            }
            ops.Add(new VoxelOp { pos = new Vector3Int(x, y, z), id = id });
        }
    }

    // Дерево: стовбур вздовж радіуса планети, крона кулькою
    void PlaceTree(Vector3 n, float d, int biome, int hash)
    {
        int th = 3 + ((hash & 0x7fffffff) % 3);
        if (biome == JUNGLE) th += 2;
        if (biome == SAVANNA) th = 3;

        for (int t = 1; t <= th; t++)
        {
            Vector3 q = C + n * (d + t);
            ops.Add(new VoxelOp
            {
                pos = new Vector3Int(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y), Mathf.FloorToInt(q.z)),
                id = Pick(trunkSet, hash + t)
            });
        }

        byte[] leaves = leafSets[biome] ?? leafSets[GRASS];
        Vector3 top = C + n * (d + th);
        Vector3Int tc = new Vector3Int(Mathf.FloorToInt(top.x), Mathf.FloorToInt(top.y), Mathf.FloorToInt(top.z));
        int rr = biome == SAVANNA ? 3 : 2;
        for (int dz = -rr; dz <= rr; dz++)
        for (int dy = -rr; dy <= rr; dy++)
        for (int dx = -rr; dx <= rr; dx++)
        {
            if (dx * dx + dy * dy + dz * dz > rr * rr + 1) continue;
            var pos = new Vector3Int(tc.x + dx, tc.y + dy, tc.z + dz);
            ops.Add(new VoxelOp { pos = pos, id = Pick(leaves, Hash(pos.x, pos.y, pos.z)) });
        }
    }

    // ---------- фізика: радіальна гравітація ----------

    void FixedUpdate()
    {
        if (rock == null || rb == null) { FindRock(); if (rock == null || rb == null) return; }
        rb.useGravity = false;
        if (rb.isKinematic) return;

        Vector3 to = CenterWorld - rb.worldCenterOfMass;
        float dist = to.magnitude;
        if (dist < 0.01f) return;
        rb.AddForce(to / dist * gravity, ForceMode.Acceleration);

        // у воді камінь сповільнюється
        float waterR = (Rb + seaHeight) * vs;
        if (dist < waterR + rock.Radius * 0.5f)
            rb.AddForce(-rb.linearVelocity * waterDrag, ForceMode.Acceleration);
    }

    // ---------- екран завантаження ----------

    void OnGUI()
    {
        if (Ready || !showProgress) return;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 28, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        GUI.Label(new Rect(0, Screen.height * 0.45f, Screen.width, 60f),
            "Генерація планети " + Mathf.RoundToInt(Progress * 100f) + "%", style);
    }

    // ---------- допоміжне ----------

    static byte Pick(byte[] set, int h)
    {
        return set[(h & 0x7fffffff) % set.Length];
    }

    static int Hash(int a, int b, int c)
    {
        unchecked
        {
            int h = (a * 73856093) ^ (b * 19349663) ^ (c * 83492791);
            h ^= h >> 13;
            h *= 0x5bd1e995;
            h ^= h >> 15;
            return h;
        }
    }

    static float Hash01(int x, int y, int z)
    {
        return (Hash(x, y, z) & 0xFFFF) / 65535f;
    }
}