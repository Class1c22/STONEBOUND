using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Трава для 2 рівня: маленькі травинки на землі ПОЗА дорогами. Камінь не ламає їх, а притискає:
// травинки під каменем лягають, поряд відхиляються, а позаду лишається слід, який повільно розпрямляється.
//
// Як це працює:
//  * Земля навколо каменя ділиться на чанки (по 4 на сторону кварталу Tier2CityGenerator).
//    Чанк будується, коли генератор міста закінчив відповідний квартал, тож трава не лізе в будинки.
//  * Травинка не ставиться на дорогу (за тією ж сіткою, що й у генератора), а також у клітинку,
//    де на землі вже стоїть воксель (будинок, паркан, ставок, клумба, дерево тощо).
//  * Усе малюється GPU-інстансингом (Graphics.RenderMeshInstanced), без колайдерів і Rigidbody.
//  * Нахил від каменя рахується тільки для травинок поруч із ним і тих, що ще розпрямляються.
//  * ВІТЕР: напрям і силу бере з глобального WindSystem (той самий вітер, що на 1 рівні й для каменя).
//    Травинки хиляться за вітром навколо основи, пориви біжать по землі, а ще є дрібне тремтіння.
//    Притиснута каменем травинка вітру не слухає (чим сильніше притиснута, тим менше гойдається).
//    Вітер перераховується лише для травинок у кадрі й з кроком Wind Step, тож це дешево.
//
// Як підключити: повісь на порожній GameObject у сцені 2. Tier2CityGenerator і VoxelWorld знайдуться самі.
// Підлога (FloorGenerator) має бути на висоті groundY (за замовчуванням 0), як і в генератора.
public class Tier2Grass : MonoBehaviour
{
    [Header("Зв'язки (порожнє = знайти в сцені)")]
    public Tier2CityGenerator city;
    public VoxelWorld world;
    public int seed = 777;

    [Header("Травинки (у вокселях світу, тож масштабуються з ним)")]
    [Tooltip("Скільки травинок на одну воксельну клітинку землі")]
    public float bladesPerCell = 1.2f;
    public float heightMinVoxels = 1.5f;
    public float heightMaxVoxels = 3f;
    [Tooltip("Ширина основи травинки у вокселях")]
    public float widthVoxels = 0.55f;
    [Tooltip("Висота землі у світі (де стоять основи травинок)")]
    public float groundY = 0f;
    [Tooltip("Скільки клітинок від краю дороги лишати порожніми")]
    public int roadMarginCells = 0;

    [Header("Видимість")]
    [Tooltip("Як далеко від каменя є трава (метри)")]
    public float viewRadius = 40f;
    [Tooltip("Те саме в радіусах каменя: коли камінь росте, трава з'являється далі. Береться більше з двох")]
    public float viewRadiusInRockRadii = 20f;
    [Range(2, 8)] public int chunksPerBlockSide = 4;
    public int chunksPerScan = 3;
    public float scanInterval = 0.15f;

    [Header("Нахил під каменем")]
    [Tooltip("Максимальний кут нахилу, градуси (90 = лягає на землю)")]
    [Range(10f, 89f)] public float maxBendAngle = 80f;
    [Tooltip("Наскільки сильно відхиляються травинки поруч із каменем, але поза його тінню (0..1)")]
    [Range(0f, 1f)] public float wakeBend = 0.55f;
    [Tooltip("Ширина цієї зони в радіусах каменя")]
    [Range(0.05f, 1.5f)] public float wakeWidth = 0.5f;
    [Tooltip("Швидкість, з якою травинка лягає (1/с). Більше = лягає миттєвіше")]
    public float pressSpeed = 10f;
    [Tooltip("За скільки секунд притиснута травинка повністю розпрямляється. 0 = слід лишається назавжди")]
    public float recoverTime = 8f;
    [Tooltip("Наскільки швидкість каменя тягне травинку вздовж руху (0 = лише в сторони від каменя)")]
    [Range(0f, 2f)] public float travelInfluence = 0.7f;
    [Tooltip("Швидкість (м/с), з якої слід від каменя максимальний")]
    public float fullWakeSpeed = 8f;

    [Header("Вітер (береться з глобального WindSystem)")]
    public bool windEnabled = true;
    [Tooltip("Нахил при найсильнішому вітрі, градуси")]
    [Range(0f, 80f)] public float maxTilt = 28f;
    [Tooltip("Нахил у штиль, градуси")]
    [Range(0f, 20f)] public float calmTilt = 3f;
    [Tooltip("Загальний множник гойдання: 1 = як було, 2 = удвічі сильніше. Нове поле, тож діє одразу, без скидання сцени")]
    [Range(0.5f, 6f)] public float swayMultiplier = 2.5f;
    [Tooltip("Розмір поривів, метри")]
    public float gustSizeMeters = 10f;
    [Tooltip("Швидкість, з якою пориви біжать полем, м/с")]
    public float gustSpeedMeters = 4f;
    [Tooltip("Розмір дрібного тремтіння, метри")]
    public float turbulenceSizeMeters = 1.5f;
    public float turbulenceSpeed = 1.6f;
    [Range(0f, 1f)] public float turbulence = 0.35f;
    [Tooltip("На скільки градусів місцевий напрям може відхилятись від загального")]
    [Range(0f, 120f)] public float localDirectionJitter = 45f;
    [Tooltip("Як часто перераховувати вітер (секунди). Менше = плавніше, але дорожче")]
    [Range(0.01f, 0.2f)] public float windStep = 0.033f;

    [Header("Вигляд")]
    [Range(1, 6)] public int colorBands = 3;
    public Color baseColor = new Color(0.16f, 0.42f, 0.12f);
    public Color tipColor = new Color(0.46f, 0.74f, 0.22f);
    public bool receiveShadows = true;

    // ---------- внутрішній стан ----------

    struct Blade
    {
        public Vector3 pos;
        public float yaw, h, w;
        public float bend;      // 0 = стоїть, 1 = лежить
        public float lx, lz;    // напрямок нахилу (одиничний, в xz)
    }

    class Band
    {
        public Blade[] b;
        public Matrix4x4[] m;
        public int count;
    }

    class Chunk
    {
        public Band[] bands;
        public Bounds bounds;
        public Vector2 centerXZ;
        public int bentCount;
        public int lastFrame = -1;   // у якому кадрі чанк уже оброблено
        public int windStamp = -1;   // з якою версією вітру пораховані матриці
    }

    readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    readonly HashSet<Chunk> recoveringSet = new HashSet<Chunk>();
    readonly List<Vector2Int> removeBuf = new List<Vector2Int>();
    readonly List<Blade>[] tempBlades = new List<Blade>[8];

    Material[] materials;
    Mesh bladeMesh;
    RockAbsorber rock;
    Rigidbody rockBody;
    float vs;
    int blockV, roadV, n;
    float scanTimer;
    bool ready;
    readonly Plane[] planes = new Plane[6];

    // стан вітру (оновлюється раз на windStep)
    float windAngleRad;        // загальний напрям, радіани від +Z
    float windStrength;        // 0..1
    float windT;               // накопичений час
    float windAccum;           // час від останнього перерахунку
    float windTimer;
    Vector2 gustOffset;        // зсув поля поривів (вони біжать за вітром)
    float noiseO;              // великий зсув шуму від seed
    int windVersion;           // збільшується при кожному перерахунку, чанки звіряють із ним свої матриці
    bool lastWindEnabled = true;

    // ---------- життєвий цикл ----------

    void OnDestroy()
    {
        if (bladeMesh != null) Destroy(bladeMesh);
        if (materials != null)
            foreach (var m in materials) if (m != null) Destroy(m);
    }

    bool EnsureReady()
    {
        if (ready) return true;

        if (city == null) city = FindFirstObjectByType<Tier2CityGenerator>();
        if (city == null) return false;
        if (world == null) world = city.world != null ? city.world : FindFirstObjectByType<VoxelWorld>();
        if (world == null || !city.LayoutReady) return false;

        vs = world.voxelSize;
        blockV = city.BlockVoxels;
        roadV = city.RoadVoxels;
        n = Mathf.Max(1, chunksPerBlockSide);
        noiseO = 10000f + seed * 17.3f;
        PrepareMaterials();
        bladeMesh = BuildBladeMesh();
        for (int i = 0; i < tempBlades.Length; i++) tempBlades[i] = new List<Blade>(1024);
        UpdateWindState(0f);
        ready = true;
        return true;
    }

    void Update()
    {
        if (!EnsureReady()) return;

        FindRock();
        if (rock == null) return;

        scanTimer -= Time.deltaTime;
        if (scanTimer <= 0f)
        {
            scanTimer = Mathf.Max(0.05f, scanInterval);
            Stream();
        }

        TickWind(Time.deltaTime);
        SimulateBlades(Time.deltaTime);
        RenderChunks();
    }

    // Під час переходу між сценами існує кілька каменів: беремо той, що в активній сцені (як у генератора).
    void FindRock()
    {
        RockAbsorber[] all = FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None);
        RockAbsorber found = null;
        if (all.Length == 1) found = all[0];
        else if (all.Length > 1)
        {
            Scene active = SceneManager.GetActiveScene();
            found = all[0];
            for (int i = 0; i < all.Length; i++)
                if (all[i].gameObject.scene == active) { found = all[i]; break; }
        }

        if (found != rock)
        {
            rock = found;
            rockBody = rock != null ? rock.GetComponent<Rigidbody>() : null;
        }
    }

    // ---------- вітер ----------

    static float Contrast(float v)
    {
        return Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.25f, 0.75f, v));
    }

    // Раз на windStep бере напрям і силу з WindSystem та зсуває поле поривів. Збільшує windVersion,
    // тож усі видимі чанки перерахують свої матриці.
    void TickWind(float dt)
    {
        if (windEnabled != lastWindEnabled)
        {
            lastWindEnabled = windEnabled;
            windVersion++;   // вітер увімкнули чи вимкнули: один раз усе перерахувати
        }
        if (!windEnabled) return;

        windAccum += dt;
        windTimer -= dt;
        if (windTimer > 0f) return;
        windTimer = Mathf.Max(0.01f, windStep);

        UpdateWindState(windAccum);
        windAccum = 0f;
    }

    void UpdateWindState(float dt)
    {
        WindSystem ws = WindSystem.Instance;
        if (ws != null)
        {
            windAngleRad = ws.AngleDegrees * Mathf.Deg2Rad;
            windStrength = ws.Strength01;
        }

        windT += dt;
        Vector3 d = new Vector3(Mathf.Sin(windAngleRad), 0f, Mathf.Cos(windAngleRad));
        float speed = gustSpeedMeters * (0.4f + windStrength);
        gustOffset += new Vector2(d.x, d.z) * (speed * dt);
        windVersion++;
    }

    // Нахил травинки від вітру в її точці (навколо основи). Один Перлін на пориви, решта на sin, щоб було дешево.
    Quaternion WindTilt(Vector3 p, float scale)
    {
        if (!windEnabled || scale <= 0.001f) return Quaternion.identity;

        float gs = Mathf.Max(0.5f, gustSizeMeters);
        float ts = Mathf.Max(0.2f, turbulenceSizeMeters);

        // порив: хвиля, що біжить землею
        float gust = Contrast(Mathf.PerlinNoise((p.x - gustOffset.x) / gs + noiseO * 0.17f, (p.z - gustOffset.y) / gs + noiseO * 0.23f));
        // дрібне тремтіння: у кожному місці своє
        float turb = 0.5f + 0.5f * Mathf.Sin(windT * turbulenceSpeed * 2.2f + p.x / ts + p.z / ts * 0.7f);
        // місцеве відхилення напряму: у різних місцях трава хилиться в різні боки
        float local = Mathf.Sin(p.x / (gs * 0.8f) + windT * 0.15f) * Mathf.Cos(p.z / (gs * 0.8f) + noiseO * 0.001f);

        float strength = Mathf.Lerp(0.15f, 1f, gust) * windStrength;
        strength = Mathf.Clamp01(strength + (turb - 0.5f) * 2f * turbulence * 0.4f);

        float tilt = Mathf.Min(85f, Mathf.Lerp(calmTilt, maxTilt, strength) * swayMultiplier) * scale;
        float ang = windAngleRad + (local * localDirectionJitter + (turb - 0.5f) * turbulence * 40f) * Mathf.Deg2Rad;

        // вісь = up x dir: додатний кут нахиляє травинку в бік dir
        return Quaternion.AngleAxis(tilt, new Vector3(Mathf.Cos(ang), 0f, -Mathf.Sin(ang)));
    }

    // ---------- матеріали й мешу ----------

    void PrepareMaterials()
    {
        materials = new Material[colorBands];
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null)
        {
            Debug.LogError("[Tier2Grass] Не знайдено URP/Lit.", this);
            lit = Shader.Find("Standard");
        }

        for (int i = 0; i < colorBands; i++)
        {
            var m = new Material(lit) { name = "Tier2GrassBand" + i };
            Color c = Color.Lerp(baseColor, tipColor, colorBands > 1 ? (float)i / (colorBands - 1) : 0.5f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
            m.enableInstancing = true;
            materials[i] = m;
        }
    }

    // Травинка: основа 1x1 одиниця, нижня частина звужується, згори вістря. Висота 1.
    // Реальний розмір дають масштаб (ширина, висота, ширина) у матриці кожної травинки.
    static Mesh BuildBladeMesh()
    {
        var verts = new List<Vector3>();
        var norms = new List<Vector3>();
        var tris = new List<int>();
        Vector3 inside = new Vector3(0f, 0.4f, 0f);

        void Tri(Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 nrm = Vector3.Cross(b - a, c - a);
            Vector3 centroid = (a + b + c) / 3f;
            if (Vector3.Dot(nrm, centroid - inside) < 0f) { Vector3 t = b; b = c; c = t; nrm = -nrm; }
            nrm.Normalize();
            int s = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            norms.Add(nrm); norms.Add(nrm); norms.Add(nrm);
            tris.Add(s); tris.Add(s + 1); tris.Add(s + 2);
        }

        const float baseHalf = 0.5f, midHalf = 0.28f, midY = 0.6f;
        Vector2[] corner = { new Vector2(-1, -1), new Vector2(1, -1), new Vector2(1, 1), new Vector2(-1, 1) };
        Vector3 apex = new Vector3(0f, 1f, 0f);

        for (int k = 0; k < 4; k++)
        {
            Vector2 c0 = corner[k], c1 = corner[(k + 1) % 4];
            Vector3 b0 = new Vector3(c0.x * baseHalf, 0f, c0.y * baseHalf);
            Vector3 b1 = new Vector3(c1.x * baseHalf, 0f, c1.y * baseHalf);
            Vector3 m0 = new Vector3(c0.x * midHalf, midY, c0.y * midHalf);
            Vector3 m1 = new Vector3(c1.x * midHalf, midY, c1.y * midHalf);
            Tri(b0, b1, m1);
            Tri(b0, m1, m0);
            Tri(m0, m1, apex);
        }

        var mesh = new Mesh { name = "Tier2GrassBlade" };
        mesh.SetVertices(verts);
        mesh.SetNormals(norms);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        return mesh;
    }

    // ---------- сітка чанків ----------

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    // Перший воксель чанка k (по одній осі). Межі чанків збігаються з межами кварталів навіть тоді,
    // коли blockV не ділиться націло на chunksPerBlockSide.
    int ChunkStart(int k)
    {
        int b = FloorDiv(k, n);
        int i = k - b * n;
        return b * blockV + (i * blockV) / n;
    }

    int ChunkIndexOf(int g)
    {
        int b = FloorDiv(g, blockV);
        int l = g - b * blockV;
        int k = b * n + Mathf.Min(n - 1, (l * n) / blockV);
        while (g < ChunkStart(k)) k--;
        while (g >= ChunkStart(k + 1)) k++;
        return k;
    }

    Vector3 ToLocal(Vector3 worldPos) => world.transform.InverseTransformPoint(worldPos);

    float ViewRadius => Mathf.Max(viewRadius, rock != null ? rock.Radius * viewRadiusInRockRadii : 0f);

    // ---------- підвантаження ----------

    void Stream()
    {
        Vector3 local = ToLocal(rock.WorldCenter);
        float rView = ViewRadius;
        int rv = Mathf.CeilToInt(rView / vs);
        int gx = Mathf.FloorToInt(local.x / vs), gz = Mathf.FloorToInt(local.z / vs);

        int kx0 = ChunkIndexOf(gx - rv), kx1 = ChunkIndexOf(gx + rv);
        int kz0 = ChunkIndexOf(gz - rv), kz1 = ChunkIndexOf(gz + rv);
        Vector2 focus = new Vector2(local.x, local.z);

        for (int built = 0; built < Mathf.Max(1, chunksPerScan); built++)
        {
            Vector2Int best = default;
            float bestD = float.MaxValue;
            for (int kz = kz0; kz <= kz1; kz++)
            for (int kx = kx0; kx <= kx1; kx++)
            {
                var key = new Vector2Int(kx, kz);
                if (chunks.ContainsKey(key)) continue;
                if (!city.IsBlockDone(FloorDiv(kx, n), FloorDiv(kz, n))) continue;

                Vector2 c = new Vector2((ChunkStart(kx) + ChunkStart(kx + 1)) * 0.5f * vs,
                                        (ChunkStart(kz) + ChunkStart(kz + 1)) * 0.5f * vs);
                float d = Vector2.Distance(c, focus);
                if (d > rView) continue;
                if (d < bestD) { bestD = d; best = key; }
            }
            if (bestD == float.MaxValue) break;
            chunks[best] = BuildChunk(best.x, best.y);
        }

        // вивантажуємо далекі чанки
        float unload = rView + blockV * vs / n * 1.5f;
        removeBuf.Clear();
        foreach (var kv in chunks)
            if (Vector2.Distance(kv.Value.centerXZ, focus) > unload) removeBuf.Add(kv.Key);
        foreach (var key in removeBuf)
        {
            recoveringSet.Remove(chunks[key]);
            chunks.Remove(key);
        }
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

    bool IsRoad(int gx, int gz)
    {
        int lx = Mod(gx, blockV), lz = Mod(gz, blockV);
        int r = roadV + Mathf.Max(0, roadMarginCells);
        return lx < r || lz < r;
    }

    bool Occupied(int gx, int gz)
    {
        return world.GetVoxel(new Vector3Int(gx, 0, gz)) != 0 || world.GetVoxel(new Vector3Int(gx, 1, gz)) != 0;
    }

    Chunk BuildChunk(int kx, int kz)
    {
        int x0 = ChunkStart(kx), x1 = ChunkStart(kx + 1);
        int z0 = ChunkStart(kz), z1 = ChunkStart(kz + 1);
        var rng = new System.Random(Hash(seed, kx, kz));
        int bands = Mathf.Clamp(colorBands, 1, tempBlades.Length);
        for (int i = 0; i < bands; i++) tempBlades[i].Clear();

        int attempts = Mathf.RoundToInt((x1 - x0) * (z1 - z0) * Mathf.Max(0f, bladesPerCell));
        float hMin = Mathf.Min(heightMinVoxels, heightMaxVoxels) * vs;
        float hMax = Mathf.Max(heightMinVoxels, heightMaxVoxels) * vs;
        float width = widthVoxels * vs;

        for (int i = 0; i < attempts; i++)
        {
            // усі випадкові числа беремо наперед, щоб те саме місце давало ту саму траву
            float fx = x0 + (float)rng.NextDouble() * (x1 - x0);
            float fz = z0 + (float)rng.NextDouble() * (z1 - z0);
            float yaw = (float)rng.NextDouble() * 360f;
            float h = Mathf.Lerp(hMin, hMax, (float)rng.NextDouble());
            float w = width * Mathf.Lerp(0.8f, 1.2f, (float)rng.NextDouble());
            int band = rng.Next(bands);

            int gx = Mathf.FloorToInt(fx), gz = Mathf.FloorToInt(fz);
            if (IsRoad(gx, gz)) continue;
            if (Occupied(gx, gz)) continue;

            tempBlades[band].Add(new Blade
            {
                pos = world.transform.TransformPoint(new Vector3(fx * vs, groundY, fz * vs)),
                yaw = yaw, h = h, w = w, bend = 0f, lx = 1f, lz = 0f
            });
        }

        var chunk = new Chunk { bands = new Band[bands], windStamp = windVersion };
        for (int i = 0; i < bands; i++)
        {
            var list = tempBlades[i];
            var band = new Band { b = list.ToArray(), count = list.Count };
            band.m = new Matrix4x4[band.count];
            for (int j = 0; j < band.count; j++) band.m[j] = MakeMatrix(band.b[j]);
            chunk.bands[i] = band;
        }

        Vector3 wMin = world.transform.TransformPoint(new Vector3(x0 * vs, groundY, z0 * vs));
        Vector3 wMax = world.transform.TransformPoint(new Vector3(x1 * vs, groundY + hMax, z1 * vs));
        chunk.bounds = new Bounds((wMin + wMax) * 0.5f, wMax - wMin);
        chunk.bounds.Expand(new Vector3(hMax * 2f, hMax, hMax * 2f));   // нахилена травинка виступає вбік
        chunk.centerXZ = new Vector2((x0 + x1) * 0.5f * vs, (z0 + z1) * 0.5f * vs);
        return chunk;
    }

    // ---------- нахил ----------

    // Поворот = нахил від вітру * нахил від каменя * власний yaw. Притиснута травинка вітру не слухає.
    Matrix4x4 MakeMatrix(in Blade b)
    {
        Quaternion rot = Quaternion.Euler(0f, b.yaw, 0f);
        float t = b.bend;
        if (t > 0.001f)
        {
            float angle = maxBendAngle * (t * t * (3f - 2f * t));
            // вісь нахилу перпендикулярна напрямку (lx, lz): так травинка хилиться саме туди
            rot = Quaternion.AngleAxis(angle, new Vector3(b.lz, 0f, -b.lx)) * rot;
        }
        rot = WindTilt(b.pos, 1f - t) * rot;
        return Matrix4x4.TRS(b.pos, rot, new Vector3(b.w, b.h * (1f - 0.12f * t), b.w));
    }

    void SimulateBlades(float dt)
    {
        Vector3 rc = rock.WorldCenter;
        float R = rock.Radius;

        Vector3 vel = rockBody != null ? rockBody.linearVelocity : Vector3.zero;
        Vector2 vel2 = new Vector2(vel.x, vel.z);
        float speed = vel2.magnitude;
        Vector2 velDir = speed > 0.3f ? vel2 / speed : Vector2.zero;
        float speedK = Mathf.Clamp01(speed / Mathf.Max(0.1f, fullWakeSpeed));

        // чанки, які зачіпає камінь
        float maxH = Mathf.Max(heightMinVoxels, heightMaxVoxels) * vs;
        float reach = R * (1f + wakeWidth) + maxH;
        Vector3 local = ToLocal(rc);
        int rv = Mathf.CeilToInt(reach / vs);
        int gx = Mathf.FloorToInt(local.x / vs), gz = Mathf.FloorToInt(local.z / vs);
        int kx0 = ChunkIndexOf(gx - rv), kx1 = ChunkIndexOf(gx + rv);
        int kz0 = ChunkIndexOf(gz - rv), kz1 = ChunkIndexOf(gz + rv);

        bool rockNearGround = rc.y - R < groundY + maxH + R;   // камінь не літає високо над травою

        if (rockNearGround)
        {
            for (int kz = kz0; kz <= kz1; kz++)
            for (int kx = kx0; kx <= kx1; kx++)
            {
                if (!chunks.TryGetValue(new Vector2Int(kx, kz), out Chunk c)) continue;
                ProcessChunk(c, true, rc, R, velDir, speedK, dt);
                if (c.bentCount > 0) recoveringSet.Add(c);
            }
        }

        // решта чанків зі слідом лише розпрямляється
        if (recoveringSet.Count > 0)
        {
            List<Chunk> done = null;
            foreach (var c in recoveringSet)
            {
                if (c.lastFrame != Time.frameCount) ProcessChunk(c, false, rc, R, velDir, speedK, dt);
                if (c.bentCount == 0)
                {
                    if (done == null) done = new List<Chunk>();
                    done.Add(c);
                }
            }
            if (done != null) foreach (var c in done) recoveringSet.Remove(c);
        }
    }

    void ProcessChunk(Chunk c, bool rockNear, Vector3 rc, float R, Vector2 velDir, float speedK, float dt)
    {
        c.lastFrame = Time.frameCount;
        int bent = 0;
        float recoverStep = recoverTime > 0f ? dt / recoverTime : 0f;
        float pressStep = pressSpeed * dt;

        for (int bi = 0; bi < c.bands.Length; bi++)
        {
            Band band = c.bands[bi];
            for (int i = 0; i < band.count; i++)
            {
                ref Blade b = ref band.b[i];
                float target = 0f;
                Vector2 dir = default;

                if (rockNear)
                {
                    float dx = b.pos.x - rc.x, dz = b.pos.z - rc.z;
                    float d2 = dx * dx + dz * dz;
                    float reach = R * (1f + wakeWidth);
                    if (d2 < reach * reach)
                    {
                        float d = Mathf.Sqrt(d2);
                        float dyTip = rc.y - (b.pos.y + b.h);   // висота центру каменя над верхівкою травинки
                        if (dyTip < R)
                        {
                            // радіус, у якому поверхня кулі нижча за верхівку травинки
                            float fr = Mathf.Sqrt(Mathf.Max(0f, R * R - dyTip * dyTip));
                            if (d <= fr)
                            {
                                float s = Mathf.Sqrt(Mathf.Max(0f, R * R - d2));
                                float gap = rc.y - s - b.pos.y;                 // просвіт між землею й каменем
                                float press = Mathf.Clamp01(1f - gap / Mathf.Max(0.001f, b.h));
                                target = Mathf.Lerp(wakeBend, 1f, press);
                            }
                            else
                            {
                                float wr = Mathf.Max(0.001f, R * wakeWidth);
                                if (d < fr + wr)
                                {
                                    float f = 1f - (d - fr) / wr;
                                    target = wakeBend * f * f * Mathf.Lerp(0.4f, 1f, speedK);
                                }
                            }
                        }

                        if (target > 0f)
                        {
                            Vector2 radial = d > 0.001f ? new Vector2(dx, dz) / d : Vector2.zero;
                            dir = radial + velDir * (travelInfluence * speedK);
                            if (dir.sqrMagnitude < 1e-6f) dir = velDir.sqrMagnitude > 0f ? velDir : new Vector2(b.lx, b.lz);
                            dir.Normalize();
                        }
                    }
                }

                bool changed = false;
                if (target > b.bend + 0.001f)
                {
                    if (b.bend < 0.02f)
                    {
                        b.lx = dir.x; b.lz = dir.y;
                    }
                    else
                    {
                        // вже притиснута: напрям міняється плавно, щоб слід не смикався
                        Vector2 mix = Vector2.Lerp(new Vector2(b.lx, b.lz), dir, 0.35f);
                        if (mix.sqrMagnitude > 1e-6f) { mix.Normalize(); b.lx = mix.x; b.lz = mix.y; }
                    }
                    b.bend = Mathf.MoveTowards(b.bend, target, pressStep);
                    changed = true;
                }
                else if (b.bend > 0f && recoverStep > 0f)
                {
                    b.bend = Mathf.Max(0f, b.bend - recoverStep);
                    changed = true;
                }

                if (changed) band.m[i] = MakeMatrix(b);
                if (b.bend > 0f) bent++;
            }
        }
        c.bentCount = bent;
    }

    // Перераховує матриці всіх травинок чанка з поточним вітром (лише для чанків у кадрі)
    void RefreshWind(Chunk c)
    {
        for (int bi = 0; bi < c.bands.Length; bi++)
        {
            Band band = c.bands[bi];
            for (int i = 0; i < band.count; i++)
                band.m[i] = MakeMatrix(band.b[i]);
        }
        c.windStamp = windVersion;
    }

    // ---------- малювання ----------

    void RenderChunks()
    {
        Camera cam = Camera.main;
        if (cam == null) return;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);

        foreach (var c in chunks.Values)
        {
            if (!GeometryUtility.TestPlanesAABB(planes, c.bounds)) continue;
            if (windEnabled && c.windStamp != windVersion) RefreshWind(c);

            for (int bi = 0; bi < c.bands.Length; bi++)
            {
                Band band = c.bands[bi];
                if (band.count == 0) continue;
                var rp = new RenderParams(materials[bi])
                {
                    shadowCastingMode = ShadowCastingMode.Off,
                    receiveShadows = receiveShadows,
                    layer = gameObject.layer,
                    worldBounds = c.bounds
                };
                Graphics.RenderMeshInstanced(rp, bladeMesh, 0, band.m, band.count);
            }
        }
    }
}