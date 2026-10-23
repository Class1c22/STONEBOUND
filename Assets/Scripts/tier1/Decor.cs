using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Декор навколо каменя. Ставиться на ЛУЖАЙКАХ = галявинах без високої трави (карта береться з GrassField).
// Два шари:
//  1) дрібний декор у VoxelWorld (в масштабі каменя): валуни, горби землі, кущі, плями моху, камінці.
//     Квітів і грибів тут немає. Це звичайні вокселі, камінь ламає їх так само, як стіну.
//  2) твої моделі з MagicaVoxel (3 камені + 3 квітки) розміром з траву. Ставляться процедурно на галявинах,
//     трава під ними прибирається, малюються GPU-інстансингом, а камінь їх ламає так само, як траву:
//     вибиває кулю вокселів, відламані шматки падають, уламки-кубики він всмоктує по E.
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
    [Tooltip("Декор ставиться лише на галявинах без трави й не всередину травинок. Поки трава не створена, нічого не ставиться")]
    public bool onlyOnLawns = true;
    [Tooltip("Наскільки густою може бути трава в цьому місці (0..1). 0 = лише там, де зовсім немає лужайки трави, 0.3 = можна ближче до її країв")]
    [Range(0f, 1f)] public float maxMeadow = 0.1f;

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
    [Tooltip("Шанс квітки в клітинці сітки трави на галявині (0.01 = одна на 100 клітинок)")]
    [Range(0f, 0.2f)] public float flowerChance = 0.01f;
    [Tooltip("Шанс каменя в клітинці сітки трави на галявині")]
    [Range(0f, 0.2f)] public float stoneChance = 0.005f;
    [Tooltip("Сторона клітинки генерації моделей у клітинках сітки трави")]
    public int modelCellGrassCells = 6;
    [Tooltip("Скільки клітинок моделей генерується за кадр (перше заповнення йде одразу все)")]
    public int modelCellsPerFrame = 4;
    [Tooltip("Запас між краєм моделі й травою, у клітинках сітки трави. Збільш, якщо трава ще лізе в модель")]
    [Range(0f, 2f)] public float grassClearance = 0.35f;
    public bool modelsCastShadows = false;
    public bool modelsReceiveShadows = true;

    [Header("Ламання моделей")]
    public bool breakable = true;
    [Tooltip("Радіус ламання в радіусах каменя")]
    public float breakRadiusFactor = 1f;
    [Tooltip("Швидкість каменя, з якої удар максимальний")]
    public float fullDamageSpeed = 25f;
    [Tooltip("Наскільки сильний удар збільшує яму")]
    public float radiusBoostAtFullDamage = 0.6f;
    [Tooltip("На скільки дрібніших вокселів ділиться воксель моделі, коли її б'ють (більше = дрібніші дірки, але важче)")]
    [Range(1, 4)] public int breakSubdivision = 2;
    [Tooltip("Відірвані від землі шматки падають з фізикою")]
    public bool fallingPieces = true;
    [Tooltip("Менші шматки не падають, а розсипаються на кубики (в дрібних вокселях)")]
    public int minPieceCells = 12;
    public int maxPiecesPerHit = 6;
    [Tooltip("Маса відламаного шматка відносно маси каменя. Більше = важчий, камінь його не відкидає, а штовхає")]
    public float pieceWeight = 3f;
    [Tooltip("Маса уламка-кубика відносно маси каменя (мінімум 0.05)")]
    public float debrisWeight = 0.15f;
    [Tooltip("Максимальна швидкість, з якою фізика виштовхує шматок із каменя. Мало = не підстрибує, коли з'являється поруч з каменем")]
    public float maxPushOutSpeed = 1.5f;
    [Tooltip("Швидкість удару (м/с), з якої шматок, що впав або вдарився, ламається (вибиває яму в собі)")]
    public float pieceBreakSpeed = 3f;
    [Tooltip("Радіус ями від удару об землю, в частках висоти шматка")]
    public float pieceImpactRadius = 0.25f;
    [Tooltip("Скільки секунд після відлому шматок не ламається (щоб не розбивався в момент появи)")]
    public float pieceGraceTime = 0.5f;
    [Tooltip("Шматок ламає траву, в яку врізається, коли летить швидше за це (м/с)")]
    public float pieceGrassMinSpeed = 1f;
    [Tooltip("Радіус, яким шматок б'є траву, в частках його розміру")]
    public float pieceGrassRadius = 0.7f;
    [Tooltip("Яку частку швидкості шматок зберігає після удару по траві (менше = трава гальмує сильніше)")]
    [Range(0.5f, 1f)] public float pieceGrassDrag = 0.92f;
    public int maxPieces = 30;
    public float pieceLifetime = 25f;
    [Tooltip("Розмір уламка-кубика в радіусах стартового каменя")]
    public float debrisSizeInRockRadii = 0.45f;
    public float debrisSpread = 4f;
    [Tooltip("Розмір пулу кубиків (найстаріші використовуються повторно)")]
    public int maxDebris = 300;
    public int minCubesPerHit = 2;
    public int maxCubesPerHit = 40;
    [Range(0.05f, 1f)] public float cubeFraction = 0.25f;
    public float maxCubeGrow = 2.5f;
    public float absorbVolumeFactor = 1f;

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
        if (onlyOnLawns && !lawnReady) return;   // без карти лужаек і трави нічого не ставимо
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

    // Точка на галявині без трави (за картою лужаек із GrassField)
    bool ClearingAt(Vector3 p)
    {
        return grass.MeadowAt(p) <= maxMeadow;
    }

    bool OnLawn(Vector3 p)
    {
        if (!onlyOnLawns) return true;
        return grass != null && grass.Ready && ClearingAt(p);
    }

    // Сюди декор ставити не можна: біля старту каменя або там, де густа трава
    bool Blocked(Vector3 p)
    {
        return InClearZone(p) || !OnLawn(p);
    }

    // У колі радіуса radius стоїть травинка (декор вріс би в неї)
    bool GrassBlocks(Vector3 p, float radius)
    {
        return onlyOnLawns && grass != null && grass.Ready && grass.BladeNear(p, radius);
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
        if (GrassBlocks(pos, R * Vs)) return;
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
        if (GrassBlocks(pos, R * Vs)) return;
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
        if (GrassBlocks(pos, R * Vs)) return;
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
        if (GrassBlocks(pos, R * Vs)) return;
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
        if (GrassBlocks(pos, r * Vs)) return;
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
    // Свої моделі з MagicaVoxel: розміром з траву, на галявинах, ламаються як трава
    // =====================================================================================

    class DecorModel
    {
        public Mesh mesh;             // ціла модель, висота 1, центр по ширині, низ у y = 0
        public byte[] grid;           // ідентифікатори кольорів, індекс x + W * (y + H * z), y = вгору
        public int W, H, D;
        public float inv;             // 1 / H
        public float halfFootprint;   // половина найбільшої ширини в висотах моделі
    }

    // Один екземпляр моделі у світі
    class ModelInst
    {
        public int model;
        public Vector3 pos;
        public Quaternion rot;
        public float scale;           // висота моделі в метрах
        public Matrix4x4 matrix;
        public Bounds bounds;
        public byte[] cells;          // null = ціла; інакше дрібна сітка (W*s, H*s, D*s) після удару
        public int count;
        public Mesh mesh;             // меш вдарена версії
    }

    class ModelCell
    {
        public List<ModelInst> insts = new List<ModelInst>();
        public List<Matrix4x4>[] intact;   // матриці цілих екземплярів за моделлю
        public bool dirty = true;
        public Bounds bounds;
    }

    struct Dest
    {
        public Vector3 pos;
        public byte id;
        public float size;
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

    // ламання
    Rigidbody rockBody;
    readonly List<Dest> destroyed = new List<Dest>();
    readonly List<int> compIdx = new List<int>();
    readonly Stack<int> stack = new Stack<int>();
    readonly Dictionary<byte, Mesh> cubeMeshes = new Dictionary<byte, Mesh>();
    readonly Queue<GameObject> pieces = new Queue<GameObject>();
    readonly List<DecorPieceBody> livePieces = new List<DecorPieceBody>();
    Rigidbody[] debrisPool;
    int debrisIndex, piecesThisHit;
    Transform debrisRoot;

    [ContextMenu("Перебудувати моделі")]
    void RebuildModels()
    {
        DestroyModels();
        modelsBuilt = false;
        ResetModelStream();
    }

    void ResetModelStream()
    {
        if (grass != null) foreach (var k in modelCells.Keys) grass.RemoveFreeZones(k);
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
        foreach (var cell in modelCells.Values)
            foreach (var inst in cell.insts) if (inst.mesh != null) Destroy(inst.mesh);
        foreach (var m in cubeMeshes.Values) if (m != null) Destroy(m);
        cubeMeshes.Clear();
        models = new DecorModel[0];
        stoneCount = flowerCount = 0;
        visible = null;
        if (modelMat != null) Destroy(modelMat);
        if (modelPalette != null) Destroy(modelPalette);
        if (deadMat != null) Destroy(deadMat);
        deadMat = null;
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
            list.Add(BuildModel(vox, idx, a.name));
            added++;
        }
        return added;
    }

    // У MagicaVoxel вгору йде Z, у Unity Y, тож (x, y, z) -> (x, z, y), як у VoxelWorld.LoadVox.
    DecorModel BuildModel(VoxLoader.Model vox, System.Func<Color32, byte> idx, string modelName)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var v in vox.voxels)
        {
            int ux = v.x, uy = v.z, uz = v.y;
            if (ux < minX) minX = ux;
            if (ux > maxX) maxX = ux;
            if (uy < minY) minY = uy;
            if (uy > maxY) maxY = uy;
            if (uz < minZ) minZ = uz;
            if (uz > maxZ) maxZ = uz;
        }
        int W = maxX - minX + 1, H = maxY - minY + 1, D = maxZ - minZ + 1;

        var grid = new byte[W * H * D];
        foreach (var v in vox.voxels)
        {
            Color32 col = vox.palette[Mathf.Max(0, v.color - 1)];
            int x = v.x - minX, y = v.z - minY, z = v.y - minZ;
            grid[x + W * (y + H * z)] = idx(col);
        }

        var mesh = new Mesh { name = "DecorModel_" + modelName };
        FillMesh(mesh, grid, W, H, D);

        float inv = 1f / H;
        return new DecorModel
        {
            mesh = mesh, grid = grid, W = W, H = H, D = D, inv = inv,
            halfFootprint = 0.5f * Mathf.Max(W, D) * inv
        };
    }

    // ---------- меші ----------

    static readonly List<Vector3> mVerts = new List<Vector3>();
    static readonly List<Vector3> mNormals = new List<Vector3>();
    static readonly List<Vector2> mUvs = new List<Vector2>();
    static readonly List<int> mTris = new List<int>();

    // Меш сітки gw x gh x gd вокселів (індекс x + gw * (y + gh * z)). Висота моделі = 1, центр по ширині, низ у y = 0.
    // Ціла модель, вдарена модель і відірваний шматок мають однакові координати, тож ставляться тією самою матрицею.
    static void FillMesh(Mesh mesh, byte[] g, int gw, int gh, int gd)
    {
        mVerts.Clear(); mNormals.Clear(); mUvs.Clear(); mTris.Clear();
        float u = 1f / gh;

        for (int z = 0; z < gd; z++)
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
        {
            byte id = g[x + gw * (y + gh * z)];
            if (id == 0) continue;
            Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);

            for (int f = 0; f < 6; f++)
            {
                Vector3Int d = VoxelChunk.Dirs[f];
                int nx = x + d.x, ny = y + d.y, nz = z + d.z;
                if (nx >= 0 && nx < gw && ny >= 0 && ny < gh && nz >= 0 && nz < gd &&
                    g[nx + gw * (ny + gh * nz)] != 0) continue;   // грань закрита сусідом

                int start = mVerts.Count;
                for (int k = 0; k < 4; k++)
                {
                    Vector3 fv = VoxelChunk.FaceVerts[f, k];
                    mVerts.Add(new Vector3((x + fv.x - gw * 0.5f) * u, (y + fv.y) * u, (z + fv.z - gd * 0.5f) * u));
                    mNormals.Add(new Vector3(d.x, d.y, d.z));
                    mUvs.Add(uv);
                }
                mTris.Add(start); mTris.Add(start + 1); mTris.Add(start + 2);
                mTris.Add(start); mTris.Add(start + 2); mTris.Add(start + 3);
            }
        }

        mesh.Clear();
        mesh.indexFormat = IndexFormat.UInt32;
        if (mVerts.Count == 0) return;
        mesh.SetVertices(mVerts);
        mesh.SetNormals(mNormals);
        mesh.SetUVs(0, mUvs);
        mesh.SetTriangles(mTris, 0);
        mesh.RecalculateBounds();
    }

    // Кубик, у якого всі UV дивляться в піксель палітри з цим кольором
    Mesh CubeMesh(byte id)
    {
        if (cubeMeshes.TryGetValue(id, out Mesh cached) && cached != null) return cached;

        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();
        Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);
        for (int f = 0; f < 6; f++)
        {
            Vector3Int d = VoxelChunk.Dirs[f];
            int start = verts.Count;
            for (int k = 0; k < 4; k++)
            {
                verts.Add(VoxelChunk.FaceVerts[f, k] - new Vector3(0.5f, 0.5f, 0.5f));
                normals.Add(new Vector3(d.x, d.y, d.z));
                uvs.Add(uv);
            }
            tris.Add(start); tris.Add(start + 1); tris.Add(start + 2);
            tris.Add(start); tris.Add(start + 2); tris.Add(start + 3);
        }
        var mesh = new Mesh { name = "DecorCube" + id };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();
        cubeMeshes[id] = mesh;
        return mesh;
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
            modelCells[key] = GenerateModelCell(key);
            budget--;
        }
        if (modelPending.Count == 0) modelFirstFill = false;

        if (breakable) BreakAroundRock();
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
        foreach (var k in modelRemove)
        {
            foreach (var inst in modelCells[k].insts) if (inst.mesh != null) Destroy(inst.mesh);
            modelCells.Remove(k);
            grass.RemoveFreeZones(k);
        }

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
    // У кожній клітинці сітки трави на галявині є невеликий шанс квітки чи каменя.
    ModelCell GenerateModelCell(Vector2Int key)
    {
        int cx = key.x, cz = key.y;
        var cell = new ModelCell { intact = new List<Matrix4x4>[models.Length] };
        int N = Mathf.Max(1, modelCellGrassCells);
        float vs = grass.CellSize;
        float grassH = grass.AverageBladeHeight;
        var rng = new System.Random(unchecked(seed * 83492791 ^ cx * 73856093 ^ cz * 19349663 ^ 0x5bd1e995));
        var zones = new List<Vector3>();

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

            // швидка відсічка: клітинка явно в густій траві
            if (onlyOnLawns && grass.MeadowAt(ix, iz) > maxMeadow + 0.15f) continue;

            float pFlower = flowerCount > 0 ? flowerChance : 0f;
            float pStone = stoneCount > 0 ? stoneChance : 0f;

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

            // уся ширина моделі має стояти на галявині: центр і чотири точки по краях
            if (onlyOnLawns)
            {
                if (!ClearingAt(new Vector3(px, 0f, pz)) ||
                    !ClearingAt(new Vector3(px + half, 0f, pz)) || !ClearingAt(new Vector3(px - half, 0f, pz)) ||
                    !ClearingAt(new Vector3(px, 0f, pz + half)) || !ClearingAt(new Vector3(px, 0f, pz - half)))
                    continue;
            }

            // не на старті каменя (з запасом на ширину моделі)
            float clear = clearZoneInRockRadii * RockR + half;
            float sdx = px - spawn.x, sdz = pz - spawn.z;
            if (sdx * sdx + sdz * sdz < clear * clear) continue;

            // не всередині споруд із вокселів
            Vector2Int g = ToGrid(new Vector3(px, 0f, pz));
            if (world.GetVoxel(new Vector3Int(g.x, 0, g.y)) != 0) continue;

            if (!grass.TryGetGround(px, pz, out float gy)) continue;
            gy -= h * 0.03f;   // трохи в землю, щоб не було щілин

            var inst = new ModelInst
            {
                model = mi,
                pos = new Vector3(px, gy, pz),
                rot = Quaternion.Euler(0f, yaw, 0f),
                scale = h,
                count = 1
            };
            inst.matrix = Matrix4x4.TRS(inst.pos, inst.rot, Vector3.one * h);
            inst.bounds = new Bounds(new Vector3(px, gy + h * 0.5f, pz), new Vector3(half * 2.9f, h * 1.1f, half * 2.9f));
            cell.insts.Add(inst);

            // Під моделлю трава не росте. Зона = коло, що накриває всю квадратну основу при будь-якому yaw:
            // 1.42 (діагональ квадрата) * half + пів товщини травинки (її бокова грань) + запас grassClearance.
            float bladeHalf = grass.CellSize * 0.5f;
            zones.Add(new Vector3(px, pz, half * 1.42f + bladeHalf + grass.CellSize * grassClearance));

            if (!any) { total = inst.bounds; any = true; }
            else total.Encapsulate(inst.bounds);
        }

        cell.bounds = total;
        if (zones.Count > 0) grass.SetFreeZones(key, zones);
        return cell;
    }

    // ---------- малювання ----------

    void RebuildIntact(ModelCell cell)
    {
        cell.dirty = false;
        foreach (var l in cell.intact) if (l != null) l.Clear();
        foreach (var inst in cell.insts)
        {
            if (inst.cells != null) continue;
            if (cell.intact[inst.model] == null) cell.intact[inst.model] = new List<Matrix4x4>();
            cell.intact[inst.model].Add(inst.matrix);
        }
    }

    // Збирає видимі цілі екземпляри і малює кожну модель кількома викликами інстансингу;
    // вдарені екземпляри малюються по одному зі своїм мешем
    void RenderModels()
    {
        if (visible == null || modelMat == null) return;
        Camera cam = Camera.main;
        if (cam == null) return;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);

        var shadow = modelsCastShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
        for (int i = 0; i < visible.Length; i++) visible[i].Clear();

        bool any = false;
        Bounds all = default;
        foreach (var cell in modelCells.Values)
        {
            if (cell.insts.Count == 0) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, cell.bounds)) continue;
            if (cell.dirty) RebuildIntact(cell);

            for (int i = 0; i < cell.intact.Length; i++)
                if (cell.intact[i] != null) visible[i].AddRange(cell.intact[i]);
            if (!any) { all = cell.bounds; any = true; }
            else all.Encapsulate(cell.bounds);

            foreach (var inst in cell.insts)
            {
                if (inst.cells == null || inst.mesh == null) continue;
                var rpd = new RenderParams(modelMat)
                {
                    shadowCastingMode = shadow,
                    receiveShadows = modelsReceiveShadows,
                    layer = gameObject.layer,
                    worldBounds = inst.bounds
                };
                Graphics.RenderMesh(rpd, inst.mesh, 0, inst.matrix);
            }
        }
        if (!any) return;

        var rp = new RenderParams(modelMat)
        {
            shadowCastingMode = shadow,
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

    // =====================================================================================
    // Ламання моделей (так само, як у GrassField: куля вокселів, відламані шматки, кубики)
    // =====================================================================================

    void BreakAroundRock()
    {
        if (rock == null) return;
        if (rockBody == null) rockBody = rock.GetComponent<Rigidbody>();

        Vector3 c = rock.WorldCenter;
        float r = rock.Radius * breakRadiusFactor;
        Vector3 rockVel = rockBody != null ? rockBody.linearVelocity : Vector3.zero;

        // сила удару від швидкості каменя: сильніший удар = більша яма
        float damage = Mathf.Clamp01(rockVel.magnitude / Mathf.Max(0.01f, fullDamageSpeed));
        r *= 1f + damage * Mathf.Max(0f, radiusBoostAtFullDamage);
        float r2 = r * r;

        destroyed.Clear();
        piecesThisHit = 0;
        bool hit = false;

        foreach (var cell in modelCells.Values)
        {
            if (cell.insts.Count == 0 || cell.bounds.SqrDistance(c) > r2) continue;

            for (int i = cell.insts.Count - 1; i >= 0; i--)
            {
                ModelInst inst = cell.insts[i];
                if (inst.bounds.SqrDistance(c) > r2) continue;
                if (!Carve(inst, c, r, rockVel)) continue;

                hit = true;
                cell.dirty = true;   // цілий екземпляр вийшов з інстансингу (або зник)
                if (inst.count <= 0)
                {
                    if (inst.mesh != null) Destroy(inst.mesh);
                    cell.insts.RemoveAt(i);
                }
            }
        }

        // лежачі й падаючі шматки камінь ламає так само, як стоячі моделі
        for (int i = livePieces.Count - 1; i >= 0; i--)
        {
            DecorPieceBody p = livePieces[i];
            if (p == null || p.dead) { livePieces.RemoveAt(i); continue; }
            if (Time.time - p.spawnTime < pieceGraceTime) continue;
            if (p.rend.bounds.SqrDistance(c) > r2) continue;
            if (CarvePiece(p, c, r)) hit = true;
            if (p.dead) livePieces.RemoveAt(i);
        }

        if (hit) SpawnCubes(damage, c, rockVel, true);
        destroyed.Clear();
    }

    // =====================================================================================
    // Ламання відірваних шматків (падіння, удар об землю, удар каменя, удар по траві)
    // =====================================================================================

    // Вибиває кулю вокселів у шматку, відкидає уламки, що відірвались від його основної частини
    bool CarvePiece(DecorPieceBody p, Vector3 c, float r)
    {
        int gw = p.gw, gh = p.gh, gd = p.gd;
        Transform t = p.transform;
        float u = 1f / gh;
        float offX = gw * 0.5f * u, offZ = gd * 0.5f * u;
        Vector3 lc = Quaternion.Inverse(t.rotation) * (c - t.position) / p.scale;
        float rl = r / p.scale;
        float rl2 = rl * rl, half = u * 0.5f;

        int x0 = Mathf.Max(0, Mathf.FloorToInt((lc.x + offX - rl) / u));
        int x1 = Mathf.Min(gw - 1, Mathf.FloorToInt((lc.x + offX + rl) / u));
        int y0 = Mathf.Max(0, Mathf.FloorToInt((lc.y - rl) / u));
        int y1 = Mathf.Min(gh - 1, Mathf.FloorToInt((lc.y + rl) / u));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((lc.z + offZ - rl) / u));
        int z1 = Mathf.Min(gd - 1, Mathf.FloorToInt((lc.z + offZ + rl) / u));

        int removed = 0;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int idx = x + gw * (y + gh * z);
            byte id = p.cells[idx];
            if (id == 0) continue;

            Vector3 q = new Vector3((x + 0.5f) * u - offX, (y + 0.5f) * u, (z + 0.5f) * u - offZ);
            float dx = Mathf.Max(Mathf.Abs(lc.x - q.x) - half, 0f);
            float dy = Mathf.Max(Mathf.Abs(lc.y - q.y) - half, 0f);
            float dz = Mathf.Max(Mathf.Abs(lc.z - q.z) - half, 0f);
            if (dx * dx + dy * dy + dz * dz > rl2) continue;

            p.cells[idx] = 0;
            p.count--;
            removed++;
            AddDestPiece(p, x, y, z, id);
        }
        if (removed == 0) return false;

        if (p.count > 0) SplitPiece(p);
        RefreshPiece(p);
        return true;
    }

    void AddDestPiece(DecorPieceBody p, int x, int y, int z, byte id)
    {
        float u = 1f / p.gh;
        Vector3 lp = new Vector3((x + 0.5f - p.gw * 0.5f) * u, (y + 0.5f) * u, (z + 0.5f - p.gd * 0.5f) * u);
        destroyed.Add(new Dest { pos = p.transform.TransformPoint(lp), id = id, size = u * p.scale });
    }

    // Лишає найбільшу зв'язну частину шматка, решта розсипається на кубики
    void SplitPiece(DecorPieceBody p)
    {
        byte[] cells = p.cells;
        int gw = p.gw, gh = p.gh, gd = p.gd;
        int layer = gw * gh;
        var visited = new bool[cells.Length];
        int comps = 0, bestStart = -1, bestSize = 0;

        for (int start = 0; start < cells.Length; start++)
        {
            if (cells[start] == 0 || visited[start]) continue;
            comps++;
            int size = 0;
            stack.Clear();
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                size++;
                int z = idx / layer;
                int rem = idx - z * layer;
                int y = rem / gw;
                int x = rem - y * gw;
                TryVisit(cells, visited, x + 1, y, z, gw, gh, gd);
                TryVisit(cells, visited, x - 1, y, z, gw, gh, gd);
                TryVisit(cells, visited, x, y + 1, z, gw, gh, gd);
                TryVisit(cells, visited, x, y - 1, z, gw, gh, gd);
                TryVisit(cells, visited, x, y, z + 1, gw, gh, gd);
                TryVisit(cells, visited, x, y, z - 1, gw, gh, gd);
            }
            if (size > bestSize) { bestSize = size; bestStart = start; }
        }
        if (comps <= 1) return;

        // позначаємо найбільшу частину, все інше розсипається
        var keep = new bool[cells.Length];
        stack.Clear();
        stack.Push(bestStart);
        keep[bestStart] = true;
        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            int z = idx / layer;
            int rem = idx - z * layer;
            int y = rem / gw;
            int x = rem - y * gw;
            TryVisit(cells, keep, x + 1, y, z, gw, gh, gd);
            TryVisit(cells, keep, x - 1, y, z, gw, gh, gd);
            TryVisit(cells, keep, x, y + 1, z, gw, gh, gd);
            TryVisit(cells, keep, x, y - 1, z, gw, gh, gd);
            TryVisit(cells, keep, x, y, z + 1, gw, gh, gd);
            TryVisit(cells, keep, x, y, z - 1, gw, gh, gd);
        }

        int n = 0;
        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] == 0 || keep[i]) continue;
            if (n++ % 3 == 0)
            {
                int z = i / layer;
                int rem = i - z * layer;
                int y = rem / gw;
                int x = rem - y * gw;
                AddDestPiece(p, x, y, z, cells[i]);
            }
            cells[i] = 0;
            p.count--;
        }
    }

    // Оновлює меш, колайдер, масу й об'єм шматка після ламання
    void RefreshPiece(DecorPieceBody p)
    {
        if (p.count <= 0)
        {
            p.dead = true;
            Destroy(p.gameObject);
            return;
        }

        FillMesh(p.mesh, p.cells, p.gw, p.gh, p.gd);
        Bounds b = p.mesh.bounds;
        p.box.center = b.center;
        p.box.size = Vector3.Max(b.size, Vector3.one * 0.001f);
        p.rb.mass = Mathf.Max(0.05f, p.baseMass * p.count / (float)p.initialCount);

        var a = p.GetComponent<Absorbable>();
        if (a != null) a.volume = p.count * p.voxelVolume * absorbVolumeFactor;
    }

    // Викликається зі шматка при зіткненні: сильний удар об землю чи стіну вибиває яму в самому шматку
    public void OnPieceImpact(DecorPieceBody p, Collision col)
    {
        if (p.dead || Time.time - p.spawnTime < pieceGraceTime) return;
        if (col.collider.GetComponentInParent<RockAbsorber>() != null) return;   // камінь ламає шматок окремо
        if (col.rigidbody != null && col.rigidbody.mass < p.rb.mass * 0.2f) return;   // дрібні кубики не ламають

        float speed = col.relativeVelocity.magnitude;
        if (speed < pieceBreakSpeed) return;

        ContactPoint cp = col.GetContact(0);
        float damage = Mathf.Clamp01(speed / Mathf.Max(0.01f, fullDamageSpeed));
        float r = Mathf.Max(p.scale / p.gh * 1.5f, p.scale * pieceImpactRadius * (1f + damage));

        destroyed.Clear();
        if (CarvePiece(p, cp.point, r)) SpawnCubes(damage, cp.point, p.rb.linearVelocity, false);
        destroyed.Clear();
    }

    // Викликається зі шматка щофізикакадр: летючий шматок ламає траву, в яку врізається
    public void PieceVsGrass(DecorPieceBody p)
    {
        if (p.dead || grass == null || !grass.Ready || p.rb == null) return;
        Vector3 v = p.rb.linearVelocity;
        if (v.magnitude < pieceGrassMinSpeed) return;

        Bounds b = p.rend.bounds;
        float rad = (b.extents.x + b.extents.y + b.extents.z) / 3f * pieceGrassRadius;
        if (grass.BreakInSphere(b.center, rad, v) > 0) p.rb.linearVelocity = v * pieceGrassDrag;
    }

    // Дрібна сітка екземпляра: кожен воксель моделі ділиться на s x s x s
    void InitCells(ModelInst inst, DecorModel dm, int s)
    {
        int gw = dm.W * s, gh = dm.H * s, gd = dm.D * s;
        inst.cells = new byte[gw * gh * gd];
        inst.count = 0;
        for (int z = 0; z < gd; z++)
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
        {
            byte id = dm.grid[x / s + dm.W * (y / s + dm.H * (z / s))];
            if (id == 0) continue;
            inst.cells[x + gw * (y + gh * z)] = id;
            inst.count++;
        }
    }

    // Вибиває кулю вокселів. Повертає true, якщо щось змінилось.
    bool Carve(ModelInst inst, Vector3 c, float r, Vector3 rockVel)
    {
        DecorModel dm = models[inst.model];
        int s = Mathf.Clamp(breakSubdivision, 1, 4);
        int gw = dm.W * s, gh = dm.H * s, gd = dm.D * s;
        if (inst.cells == null || inst.cells.Length != gw * gh * gd) InitCells(inst, dm, s);

        float u = 1f / gh;                       // розмір малого вокселя в одиницях моделі (висота = 1)
        float offX = gw * 0.5f * u, offZ = gd * 0.5f * u;
        Vector3 lc = Quaternion.Inverse(inst.rot) * (c - inst.pos) / inst.scale;
        float rl = r / inst.scale;
        float rl2 = rl * rl, half = u * 0.5f;

        int x0 = Mathf.Max(0, Mathf.FloorToInt((lc.x + offX - rl) / u));
        int x1 = Mathf.Min(gw - 1, Mathf.FloorToInt((lc.x + offX + rl) / u));
        int y0 = Mathf.Max(0, Mathf.FloorToInt((lc.y - rl) / u));
        int y1 = Mathf.Min(gh - 1, Mathf.FloorToInt((lc.y + rl) / u));
        int z0 = Mathf.Max(0, Mathf.FloorToInt((lc.z + offZ - rl) / u));
        int z1 = Mathf.Min(gd - 1, Mathf.FloorToInt((lc.z + offZ + rl) / u));

        int removed = 0;
        for (int z = z0; z <= z1; z++)
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            int idx = x + gw * (y + gh * z);
            byte id = inst.cells[idx];
            if (id == 0) continue;

            // воксель зачеплено, якщо куля торкається його куба
            Vector3 p = new Vector3((x + 0.5f) * u - offX, (y + 0.5f) * u, (z + 0.5f) * u - offZ);
            float dx = Mathf.Max(Mathf.Abs(lc.x - p.x) - half, 0f);
            float dy = Mathf.Max(Mathf.Abs(lc.y - p.y) - half, 0f);
            float dz = Mathf.Max(Mathf.Abs(lc.z - p.z) - half, 0f);
            if (dx * dx + dy * dy + dz * dz > rl2) continue;

            inst.cells[idx] = 0;
            inst.count--;
            removed++;
            AddDest(inst, x, y, z, id, gw, gh, gd);
        }
        if (removed == 0) return false;

        // шматки, не з'єднані із землею (нижній шар), відламуються
        if (inst.count > 0) DetachFloating(inst, gw, gh, gd, rockVel, c);

        if (inst.count > 0)
        {
            if (inst.mesh == null) inst.mesh = new Mesh { name = "DecorDamaged" };
            FillMesh(inst.mesh, inst.cells, gw, gh, gd);
        }
        return true;
    }

    void AddDest(ModelInst inst, int x, int y, int z, byte id, int gw, int gh, int gd)
    {
        float u = 1f / gh;
        Vector3 p = new Vector3((x + 0.5f - gw * 0.5f) * u, (y + 0.5f) * u, (z + 0.5f - gd * 0.5f) * u);
        destroyed.Add(new Dest { pos = inst.pos + inst.rot * (p * inst.scale), id = id, size = u * inst.scale });
    }

    // Зв'язні групи вокселів (6 сусідів). Група без вокселів нижнього шару відірвана від землі.
    void DetachFloating(ModelInst inst, int gw, int gh, int gd, Vector3 rockVel, Vector3 rockCenter)
    {
        byte[] cells = inst.cells;
        int layer = gw * gh;
        var visited = new bool[cells.Length];

        for (int start = 0; start < cells.Length; start++)
        {
            if (cells[start] == 0 || visited[start]) continue;

            compIdx.Clear();
            stack.Clear();
            stack.Push(start);
            visited[start] = true;
            bool grounded = false;

            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                compIdx.Add(idx);
                int z = idx / layer;
                int rem = idx - z * layer;
                int y = rem / gw;
                int x = rem - y * gw;
                if (y == 0) grounded = true;

                TryVisit(cells, visited, x + 1, y, z, gw, gh, gd);
                TryVisit(cells, visited, x - 1, y, z, gw, gh, gd);
                TryVisit(cells, visited, x, y + 1, z, gw, gh, gd);
                TryVisit(cells, visited, x, y - 1, z, gw, gh, gd);
                TryVisit(cells, visited, x, y, z + 1, gw, gh, gd);
                TryVisit(cells, visited, x, y, z - 1, gw, gh, gd);
            }
            if (grounded) continue;

            // відірваний шматок
            if (fallingPieces && compIdx.Count >= minPieceCells && piecesThisHit < maxPiecesPerHit)
            {
                piecesThisHit++;
                SpawnPiece(inst, gw, gh, gd, rockCenter, rockVel);
            }
            else
            {
                // дрібний шматок (або забагато шматків): розсипається, беремо частину вокселів на кубики
                int step = Mathf.Max(1, compIdx.Count / 12);
                for (int i = 0; i < compIdx.Count; i += step)
                {
                    int idx = compIdx[i];
                    int z = idx / layer;
                    int rem = idx - z * layer;
                    int y = rem / gw;
                    int x = rem - y * gw;
                    AddDest(inst, x, y, z, cells[idx], gw, gh, gd);
                }
            }

            for (int i = 0; i < compIdx.Count; i++) cells[compIdx[i]] = 0;
            inst.count -= compIdx.Count;
        }
    }

    void TryVisit(byte[] cells, bool[] visited, int x, int y, int z, int gw, int gh, int gd)
    {
        if (x < 0 || x >= gw || y < 0 || y >= gh || z < 0 || z >= gd) return;
        int idx = x + gw * (y + gh * z);
        if (cells[idx] == 0 || visited[idx]) return;
        visited[idx] = true;
        stack.Push(idx);
    }

    void EnsureRoot()
    {
        if (debrisRoot == null) debrisRoot = new GameObject("DecorDebris").transform;
    }

    // Матеріал без відскоку і з великим тертям: шматки й кубики не пригають від каменя
    PhysicsMaterial deadMat;

    PhysicsMaterial DeadMaterial()
    {
        if (deadMat != null) return deadMat;
        deadMat = new PhysicsMaterial("DecorDead")
        {
            bounciness = 0f,
            dynamicFriction = 0.9f,
            staticFriction = 1f,
            bounceCombine = PhysicsMaterialCombine.Minimum,
            frictionCombine = PhysicsMaterialCombine.Maximum
        };
        return deadMat;
    }

    float RockMass()
    {
        if (rockBody == null && rock != null) rockBody = rock.GetComponent<Rigidbody>();
        return rockBody != null ? rockBody.mass : 1f;
    }

    // Падаючий шматок моделі: compIdx містить його вокселі
    void SpawnPiece(ModelInst inst, int gw, int gh, int gd, Vector3 rockCenter, Vector3 rockVel)
    {
        if (modelMat == null) return;
        EnsureRoot();

        // не тримаємо більше maxPieces шматків: найстаріші зникають
        while (pieces.Count >= Mathf.Max(1, maxPieces))
        {
            GameObject old = pieces.Dequeue();
            if (old != null) Destroy(old);
        }

        var mask = new byte[inst.cells.Length];
        for (int i = 0; i < compIdx.Count; i++) mask[compIdx[i]] = inst.cells[compIdx[i]];
        var mesh = new Mesh { name = "DecorPiece" };
        FillMesh(mesh, mask, gw, gh, gd);
        Destroy(mesh, Mathf.Max(1f, pieceLifetime) + 1f);

        var go = new GameObject("DecorPiece");
        go.transform.SetParent(debrisRoot, false);
        go.transform.SetPositionAndRotation(inst.pos, inst.rot);
        go.transform.localScale = Vector3.one * inst.scale;

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = modelMat;
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = modelsReceiveShadows;

        Bounds bnd = mesh.bounds;
        var box = go.AddComponent<BoxCollider>();
        box.center = bnd.center;
        box.size = Vector3.Max(bnd.size, Vector3.one * 0.001f);
        box.sharedMaterial = DeadMaterial();

        var rb = go.AddComponent<Rigidbody>();
        rb.mass = Mathf.Max(0.05f, RockMass() * pieceWeight);
        rb.linearDamping = 0.2f;
        rb.angularDamping = 1f;
        rb.maxDepenetrationVelocity = Mathf.Max(0.1f, maxPushOutSpeed);

        // валиться від каменя: горизонтальний напрямок від нього
        Vector3 away = go.transform.TransformPoint(bnd.center) - rockCenter;
        away.y = 0f;
        away = away.sqrMagnitude > 1e-4f ? away.normalized : new Vector3(Random.value - 0.5f, 0f, Random.value - 0.5f).normalized;
        rb.linearVelocity = rockVel * 0.4f + away * 0.5f;
        rb.angularVelocity = Vector3.Cross(Vector3.up, away) * 1.2f + Random.insideUnitSphere * 0.2f;

        // великий шматок камінь не всмокче (не пройде за розміром), але більший камінь згодом зможе
        float vox = inst.scale / gh;
        var a = go.AddComponent<Absorbable>();
        a.volume = compIdx.Count * vox * vox * vox * absorbVolumeFactor;

        // шматок знає свої вокселі: його можна розбити (удар об землю, камінь), він б'є траву
        var body = go.AddComponent<DecorPieceBody>();
        body.owner = this;
        body.cells = mask;
        body.gw = gw; body.gh = gh; body.gd = gd;
        body.scale = inst.scale;
        body.count = body.initialCount = compIdx.Count;
        body.voxelVolume = vox * vox * vox;
        body.baseMass = rb.mass;
        body.spawnTime = Time.time;
        body.mesh = mesh;
        body.rb = rb;
        body.box = box;
        body.rend = rend;
        livePieces.Add(body);

        pieces.Enqueue(go);
        Destroy(go, Mathf.Max(1f, pieceLifetime));
    }

    // ---------- уламки-кубики ----------

    float DebrisEdge()
    {
        return Mathf.Max(0.02f, RockR * debrisSizeInRockRadii);
    }

    // Кубики з вибитих вокселів: слабкий удар = мало, сильний = більше, але не більше maxCubesPerHit
    // origin = центр каменя (avoidRock = true) або точка удару шматка (avoidRock = false)
    void SpawnCubes(float damage, Vector3 rockCenter, Vector3 rockVel, bool avoidRock)
    {
        int n = destroyed.Count;
        if (n == 0 || modelMat == null) return;

        int count = Mathf.CeilToInt(n * cubeFraction * (0.5f + damage));
        count = Mathf.Clamp(count, Mathf.Max(1, minCubesPerHit), Mathf.Max(1, maxCubesPerHit));

        // менше кубиків = кожен більший (об'єм зберігається)
        float volume = 0f;
        for (int i = 0; i < n; i++) volume += destroyed[i].size * destroyed[i].size * destroyed[i].size;
        float baseEdge = DebrisEdge();
        float edge = Mathf.Clamp(Mathf.Pow(volume / count, 1f / 3f), baseEdge * 0.7f, baseEdge * Mathf.Max(1f, maxCubeGrow));

        for (int k = 0; k < count; k++)
        {
            Dest dst = destroyed[Random.Range(0, n)];
            Vector3 pos = dst.pos + Random.insideUnitSphere * (dst.size * 0.4f);

            // кубик не має з'являтись усередині каменя (інакше фізика його різко виштовхне)
            Vector3 dir = pos - rockCenter;
            float dist = dir.magnitude;
            dir = dist > 1e-4f ? dir / dist : Random.onUnitSphere;
            float minDist = avoidRock ? rock.Radius * 1.1f : 0f;
            if (dist < minDist) pos = rockCenter + dir * minDist;

            Rigidbody d = GetDebris(dst.id);
            d.transform.SetPositionAndRotation(pos, Random.rotation);
            d.linearVelocity = dir * Random.Range(1f, debrisSpread) + rockVel * 0.3f + Random.insideUnitSphere;
            d.angularVelocity = Random.insideUnitSphere * 5f;

            // розмір і об'єм кубика (камінь росте від об'єму, тому він має відповідати розміру)
            d.transform.localScale = Vector3.one * edge;
            var ab = d.GetComponent<Absorbable>();
            if (ab != null) ab.volume = edge * edge * edge * absorbVolumeFactor;
        }
    }

    Rigidbody GetDebris(byte id)
    {
        if (debrisPool == null) debrisPool = new Rigidbody[Mathf.Max(1, maxDebris)];
        EnsureRoot();

        Rigidbody d = debrisPool[debrisIndex];
        if (d == null)
        {
            // порожній слот (ще не створювали або камінь уже всмоктав кубик): робимо новий
            var cube = new GameObject("DecorCube");
            cube.transform.SetParent(debrisRoot, false);
            cube.AddComponent<MeshFilter>();
            var rend = cube.AddComponent<MeshRenderer>();
            rend.sharedMaterial = modelMat;
            rend.shadowCastingMode = ShadowCastingMode.Off;
            var box = cube.AddComponent<BoxCollider>();
            box.size = Vector3.one;
            box.sharedMaterial = DeadMaterial();
            d = cube.AddComponent<Rigidbody>();
            d.mass = Mathf.Max(0.05f, RockMass() * debrisWeight);
            d.angularDamping = 0.5f;
            d.maxDepenetrationVelocity = Mathf.Max(0.1f, maxPushOutSpeed);
            cube.AddComponent<Absorbable>();
            debrisPool[debrisIndex] = d;
        }
        else
        {
            var a = d.GetComponent<Absorbable>();
            if (a != null) a.attached = false;
        }
        d.GetComponent<MeshFilter>().sharedMesh = CubeMesh(id);
        debrisIndex = (debrisIndex + 1) % debrisPool.Length;   // найстаріший кубик використовується повторно
        return d;
    }
}

// Відірваний від моделі шматок. Живе в WorldDecorator.cs, бо створюється лише кодом (AddComponent).
// Знає свої вокселі, тому ламається при ударі об землю чи від каменя, а в польоті ламає траву.
public class DecorPieceBody : MonoBehaviour
{
    [System.NonSerialized] public WorldDecorator owner;
    [System.NonSerialized] public byte[] cells;
    [System.NonSerialized] public int gw, gh, gd, count, initialCount;
    [System.NonSerialized] public float scale, voxelVolume, baseMass, spawnTime;
    [System.NonSerialized] public Mesh mesh;
    [System.NonSerialized] public Rigidbody rb;
    [System.NonSerialized] public BoxCollider box;
    [System.NonSerialized] public MeshRenderer rend;
    [System.NonSerialized] public bool dead;

    void OnCollisionEnter(Collision col)
    {
        if (owner != null) owner.OnPieceImpact(this, col);
    }

    void FixedUpdate()
    {
        if (owner != null) owner.PieceVsGrass(this);
    }
}