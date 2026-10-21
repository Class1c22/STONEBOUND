using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Воксельна трава рівня 1: нескінченне поле навколо каменя, травинки розміром з дерева.
// - поле генерується чанками навколо каменя і підвантажується далі, куди б він не їхав (без кінця);
//   чанки позаду вивантажуються. Той самий seed = та сама трава в тому самому місці;
// - трава росте лужайками (карта щільності на шумі), між ними рідка трава й проплішини;
// - травинка = стовпчик із кубів, малюється GPU-інстансингом без кастомного шейдера (матеріали URP/Lit);
// - камінь ламає травинку як воксельний світ: вибиває лише ті куби, яких торкнувся (вони розсипаються
//   на кубики-уламки), нижній пеньок лишається стояти, а верхня частина падає окремим шматком із фізикою;
// - кубики-уламки (Rigidbody + Absorbable) камінь всмоктує по E (RockAbsorber.Suck) і росте.
public class GrassField : MonoBehaviour
{
    [Header("Масштаб")]
    [Tooltip("Камінь, від розміру якого рахується трава та уламки")]
    public RockAbsorber rock;
    public bool scaleFromRock = true;
    [Tooltip("Базовий розмір вокселя трави в радіусах каменя (було 2.5). Не чіпай, міняй множник нижче")]
    public float voxelSizeInRockRadii = 2.5f;
    [Tooltip("У скільки разів трава більша за базову: 5 = травинки як дерева. Нове поле, тому сцена не тримає стару цифру")]
    public float grassScale = 5f;
    [Tooltip("Розмір вокселя у метрах, якщо камінь не береться в розрахунок")]
    public float voxelSize = 6f;

    [Header("Нескінченне поле")]
    public int seed = 1;
    [Tooltip("Розмір чанка в клітинках сітки")]
    public int chunkCells = 10;
    [Tooltip("Скільки чанків навколо каменя тримати живими (радіус). Більше = далі видно, але важче")]
    public int viewChunks = 4;
    [Tooltip("Скільки нових чанків генерувати за кадр (перше заповнення йде одразу все)")]
    public int chunksPerFrame = 2;
    [Tooltip("Секунд очікування перед стартом (поки воксельний світ створить колайдери)")]
    public float startDelay = 0.5f;

    [Header("Лужайки (розстановка)")]
    [Tooltip("Розмір лужайки в клітинках сітки: більше = більші острови трави, менше = дрібні плями")]
    public float meadowSizeCells = 16f;
    [Tooltip("Яку частину карти займають лужайки (0.65 = 65%). Решта: рідка трава й проплішини")]
    [Range(0.05f, 0.95f)] public float meadowAmount = 0.65f;
    [Tooltip("Наскільки різкий край лужайки: 0 = різкий, 0.3 = плавно переходить у рідку траву")]
    [Range(0f, 0.4f)] public float meadowEdgeSoftness = 0.12f;
    [Tooltip("Частка клітинок із травою всередині лужайки (1 = суцільна)")]
    [Range(0f, 1f)] public float meadowDensity = 1f;
    [Tooltip("Частка клітинок із травою поза лужайками (0 = голі проплішини, 0.05 = подекуди кущики)")]
    [Range(0f, 0.5f)] public float sparseDensity = 0.12f;
    [Tooltip("У центрі лужайки в клітинці росте друга травинка, щоб було густо. 0 = ніколи, 1 = завжди")]
    [Range(0f, 1f)] public float meadowDoubleChance = 0.8f;
    [Tooltip("Зсув травинки в клітинці, частка клітинки: ламає рівну сітку")]
    [Range(0f, 0.9f)] public float placementJitter = 0.7f;
    [Tooltip("У центрі лужайки трава вища, по краях нижча (0 = висота випадкова)")]
    [Range(0f, 1f)] public float tallInCenter = 0.6f;

    [Header("Земля")]
    [Tooltip("Шукати висоту землі променем униз. Вимкни, якщо землі без колайдера: тоді трава стоїть на висоті цього об'єкта")]
    public bool useRaycast = true;
    public LayerMask groundMask = ~0;
    [Tooltip("З якої висоти над об'єктом кидати промінь вниз. Де землі вище цього (високі гори), трава не росте")]
    public float rayHeight = 60f;
    [Tooltip("Максимальний ухил поверхні, градусів")]
    [Range(0f, 90f)] public float maxSlope = 45f;

    [Header("Травинка (у вокселях)")]
    public int minVoxels = 6;
    public int maxVoxels = 10;
    [Tooltip("Товщина стовпчика, частка вокселя")]
    [Range(0.1f, 1f)] public float thickness = 0.6f;
    [Tooltip("Товщина верхнього кубика, частка вокселя")]
    [Range(0.1f, 1f)] public float tipThickness = 0.35f;

    [Header("Матеріали")]
    [Tooltip("Свої матеріали по смугах знизу вгору (Color Bands штук). Якщо порожньо, створяться автоматично")]
    public Material[] levelMaterials;
    [Range(1, 6)] public int colorBands = 3;
    public Color baseColor = new Color(0.10f, 0.30f, 0.08f);
    public Color tipColor = new Color(0.55f, 0.82f, 0.25f);
    public bool receiveShadows = true;

    [Header("Ламання")]
    [Tooltip("Камінь ламає траву, якої торкається")]
    public bool breakable = true;
    [Tooltip("Радіус ламання дотиком = радіус каменя × це число")]
    public float breakRadiusFactor = 1f;
    [Tooltip("Вимкнено (за замовчуванням): E трави не ламає, її ламає лише дотик/удар каменя. Увімкни, якщо хочеш, щоб E теж розсипала траву в зоні всмоктування")]
    public bool breakOnSuck = false;
    [Tooltip("Яка частка радіуса всмоктування (RockAbsorber.suckRadius) руйнує траву по E (якщо Break On Suck увімкнено)")]
    [Range(0.1f, 1f)] public float suckBreakFactor = 0.8f;

    [Header("Травинка ламається частинами")]
    [Tooltip("Увімкнено: вибиваються лише куби, яких торкнувся камінь, нижня частина лишається пеньком, верхня падає. Вимкнено: травинка зникає ціла")]
    public bool cutBlades = true;
    [Tooltip("Скільки кубів ВИЩЕ місця удару додатково вибивається при найсильнішому ударі (слабкий удар = 0)")]
    public int extraLevelsAtFullDamage = 1;
    [Tooltip("Верхня частина, що відламалась, падає фізичним шматком. Вимкнено: вона розсипається на кубики")]
    public bool fallingPieces = true;
    [Tooltip("Маса падаючого шматка відносно маси каменя (менше = легше перекинути)")]
    public float pieceMassInRocks = 0.4f;
    [Tooltip("Наскільки сильно шматок валиться від каменя (рад/с)")]
    public float pieceTipSpeed = 1.2f;
    [Tooltip("Яку частку швидкості каменя шматок перейме")]
    [Range(0f, 1f)] public float pieceKick = 0.4f;
    [Tooltip("Скільки шматків одночасно живе (найстаріші зникають)")]
    public int maxPieces = 40;
    [Tooltip("Скільки секунд лежить шматок, потім зникає")]
    public float pieceLifetime = 25f;
    [Tooltip("Скільки шматків може створитись за один кадр. Решта верхівок розсиплеться на кубики")]
    public int maxPiecesPerHit = 8;

    [Header("Сила удару -> кількість частинок")]
    [Tooltip("Швидкість каменя (м/с), при якій удар вважається найсильнішим")]
    public float fullDamageSpeed = 25f;
    [Tooltip("Скільки кубиків дає уражена частина травинки при найслабшому дотику")]
    public int minCubesPerBlade = 1;
    [Tooltip("Скільки кубиків дає уражена частина травинки при найсильнішому ударі")]
    public int maxCubesPerBlade = 10;
    [Tooltip("Сила (0..1), коли трава руйнується по E. Не залежить від швидкості каменя")]
    [Range(0f, 1f)] public float suckDamage = 0.4f;
    [Tooltip("Усього кубиків за один кадр на всі травинки разом. Це головний захист від мільярда частинок")]
    public int maxCubesPerHit = 60;
    [Tooltip("Якщо кубиків мало, вони більші (об'єм зберігається). Найбільше збільшення ребра")]
    public float maxCubeGrow = 2.5f;

    [Header("Кубики")]
    [Tooltip("Ребро кубика = початковий радіус каменя × це число (кубик має бути досить малий, щоб камінь міг його всмоктати)")]
    public float debrisSizeInRockRadii = 0.45f;
    [Tooltip("Швидкість розльоту")]
    public float debrisSpread = 4f;
    [Tooltip("Розмір пулу кубиків (найстаріші використовуються повторно, як у VoxelWorld)")]
    public int maxDebris = 600;
    [Tooltip("Стеля нових кубиків за кадр (захист від просадки FPS)")]
    public int maxDebrisPerFrame = 120;
    [Tooltip("Множник об'єму, який кубик додає каменю при всмоктуванні (1 = ребро в кубі)")]
    public float absorbVolumeFactor = 1f;
    public bool debris = true;

    // Скільки травинок зламано за кадр (для звуку, ефектів)
    public event System.Action<int> BladesBroken;
    public int TotalBroken { get; private set; }

    // Одна травинка: H = початкова висота, s = скільки кубів лишилось знизу (пеньок)
    struct Blade
    {
        public Vector3 pos;
        public float yaw;
        public int H, s;
    }

    // Частина травинки, що розсипається на кубики
    struct BrokenPart
    {
        public Vector3 basePos;   // низ травинки
        public float yaw;
        public int start, count, H;
    }

    class Batch
    {
        public Mesh mesh;
        public Matrix4x4[] matrices;
        public int count;
    }

    class Chunk
    {
        public bool empty = true;
        public bool dirty = true;
        public Bounds bounds;
        public List<Blade> blades = new List<Blade>();
        public List<Batch> batches = new List<Batch>();
    }

    const int MaxBatch = 1023;

    readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    readonly List<Vector2Int> pending = new List<Vector2Int>();
    readonly List<Vector2Int> removeKeys = new List<Vector2Int>();
    Vector2Int lastCenter;
    bool streamInit, initialFill, built;

    int[] variantHeights;          // висота кожного варіанта травинки у вокселях
    readonly Dictionary<int, Mesh> meshCache = new Dictionary<int, Mesh>();
    readonly Dictionary<int, List<Matrix4x4>> groupTemp = new Dictionary<int, List<Matrix4x4>>();
    Material[] materials;
    float vs;                      // поточний розмір вокселя трави
    float findTimer;
    Plane[] planes = new Plane[6];

    Rigidbody[] debrisPool;
    int debrisIndex, spawnedThisFrame, piecesThisFrame;
    Transform debrisRoot;
    readonly Queue<GameObject> pieces = new Queue<GameObject>();
    static readonly RaycastHit[] hitBuffer = new RaycastHit[16];
    readonly List<BrokenPart> brokenParts = new List<BrokenPart>();

    IEnumerator Start()
    {
        if (startDelay > 0f) yield return new WaitForSecondsRealtime(startDelay);
        else yield return null;
        Physics.SyncTransforms();
        Setup();
    }

    // Значення полів зберігаються в сцені, тому нові стандартні цифри самі не підтягнуться.
    // Натисни ⋮ компонента -> цей пункт, щоб виставити густу траву, потім Rebuild.
    [ContextMenu("Густа трава (скинути лужайки)")]
    void ResetToLushGrass()
    {
        meadowSizeCells = 16f;
        meadowAmount = 0.65f;
        meadowEdgeSoftness = 0.12f;
        meadowDensity = 1f;
        sparseDensity = 0.12f;
        meadowDoubleChance = 0.8f;
        viewChunks = 4;
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    [ContextMenu("Rebuild")]
    public void Setup()
    {
        minVoxels = Mathf.Max(1, minVoxels);
        maxVoxels = Mathf.Max(minVoxels, maxVoxels);
        chunkCells = Mathf.Max(1, chunkCells);
        vs = ResolveVoxelSize();

        PrepareMaterials();

        int variants = Mathf.Clamp(maxVoxels - minVoxels + 1, 1, 4);
        variantHeights = new int[variants];
        for (int k = 0; k < variants; k++)
        {
            float t = variants == 1 ? 0f : k / (float)(variants - 1);
            variantHeights[k] = Mathf.RoundToInt(Mathf.Lerp(minVoxels, maxVoxels, t));
        }
        meshCache.Clear();

        chunks.Clear();
        pending.Clear();
        streamInit = false;
        initialFill = true;
        built = true;

        Debug.Log("[GrassField] Voxel Size = " + vs.ToString("0.##") + " м, висота травинки " +
                  (minVoxels * vs).ToString("0") + "-" + (maxVoxels * vs).ToString("0") + " м.", this);
    }

    float ResolveVoxelSize()
    {
        // після переходу між сценами заглушка каменя знищується, тож шукаємо актуальний
        if (scaleFromRock && rock == null) rock = FindFirstObjectByType<RockAbsorber>();
        if (scaleFromRock && rock != null)
            return Mathf.Max(0.01f, rock.StartRadius * voxelSizeInRockRadii * grassScale);
        return Mathf.Max(0.01f, voxelSize);
    }

    void PrepareMaterials()
    {
        materials = new Material[colorBands];
        Shader lit = null;

        for (int i = 0; i < colorBands; i++)
        {
            if (levelMaterials != null && i < levelMaterials.Length && levelMaterials[i] != null)
            {
                materials[i] = levelMaterials[i];
            }
            else
            {
                if (lit == null)
                {
                    lit = Shader.Find("Universal Render Pipeline/Lit");
                    if (lit == null)
                    {
                        Debug.LogError("[GrassField] Не знайдено URP/Lit. Призначте матеріали вручну в Level Materials.", this);
                        lit = Shader.Find("Standard");
                    }
                }
                var m = new Material(lit) { name = "GrassBand" + i };
                Color c = Color.Lerp(baseColor, tipColor, colorBands > 1 ? (float)i / (colorBands - 1) : 1f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                materials[i] = m;
            }
            materials[i].enableInstancing = true;
        }
    }

    // ---------- нескінченне підвантаження ----------

    Vector3 StreamFocus()
    {
        if (rock == null)
        {
            findTimer -= Time.deltaTime;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                rock = FindFirstObjectByType<RockAbsorber>();
            }
        }
        if (rock != null) return rock.WorldCenter;
        Camera cam = Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    void UpdateStreaming()
    {
        Vector3 focus = StreamFocus();
        float cw = chunkCells * vs;
        var center = new Vector2Int(Mathf.FloorToInt(focus.x / cw), Mathf.FloorToInt(focus.z / cw));

        if (!streamInit || center != lastCenter)
        {
            RefreshPending(center);
            lastCenter = center;
            streamInit = true;
        }

        int budget = initialFill ? int.MaxValue : Mathf.Max(1, chunksPerFrame);
        while (budget > 0 && pending.Count > 0)
        {
            Vector2Int key = pending[0];
            pending.RemoveAt(0);
            if (chunks.ContainsKey(key)) continue;
            chunks[key] = GenerateChunk(key.x, key.y);
            budget--;
        }
        if (pending.Count == 0) initialFill = false;
    }

    // Вивантажує чанки, що лишились позаду, і складає чергу нових (ближчі першими)
    void RefreshPending(Vector2Int center)
    {
        float keep = viewChunks + 1.5f;
        removeKeys.Clear();
        foreach (var kv in chunks)
        {
            float dx = kv.Key.x - center.x, dz = kv.Key.y - center.y;
            if (dx * dx + dz * dz > keep * keep) removeKeys.Add(kv.Key);
        }
        foreach (var k in removeKeys) chunks.Remove(k);

        pending.Clear();
        float want = viewChunks + 0.5f;
        for (int dz = -viewChunks - 1; dz <= viewChunks + 1; dz++)
        for (int dx = -viewChunks - 1; dx <= viewChunks + 1; dx++)
        {
            if (dx * dx + dz * dz > want * want) continue;
            var key = new Vector2Int(center.x + dx, center.y + dz);
            if (!chunks.ContainsKey(key)) pending.Add(key);
        }
        pending.Sort((a, b) =>
        {
            float da = (a.x - center.x) * (a.x - center.x) + (a.y - center.y) * (a.y - center.y);
            float db = (b.x - center.x) * (b.x - center.x) + (b.y - center.y) * (b.y - center.y);
            return da.CompareTo(db);
        });
    }

    bool SampleGround(float x, float z, float minNormalY, out float y)
    {
        y = transform.position.y;
        if (!useRaycast) return true;

        Vector3 from = new Vector3(x, transform.position.y + rayHeight, z);
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hitBuffer, rayHeight * 2f, groundMask, QueryTriggerInteraction.Ignore);

        // найближче влучання, що не є рухомим тілом (уламки, камінь, шматки не рахуємо землею)
        float best = float.MaxValue, normalY = 0f;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hitBuffer[i];
            if (h.collider.attachedRigidbody != null) continue;
            if (h.distance < best)
            {
                best = h.distance;
                y = h.point.y;
                normalY = h.normal.y;
                found = true;
            }
        }
        return found && normalY >= minNormalY;
    }

    // 0 = поза лужайкою (рідка трава чи проплішина), 1 = центр лужайки. Два шари шуму: великі острови + рваний край
    float MeadowMask(int ix, int iz)
    {
        float size = Mathf.Max(2f, meadowSizeCells);
        float o = 5000f + seed * 13.37f;
        float big = Mathf.PerlinNoise(ix / size + o, iz / size + o * 0.7f);
        float fine = Mathf.PerlinNoise(ix / (size * 0.3f) + o + 50f, iz / (size * 0.3f) + o * 0.7f + 50f);
        float n = big * 0.8f + fine * 0.2f;

        // meadowAmount -> поріг (перлін зосереджений навколо 0.5, тож поріг ходить у вузьких межах)
        float threshold = Mathf.Lerp(0.66f, 0.36f, meadowAmount);
        float soft = Mathf.Max(0.001f, meadowEdgeSoftness * 0.5f);
        float edge = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(threshold - soft, threshold + soft, n));

        // глибина всередині лужайки: у центрі 1, на краю 0 (для висоти й другої травинки)
        float depth = Mathf.Clamp01((n - threshold) / 0.18f);
        return edge * Mathf.Lerp(1f, depth, 0.7f);
    }

    void TryPlaceBlade(Chunk chunk, int ix, int iz, float jx, float jz, float yaw, float u, float m,
                       float minNormalY, ref float minY, ref float maxY)
    {
        int variants = variantHeights.Length;

        float px = (ix + 0.5f + (jx - 0.5f) * placementJitter) * vs;
        float pz = (iz + 0.5f + (jz - 0.5f) * placementJitter) * vs;
        if (!SampleGround(px, pz, minNormalY, out float py)) return;
        py -= vs * 0.1f;   // трохи в землю, щоб не було щілин

        // варіант висоти: у центрі лужайки вища, по краях і в рідкій траві нижча
        float t = Mathf.Lerp(u, Mathf.Lerp(0.15f * u, 1f, m), tallInCenter);
        int variant = Mathf.Clamp(Mathf.FloorToInt(t * variants), 0, variants - 1);
        int H = variantHeights[variant];

        chunk.blades.Add(new Blade { pos = new Vector3(px, py, pz), yaw = yaw, H = H, s = H });
        minY = Mathf.Min(minY, py);
        maxY = Mathf.Max(maxY, py + H * vs);
    }

    Chunk GenerateChunk(int cx, int cz)
    {
        var chunk = new Chunk();
        float minNormalY = Mathf.Cos(maxSlope * Mathf.Deg2Rad);

        var rng = new System.Random(seed * 73856093 ^ cx * 19349663 ^ cz * 83492791);
        float minY = float.MaxValue, maxY = float.MinValue;

        for (int lx = 0; lx < chunkCells; lx++)
        for (int lz = 0; lz < chunkCells; lz++)
        {
            int ix = cx * chunkCells + lx;
            int iz = cz * chunkCells + lz;

            // порядок викликів rng сталий (завжди тягнемо всі числа), тож результат детермінований
            double roll = rng.NextDouble();
            double roll2 = rng.NextDouble();
            float u1 = (float)rng.NextDouble(), u2 = (float)rng.NextDouble();
            float j1x = (float)rng.NextDouble(), j1z = (float)rng.NextDouble();
            float j2x = (float)rng.NextDouble(), j2z = (float)rng.NextDouble();
            float yaw1 = (float)rng.NextDouble() * 360f, yaw2 = (float)rng.NextDouble() * 360f;

            // маска лужайки 0..1 в цій клітинці (1 = центр лужайки)
            float m = MeadowMask(ix, iz);
            float chance = Mathf.Lerp(sparseDensity, meadowDensity, m);
            if (roll > chance) continue;

            TryPlaceBlade(chunk, ix, iz, j1x, j1z, yaw1, u1, m, minNormalY, ref minY, ref maxY);

            // густа лужайка: друга травинка в тій самій клітинці
            if (m > 0.6f && roll2 < meadowDoubleChance * Mathf.InverseLerp(0.6f, 1f, m))
                TryPlaceBlade(chunk, ix, iz, j2x, j2z, yaw2, u2, m, minNormalY, ref minY, ref maxY);
        }

        if (chunk.blades.Count == 0) return chunk;   // порожній чанк теж запам'ятовуємо, щоб не перераховувати

        float cw = chunkCells * vs;
        chunk.empty = false;
        chunk.bounds = new Bounds(
            new Vector3((cx + 0.5f) * cw, (minY + maxY) * 0.5f, (cz + 0.5f) * cw),
            new Vector3(cw + vs, maxY - minY + vs, cw + vs));
        return chunk;
    }

    // ---------- ламання ----------

    // Ламає травинки, яких торкається камінь (або які в зоні всмоктування при E, якщо це ввімкнено).
    // Зачеплені куби розсипаються на кубики, нижній пеньок лишається, верх падає шматком.
    void BreakAroundRock()
    {
        if (rock == null) return;

        Vector3 c = rock.WorldCenter;
        float r = rock.Radius * breakRadiusFactor;

        var kb = Keyboard.current;
        bool sucking = breakOnSuck && kb != null && kb.eKey.isPressed;
        if (sucking) r = Mathf.Max(r, rock.suckRadius * rock.SizeRatio * suckBreakFactor);
        float r2 = r * r;

        Vector3 rockVel = Vector3.zero;
        var rockBody = rock.GetComponent<Rigidbody>();
        if (rockBody != null) rockVel = rockBody.linearVelocity;

        // сила удару: від швидкості каменя, а по E не нижче suckDamage
        float damage = Mathf.Clamp01(rockVel.magnitude / Mathf.Max(0.01f, fullDamageSpeed));
        if (sucking) damage = Mathf.Max(damage, suckDamage);
        int extra = Mathf.RoundToInt(damage * Mathf.Max(0, extraLevelsAtFullDamage));

        float half = thickness * vs * 0.5f;

        brokenParts.Clear();
        int broken = 0;
        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty || chunk.bounds.SqrDistance(c) > r2) continue;

            var list = chunk.blades;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Blade b = list[i];
                if (b.s <= 0) { list.RemoveAt(i); continue; }

                // швидка перевірка: найближча точка стовпчика до центру каменя
                Vector3 q = ClosestOnBox(c, b.pos.x - half, b.pos.y, b.pos.z - half, b.pos.x + half, b.pos.y + b.s * vs, b.pos.z + half);
                if ((c - q).sqrMagnitude > r2) continue;

                // які кубі стовпчика зачеплені
                int lo = -1, hi = -1;
                for (int lvl = 0; lvl < b.s; lvl++)
                {
                    float h2 = (lvl == b.H - 1 && b.H > 1 ? tipThickness : thickness) * vs * 0.5f;
                    Vector3 qq = ClosestOnBox(c, b.pos.x - h2, b.pos.y + lvl * vs, b.pos.z - h2, b.pos.x + h2, b.pos.y + (lvl + 1) * vs, b.pos.z + h2);
                    if ((c - qq).sqrMagnitude > r2) continue;
                    if (lo < 0) lo = lvl;
                    hi = lvl;
                }
                if (lo < 0) continue;

                if (!cutBlades)
                {
                    // стара поведінка: травинка зникає ціла
                    if (debris) brokenParts.Add(new BrokenPart { basePos = b.pos, yaw = b.yaw, start = 0, count = b.s, H = b.H });
                    list.RemoveAt(i);
                    chunk.dirty = true;
                    broken++;
                    continue;
                }

                hi = Mathf.Min(b.s - 1, hi + extra);

                // 1) зачеплена частина розсипається на кубики
                if (debris) brokenParts.Add(new BrokenPart { basePos = b.pos, yaw = b.yaw, start = lo, count = hi - lo + 1, H = b.H });

                // 2) верхня частина падає шматком (або розсипається, якщо шматків забагато)
                int upper = b.s - 1 - hi;
                if (upper > 0)
                {
                    if (fallingPieces && piecesThisFrame < maxPiecesPerHit)
                    {
                        piecesThisFrame++;
                        SpawnPiece(b, hi + 1, upper, c, rockVel);
                    }
                    else if (debris)
                    {
                        brokenParts.Add(new BrokenPart { basePos = b.pos, yaw = b.yaw, start = hi + 1, count = upper, H = b.H });
                    }
                }

                // 3) нижня частина лишається пеньком (або зникає, якщо зачеплено найнижчий куб)
                if (lo > 0)
                {
                    b.s = lo;
                    list[i] = b;
                }
                else
                {
                    list.RemoveAt(i);
                }
                chunk.dirty = true;
                broken++;
            }
        }

        if (broken > 0)
        {
            if (debris) SpawnForParts(damage, c, rockVel);
            TotalBroken += broken;
            BladesBroken?.Invoke(broken);
        }
    }

    static Vector3 ClosestOnBox(Vector3 p, float x0, float y0, float z0, float x1, float y1, float z1)
    {
        return new Vector3(Mathf.Clamp(p.x, x0, x1), Mathf.Clamp(p.y, y0, y1), Mathf.Clamp(p.z, z0, z1));
    }

    // ---------- падаючий шматок травинки ----------

    void SpawnPiece(Blade b, int startLevel, int count, Vector3 rockCenter, Vector3 rockVel)
    {
        EnsureRoot();

        // не тримаємо більше maxPieces шматків: найстаріші зникають
        while (pieces.Count >= Mathf.Max(1, maxPieces))
        {
            GameObject old = pieces.Dequeue();
            if (old != null) Destroy(old);
        }

        float w = thickness * vs;
        var go = new GameObject("GrassPiece");
        go.transform.SetParent(debrisRoot, false);
        // низ шматка трохи вище місця зламу, щоб він не застряг у пеньку і камені
        go.transform.SetPositionAndRotation(b.pos + Vector3.up * (startLevel * vs + vs * 0.02f), Quaternion.Euler(0f, b.yaw, 0f));

        go.AddComponent<MeshFilter>().sharedMesh = GetMesh(b.H, startLevel, count);
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterials = materials;
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = receiveShadows;

        var box = go.AddComponent<BoxCollider>();
        box.size = new Vector3(w, count * vs, w);
        box.center = new Vector3(0f, count * vs * 0.5f, 0f);

        var rb = go.AddComponent<Rigidbody>();
        float rockMass = 1f;
        var rockBody = rock != null ? rock.GetComponent<Rigidbody>() : null;
        if (rockBody != null) rockMass = rockBody.mass;
        rb.mass = Mathf.Max(0.05f, rockMass * pieceMassInRocks);
        rb.angularDamping = 0.5f;

        // валиться від каменя: горизонтальний напрямок від нього
        Vector3 away = go.transform.position - rockCenter;
        away.y = 0f;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : new Vector3(Random.value - 0.5f, 0f, Random.value - 0.5f).normalized;
        rb.linearVelocity = rockVel * pieceKick + away * 0.5f;
        rb.angularVelocity = Vector3.Cross(Vector3.up, away) * pieceTipSpeed + Random.insideUnitSphere * 0.2f;

        // великий шматок камінь не всмокче (не пройде за розміром), але більший камінь згодом зможе
        var a = go.AddComponent<Absorbable>();
        a.volume = count * w * w * vs * absorbVolumeFactor;

        pieces.Enqueue(go);
        Destroy(go, Mathf.Max(1f, pieceLifetime));
    }

    // ---------- уламки-кубики (як VoxelWorld.SpawnDebris) ----------

    float DebrisEdge()
    {
        float baseRadius = rock != null ? rock.StartRadius : vs * 0.08f;
        return Mathf.Max(0.02f, baseRadius * debrisSizeInRockRadii);
    }

    void EnsureRoot()
    {
        if (debrisRoot == null)
        {
            var root = new GameObject("GrassDebris");
            debrisRoot = root.transform;
        }
    }

    // Розподіляє бюджет кубиків на всі уражені частини, зламані цього кадру.
    // Слабкий удар = мало кубиків, сильний = більше, але разом не більше maxCubesPerHit.
    void SpawnForParts(float damage, Vector3 rockCenter, Vector3 rockVel)
    {
        int partCount = brokenParts.Count;
        if (partCount == 0) return;

        float perPart = Mathf.Lerp(minCubesPerBlade, Mathf.Max(minCubesPerBlade, maxCubesPerBlade), damage);
        float budget = Mathf.Min(maxCubesPerHit, maxDebrisPerFrame - spawnedThisFrame);
        perPart = Mathf.Min(perPart, budget / partCount);   // багато частин: кожна дає менше
        if (perPart <= 0f) { brokenParts.Clear(); return; }

        // скільки максимум кубиків могло б бути, щоб кубик ріс, коли їх мало (об'єм зберігається)
        float refCubes = Mathf.Max(1f, maxCubesPerBlade);

        for (int i = 0; i < partCount; i++)
        {
            int count = Mathf.FloorToInt(perPart);
            if (Random.value < perPart - count) count++;   // дробову частину кидаємо жеребом
            if (count <= 0) continue;

            float grow = Mathf.Clamp(Mathf.Pow(refCubes / count, 1f / 3f), 1f, maxCubeGrow);
            SpawnCubes(brokenParts[i], count, grow, rockCenter, rockVel);
        }
        brokenParts.Clear();
    }

    void SpawnCubes(BrokenPart part, int count, float grow, Vector3 rockCenter, Vector3 rockVel)
    {
        for (int k = 0; k < count; k++)
        {
            if (spawnedThisFrame >= maxDebrisPerFrame) return;
            spawnedThisFrame++;

            // випадкова точка всередині ураженої частини стовпчика
            int level = part.start + Random.Range(0, part.count);
            float half = (level == part.H - 1 && part.H > 1 ? tipThickness : thickness) * vs * 0.5f;
            Vector3 pos = part.basePos + new Vector3(
                Random.Range(-half, half),
                (level + Random.value) * vs,
                Random.Range(-half, half));
            int band = Mathf.Min(colorBands - 1, level * colorBands / part.H);

            // кубик не має з'являтись усередині каменя (інакше фізика його різко виштовхне)
            Vector3 dir = pos - rockCenter;
            float dist = dir.magnitude;
            dir = dist > 1e-4f ? dir / dist : Random.onUnitSphere;
            float minDist = rock != null ? rock.Radius * 1.1f : 0f;
            if (dist < minDist) pos = rockCenter + dir * minDist;

            Rigidbody d = GetDebris();
            d.transform.SetPositionAndRotation(pos, Random.rotation);
            d.linearVelocity = dir * Random.Range(1f, debrisSpread) + rockVel * 0.3f + Random.insideUnitSphere;
            d.angularVelocity = Random.insideUnitSphere * 5f;
            d.GetComponent<Renderer>().sharedMaterial = materials[band];

            // розмір і об'єм кубика (камінь росте від об'єму, тому він має відповідати розміру)
            float edge = DebrisEdge() * grow;
            d.transform.localScale = Vector3.one * edge;
            var ab = d.GetComponent<Absorbable>();
            if (ab != null) ab.volume = edge * edge * edge * absorbVolumeFactor;
        }
    }

    Rigidbody GetDebris()
    {
        if (debrisPool == null) debrisPool = new Rigidbody[Mathf.Max(1, maxDebris)];
        EnsureRoot();

        Rigidbody d = debrisPool[debrisIndex];
        if (d == null)
        {
            // порожній слот (ще не створювали або камінь уже всмоктав кубик): робимо новий
            float edge = DebrisEdge();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "GrassCube";
            cube.transform.SetParent(debrisRoot, false);
            cube.transform.localScale = Vector3.one * edge;
            d = cube.AddComponent<Rigidbody>();
            d.mass = 0.05f;
            var a = cube.AddComponent<Absorbable>();
            a.volume = edge * edge * edge * absorbVolumeFactor;
            debrisPool[debrisIndex] = d;
        }
        else
        {
            var a = d.GetComponent<Absorbable>();
            if (a != null) a.attached = false;
        }
        debrisIndex = (debrisIndex + 1) % debrisPool.Length;   // найстаріший кубик використовується повторно
        return d;
    }

    // ---------- малювання ----------

    void Update()
    {
        if (!built || materials == null || variantHeights == null) return;
        spawnedThisFrame = 0;
        piecesThisFrame = 0;

        UpdateStreaming();
        if (breakable) BreakAroundRock();

        Camera cam = Camera.main;
        if (cam == null) return;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);

        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, chunk.bounds)) continue;
            if (chunk.dirty) RebuildBatches(chunk);

            foreach (var b in chunk.batches)
            {
                if (b.count == 0) continue;
                for (int sub = 0; sub < b.mesh.subMeshCount; sub++)
                {
                    if (b.mesh.GetIndexCount(sub) == 0) continue;   // порожня смуга кольору (низький пеньок)
                    var rp = new RenderParams(materials[sub])
                    {
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = receiveShadows,
                        layer = gameObject.layer,
                        worldBounds = chunk.bounds
                    };
                    Graphics.RenderMeshInstanced(rp, b.mesh, sub, b.matrices, b.count);
                }
            }
        }
    }

    // Групує живі травинки чанка за формою (початкова висота + висота пенька) і складає матриці для GPU
    void RebuildBatches(Chunk chunk)
    {
        chunk.dirty = false;
        chunk.batches.Clear();

        foreach (var l in groupTemp.Values) l.Clear();
        foreach (var b in chunk.blades)
        {
            int key = b.H * 100 + b.s;
            if (!groupTemp.TryGetValue(key, out var l))
            {
                l = new List<Matrix4x4>();
                groupTemp[key] = l;
            }
            l.Add(Matrix4x4.TRS(b.pos, Quaternion.Euler(0f, b.yaw, 0f), Vector3.one * vs));
        }

        foreach (var kv in groupTemp)
        {
            var list = kv.Value;
            if (list.Count == 0) continue;
            Mesh mesh = GetMesh(kv.Key / 100, 0, kv.Key % 100);
            for (int s = 0; s < list.Count; s += MaxBatch)
            {
                int n = Mathf.Min(MaxBatch, list.Count - s);
                var arr = new Matrix4x4[n];
                list.CopyTo(s, arr, 0, n);
                chunk.batches.Add(new Batch { mesh = mesh, matrices = arr, count = n });
            }
        }
    }

    // ---------- меш травинки ----------

    // Меш частини стовпчика: куби з рівнями start..start+count-1 травинки висотою H. Низ меша на y = 0.
    // Кольори й тонкий верхній кубик рахуються за ПОЧАТКОВОЮ висотою H, тож пеньок і шматок мають ті самі кольори.
    Mesh GetMesh(int H, int start, int count)
    {
        int key = H * 10000 + start * 100 + count;
        if (meshCache.TryGetValue(key, out var mesh) && mesh != null) return mesh;
        mesh = BuildColumnMesh(H, start, count, thickness, tipThickness, colorBands);
        meshCache[key] = mesh;
        return mesh;
    }

    static Mesh BuildColumnMesh(int H, int start, int count, float thick, float tipThick, int bands)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>[bands];
        for (int b = 0; b < bands; b++) tris[b] = new List<int>();

        for (int i = start; i < start + count; i++)
        {
            float half = (i == H - 1 && H > 1 ? tipThick : thick) * 0.5f;
            int band = Mathf.Min(bands - 1, i * bands / H);
            AddCube(verts, normals, tris[band], -half, i - start, -half, half, i - start + 1, half);
        }

        var mesh = new Mesh { name = "VoxelBlade" + H + "_" + start + "_" + count };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.subMeshCount = bands;
        for (int b = 0; b < bands; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();
        return mesh;
    }

    static void AddCube(List<Vector3> v, List<Vector3> nrm, List<int> t,
                        float x0, float y0, float z0, float x1, float y1, float z1)
    {
        Face(v, nrm, t, new Vector3(1, 0, 0),
            new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1));
        Face(v, nrm, t, new Vector3(-1, 0, 0),
            new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0));
        Face(v, nrm, t, new Vector3(0, 1, 0),
            new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0));
        Face(v, nrm, t, new Vector3(0, 0, 1),
            new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1));
        Face(v, nrm, t, new Vector3(0, 0, -1),
            new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0));
    }

    static void Face(List<Vector3> v, List<Vector3> nrm, List<int> t, Vector3 normal,
                     Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int start = v.Count;
        v.Add(a); v.Add(b); v.Add(c); v.Add(d);
        for (int i = 0; i < 4; i++) nrm.Add(normal);
        t.Add(start); t.Add(start + 1); t.Add(start + 2);
        t.Add(start); t.Add(start + 2); t.Add(start + 3);
    }
}