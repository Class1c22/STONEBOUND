using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Воксельна трава рівня 1: нескінченне поле навколо каменя, травинки розміром з дерева.
// - поле генерується чанками навколо каменя і підвантажується далі, куди б він не їхав (без кінця);
//   чанки позаду вивантажуються. Той самий seed = та сама трава в тому самому місці;
// - трава росте лужайками (карта щільності на шумі), між ними рідка трава й проплішини;
// - ціла травинка = стовпчик із кубів, малюється GPU-інстансингом без кастомного шейдера (матеріали URP/Lit);
// - при ударі травинка стає дрібною воксельною сіткою, як стіна у VoxelWorld: камінь вибиває кулю вокселів
//   (видно дірку), решта травинки стоїть далі, а шматки, що відірвались від землі, падають з фізикою;
// - вибиті вокселі розлітаються кубиками (Rigidbody + Absorbable): камінь всмоктує їх по E (RockAbsorber.Suck) і росте;
// - BreakInSphere: траву в сфері ламають і падаючі шматки декору (WorldDecorator).
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

    [Tooltip("Не ставити траву на воксельні об'єкти (валуни, горби, будинки): ґрунтом вважається лише підлога. Трава під ними просто ховається")]
    public bool ignoreVoxelColliders = true;

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
    [Tooltip("Радіус ями, яку камінь вибиває = радіус каменя × це число")]
    public float breakRadiusFactor = 1f;
    [Tooltip("Вимкнено (за замовчуванням): E трави не ламає, її ламає лише дотик/удар каменя. Увімкни, якщо хочеш, щоб E теж розсипала траву в зоні всмоктування")]
    public bool breakOnSuck = false;
    [Tooltip("Яка частка радіуса всмоктування (RockAbsorber.suckRadius) руйнує траву по E (якщо Break On Suck увімкнено)")]
    [Range(0.1f, 1f)] public float suckBreakFactor = 0.8f;

    [Header("Травинка = воксельна сітка (дірки)")]
    [Tooltip("На скільки вокселів ділиться ширина травинки. Більше = менші дірки й точніший відлом, але важче. Нове поле")]
    [Range(2, 12)] public int bladeResolution = 6;
    [Tooltip("Наскільки більша яма при найсильнішому ударі (0.6 = на 60% ширша)")]
    public float radiusBoostAtFullDamage = 0.6f;
    [Tooltip("Шматок травинки, відірваний від землі, падає з фізикою. Вимкнено: він розсипається на кубики")]
    public bool fallingPieces = true;
    [Tooltip("Менші відірвані шматки (у вокселях) не падають, а розсипаються на кубики")]
    public int minPieceVoxels = 12;
    [Tooltip("Маса падаючого шматка відносно маси каменя (менше = легше перекинути)")]
    public float pieceMassInRocks = 0.4f;
    [Tooltip("Наскільки сильно шматок валиться від каменя (рад/с)")]
    public float pieceTipSpeed = 1.2f;
    [Tooltip("Яку частку швидкості каменя шматок перейме")]
    [Range(0f, 1f)] public float pieceKick = 0.4f;
    [Tooltip("Скільки шматків одночасно живе (найстарші зникають)")]
    public int maxPieces = 40;
    [Tooltip("Скільки секунд лежить шматок, потім зникає")]
    public float pieceLifetime = 25f;
    [Tooltip("Скільки шматків може створитись за один кадр. Решта розсиплеться на кубики")]
    public int maxPiecesPerHit = 8;

    [Header("Сила удару -> кількість частинок")]
    [Tooltip("Швидкість каменя (м/с), при якій удар вважається найсильнішим")]
    public float fullDamageSpeed = 25f;
    [Tooltip("Скільки кубиків дає одна уражена травинка при найслабшому дотику")]
    public int minCubesPerBlade = 1;
    [Tooltip("Скільки кубиків дає одна уражена травинка при найсильнішому ударі")]
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
    [Tooltip("Розмір пулу кубиків (найстарші використовуються повторно, як у VoxelWorld)")]
    public int maxDebris = 600;
    [Tooltip("Стеля нових кубиків за кадр (захист від просадки FPS)")]
    public int maxDebrisPerFrame = 120;
    [Tooltip("Множник об'єму, який кубик додає каменю при всмоктуванні (1 = ребро в кубі)")]
    public float absorbVolumeFactor = 1f;
    public bool debris = true;

    // Скільки травинок зламано за кадр (для звуку, ефектів)
    public event System.Action<int> BladesBroken;
    public int TotalBroken { get; private set; }

    // Одна травинка. Поки не вдарена, cells == null і вона малюється інстансингом.
    // Після удару cells = воксельна сітка (1 = є воксель), mesh = її власний меш.
    class Blade
    {
        public Vector3 pos;
        public float yaw;
        public int H;            // висота в «великих» вокселях
        public byte[] cells;
        public int count;        // скільки вокселів лишилось
        public Mesh mesh;
    }

    struct Cell
    {
        public Vector3 pos;
        public int band;
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
        public List<Blade> damaged = new List<Blade>();   // вдарені травинки: малюються по одній
        public List<Batch> batches = new List<Batch>();   // цілі травинки: інстансинг
    }

    const int MaxBatch = 1023;

    readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    readonly List<Vector2Int> pending = new List<Vector2Int>();
    readonly List<Vector2Int> removeKeys = new List<Vector2Int>();
    Vector2Int lastCenter;
    bool streamInit, initialFill, built;

    int[] variantHeights;          // висота кожного варіанта травинки у вокселях
    readonly Dictionary<int, Mesh> meshCache = new Dictionary<int, Mesh>();   // цілі травинки за висотою
    readonly Dictionary<int, List<Matrix4x4>> groupTemp = new Dictionary<int, List<Matrix4x4>>();
    Material[] materials;
    float vs;                      // розмір великого вокселя трави
    float sv, svy;                 // розмір малого вокселя сітки (по ширині й по висоті)
    int gridW, sub;                // клітинок по ширині; малих вокселів у одному великому по висоті
    float findTimer;
    Plane[] planes = new Plane[6];

    Rigidbody[] debrisPool;
    int debrisIndex, spawnedThisFrame, piecesThisFrame;
    Transform debrisRoot;
    readonly Queue<GameObject> pieces = new Queue<GameObject>();
    static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    // тимчасові буфери ламання (щоб не створювати сміття щокадру)
    readonly List<Cell> destroyedCells = new List<Cell>();
    readonly List<int> compIdx = new List<int>();
    readonly Stack<int> stack = new Stack<int>();

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

        // сітка травинки: ширина стовпчика ділиться на gridW вокселів
        gridW = Mathf.Clamp(bladeResolution, 2, 12);
        sv = thickness * vs / gridW;
        sub = Mathf.Max(1, Mathf.RoundToInt(vs / sv));
        svy = vs / sub;

        PrepareMaterials();

        int variants = Mathf.Clamp(maxVoxels - minVoxels + 1, 1, 4);
        variantHeights = new int[variants];
        for (int k = 0; k < variants; k++)
        {
            float t = variants == 1 ? 0f : k / (float)(variants - 1);
            variantHeights[k] = Mathf.RoundToInt(Mathf.Lerp(minVoxels, maxVoxels, t));
        }

        foreach (var m in meshCache.Values) if (m != null) Destroy(m);
        meshCache.Clear();
        foreach (var ch in chunks.Values) ReleaseChunk(ch);
        chunks.Clear();
        pending.Clear();
        streamInit = false;
        initialFill = true;
        built = true;

        Debug.Log("[GrassField] Voxel Size = " + vs.ToString("0.##") + " м, висота травинки " +
                  (minVoxels * vs).ToString("0") + "-" + (maxVoxels * vs).ToString("0") + " м, сітка " +
                  gridW + "x" + gridW + " вокселів в ширину.", this);
    }

    float ResolveVoxelSize()
    {
        // після переходу між сценами заглушка каменя знищується, тож шукаємо актуальний
        if (scaleFromRock && rock == null) rock = FindFirstObjectByType<RockAbsorber>();
        if (scaleFromRock && rock != null)
            return Mathf.Max(0.01f, rock.StartRadius * voxelSizeInRockRadii * grassScale);
        return Mathf.Max(0.01f, voxelSize);
    }

    // ---------- доступ для інших скриптів (Decor.cs ставить декор лише на лужайки й під розмір трави) ----------

    // Трава створена і знає свій розмір (після Setup)
    public bool Ready => built && vs > 0f && streamInit && !initialFill;
    // Розмір великого вокселя трави = сторона клітинки сітки, в метрах
    public float CellSize => vs;
    public float MinBladeHeight => minVoxels * vs;
    public float MaxBladeHeight => maxVoxels * vs;
    public float AverageBladeHeight => 0.5f * (minVoxels + maxVoxels) * vs;

    // Маска лужайки 0..1 для клітинки сітки (1 = центр лужайки, 0 = поза лужайкою)
    public float MeadowAt(int ix, int iz) => MeadowMask(ix, iz);

    // Те саме для точки у світі
    public float MeadowAt(Vector3 worldPos)
    {
        if (vs <= 0f) return 0f;
        return MeadowMask(Mathf.FloorToInt(worldPos.x / vs), Mathf.FloorToInt(worldPos.z / vs));
    }

    // Чи є ціла травинка поблизу точки (для дрібного декору: не ставити його всередину трави).
    // Чанк, якого ще немає, вважається порожнім.
    public bool BladeNear(Vector3 p, float radius)
    {
        if (vs <= 0f) return false;
        float half = thickness * vs * 0.5f;
        float rr = radius + half;
        float cw = chunkCells * vs;
        int cx0 = Mathf.FloorToInt((p.x - rr) / cw), cx1 = Mathf.FloorToInt((p.x + rr) / cw);
        int cz0 = Mathf.FloorToInt((p.z - rr) / cw), cz1 = Mathf.FloorToInt((p.z + rr) / cw);
        for (int cz = cz0; cz <= cz1; cz++)
        for (int cx = cx0; cx <= cx1; cx++)
        {
            if (!chunks.TryGetValue(new Vector2Int(cx, cz), out Chunk ch) || ch.empty) continue;
            foreach (var b in ch.blades)
                if (Mathf.Abs(b.pos.x - p.x) <= rr && Mathf.Abs(b.pos.z - p.z) <= rr) return true;
        }
        return false;
    }

    // Зони без трави під моделі декору: owner = клітинка декору, zone = (x, z, радіус).
    // Травинки в цих зонах зникають і більше не садяться, поки зони не зняті.
    readonly Dictionary<Vector2Int, List<Vector3>> freeZones = new Dictionary<Vector2Int, List<Vector3>>();

    bool InFreeZone(float x, float z)
    {
        foreach (var list in freeZones.Values)
            for (int i = 0; i < list.Count; i++)
            {
                Vector3 zn = list[i];
                float dx = x - zn.x, dz = z - zn.y;
                if (dx * dx + dz * dz < zn.z * zn.z) return true;
            }
        return false;
    }

    public void SetFreeZones(Vector2Int owner, List<Vector3> zones)
    {
        freeZones[owner] = zones;

        // прибираємо вже посаджені цілі травинки (вдарені не чіпаємо)
        foreach (var zn in zones)
        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty) continue;
            if (chunk.bounds.SqrDistance(new Vector3(zn.x, chunk.bounds.center.y, zn.y)) > zn.z * zn.z) continue;

            bool changed = false;
            for (int i = chunk.blades.Count - 1; i >= 0; i--)
            {
                Blade b = chunk.blades[i];
                if (b.cells != null) continue;
                float dx = b.pos.x - zn.x, dz = b.pos.z - zn.y;
                if (dx * dx + dz * dz >= zn.z * zn.z) continue;
                chunk.blades.RemoveAt(i);
                changed = true;
            }
            if (changed) chunk.dirty = true;
        }
    }

    public void RemoveFreeZones(Vector2Int owner)
    {
        freeZones.Remove(owner);
    }

    // Висота землі в точці (той самий промінь і ліміт нахилу, що й для травинок)
    public bool TryGetGround(float x, float z, out float y)
    {
        return SampleGround(x, z, Mathf.Cos(maxSlope * Mathf.Deg2Rad), out y);
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
        foreach (var k in removeKeys)
        {
            ReleaseChunk(chunks[k]);
            chunks.Remove(k);
        }

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

    // Знищує власні меші вдарених травинок вивантаженого чанка
    void ReleaseChunk(Chunk chunk)
    {
        foreach (var b in chunk.damaged)
            if (b.mesh != null) Destroy(b.mesh);
        chunk.damaged.Clear();
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
            if (ignoreVoxelColliders && h.collider.GetComponent<VoxelChunk>() != null) continue;
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
        if (freeZones.Count > 0 && InFreeZone(px, pz)) return;
        if (!SampleGround(px, pz, minNormalY, out float py)) return;
        py -= vs * 0.1f;   // трохи в землю, щоб не було щілин

        // варіант висоти: у центрі лужайки вища, по краях і в рідкій траві нижча
        float t = Mathf.Lerp(u, Mathf.Lerp(0.15f * u, 1f, m), tallInCenter);
        int variant = Mathf.Clamp(Mathf.FloorToInt(t * variants), 0, variants - 1);
        int H = variantHeights[variant];

        chunk.blades.Add(new Blade { pos = new Vector3(px, py, pz), yaw = yaw, H = H });
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

    // ---------- воксельна сітка травинки ----------

    int GridLen(Blade b) { return gridW * gridW * b.H * sub; }

    // Заповнює сітку цілою травинкою (та сама форма, що й меш інстансингу: тонший верхній куб)
    void InitCells(Blade b)
    {
        int L = b.H * sub;
        b.cells = new byte[gridW * gridW * L];
        b.count = 0;
        for (int iy = 0; iy < L; iy++)
        {
            int level = iy / sub;
            float half = (level == b.H - 1 && b.H > 1 ? tipThickness : thickness) * vs * 0.5f + 1e-4f;
            for (int iz = 0; iz < gridW; iz++)
            for (int ix = 0; ix < gridW; ix++)
            {
                float ox = (ix + 0.5f - gridW * 0.5f) * sv;
                float oz = (iz + 0.5f - gridW * 0.5f) * sv;
                if (Mathf.Abs(ox) > half || Mathf.Abs(oz) > half) continue;
                b.cells[(iy * gridW + iz) * gridW + ix] = 1;
                b.count++;
            }
        }
    }

    // Центр малого вокселя в локальних координатах травинки (до повороту), низ травинки = y 0
    Vector3 CellCenter(int ix, int iy, int iz)
    {
        return new Vector3((ix + 0.5f - gridW * 0.5f) * sv, (iy + 0.5f) * svy, (iz + 0.5f - gridW * 0.5f) * sv);
    }

    // Вибиває кулю вокселів у травинці. Те, що відірвалось від землі, падає шматком або розсипається.
    // Повертає true, якщо щось змінилось.
    bool Carve(Blade b, Chunk chunk, Vector3 c, float r, Vector3 rockVel)
    {
        if (b.cells == null) InitCells(b);

        int L = b.H * sub;
        Quaternion rot = Quaternion.Euler(0f, b.yaw, 0f);
        Vector3 lc = Quaternion.Inverse(rot) * (c - b.pos);
        float r2 = r * r;

        int x0 = Mathf.Max(0, Mathf.FloorToInt((lc.x - r) / sv + gridW * 0.5f - 0.5f));
        int x1 = Mathf.Min(gridW - 1, Mathf.CeilToInt((lc.x + r) / sv + gridW * 0.5f - 0.5f));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((lc.z - r) / sv + gridW * 0.5f - 0.5f));
        int z1 = Mathf.Min(gridW - 1, Mathf.CeilToInt((lc.z + r) / sv + gridW * 0.5f - 0.5f));
        int y0 = Mathf.Max(0, Mathf.FloorToInt((lc.y - r) / svy - 0.5f));
        int y1 = Mathf.Min(L - 1, Mathf.CeilToInt((lc.y + r) / svy - 0.5f));

        int removed = 0;
        for (int iy = y0; iy <= y1; iy++)
        for (int iz = z0; iz <= z1; iz++)
        for (int ix = x0; ix <= x1; ix++)
        {
            int idx = (iy * gridW + iz) * gridW + ix;
            if (b.cells[idx] == 0) continue;
            Vector3 p = CellCenter(ix, iy, iz);
            if ((p - lc).sqrMagnitude > r2) continue;

            b.cells[idx] = 0;
            b.count--;
            removed++;
            AddDestroyed(b, rot, p, iy);
        }
        if (removed == 0) return false;

        // знаходимо шматки, не з'єднані із землею (нижній шар iy == 0): вони відламуються
        if (b.count > 0) DetachFloating(b, rot, rockVel, c);

        // перебудовуємо меш тієї частини, що стоїть
        if (b.mesh != null) { Destroy(b.mesh); b.mesh = null; }
        if (b.count > 0)
        {
            b.mesh = BuildVoxelMesh(b.cells, b.H, out _);
            if (!chunk.damaged.Contains(b)) chunk.damaged.Add(b);
        }
        else
        {
            chunk.damaged.Remove(b);
        }
        return true;
    }

    void AddDestroyed(Blade b, Quaternion rot, Vector3 localPos, int iy)
    {
        int level = iy / sub;
        destroyedCells.Add(new Cell
        {
            pos = b.pos + rot * localPos,
            band = Mathf.Min(colorBands - 1, level * colorBands / b.H)
        });
    }

    // Шукає зв'язні групи вокселів (6 сусідів). Група без вокселів нижнього шару відірвана від землі.
    void DetachFloating(Blade b, Quaternion rot, Vector3 rockVel, Vector3 rockCenter)
    {
        int L = b.H * sub;
        int layer = gridW * gridW;
        var visited = new bool[b.cells.Length];

        for (int start = 0; start < b.cells.Length; start++)
        {
            if (b.cells[start] == 0 || visited[start]) continue;

            compIdx.Clear();
            stack.Clear();
            stack.Push(start);
            visited[start] = true;
            bool grounded = false;

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                compIdx.Add(idx);
                int iy = idx / layer;
                int rem = idx - iy * layer;
                int iz = rem / gridW;
                int ix = rem - iz * gridW;
                if (iy == 0) grounded = true;

                TryVisit(b, visited, ix + 1, iy, iz);
                TryVisit(b, visited, ix - 1, iy, iz);
                TryVisit(b, visited, ix, iy + 1, iz);
                TryVisit(b, visited, ix, iy - 1, iz);
                TryVisit(b, visited, ix, iy, iz + 1);
                TryVisit(b, visited, ix, iy, iz - 1);
            }
            if (grounded) continue;

            // відірваний шматок
            if (fallingPieces && compIdx.Count >= minPieceVoxels && piecesThisFrame < maxPiecesPerHit)
            {
                piecesThisFrame++;
                SpawnPiece(b, rot, compIdx, rockCenter, rockVel);
            }
            else
            {
                // дрібний шматок (або забагато шматків): розсипається, беремо частину вокселів на кубики
                int step = Mathf.Max(1, compIdx.Count / 12);
                for (int i = 0; i < compIdx.Count; i += step)
                {
                    int idx = compIdx[i];
                    int iy = idx / layer;
                    int rem = idx - iy * layer;
                    int iz = rem / gridW;
                    int ix = rem - iz * gridW;
                    AddDestroyed(b, rot, CellCenter(ix, iy, iz), iy);
                }
            }

            for (int i = 0; i < compIdx.Count; i++) b.cells[compIdx[i]] = 0;
            b.count -= compIdx.Count;
        }
    }

    void TryVisit(Blade b, bool[] visited, int ix, int iy, int iz)
    {
        if (ix < 0 || ix >= gridW || iz < 0 || iz >= gridW || iy < 0 || iy >= b.H * sub) return;
        int idx = (iy * gridW + iz) * gridW + ix;
        if (b.cells[idx] == 0 || visited[idx]) return;
        visited[idx] = true;
        stack.Push(idx);
    }

    // ---------- ламання ----------

    // Ламає травинки, яких торкається камінь (або які в зоні всмоктування при E, якщо це ввімкнено).
    void BreakAroundRock()
    {
        if (rock == null) return;

        Vector3 c = rock.WorldCenter;
        float r = rock.Radius * breakRadiusFactor;

        var kb = Keyboard.current;
        bool sucking = breakOnSuck && kb != null && kb.eKey.isPressed;
        if (sucking) r = Mathf.Max(r, rock.suckRadius * rock.SizeRatio * suckBreakFactor);

        Vector3 rockVel = Vector3.zero;
        var rockBody = rock.GetComponent<Rigidbody>();
        if (rockBody != null) rockVel = rockBody.linearVelocity;

        // сила удару: від швидкості каменя, а по E не нижче suckDamage
        float damage = Mathf.Clamp01(rockVel.magnitude / Mathf.Max(0.01f, fullDamageSpeed));
        if (sucking) damage = Mathf.Max(damage, suckDamage);
        r *= 1f + damage * Mathf.Max(0f, radiusBoostAtFullDamage);   // сильніший удар = більша яма

        BreakSphere(c, r, damage, rockVel);
    }

    // Те саме для будь-якого тіла: падаючий шматок декору (WorldDecorator) ламає траву, в яку врізається.
    // Повертає, скільки травинок зачеплено.
    public int BreakInSphere(Vector3 c, float r, Vector3 vel)
    {
        if (!Ready || r <= 0f) return 0;

        float damage = Mathf.Clamp01(vel.magnitude / Mathf.Max(0.01f, fullDamageSpeed));
        r *= 1f + damage * Mathf.Max(0f, radiusBoostAtFullDamage);
        return BreakSphere(c, r, damage, vel);
    }

    // Спільне ядро: вибиває кулю вокселів у всіх травинках, яких торкається сфера (c, r)
    int BreakSphere(Vector3 c, float r, float damage, Vector3 vel)
    {
        float r2 = r * r;
        float half = thickness * vs * 0.5f;

        destroyedCells.Clear();
        int broken = 0;
        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty || chunk.bounds.SqrDistance(c) > r2) continue;

            var list = chunk.blades;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Blade b = list[i];

                // швидка перевірка: найближча точка вихідного стовпчика до центру сфери
                float top = b.pos.y + b.H * vs;
                Vector3 q = new Vector3(
                    Mathf.Clamp(c.x, b.pos.x - half, b.pos.x + half),
                    Mathf.Clamp(c.y, b.pos.y, top),
                    Mathf.Clamp(c.z, b.pos.z - half, b.pos.z + half));
                if ((c - q).sqrMagnitude > r2) continue;

                if (!Carve(b, chunk, c, r, vel)) continue;
                broken++;

                if (b.count <= 0) list.RemoveAt(i);
                chunk.dirty = true;   // ціла травинка вийшла з інстансингу (або зникла)
            }
        }

        if (broken > 0)
        {
            if (debris) SpawnForCells(damage, broken, c, vel);
            destroyedCells.Clear();
            TotalBroken += broken;
            BladesBroken?.Invoke(broken);
        }
        return broken;
    }

    // ---------- падаючий шматок травинки ----------

    void SpawnPiece(Blade b, Quaternion rot, List<int> comp, Vector3 rockCenter, Vector3 rockVel)
    {
        EnsureRoot();

        // не тримаємо більше maxPieces шматків: найстарші зникають
        while (pieces.Count >= Mathf.Max(1, maxPieces))
        {
            GameObject old = pieces.Dequeue();
            if (old != null) Destroy(old);
        }

        // маска шматка й меш
        var mask = new byte[b.cells.Length];
        for (int i = 0; i < comp.Count; i++) mask[comp[i]] = 1;
        Mesh mesh = BuildVoxelMesh(mask, b.H, out Bounds bnd);
        Destroy(mesh, Mathf.Max(1f, pieceLifetime) + 1f);

        var go = new GameObject("GrassPiece");
        go.transform.SetParent(debrisRoot, false);
        go.transform.SetPositionAndRotation(b.pos, rot);

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterials = materials;
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = receiveShadows;

        var box = go.AddComponent<BoxCollider>();
        box.center = bnd.center;
        box.size = Vector3.Max(bnd.size, Vector3.one * 0.01f);

        var rb = go.AddComponent<Rigidbody>();
        float rockMass = 1f;
        var rockBody = rock != null ? rock.GetComponent<Rigidbody>() : null;
        if (rockBody != null) rockMass = rockBody.mass;
        rb.mass = Mathf.Max(0.05f, rockMass * pieceMassInRocks);
        rb.angularDamping = 0.5f;

        // валиться від каменя: горизонтальний напрямок від нього
        Vector3 away = go.transform.TransformPoint(bnd.center) - rockCenter;
        away.y = 0f;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : new Vector3(Random.value - 0.5f, 0f, Random.value - 0.5f).normalized;
        rb.linearVelocity = rockVel * pieceKick + away * 0.5f;
        rb.angularVelocity = Vector3.Cross(Vector3.up, away) * pieceTipSpeed + Random.insideUnitSphere * 0.2f;

        // великий шматок камінь не всмокче (не пройде за розміром), але більший камінь згодом зможе
        var a = go.AddComponent<Absorbable>();
        a.volume = comp.Count * sv * sv * svy * absorbVolumeFactor;

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

    // Кубики з вибитих вокселів. Слабкий удар = мало кубиків, сильний = більше, але разом не більше maxCubesPerHit.
    void SpawnForCells(float damage, int bladesHit, Vector3 rockCenter, Vector3 rockVel)
    {
        int n = destroyedCells.Count;
        if (n == 0) return;

        float perBlade = Mathf.Lerp(minCubesPerBlade, Mathf.Max(minCubesPerBlade, maxCubesPerBlade), damage);
        float total = Mathf.Min(perBlade * bladesHit, Mathf.Min(maxCubesPerHit, maxDebrisPerFrame - spawnedThisFrame));
        int count = Mathf.FloorToInt(total);
        if (Random.value < total - count) count++;   // дробову частину кидаємо жеребом
        if (count <= 0) return;

        // менше кубиків = кожен більший (об'єм зберігається)
        float refCubes = Mathf.Max(1f, maxCubesPerBlade) * bladesHit;
        float grow = Mathf.Clamp(Mathf.Pow(refCubes / count, 1f / 3f), 1f, maxCubeGrow);

        for (int k = 0; k < count; k++)
        {
            if (spawnedThisFrame >= maxDebrisPerFrame) return;
            spawnedThisFrame++;

            Cell cell = destroyedCells[Random.Range(0, n)];
            Vector3 pos = cell.pos + Random.insideUnitSphere * (sv * 0.4f);

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
            d.GetComponent<Renderer>().sharedMaterial = materials[cell.band];

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

            // цілі травинки: інстансинг
            foreach (var b in chunk.batches)
            {
                if (b.count == 0) continue;
                for (int s = 0; s < b.mesh.subMeshCount; s++)
                {
                    if (b.mesh.GetIndexCount(s) == 0) continue;
                    var rp = new RenderParams(materials[s])
                    {
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = receiveShadows,
                        layer = gameObject.layer,
                        worldBounds = chunk.bounds
                    };
                    Graphics.RenderMeshInstanced(rp, b.mesh, s, b.matrices, b.count);
                }
            }

            // вдарені травинки: кожна зі своїм мешем (з дірками)
            foreach (var bl in chunk.damaged)
            {
                if (bl.mesh == null) continue;
                var m = Matrix4x4.TRS(bl.pos, Quaternion.Euler(0f, bl.yaw, 0f), Vector3.one);
                for (int s = 0; s < bl.mesh.subMeshCount; s++)
                {
                    if (bl.mesh.GetIndexCount(s) == 0) continue;
                    var rp = new RenderParams(materials[s])
                    {
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = receiveShadows,
                        layer = gameObject.layer,
                        worldBounds = chunk.bounds
                    };
                    Graphics.RenderMesh(rp, bl.mesh, s, m);
                }
            }
        }
    }

    // Групує цілі травинки чанка за висотою і складає матриці для GPU (вдарені сюди не потрапляють)
    void RebuildBatches(Chunk chunk)
    {
        chunk.dirty = false;
        chunk.batches.Clear();

        foreach (var l in groupTemp.Values) l.Clear();
        foreach (var b in chunk.blades)
        {
            if (b.cells != null) continue;
            if (!groupTemp.TryGetValue(b.H, out var l))
            {
                l = new List<Matrix4x4>();
                groupTemp[b.H] = l;
            }
            l.Add(Matrix4x4.TRS(b.pos, Quaternion.Euler(0f, b.yaw, 0f), Vector3.one * vs));
        }

        foreach (var kv in groupTemp)
        {
            var list = kv.Value;
            if (list.Count == 0) continue;
            Mesh mesh = GetIntactMesh(kv.Key);
            for (int s = 0; s < list.Count; s += MaxBatch)
            {
                int n = Mathf.Min(MaxBatch, list.Count - s);
                var arr = new Matrix4x4[n];
                list.CopyTo(s, arr, 0, n);
                chunk.batches.Add(new Batch { mesh = mesh, matrices = arr, count = n });
            }
        }
    }

    // ---------- меші ----------

    // Ціла травинка висотою H (одиничний воксель, масштабується матрицею)
    Mesh GetIntactMesh(int H)
    {
        if (meshCache.TryGetValue(H, out var mesh) && mesh != null) return mesh;
        mesh = BuildColumnMesh(H, thickness, tipThickness, colorBands);
        meshCache[H] = mesh;
        return mesh;
    }

    static Mesh BuildColumnMesh(int n, float thick, float tipThick, int bands)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>[bands];
        for (int b = 0; b < bands; b++) tris[b] = new List<int>();

        for (int i = 0; i < n; i++)
        {
            float half = (i == n - 1 && n > 1 ? tipThick : thick) * 0.5f;
            int band = Mathf.Min(bands - 1, i * bands / n);
            AddCube(verts, normals, tris[band], -half, i, -half, half, i + 1, half);
        }

        var mesh = new Mesh { name = "VoxelBlade" + n };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.subMeshCount = bands;
        for (int b = 0; b < bands; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();
        return mesh;
    }

    // Меш воксельної сітки: малюються лише відкриті грані. Координати в метрах (не масштабується).
    // Колір смуги береться за «великим» рівнем (iy / sub), як у цілої травинки.
    Mesh BuildVoxelMesh(byte[] mask, int H, out Bounds bounds)
    {
        int L = H * sub;
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>[colorBands];
        for (int b = 0; b < colorBands; b++) tris[b] = new List<int>();

        Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        for (int iy = 0; iy < L; iy++)
        {
            int band = Mathf.Min(colorBands - 1, (iy / sub) * colorBands / H);
            var t = tris[band];
            for (int iz = 0; iz < gridW; iz++)
            for (int ix = 0; ix < gridW; ix++)
            {
                if (mask[(iy * gridW + iz) * gridW + ix] == 0) continue;

                float x0 = (ix - gridW * 0.5f) * sv, x1 = x0 + sv;
                float y0 = iy * svy, y1 = y0 + svy;
                float z0 = (iz - gridW * 0.5f) * sv, z1 = z0 + sv;

                min = Vector3.Min(min, new Vector3(x0, y0, z0));
                max = Vector3.Max(max, new Vector3(x1, y1, z1));

                if (!Filled(mask, ix + 1, iy, iz, L))
                    Face(verts, normals, t, new Vector3(1, 0, 0),
                        new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1));
                if (!Filled(mask, ix - 1, iy, iz, L))
                    Face(verts, normals, t, new Vector3(-1, 0, 0),
                        new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0));
                if (!Filled(mask, ix, iy + 1, iz, L))
                    Face(verts, normals, t, new Vector3(0, 1, 0),
                        new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0));
                if (iy > 0 && !Filled(mask, ix, iy - 1, iz, L))   // низ травинки в землі не малюємо
                    Face(verts, normals, t, new Vector3(0, -1, 0),
                        new Vector3(x0, y0, z0), new Vector3(x1, y0, z0), new Vector3(x1, y0, z1), new Vector3(x0, y0, z1));
                if (!Filled(mask, ix, iy, iz + 1, L))
                    Face(verts, normals, t, new Vector3(0, 0, 1),
                        new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1));
                if (!Filled(mask, ix, iy, iz - 1, L))
                    Face(verts, normals, t, new Vector3(0, 0, -1),
                        new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0));
            }
        }

        var mesh = new Mesh { name = "GrassVoxels", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.subMeshCount = colorBands;
        for (int b = 0; b < colorBands; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();

        bounds = new Bounds();
        if (min.x <= max.x) bounds.SetMinMax(min, max);
        return mesh;
    }

    bool Filled(byte[] mask, int ix, int iy, int iz, int L)
    {
        if (ix < 0 || ix >= gridW || iz < 0 || iz >= gridW || iy < 0 || iy >= L) return false;
        return mask[(iy * gridW + iz) * gridW + ix] != 0;
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