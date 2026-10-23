using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Декор навколо каменя. Ставиться ТІЛЬКИ на лужайках (карта лужаек береться з GrassField).
// Два шари:
//  1) дрібний декор у VoxelWorld (в масштабі каменя): валуни, горби землі, кущі, плями моху, камінці.
//     Квітів і грибів тут більше немає. Це звичайні вокселі, камінь ламає їх так само, як стіну.
//  2) твої моделі з MagicaVoxel (3 камені + 3 квітки) розміром з траву. Ставляться процедурно на сітці трави
//     (кожна модель займає стільки місця, скільки травинка) і малюються GPU-інстансингом, як сама трава.
// Повісь на порожній GameObject, у world перетягни VoxelWorld (rock і grass знайдуться самі).
// Низ світу вокселів = y = 0 (там, де підлога FloorGenerator).
// Розміри дрібного декору задаються в радіусах стартового каменя, тому генерація працює на будь-якому масштабі.
// Клітинка дрібного декору генерується один раз і більше ніколи, тож зламане не відростає, коли повертаєшся.
public class WorldDecorator : MonoBehaviour
{
    [Header("Зв'язки")]
    public VoxelWorld world;
    [Tooltip("Якщо порожньо, шукається сам (після переходу між сценами теж)")]
    public RockAbsorber rock;
    [Tooltip("Трава: з неї береться карта лужаек і розмір моделей. Якщо порожньо, шукається сама")]
    public GrassField grass;

    [Header("Лужайки")]
    [Tooltip("Декор ставиться лише там, де лужайка. Поки трава не створена, нічого не ставиться")]
    public bool onlyOnLawns = true;
    [Tooltip("Наскільки глибоко в лужайці має бути місце (0..1). 0.5 = не на самому краю, 0.8 = лише в серці лужайки")]
    [Range(0.05f, 1f)] public float minMeadow = 0.5f;

    [Header("Нескінченна генерація (дрібний декор)")]
    public int seed = 11;
    [Tooltip("Сторона клітинки в радіусах стартового каменя")]
    public float cellSizeInRockRadii = 10f;
    [Tooltip("Як далеко від каменя генерується декор (в радіусах стартового каменя). Має бути більше за дальність камери")]
    public float viewRadiusInRockRadii = 70f;
    [Tooltip("Скільки клітинок генерується за кадр (менше = плавніше)")]
    public int cellsPerFrame = 2;
    public float startDelay = 0.3f;
    [Tooltip("Навколо місця старту нічого не ставимо (в радіусах стартового каменя)")]
    public float clearZoneInRockRadii = 4f;

    [Header("Скільки чого (в середньому на клітинку)")]
    public float boulderDensity = 1.2f;
    public float moundDensity = 0.7f;
    public float bushDensity = 0.8f;
    public float mossPatchDensity = 1.5f;
    public float pebbleDensity = 4f;

    [Header("Розміри (в радіусах стартового каменя)")]
    public Vector2 boulderRadius = new Vector2(0.5f, 2.0f);
    public Vector2 moundRadius = new Vector2(1.0f, 3.0f);
    public Vector2 bushRadius = new Vector2(0.4f, 0.9f);
    public Vector2 mossPatchRadius = new Vector2(0.8f, 1.8f);
    public Vector2 pebbleRadius = new Vector2(0.08f, 0.22f);
    [Tooltip("Стеля розміру валуна у вокселях (захист від лагів, якщо камінь великий)")]
    public int maxBoulderVoxels = 36;
    [Range(0f, 1f)] public float mossAmount = 0.35f;

    [Header("Свої моделі з MagicaVoxel (розміром з траву)")]
    [Tooltip("3 камені. Файл .vox перейменуй у .vox.bytes і перетягни сюди")]
    public TextAsset[] stoneModels;
    [Tooltip("3 квітки. Файл .vox перейменуй у .vox.bytes і перетягни сюди")]
    public TextAsset[] flowerModels;
    [Tooltip("Висота квітки в середніх висотах травинки (1 = як трава)")]
    public Vector2 flowerHeightInGrass = new Vector2(0.9f, 1.3f);
    [Tooltip("Висота каменя в середніх висотах травинки (1 = як трава)")]
    public Vector2 stoneHeightInGrass = new Vector2(0.6f, 1.1f);
    [Tooltip("Шанс квітки в клітинці сітки трави на лужайці (0.01 = одна на 100 клітинок)")]
    [Range(0f, 0.2f)] public float flowerChance = 0.01f;
    [Tooltip("Шанс каменя в клітинці сітки трави на лужайці")]
    [Range(0f, 0.2f)] public float stoneChance = 0.005f;
    [Tooltip("Сторона клітинки генерації моделей у клітинках сітки трави")]
    public int modelCellGrassCells = 6;
    [Tooltip("Скільки клітинок моделей генерується за кадр (перше заповнення йде одразу все)")]
    public int modelCellsPerFrame = 4;
    public bool modelsCastShadows = false;
    public bool modelsReceiveShadows = true;

    // ---- кольори дрібного декору (палітра VoxelWorld має 256 місць, беремо небагато) ----
    byte[] rockShades, moss, dirtShades, dirtTop, bushShades, berry;

    readonly HashSet<Vector2Int> done = new HashSet<Vector2Int>();
    float findTimer, startTime;
    Vector3 spawn;
    bool spawnSet, colorsReady;

    void Start()
    {
        startTime = Time.time;
    }

    void BuildColors()
    {
        byte C(int r, int g, int b) => world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));

        rockShades  = new[] { C(128, 128, 126), C(112, 112, 112), C(140, 138, 134), C(98, 98, 100) };
        moss        = new[] { C(86, 120, 58), C(72, 104, 50) };
        dirtShades  = new[] { C(104, 76, 52), C(92, 66, 44), C(118, 86, 58) };
        dirtTop     = new[] { C(82, 128, 46), C(96, 142, 56) };
        bushShades  = new[] { C(48, 104, 36), C(60, 122, 44), C(40, 90, 32) };
        berry       = new[] { C(190, 40, 52), C(120, 50, 150) };
        colorsReady = true;
    }

    // ---------- стрімінг ----------

    void Update()
    {
        if (world == null) return;

        findTimer -= Time.deltaTime;
        if (findTimer <= 0f)
        {
            findTimer = 0.5f;
            if (rock == null) rock = FindFirstObjectByType<RockAbsorber>();
            if (grass == null) grass = FindFirstObjectByType<GrassField>();
        }
        if (rock == null) return;

        bool lawnReady = grass != null && grass.Ready;
        if (onlyOnLawns && !lawnReady) return;   // без карти лужаек нічого не ставимо
        if (Time.time - startTime < startDelay) return;
        if (!colorsReady) BuildColors();

        Vector3 c = rock.WorldCenter;
        if (!spawnSet) { spawn = c; spawnSet = true; }

        StreamVoxelDecor(c);
        if (lawnReady) UpdateModels(c);
    }

    void StreamVoxelDecor(Vector3 c)
    {
        float cw = CellSize;
        int cxr = Mathf.FloorToInt(c.x / cw), czr = Mathf.FloorToInt(c.z / cw);
        int R = Mathf.CeilToInt(viewRadiusInRockRadii * rock.StartRadius / cw);

        for (int n = 0; n < Mathf.Max(1, cellsPerFrame); n++)
        {
            // найближча ще не згенерована клітинка
            bool found = false;
            Vector2Int best = default;
            int bestD = int.MaxValue;
            for (int dz = -R; dz <= R; dz++)
            for (int dx = -R; dx <= R; dx++)
            {
                int d = dx * dx + dz * dz;
                if (d > R * R || d >= bestD) continue;
                var cell = new Vector2Int(cxr + dx, czr + dz);
                if (done.Contains(cell)) continue;
                best = cell; bestD = d; found = true;
            }
            if (!found) return;
            done.Add(best);
            GenerateCell(best.x, best.y);
        }
    }

    float CellSize => Mathf.Max(1f, cellSizeInRockRadii * rock.StartRadius);
    float Vs => world.voxelSize;
    float RockR => rock.StartRadius;

    // ---------- клітинка дрібного декору ----------

    void GenerateCell(int cx, int cz)
    {
        var rng = new System.Random(unchecked(seed * 73856093 ^ cx * 19349663 ^ cz * 83492791));
        float o = seed * 3.17f;
        float rockiness = Mathf.PerlinNoise(cx * 0.23f + o + 100f, cz * 0.23f + o * 0.6f + 200f);
        float meadow = Mathf.PerlinNoise(cx * 0.19f + o + 300f, cz * 0.19f + o * 0.9f + 400f);
        float rockMod = Mathf.Lerp(0.3f, 1.9f, rockiness);
        float meadowMod = Mathf.Lerp(0.2f, 2.0f, meadow);

        for (int i = Count(rng, boulderDensity * rockMod); i > 0; i--) PlaceBoulder(rng, RandCell(rng, cx, cz));
        for (int i = Count(rng, moundDensity); i > 0; i--) PlaceMound(rng, RandCell(rng, cx, cz));
        for (int i = Count(rng, bushDensity * meadowMod); i > 0; i--) PlaceBush(rng, RandCell(rng, cx, cz));
        for (int i = Count(rng, mossPatchDensity * meadowMod); i > 0; i--) PlaceMossPatch(rng, RandCell(rng, cx, cz));
        for (int i = Count(rng, pebbleDensity * rockMod); i > 0; i--) PlacePebble(rng, RandCell(rng, cx, cz));
    }

    static int Count(System.Random rng, float avg)
    {
        int n = Mathf.FloorToInt(avg);
        if (rng.NextDouble() < avg - n) n++;
        return n;
    }

    Vector3 RandCell(System.Random rng, int cx, int cz)
    {
        float cw = CellSize;
        return new Vector3((cx + (float)rng.NextDouble()) * cw, world.transform.position.y, (cz + (float)rng.NextDouble()) * cw);
    }

    static float Rand(System.Random rng, Vector2 range) => Mathf.Lerp(range.x, range.y, (float)rng.NextDouble());

    // ---------- допоміжне ----------

    Vector2Int ToGrid(Vector3 worldPos)
    {
        Vector3 l = world.transform.InverseTransformPoint(worldPos) / Vs;
        return new Vector2Int(Mathf.FloorToInt(l.x), Mathf.FloorToInt(l.z));
    }

    // Скільки вокселів у n радіусах стартового каменя (мінімум min)
    int Vox(float rockRadii, int min, int max)
    {
        return Mathf.Clamp(Mathf.RoundToInt(rockRadii * RockR / Vs), min, max);
    }

    bool InClearZone(Vector3 p)
    {
        float d = clearZoneInRockRadii * RockR;
        float dx = p.x - spawn.x, dz = p.z - spawn.z;
        return dx * dx + dz * dz < d * d;
    }

    // Точка на лужайці (карта лужаек з GrassField)
    bool OnLawn(Vector3 p)
    {
        if (!onlyOnLawns) return true;
        return grass != null && grass.Ready && grass.MeadowAt(p) >= minMeadow;
    }

    // Сюди декор ставити не можна: біля старту каменя або поза лужайкою
    bool Blocked(Vector3 p)
    {
        return InClearZone(p) || !OnLawn(p);
    }

    // Місце вільне: у центрі й на кінцях хреста немає вокселів (щоб не вростати в стіни й будинки)
    bool Free(Vector2Int g, int r)
    {
        return world.GetVoxel(new Vector3Int(g.x, 0, g.y)) == 0 &&
               world.GetVoxel(new Vector3Int(g.x + r, 0, g.y)) == 0 &&
               world.GetVoxel(new Vector3Int(g.x - r, 0, g.y)) == 0 &&
               world.GetVoxel(new Vector3Int(g.x, 0, g.y + r)) == 0 &&
               world.GetVoxel(new Vector3Int(g.x, 0, g.y - r)) == 0;
    }

    // Ставить воксель лише в порожнє місце
    bool Put(int x, int y, int z, byte id)
    {
        if (y < 0) return false;
        var g = new Vector3Int(x, y, z);
        if (world.GetVoxel(g) != 0) return false;
        world.SetVoxel(g, id);
        return true;
    }

    static int Hash(int a, int b, int c, int s)
    {
        unchecked
        {
            int h = a * 73856093 ^ b * 19349663 ^ c * 83492791 ^ s * 40503;
            h ^= h >> 13; h *= 1274126177; h ^= h >> 16;
            return h & 0x7fffffff;
        }
    }

    static byte Pick(byte[] arr, int hash) => arr[(hash & 0x7fffffff) % arr.Length];

    // ---------- валун ----------

    void PlaceBoulder(System.Random rng, Vector3 pos)
    {
        if (Blocked(pos)) return;
        Vector2Int g = ToGrid(pos);
        float R = Vox(Rand(rng, boulderRadius), 2, maxBoulderVoxels);
        if (!Free(g, Mathf.RoundToInt(R))) return;

        int salt = rng.Next();
        BoulderBlob(rng, g.x, g.y, R, (float)(0.6 + rng.NextDouble() * 0.3), salt);

        // купка менших каменів збоку
        int extra = rng.Next(0, 3);
        for (int i = 0; i < extra; i++)
        {
            double a = rng.NextDouble() * Mathf.PI * 2.0;
            float dist = R * (0.85f + (float)rng.NextDouble() * 0.4f);
            float r2 = Mathf.Max(2f, R * (0.3f + (float)rng.NextDouble() * 0.25f));
            int ox = g.x + Mathf.RoundToInt(Mathf.Cos((float)a) * dist);
            int oz = g.y + Mathf.RoundToInt(Mathf.Sin((float)a) * dist);
            if (!Free(new Vector2Int(ox, oz), Mathf.RoundToInt(r2))) continue;
            BoulderBlob(rng, ox, oz, r2, (float)(0.6 + rng.NextDouble() * 0.3), rng.Next());
        }
    }

    // Нерівна напівзарита куля. Стовпці суцільні від землі, тому нічого не висить у повітрі
    void BoulderBlob(System.Random rng, int gx, int gz, float R, float squash, int salt)
    {
        float Hh = R * squash;
        float cy = Hh * 0.35f;   // центр трохи вище землі, низ ніби зарито
        int ext = Mathf.CeilToInt(R) + 1;
        float ox = (salt & 1023) * 0.37f, oz = ((salt >> 10) & 1023) * 0.53f;

        for (int dz = -ext; dz <= ext; dz++)
        for (int dx = -ext; dx <= ext; dx++)
        {
            int top = -1;
            int ymax = Mathf.CeilToInt(cy + Hh) + 1;
            for (int y = ymax; y >= 0; y--)
            {
                float nx = dx / R, ny = (y + 0.5f - cy) / Hh, nz = dz / R;
                float n = Mathf.PerlinNoise(dx * 0.32f + ox, dz * 0.32f + y * 0.21f + oz) - 0.5f;
                if (nx * nx + ny * ny + nz * nz < 1f + n * 0.45f) { top = y; break; }
            }
            for (int y = 0; y <= top; y++)
            {
                int h = Hash(gx + dx, y, gz + dz, salt);
                byte id = Pick(rockShades, h);
                if (y == top && (h % 1000) / 1000f < mossAmount) id = Pick(moss, h >> 4);
                Put(gx + dx, y, gz + dz, id);
            }
        }
    }

    // ---------- горб землі ----------

    void PlaceMound(System.Random rng, Vector3 pos)
    {
        if (Blocked(pos)) return;
        Vector2Int g = ToGrid(pos);
        float R = Vox(Rand(rng, moundRadius), 3, maxBoulderVoxels + 8);
        float Hh = Mathf.Max(2f, R * (0.3f + (float)rng.NextDouble() * 0.25f));
        if (!Free(g, Mathf.RoundToInt(R))) return;

        int salt = rng.Next();
        float ox = (salt & 1023) * 0.41f, oz = ((salt >> 10) & 1023) * 0.29f;
        int ext = Mathf.CeilToInt(R) + 1;

        for (int dz = -ext; dz <= ext; dz++)
        for (int dx = -ext; dx <= ext; dx++)
        {
            float rr = (dx * dx + dz * dz) / (R * R);
            if (rr >= 1f) continue;
            float n = Mathf.PerlinNoise(dx * 0.25f + ox, dz * 0.25f + oz);
            float h = Hh * Mathf.Sqrt(1f - rr) * Mathf.Lerp(0.8f, 1.15f, n);
            int top = Mathf.FloorToInt(h);
            if (top < 0) continue;
            for (int y = 0; y <= top; y++)
            {
                int hh = Hash(g.x + dx, y, g.y + dz, salt);
                byte id = y == top ? Pick(dirtTop, hh) : Pick(dirtShades, hh);
                Put(g.x + dx, y, g.y + dz, id);
            }
        }
    }

    // ---------- кущ ----------

    void PlaceBush(System.Random rng, Vector3 pos)
    {
        if (Blocked(pos)) return;
        Vector2Int g = ToGrid(pos);
        float R = Vox(Rand(rng, bushRadius), 2, 10);
        if (!Free(g, Mathf.RoundToInt(R))) return;

        int salt = rng.Next();
        bool berries = rng.NextDouble() < 0.4;
        byte berryId = Pick(berry, salt >> 3);
        float Hh = R * (0.7f + (float)rng.NextDouble() * 0.3f);
        float ox = (salt & 1023) * 0.43f, oz = ((salt >> 10) & 1023) * 0.31f;
        int ext = Mathf.CeilToInt(R) + 1;

        for (int dz = -ext; dz <= ext; dz++)
        for (int dx = -ext; dx <= ext; dx++)
        {
            int top = -1;
            for (int y = Mathf.CeilToInt(Hh) + 1; y >= 0; y--)
            {
                float nx = dx / R, ny = (y + 0.5f) / Hh, nz = dz / R;
                float n = Mathf.PerlinNoise(dx * 0.5f + ox, dz * 0.5f + y * 0.4f + oz) - 0.5f;
                if (nx * nx + ny * ny + nz * nz < 1f + n * 0.6f) { top = y; break; }
            }
            for (int y = 0; y <= top; y++)
            {
                int h = Hash(g.x + dx, y, g.y + dz, salt);
                byte id = Pick(bushShades, h);
                if (berries && y >= top - 1 && h % 11 == 0) id = berryId;
                Put(g.x + dx, y, g.y + dz, id);
            }
        }
    }

    // ---------- пляма моху ----------

    // Низька рвана пляма моху на землі (1-2 вокселі заввишки), сама лише зелень без квітів
    void PlaceMossPatch(System.Random rng, Vector3 pos)
    {
        if (Blocked(pos)) return;
        Vector2Int g = ToGrid(pos);
        float R = Mathf.Max(2f, Vox(Rand(rng, mossPatchRadius), 2, 24));
        if (world.GetVoxel(new Vector3Int(g.x, 0, g.y)) != 0) return;

        int salt = rng.Next();
        float ox = (salt & 1023) * 0.39f, oz = ((salt >> 10) & 1023) * 0.27f;
        int ext = Mathf.CeilToInt(R) + 1;

        for (int dz = -ext; dz <= ext; dz++)
        for (int dx = -ext; dx <= ext; dx++)
        {
            float rr = (dx * dx + dz * dz) / (R * R);
            float n = Mathf.PerlinNoise(dx * 0.3f + ox, dz * 0.3f + oz);
            if (rr > 0.55f + n * 0.6f) continue;   // рваний край

            int top = (rr < 0.3f && n > 0.55f) ? 1 : 0;
            for (int y = 0; y <= top; y++)
                Put(g.x + dx, y, g.y + dz, Pick(moss, Hash(g.x + dx, y, g.y + dz, salt)));
        }
    }

    // ---------- камінці ----------

    void PlacePebble(System.Random rng, Vector3 pos)
    {
        if (Blocked(pos)) return;
        Vector2Int g = ToGrid(pos);
        int r = Vox(Rand(rng, pebbleRadius), 1, 3);
        int salt = rng.Next();
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            if (dx * dx + dz * dz > r * r + 1) continue;
            if (r == 1 && dx != 0 && dz != 0) continue;
            int h = Hash(g.x + dx, 0, g.y + dz, salt);
            int top = (r >= 2 && dx * dx + dz * dz <= (r - 1) * (r - 1)) ? 1 : 0;
            for (int y = 0; y <= top; y++) Put(g.x + dx, y, g.y + dz, Pick(rockShades, h + y));
        }
    }

    // =====================================================================================
    // Свої моделі з MagicaVoxel: розміром з траву, лише на лужайках, інстансинг без колайдерів
    // =====================================================================================

    class DecorModel
    {
        public Mesh mesh;
        public float halfFootprint;   // половина найбільшої ширини в висотах моделі (висота = 1)
    }

    class ModelCell
    {
        public List<Matrix4x4>[] byModel;
        public int total;
        public Bounds bounds;
    }

    DecorModel[] models = new DecorModel[0];   // спершу камені (stoneCount штук), далі квіти
    int stoneCount, flowerCount;
    Material modelMat;
    Texture2D modelPalette;
    bool modelsBuilt;

    readonly Dictionary<Vector2Int, ModelCell> modelCells = new Dictionary<Vector2Int, ModelCell>();
    readonly List<Vector2Int> modelPending = new List<Vector2Int>();
    readonly List<Vector2Int> modelRemove = new List<Vector2Int>();
    List<Matrix4x4>[] visible;
    readonly Matrix4x4[] batchBuf = new Matrix4x4[1023];
    readonly Plane[] planes = new Plane[6];
    Vector2Int modelCenter;
    int modelRadius;
    float modelVs;
    bool modelInit, modelFirstFill = true;

    [ContextMenu("Перебудувати моделі")]
    void RebuildModels()
    {
        DestroyModels();
        modelsBuilt = false;
        ResetModelStream();
    }

    void ResetModelStream()
    {
        modelCells.Clear();
        modelPending.Clear();
        modelInit = false;
        modelFirstFill = true;
    }

    void OnDestroy()
    {
        DestroyModels();
    }

    void DestroyModels()
    {
        foreach (var m in models) if (m != null && m.mesh != null) Destroy(m.mesh);
        models = new DecorModel[0];
        stoneCount = flowerCount = 0;
        visible = null;
        if (modelMat != null) Destroy(modelMat);
        if (modelPalette != null) Destroy(modelPalette);
        modelMat = null;
        modelPalette = null;
    }

    void BuildModels()
    {
        modelsBuilt = true;
        DestroyModels();

        // одна спільна палітра на всі 6 моделей: колір -> піксель у текстурі 256x1
        var pal = new Color32[256];
        pal[0] = new Color32(255, 0, 255, 255);
        var lookup = new Dictionary<uint, byte>();
        int palCount = 0;

        byte Idx(Color32 c)
        {
            uint key = (uint)(c.r | (c.g << 8) | (c.b << 16));
            if (lookup.TryGetValue(key, out byte existing)) return existing;
            if (palCount >= 255) return 255;
            palCount++;
            pal[palCount] = new Color32(c.r, c.g, c.b, 255);
            lookup[key] = (byte)palCount;
            return (byte)palCount;
        }

        var list = new List<DecorModel>();
        stoneCount = AddModels(stoneModels, list, Idx);
        flowerCount = AddModels(flowerModels, list, Idx);
        models = list.ToArray();
        if (models.Length == 0)
        {
            Debug.LogWarning("[WorldDecorator] Моделі не задані: перетягни .vox.bytes у Stone Models і Flower Models.", this);
            return;
        }

        modelPalette = new Texture2D(256, 1, TextureFormat.RGBA32, false)
        {
            name = "DecorPalette",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        modelPalette.SetPixels32(pal);
        modelPalette.Apply(false);

        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) lit = Shader.Find("Standard");
        modelMat = new Material(lit) { name = "DecorModels" };
        if (modelMat.HasProperty("_BaseMap")) modelMat.SetTexture("_BaseMap", modelPalette);
        if (modelMat.HasProperty("_MainTex")) modelMat.SetTexture("_MainTex", modelPalette);
        if (modelMat.HasProperty("_BaseColor")) modelMat.SetColor("_BaseColor", Color.white);
        if (modelMat.HasProperty("_Color")) modelMat.SetColor("_Color", Color.white);
        if (modelMat.HasProperty("_Smoothness")) modelMat.SetFloat("_Smoothness", 0f);
        if (modelMat.HasProperty("_Metallic")) modelMat.SetFloat("_Metallic", 0f);
        modelMat.enableInstancing = true;

        visible = new List<Matrix4x4>[models.Length];
        for (int i = 0; i < visible.Length; i++) visible[i] = new List<Matrix4x4>();

        Debug.Log("[WorldDecorator] Моделей: " + stoneCount + " каменів, " + flowerCount + " квіток.", this);
    }

    int AddModels(TextAsset[] assets, List<DecorModel> list, System.Func<Color32, byte> idx)
    {
        int added = 0;
        if (assets == null) return 0;
        foreach (var a in assets)
        {
            if (a == null) continue;
            var vox = VoxLoader.Load(a.bytes);
            if (vox == null || vox.voxels.Count == 0)
            {
                Debug.LogWarning("[WorldDecorator] Модель " + a.name + " порожня або це не .vox.", this);
                continue;
            }
            list.Add(BuildModelMesh(vox, idx, a.name));
            added++;
        }
        return added;
    }

    // Меш моделі висотою 1, центр по ширині, низ у y = 0 (масштабується матрицею до висоти травинки).
    // У MagicaVoxel вгору йде Z, у Unity Y, тож (x, y, z) -> (x, z, y), як у VoxelWorld.LoadVox.
    DecorModel BuildModelMesh(VoxLoader.Model vox, System.Func<Color32, byte> idx, string modelName)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var v in vox.voxels)
        {
            int ux = v.x, uy = v.z, uz = v.y;
            if (ux < minX) minX = ux; if (ux > maxX) maxX = ux;
            if (uy < minY) minY = uy; if (uy > maxY) maxY = uy;
            if (uz < minZ) minZ = uz; if (uz > maxZ) maxZ = uz;
        }
        int W = maxX - minX + 1, H = maxY - minY + 1, D = maxZ - minZ + 1;

        var grid = new byte[W * H * D];
        foreach (var v in vox.voxels)
        {
            Color32 col = vox.palette[Mathf.Max(0, v.color - 1)];
            int x = v.x - minX, y = v.z - minY, z = v.y - minZ;
            grid[x + W * (y + H * z)] = idx(col);
        }

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        float inv = 1f / H;

        for (int z = 0; z < D; z++)
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            byte id = grid[x + W * (y + H * z)];
            if (id == 0) continue;
            Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);

            for (int f = 0; f < 6; f++)
            {
                Vector3Int d = VoxelChunk.Dirs[f];
                int nx = x + d.x, ny = y + d.y, nz = z + d.z;
                if (nx >= 0 && nx < W && ny >= 0 && ny < H && nz >= 0 && nz < D &&
                    grid[nx + W * (ny + H * nz)] != 0) continue;   // грань закрита сусідом

                int start = verts.Count;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 fv = VoxelChunk.FaceVerts[f, k];
                    verts.Add(new Vector3((x + fv.x - W * 0.5f) * inv, (y + fv.y) * inv, (z + fv.z - D * 0.5f) * inv));
                    normals.Add(new Vector3(d.x, d.y, d.z));
                    uvs.Add(uv);
                }
                tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
                tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
            }
        }

        var mesh = new Mesh { name = "DecorModel_" + modelName, indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        return new DecorModel { mesh = mesh, halfFootprint = 0.5f * Mathf.Max(W, D) * inv };
    }

    // ---------- стрімінг моделей ----------

    void UpdateModels(Vector3 focus)
    {
        if (!modelsBuilt) BuildModels();
        if (models.Length == 0) return;

        float vs = grass.CellSize;
        if (!Mathf.Approximately(vs, modelVs))   // трава перебудувалась з іншим розміром клітинки
        {
            modelVs = vs;
            ResetModelStream();
        }

        int N = Mathf.Max(1, modelCellGrassCells);
        float cw = N * vs;
        var center = new Vector2Int(Mathf.FloorToInt(focus.x / cw), Mathf.FloorToInt(focus.z / cw));
        // видно рівно так далеко, як трава
        int R = Mathf.Max(1, Mathf.CeilToInt(grass.viewChunks * grass.chunkCells / (float)N));

        if (!modelInit || center != modelCenter || R != modelRadius)
        {
            RefreshModelPending(center, R);
            modelCenter = center;
            modelRadius = R;
            modelInit = true;
        }

        int budget = modelFirstFill ? int.MaxValue : Mathf.Max(1, modelCellsPerFrame);
        while (budget > 0 && modelPending.Count > 0)
        {
            Vector2Int key = modelPending[0];
            modelPending.RemoveAt(0);
            if (modelCells.ContainsKey(key)) continue;
            modelCells[key] = GenerateModelCell(key.x, key.y);
            budget--;
        }
        if (modelPending.Count == 0) modelFirstFill = false;

        RenderModels();
    }

    // Вивантажує клітинки позаду (вони детерміновані, тож повернуться такими самими) і складає чергу нових
    void RefreshModelPending(Vector2Int center, int R)
    {
        float keep = R + 1.5f;
        modelRemove.Clear();
        foreach (var kv in modelCells)
        {
            float dx = kv.Key.x - center.x, dz = kv.Key.y - center.y;
            if (dx * dx + dz * dz > keep * keep) modelRemove.Add(kv.Key);
        }
        foreach (var k in modelRemove) modelCells.Remove(k);

        modelPending.Clear();
        float want = R + 0.5f;
        for (int dz = -R - 1; dz <= R + 1; dz++)
        for (int dx = -R - 1; dx <= R + 1; dx++)
        {
            if (dx * dx + dz * dz > want * want) continue;
            var key = new Vector2Int(center.x + dx, center.y + dz);
            if (!modelCells.ContainsKey(key)) modelPending.Add(key);
        }
        modelPending.Sort((a, b) =>
        {
            float da = (a.x - center.x) * (a.x - center.x) + (a.y - center.y) * (a.y - center.y);
            float db = (b.x - center.x) * (b.x - center.x) + (b.y - center.y) * (b.y - center.y);
            return da.CompareTo(db);
        });
    }

    // Одна клітинка моделей = modelCellGrassCells x modelCellGrassCells клітинок сітки трави.
    // У кожній клітинці сітки трави на лужайці є невеликий шанс квітки чи каменя.
    ModelCell GenerateModelCell(int cx, int cz)
    {
        var cell = new ModelCell { byModel = new List<Matrix4x4>[models.Length] };
        int N = Mathf.Max(1, modelCellGrassCells);
        float vs = grass.CellSize;
        float grassH = grass.AverageBladeHeight;
        var rng = new System.Random(unchecked(seed * 83492791 ^ cx * 73856093 ^ cz * 19349663 ^ 0x5bd1e995));

        bool any = false;
        Bounds total = default;

        for (int lz = 0; lz < N; lz++)
        for (int lx = 0; lx < N; lx++)
        {
            int ix = cx * N + lx, iz = cz * N + lz;

            // порядок викликів rng сталий (завжди тягнемо всі числа), тож результат детермінований
            double rKind = rng.NextDouble(), rPick = rng.NextDouble(), rSize = rng.NextDouble();
            float jx = (float)rng.NextDouble(), jz = (float)rng.NextDouble();
            float yaw = (float)rng.NextDouble() * 360f;

            float m = onlyOnLawns ? grass.MeadowAt(ix, iz) : 1f;
            if (m < minMeadow) continue;

            // чим глибше в лужайці, тим частіше
            float dens = Mathf.Lerp(0.5f, 1f, Mathf.InverseLerp(minMeadow, 1f, m));
            float pFlower = flowerCount > 0 ? flowerChance * dens : 0f;
            float pStone = stoneCount > 0 ? stoneChance * dens : 0f;

            int mi;
            Vector2 heightRange;
            if (rKind < pFlower)
            {
                mi = stoneCount + Mathf.Min(flowerCount - 1, (int)(rPick * flowerCount));
                heightRange = flowerHeightInGrass;
            }
            else if (rKind < pFlower + pStone)
            {
                mi = Mathf.Min(stoneCount - 1, (int)(rPick * stoneCount));
                heightRange = stoneHeightInGrass;
            }
            else continue;

            float h = grassH * Mathf.Lerp(heightRange.x, heightRange.y, (float)rSize);
            float half = models[mi].halfFootprint * h;
            float px = (ix + 0.5f + (jx - 0.5f) * 0.9f) * vs;
            float pz = (iz + 0.5f + (jz - 0.5f) * 0.9f) * vs;

            // не на старті каменя (з запасом на ширину моделі)
            float clear = clearZoneInRockRadii * RockR + half;
            float sdx = px - spawn.x, sdz = pz - spawn.z;
            if (sdx * sdx + sdz * sdz < clear * clear) continue;

            // не всередині споруд із вокселів
            Vector2Int g = ToGrid(new Vector3(px, 0f, pz));
            if (world.GetVoxel(new Vector3Int(g.x, 0, g.y)) != 0) continue;

            if (!grass.TryGetGround(px, pz, out float gy)) continue;
            gy -= h * 0.03f;   // трохи в землю, щоб не було щілин

            var mat = Matrix4x4.TRS(new Vector3(px, gy, pz), Quaternion.Euler(0f, yaw, 0f), Vector3.one * h);
            if (cell.byModel[mi] == null) cell.byModel[mi] = new List<Matrix4x4>();
            cell.byModel[mi].Add(mat);
            cell.total++;

            var bb = new Bounds(new Vector3(px, gy + h * 0.5f, pz), new Vector3(half * 2.9f, h * 1.1f, half * 2.9f));
            if (!any) { total = bb; any = true; }
            else total.Encapsulate(bb);
        }

        cell.bounds = total;
        return cell;
    }

    // Збирає видимі екземпляри всіх клітинок і малює кожну модель кількома викликами інстансингу
    void RenderModels()
    {
        if (visible == null || modelMat == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);

        for (int i = 0; i < visible.Length; i++) visible[i].Clear();

        bool any = false;
        Bounds all = default;
        foreach (var cell in modelCells.Values)
        {
            if (cell.total == 0) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, cell.bounds)) continue;
            for (int i = 0; i < cell.byModel.Length; i++)
                if (cell.byModel[i] != null) visible[i].AddRange(cell.byModel[i]);
            if (!any) { all = cell.bounds; any = true; }
            else all.Encapsulate(cell.bounds);
        }
        if (!any) return;

        var rp = new RenderParams(modelMat)
        {
            shadowCastingMode = modelsCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off,
            receiveShadows = modelsReceiveShadows,
            layer = gameObject.layer,
            worldBounds = all
        };

        for (int i = 0; i < visible.Length; i++)
        {
            var list = visible[i];
            for (int s = 0; s < list.Count; s += batchBuf.Length)
            {
                int n = Mathf.Min(batchBuf.Length, list.Count - s);
                list.CopyTo(s, batchBuf, 0, n);
                Graphics.RenderMeshInstanced(rp, models[i].mesh, 0, batchBuf, n);
            }
        }
    }
}