using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Генератор сіл для 3 рівня: окремі села (хати з дворами, комори, церква з цвинтарем, вітряк, криниця, площа з дубом,
// поля, баштан, ставок, річка з мостом), між якими лежить густий ліс. Повісь на порожній GameObject,
// VoxelWorld і камінь знайдуться самі.
// Підлога має бути на y = 0 (FloorGenerator), VoxelWorld на (0,0,0) з масштабом 1.
//
// Усі розміри тут у ВОКСЕЛЯХ, тому село масштабується разом із world.voxelSize: виставляєш розмір вокселя
// сцени 3 (приблизно в 4-5 разів більший за тир 2) і нічого більше не міняєш.
//
// Як це працює (усе росте навколо каменя й без кінця):
//  * Світ ділиться на регіони 2R x 2R вокселів. Регіон з'являється, коли камінь підходить ближче за Spawn Radius.
//    Кожен регіон це ОКРЕМЕ СЕЛО зі своїм планом: головна дорога, бічні вулички, церква, криниця, вітряк,
//    площа з дубом, двори вздовж доріг, за селом поля, лука, сад, баштан, ставок і невеликі гаї.
//  * З УСІХ чотирьох боків регіону лежить смуга густого лісу (Border Band). Між двома селами вона подвоюється,
//    тож села чітко розділені лісом.
//  * Головна дорога й річка залежать від глобальної координати x, тому вони проходять крізь ліс і зшивають
//    сусідні села в одну безкінечну смугу. Дерева дорогу не перекривають.
//  * Регіон ріжеться на плитки (Tile Size). Плитки будуються лише поблизу каменя, найближчі першими, і
//    розставляються по кадрах (Voxels Per Frame), тож підвисань немає.
//  * Дороги й річка плоскі (без колайдера): камінь їде по підлозі й не ламає їх. Усе інше справжні вокселі,
//    і камінь ламає їх.
//  * Навколо стартової позиції каменя нічого не ставиться (Start Clear In Rock Radii), крім доріг.
//  * Те саме значення Seed завжди дає те саме село.
public class VillageGenerator : MonoBehaviour
{
    [Header("Зв'язки")]
    [Tooltip("Якщо порожньо, береться VoxelWorld зі сцени")]
    public VoxelWorld world;
    public int seed = 3003;

    [Header("Стрімінг")]
    [Tooltip("Як далеко від каменя з'являється село (метри)")]
    public float spawnRadius = 150f;
    [Tooltip("Те саме, але в радіусах каменя: коли камінь росте, село з'являється далі. Береться більше з двох")]
    public float spawnRadiusInRockRadii = 80f;
    [Tooltip("Навколо стартової позиції каменя нічого не ставиться (в радіусах каменя). Дороги лишаються")]
    public float startClearInRockRadii = 6f;
    [Tooltip("Скільки вокселів ставити за кадр. Менше = плавніше, але село з'являється довше")]
    public int voxelsPerFrame = 6000;

    [Header("Регіон (у вокселях)")]
    [Tooltip("Півсторона одного села-регіону. Мінімум 120, інакше річка й поля не вміщаються")]
    public int mapRadius = 150;
    [Tooltip("Сторона плитки, яка будується за раз")]
    public int tileSize = 48;
    [Tooltip("Ширина лісу з КОЖНОГО боку регіону. Між двома селами ліс удвічі ширший")]
    public int borderBand = 30;
    [Tooltip("Щільність лісу в смузі розділювача (0..1)")]
    [Range(0.3f, 1f)] public float forestDensity = 0.92f;

    [Header("Дороги (у вокселях)")]
    public int mainRoadWidth = 6;
    public int laneWidth = 4;
    [Tooltip("Наскільки головна дорога в'ється")]
    public float roadCurve = 14f;
    [Tooltip("Частота звивів (менше = плавніше)")]
    public float roadWave = 0.035f;
    [Tooltip("Висота плоскої дороги над підлогою (метри), щоб не мерехтіла разом із підлогою")]
    public float roadHeight = 0.03f;
    [Tooltip("Скільки бічних вуличок (крім дороги через міст)")]
    public int laneCount = 6;
    public int laneLengthMin = 28;
    public int laneLengthMax = 50;

    [Header("Річка")]
    public bool river = true;
    public int riverWidth = 12;

    [Header("Двори й хати (у вокселях)")]
    [Tooltip("На скільки вокселів в обидва боки від центру тягнеться село вздовж головної дороги")]
    public int villageHalfLength = 80;
    public int yardWidthMin = 20;
    public int yardWidthMax = 26;
    public int yardDepth = 22;
    [Tooltip("Решта дворів: город із грядками")]
    [Range(0f, 1f)] public float houseChance = 0.85f;
    public int houseWidthMin = 10;
    public int houseWidthMax = 13;
    public int houseDepthMin = 7;
    public int houseDepthMax = 9;
    public int wallHeight = 5;
    [Tooltip("Шанс солом'яного даху (інакше черепиця або дранка)")]
    [Range(0f, 1f)] public float thatchChance = 0.5f;
    [Range(0f, 1f)] public float chimneyChance = 0.6f;
    [Range(0f, 1f)] public float fenceChance = 0.8f;
    public int treesPerYardMax = 2;

    [Header("Атмосфера")]
    [Tooltip("Шанс, що хата двоповерхова (з балконом)")]
    [Range(0f, 1f)] public float twoStoryChance = 0.18f;
    [Tooltip("Шанс, що двір це комора з сіном")]
    [Range(0f, 1f)] public float barnChance = 0.12f;
    [Tooltip("Скільки дрібниць максимум у дворі: дрова, копиці, вулики, корова, свині з курами, віз, будка")]
    public int yardExtrasMax = 3;
    [Tooltip("Шанс могили в кожному місці цвинтаря біля церкви")]
    [Range(0f, 1f)] public float graveChance = 0.55f;
    [Tooltip("Відстань між ліхтарними стовпами вздовж головної дороги (0 = без стовпів)")]
    public int poleSpacing = 28;

    [Header("Околиці (у вокселях)")]
    [Tooltip("Розмір ділянки поля/лісу")]
    public int plotSize = 48;

    // ---------- внутрішній стан ----------

    struct VoxelOp
    {
        public Vector3Int pos;
        public byte id;
    }

    class Lane
    {
        public int x, side, len, endZ;
        public bool exit;
    }

    readonly List<VoxelOp> ops = new List<VoxelOp>();

    // Регіон = одне село. Координати всередині регіону локальні: від -M до M. Світові = локальні + (ox, oz)
    class Region
    {
        public Vector2Int key;
        public int ox, oz;
        public byte[] roadKind;     // 0 = нічого, 1 = головна дорога, 2 = вуличка, 3 = міст
        public bool[] riverMask;
        public bool[] occ;          // зайнято дворами, церквою, вітряком
        public int[] roadZ;         // z осі головної дороги для кожного x
        public List<Lane> lanes = new List<Lane>();
        public Dictionary<Vector2Int, List<System.Action>> jobs = new Dictionary<Vector2Int, List<System.Action>>();
        public List<Vector2Int> pending = new List<Vector2Int>();
    }

    readonly Dictionary<Vector2Int, Region> regions = new Dictionary<Vector2Int, Region>();
    Region cur;
    int curOx, curOz;

    // Посилання на дані поточного регіону (SetCur перемикає їх), тож весь код нижче працює з "поточним" регіоном
    byte[] roadKind;
    bool[] riverMask;
    bool[] occ;
    int[] roadZ;
    List<Lane> lanes;
    Dictionary<Vector2Int, List<System.Action>> jobs;

    float vs;
    int M, N, T, tilesPerSide, hw, lw;
    float roadPh1, roadPh2, riverPh;   // глобальні фази: однакові в усіх регіонах, тож дорога й річка зшиваються

    RockAbsorber rock;
    bool haveStart;
    Vector2 rockV, startV;             // позиція каменя й його старт у світових вокселях
    float startClearV;

    // поточне перетворення для малювання будівель: facing 0..3, початок, розміри
    int tF, tOx, tOz, tW, tD;

    // буфери плоского меша
    readonly List<Vector3> mVerts = new List<Vector3>();
    readonly List<Vector3> mNormals = new List<Vector3>();
    readonly List<Vector2> mUvs = new List<Vector2>();
    readonly List<int> mTris = new List<int>();

    // кольори (індекси палітри VoxelWorld)
    byte[] wallSet, stripeSet, shutterSet, thatchSet, tileSet, shingleSet, gravelSet, dirtSet, plankSet, barnSet;
    byte[][] leafSets;
    byte[] pineSet, flowerColors, domeSet, wheatSet;
    byte cGlass, cDoor, cFloor, cBrick, cCap, cPath, cPost, cRail, cBarkA, cBarkB;
    byte cWater, cWaterLight, cSoil, cSoilDark, cStem, cLeaf, cSun, cSunCenter, cStraw, cStraw2;
    byte cCabbage, cCabbage2, cCorn, cLog, cLogB, cStone, cStone2, cGold, cSail, cMillWall, cMillBase, cMillRoof;
    byte cCowW, cCowB, cPink, cPigDark, cRed, cPumpkin, cMelon;

    // ---------- публічне для інших скриптів ----------

    // Точка на головній дорозі в центрі першого села (світові координати): сюди можна ставити камінь на старті
    public Vector3 StartPoint
    {
        get
        {
            if (world == null || vs <= 0f) return transform.position;
            return world.transform.TransformPoint(new Vector3(0f, 0f, RoadZFunc(0) * vs));
        }
    }
    public int TileVoxels => T;
    public int RegionRadiusVoxels => M;

    // ---------- старт ----------

    void Start()
    {
        if (world == null) world = FindFirstObjectByType<VoxelWorld>();
        if (world == null)
        {
            Debug.LogWarning("[VillageGenerator] Не знайдено VoxelWorld", this);
            enabled = false;
            return;
        }

        vs = world.voxelSize;
        M = Mathf.Max(60, mapRadius);
        N = 2 * M;
        T = Mathf.Max(16, tileSize);
        tilesPerSide = Mathf.CeilToInt(N / (float)T);
        hw = Mathf.Max(2, mainRoadWidth / 2);
        lw = Mathf.Max(3, laneWidth);

        if (M < 120)
            Debug.LogWarning("[VillageGenerator] Map Radius менший за 120: річка, поля й вуличка до мосту можуть не вміститись", this);

        // глобальні фази дороги й річки
        var g = new System.Random(seed);
        roadPh1 = (float)g.NextDouble() * 6.28f;
        roadPh2 = (float)g.NextDouble() * 6.28f;
        riverPh = (float)g.NextDouble() * 6.28f;

        InitColors();
        StartCoroutine(Worker());
    }

    byte C(int r, int g, int b)
    {
        return world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));
    }

    void InitColors()
    {
        wallSet = new[] { C(244, 242, 232), C(236, 232, 220), C(247, 245, 238) };
        stripeSet = new[] { C(70, 120, 175), C(200, 150, 70), C(130, 90, 60), C(90, 140, 110) };
        shutterSet = new[] { C(60, 110, 170), C(70, 130, 90), C(170, 70, 60) };
        thatchSet = new[] { C(196, 166, 92), C(184, 152, 80), C(206, 176, 104) };
        tileSet = new[] { C(168, 62, 50), C(152, 54, 44), C(180, 74, 58) };
        shingleSet = new[] { C(110, 78, 52), C(98, 68, 46), C(122, 88, 60) };
        gravelSet = new[] { C(150, 135, 112), C(140, 126, 104), C(120, 110, 92) };
        dirtSet = new[] { C(128, 100, 70), C(120, 94, 66) };
        plankSet = new[] { C(170, 130, 85), C(156, 118, 76) };
        barnSet = new[] { C(165, 58, 48), C(150, 50, 42), C(176, 66, 54) };

        cGlass = C(160, 200, 225);
        cDoor = C(110, 70, 42);
        cFloor = C(180, 150, 110);
        cBrick = C(150, 74, 58);
        cCap = C(96, 48, 40);
        cPath = C(160, 140, 108);
        cPost = C(95, 62, 40);
        cRail = C(150, 115, 80);
        cBarkA = C(100, 70, 45);
        cBarkB = C(88, 60, 38);

        leafSets = new[]
        {
            new[] { C(58, 125, 52), C(72, 145, 58), C(48, 108, 46) },
            new[] { C(95, 160, 70), C(110, 175, 80), C(80, 140, 62) },
            new[] { C(190, 70, 70), C(86, 150, 62), C(70, 130, 56) },   // яблуня з плодами
        };
        pineSet = new[] { C(35, 95, 60), C(42, 110, 66), C(30, 82, 52) };

        cWater = C(60, 130, 200);
        cWaterLight = C(130, 190, 235);

        cSoil = C(100, 72, 48);
        cSoilDark = C(86, 62, 40);
        cStem = C(70, 130, 56);
        cLeaf = C(60, 130, 60);
        cSun = C(250, 200, 40);
        cSunCenter = C(110, 70, 30);
        cStraw = C(214, 178, 76);
        cStraw2 = C(196, 160, 60);
        wheatSet = new[] { C(214, 178, 70), C(200, 160, 56), C(224, 190, 84) };
        cCabbage = C(90, 160, 70);
        cCabbage2 = C(140, 200, 100);
        cCorn = C(210, 196, 80);
        flowerColors = new[] { C(240, 240, 235), C(250, 210, 60), C(220, 70, 80), C(150, 110, 200), C(90, 150, 230) };

        cLog = C(150, 106, 62);
        cLogB = C(136, 94, 54);
        cStone = C(150, 150, 146);
        cStone2 = C(124, 124, 120);
        cGold = C(235, 190, 60);
        domeSet = new[] { C(60, 130, 110), C(70, 110, 170), C(235, 190, 60) };
        cSail = C(232, 226, 210);
        cMillWall = C(240, 236, 224);
        cMillBase = C(140, 120, 100);
        cMillRoof = C(110, 70, 50);

        cCowW = C(240, 238, 232);
        cCowB = C(44, 42, 46);
        cPink = C(240, 170, 170);
        cPigDark = C(214, 134, 134);
        cRed = C(200, 40, 40);
        cPumpkin = C(232, 128, 36);
        cMelon = C(54, 128, 72);
    }

    // ---------- допоміжне ----------

    int I(int x, int z) { return (x + M) + N * (z + M); }
    bool InMap(int x, int z) { return x >= -M && x < M && z >= -M && z < M; }
    int RoadZAt(int x) { return roadZ[Mathf.Clamp(x + M, 0, N - 1)]; }

    // Внутрішня межа села: далі починається ліс-розділювач
    int Inner { get { return M - Mathf.Max(8, borderBand); } }

    bool Free(int x, int z)
    {
        if (!InMap(x, z)) return false;
        int i = I(x, z);
        if (roadKind[i] != 0 || riverMask[i] || occ[i]) return false;

        // біля старту каменя нічого не ставимо (дороги лишаються)
        if (haveStart)
        {
            float dx = x + curOx - startV.x, dz = z + curOz - startV.y;
            if (dx * dx + dz * dz < startClearV * startClearV) return false;
        }
        return true;
    }

    // Вісь головної дороги за глобальною координатою x: однакова в усіх регіонах, тож дорога неперервна
    int RoadZFunc(int xGlobal)
    {
        return Mathf.RoundToInt(roadCurve * Mathf.Sin(xGlobal * roadWave + roadPh1) +
                                roadCurve * 0.4f * Mathf.Sin(xGlobal * roadWave * 2.7f + roadPh2));
    }

    int RiverZFunc(int xGlobal)
    {
        return Mathf.RoundToInt(-0.6f * M + 8f * Mathf.Sin(xGlobal * 0.03f + riverPh));
    }

    void SetCur(Region r)
    {
        cur = r;
        curOx = r.ox;
        curOz = r.oz;
        roadKind = r.roadKind;
        riverMask = r.riverMask;
        occ = r.occ;
        roadZ = r.roadZ;
        lanes = r.lanes;
        jobs = r.jobs;
    }

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    bool TreeClear(int x, int z, int r)
    {
        return Free(x, z) && Free(x + r, z) && Free(x - r, z) && Free(x, z + r) && Free(x, z - r);
    }

    bool RectClear(RectInt r)
    {
        for (int z = r.y; z < r.yMax; z++)
        for (int x = r.x; x < r.xMax; x++)
            if (!Free(x, z)) return false;
        return true;
    }

    void MarkOcc(RectInt r)
    {
        for (int z = r.y; z < r.yMax; z++)
        for (int x = r.x; x < r.xMax; x++)
            if (InMap(x, z)) occ[I(x, z)] = true;
    }

    void MarkRoad(int x, int z, int kind)
    {
        if (!InMap(x, z)) return;
        int i = I(x, z);
        if (roadKind[i] != 0) return;
        roadKind[i] = riverMask[i] ? (byte)3 : (byte)kind;
    }

    Vector2Int TileOf(int x, int z)
    {
        return new Vector2Int(Mathf.Clamp(x + M, 0, N - 1) / T, Mathf.Clamp(z + M, 0, N - 1) / T);
    }

    void AddJob(int x, int z, System.Action a)
    {
        Vector2Int tile = TileOf(x, z);
        if (!jobs.TryGetValue(tile, out var list))
        {
            list = new List<System.Action>();
            jobs[tile] = list;
        }
        list.Add(a);
    }

    static byte Pick(byte[] set, int h)
    {
        return set[(h & 0x7fffffff) % set.Length];
    }

    // Двір уздовж головної дороги: x0, ширина вздовж дороги, глибина від дороги, side = +1 північ / -1 південь
    RectInt YardOnRoad(int x0, int width, int depth, int side)
    {
        int zMax = int.MinValue, zMin = int.MaxValue;
        for (int x = x0; x < x0 + width; x++)
        {
            int z = RoadZAt(x);
            if (z > zMax) zMax = z;
            if (z < zMin) zMin = z;
        }
        if (side > 0) return new RectInt(x0, zMax + hw + 2, width, depth);
        return new RectInt(x0, zMin - hw - 2 - depth, width, depth);
    }

    // Що стоїть у дворі: 0 город, 1 хата, 2 двоповерхова хата, 3 комора
    int PickKind(System.Random rng)
    {
        if (rng.NextDouble() >= houseChance) return 0;
        double q = rng.NextDouble();
        if (q < twoStoryChance) return 2;
        if (q < twoStoryChance + barnChance) return 3;
        return 1;
    }

    // ---------- план села ----------

    // План одного села. Викликається після SetCur(reg): дані регіону вже виділені в reg
    void Plan(Region reg)
    {
        var rng = new System.Random(Hash(seed ^ 0x3131, reg.key.x, reg.key.y));
        int inner = Inner;

        // головна дорога (функція глобального x)
        for (int i = 0; i < N; i++) roadZ[i] = RoadZFunc(i - M + curOx);

        // річка на півдні (теж функція глобального x)
        if (river)
        {
            int rw = Mathf.Max(4, riverWidth);
            int lastC = RiverZFunc(-M - 1 + curOx);
            for (int i = 0; i < N; i++)
            {
                int x = i - M;
                int c = RiverZFunc(x + curOx);
                int lo = Mathf.Min(c, lastC) - rw / 2, hi = Mathf.Max(c, lastC) + rw / 2;
                for (int z = lo; z <= hi; z++)
                    if (InMap(x, z)) riverMask[I(x, z)] = true;
                lastC = c;
            }
        }

        // головна дорога (проходить крізь ліс до сусідніх сіл)
        for (int i = 0; i < N; i++)
        {
            int x = i - M;
            int zA = roadZ[i], zB = roadZ[Mathf.Min(i + 1, N - 1)];
            int lo = Mathf.Min(zA, zB) - hw;
            int hi = Mathf.Max(zA, zB) + hw - 1 + (mainRoadWidth & 1);
            for (int z = lo; z <= hi; z++) MarkRoad(x, z, 1);
        }

        // бічні вулички й дорога на південь (через міст)
        int exitX = rng.Next(-15, 16);
        int count = Mathf.Max(0, laneCount);
        float span = Mathf.Max(10f, villageHalfLength - 25f);
        for (int k = 0; k < count; k++)
        {
            float t = count == 1 ? 0.5f : k / (float)(count - 1);
            int lx = Mathf.RoundToInt(Mathf.Lerp(-span, span, t)) + rng.Next(-5, 6);
            if (Mathf.Abs(lx - exitX) < 14) continue;
            lanes.Add(new Lane
            {
                x = lx,
                side = (k & 1) == 0 ? 1 : -1,
                len = hw + RI(rng, laneLengthMin, laneLengthMax),
                exit = false
            });
        }
        // дорога через міст губиться в лісі, не доходячи до краю регіону
        lanes.Add(new Lane { x = exitX, side = -1, len = Mathf.Max(10, RoadZAt(exitX) + M - borderBand / 2), exit = true });

        foreach (var ln in lanes)
        {
            int x0 = ln.x - lw / 2;
            int zStart = RoadZAt(ln.x);
            ln.endZ = zStart;
            for (int t = 0; t < ln.len; t++)
            {
                int z = zStart + ln.side * t;
                if (!ln.exit && InMap(ln.x, z) && riverMask[I(ln.x, z)]) break;
                for (int dx = 0; dx < lw; dx++) MarkRoad(x0 + dx, z, 2);
                ln.endZ = z;
            }
        }

        // церква з двором (і цвинтарем) та криниця
        int churchX = rng.Next(-20, 21);
        int churchSide = rng.Next(2) == 0 ? 1 : -1;
        RectInt cr = YardOnRoad(churchX - 15, 30, 34, churchSide);
        if (RectClear(cr))
        {
            MarkOcc(cr);
            int cf = churchSide > 0 ? 0 : 2;
            AddJob(cr.x + cr.width / 2, cr.y + cr.height / 2, () => BuildChurchYard(cr, cf));

            RectInt wr = YardOnRoad(cr.xMax + 2, 9, 9, churchSide);
            if (RectClear(wr))
            {
                MarkOcc(wr);
                AddJob(wr.x + 4, wr.y + 4, () => BuildWell(wr.x + 4, wr.y + 4));
            }
        }

        // площа зі старим дубом і лавками
        for (int t = 0; t < 8; t++)
        {
            int ox2 = rng.Next(-70, 55);
            int os = (t & 1) == 0 ? 1 : -1;
            RectInt orr = YardOnRoad(ox2, 16, 16, os);
            if (!RectClear(orr)) continue;
            MarkOcc(orr);
            AddJob(orr.x + 8, orr.y + 8, () => BuildOakSquare(orr));
            break;
        }

        // вітряк на краю села (перед лісом)
        int mx = (rng.Next(2) == 0 ? -1 : 1) * (inner - 14);
        int mz = RoadZAt(mx) + (rng.Next(2) == 0 ? 1 : -1) * 38;
        RectInt mr = new RectInt(mx - 9, mz - 9, 18, 18);
        if (RectClear(mr))
        {
            MarkOcc(mr);
            AddJob(mx, mz, () => BuildWindmill(mx, mz));
        }

        // ліхтарні стовпи вздовж головної дороги
        if (poleSpacing > 0)
        {
            for (int px = -villageHalfLength; px <= villageHalfLength; px += poleSpacing)
            {
                int pxx = px;
                AddJob(pxx, RoadZAt(pxx) + hw + 1, () => BuildPole(pxx));
            }
        }

        // двори вздовж головної дороги
        for (int s = 0; s < 2; s++)
        {
            int side = s == 0 ? 1 : -1;
            int x = -villageHalfLength + (s == 1 ? rng.Next(0, 10) : 0);
            while (x < villageHalfLength)
            {
                int yw = RI(rng, yardWidthMin, yardWidthMax);
                RectInt r = YardOnRoad(x, yw, yardDepth, side);
                if (RectClear(r))
                {
                    int kind = PickKind(rng);
                    int f = side > 0 ? 0 : 2;
                    MarkOcc(r);
                    AddJob(r.x + r.width / 2, r.y + r.height / 2, () => BuildYard(r, f, kind));
                }
                x += yw + rng.Next(0, 3);
            }
        }

        // двори вздовж бічних вуличок
        foreach (var ln in lanes)
        {
            if (ln.exit) continue;
            int lxL = ln.x - lw / 2, lxR = lxL + lw - 1;
            int z = RoadZAt(ln.x) + ln.side * (hw + 2);
            while (ln.side * (ln.endZ - z) > yardWidthMin)
            {
                int yw = RI(rng, yardWidthMin, yardWidthMax);
                int zLo = ln.side > 0 ? z : z - yw + 1;
                for (int e = 0; e < 2; e++)
                {
                    RectInt r = e == 0
                        ? new RectInt(lxR + 2, zLo, yardDepth, yw)
                        : new RectInt(lxL - 1 - yardDepth, zLo, yardDepth, yw);
                    int f = e == 0 ? 1 : 3;
                    if (!RectClear(r)) continue;
                    int kind = PickKind(rng);
                    MarkOcc(r);
                    AddJob(r.x + r.width / 2, r.y + r.height / 2, () => BuildYard(r, f, kind));
                }
                z += ln.side * (yw + rng.Next(0, 3));
            }
        }

        // околиці: поля, лука, сад, гай, ставок, баштан (лише всередині меж села, далі йде ліс-розділювач)
        int P = Mathf.Max(24, plotSize);
        for (int pz = -inner; pz < inner; pz += P)
        for (int px = -inner; px < inner; px += P)
        {
            RectInt plot = new RectInt(px, pz, Mathf.Min(P, inner - px), Mathf.Min(P, inner - pz));
            int cx = plot.x + plot.width / 2, cz = plot.y + plot.height / 2;
            bool core = Mathf.Abs(cx) < villageHalfLength + P / 2 && Mathf.Abs(cz - RoadZAt(cx)) < yardDepth + hw + 14;
            if (core) continue;

            float h = Hash01(px, 31, pz);
            int type = h < 0.24f ? 0 : h < 0.44f ? 1 : h < 0.54f ? 2 : h < 0.64f ? 3 : h < 0.72f ? 4 : h < 0.82f ? 5 : 6;
            AddJob(cx, cz, () => BuildPlot(plot, type));
        }

        // усі плитки регіону в чергу (будуються лише поблизу каменя)
        for (int tz = 0; tz < tilesPerSide; tz++)
        for (int tx = 0; tx < tilesPerSide; tx++)
            reg.pending.Add(new Vector2Int(tx, tz));
    }

    // ---------- регіони й черга плиток ----------

    Region CreateRegion(Vector2Int key)
    {
        var r = new Region
        {
            key = key,
            ox = key.x * N,
            oz = key.y * N,
            roadKind = new byte[N * N],
            riverMask = new bool[N * N],
            occ = new bool[N * N],
            roadZ = new int[N]
        };
        regions[key] = r;
        SetCur(r);
        Plan(r);
        return r;
    }

    IEnumerator Worker()
    {
        // даємо VoxelWorld.SettleOnStart відпрацювати раніше за нас
        for (int i = 0; i < 4; i++) yield return null;

        while (true)
        {
            if (!UpdateRock())
            {
                yield return null;
                continue;
            }

            float R = Mathf.Max(spawnRadius, rock.Radius * spawnRadiusInRockRadii) / vs;   // у вокселях

            // 1. плануємо регіони, до яких камінь підійшов (по одному за раз)
            bool madeRegion = false;
            int cx0 = FloorDiv(Mathf.FloorToInt(rockV.x - R) + M, N), cx1 = FloorDiv(Mathf.FloorToInt(rockV.x + R) + M, N);
            int cz0 = FloorDiv(Mathf.FloorToInt(rockV.y - R) + M, N), cz1 = FloorDiv(Mathf.FloorToInt(rockV.y + R) + M, N);
            for (int cz = cz0; cz <= cz1 && !madeRegion; cz++)
            for (int cx = cx0; cx <= cx1 && !madeRegion; cx++)
            {
                var key = new Vector2Int(cx, cz);
                if (regions.ContainsKey(key)) continue;

                // відстань від каменя до найближчої точки регіону
                float px = Mathf.Clamp(rockV.x, key.x * N - M, key.x * N + M);
                float pz = Mathf.Clamp(rockV.y, key.y * N - M, key.y * N + M);
                float dx = px - rockV.x, dz = pz - rockV.y;
                if (dx * dx + dz * dz > R * R) continue;

                CreateRegion(key);
                madeRegion = true;
            }
            if (madeRegion)
            {
                yield return null;
                continue;
            }

            // 2. найближча до каменя ще не збудована плитка
            Region bestReg = null;
            int bestIdx = -1;
            float bestD = float.MaxValue;
            foreach (var reg in regions.Values)
            {
                for (int i = 0; i < reg.pending.Count; i++)
                {
                    Vector2 c = new Vector2(reg.ox + (reg.pending[i].x + 0.5f) * T - M, reg.oz + (reg.pending[i].y + 0.5f) * T - M);
                    float d = (c - rockV).sqrMagnitude;
                    if (d < bestD) { bestD = d; bestReg = reg; bestIdx = i; }
                }
            }
            float reach = R + T;
            if (bestReg == null || bestD > reach * reach)
            {
                yield return null;
                continue;
            }

            Vector2Int tile = bestReg.pending[bestIdx];
            bestReg.pending.RemoveAt(bestIdx);
            SetCur(bestReg);
            BuildTile(tile);

            int idx = 0;
            while (idx < ops.Count)
            {
                int end = Mathf.Min(ops.Count, idx + Mathf.Max(100, voxelsPerFrame));
                for (; idx < end; idx++)
                {
                    VoxelOp op = ops[idx];
                    // нічого не затираємо: деталі в генераторі завжди ставляться ПЕРШИМИ, а заливка потім
                    if (world.GetVoxel(op.pos) == 0) world.SetVoxel(op.pos, op.id);
                }
                yield return null;
            }
            ops.Clear();
            yield return null;
        }
    }

    // Знаходить камінь у сцені й оновлює його позицію у світових вокселях. false, поки каменя немає
    bool UpdateRock()
    {
        RockAbsorber[] all = FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None);
        RockAbsorber found = null;
        if (all.Length == 1) found = all[0];
        else if (all.Length > 1)
        {
            // під час переходу між сценами існує й заглушка нової сцени: беремо камінь активної сцени
            Scene active = SceneManager.GetActiveScene();
            found = all[0];
            for (int i = 0; i < all.Length; i++)
                if (all[i].gameObject.scene == active) { found = all[i]; break; }
        }
        if (found == null) return false;

        if (found != rock)
        {
            rock = found;
            haveStart = false;
        }

        Vector3 l = world.transform.InverseTransformPoint(rock.WorldCenter);
        rockV = new Vector2(l.x / vs, l.z / vs);
        if (!haveStart)
        {
            startV = rockV;
            startClearV = rock.Radius * startClearInRockRadii / vs;
            haveStart = true;
        }
        return true;
    }

    void BuildTile(Vector2Int tile)
    {
        ops.Clear();
        if (jobs.TryGetValue(tile, out var list))
            foreach (var job in list) job();
        BuildTileBorder(tile);
        BuildTileFlat(tile);
    }

    // ---------- плоскі дороги й річка ----------

    byte RoadColor(int kind, int x, int z)
    {
        if (kind == 3) return plankSet[(z & 1) == 0 ? 0 : 1];
        if (kind == 1) return Hash01(x, 0, z) < 0.2f ? gravelSet[2] : gravelSet[Hash01(x, 1, z) < 0.5f ? 0 : 1];
        return dirtSet[Hash01(x, 2, z) < 0.5f ? 0 : 1];
    }

    byte WaterColor(int x, int z)
    {
        return Hash01(x, 3, z) < 0.12f ? cWaterLight : cWater;
    }

    // Один квад на клітинку (колір з палітри VoxelWorld, як у чанків) + перила моста
    void BuildTileFlat(Vector2Int tile)
    {
        int x0 = tile.x * T - M, z0 = tile.y * T - M;
        int x1 = Mathf.Min(x0 + T, M), z1 = Mathf.Min(z0 + T, M);
        mVerts.Clear(); mNormals.Clear(); mUvs.Clear(); mTris.Clear();

        for (int z = z0; z < z1; z++)
        for (int x = x0; x < x1; x++)
        {
            int i = I(x, z);
            byte kind = roadKind[i];
            byte id;
            float y;
            if (kind != 0)
            {
                id = RoadColor(kind, x, z);
                y = roadHeight;
            }
            else if (riverMask[i])
            {
                id = WaterColor(x, z);
                y = roadHeight * 0.5f;
            }
            else continue;

            if (kind == 3)
            {
                // перила по краях моста
                if (!IsRoad(x + 1, z) || !IsRoad(x - 1, z) || !IsRoad(x, z + 1) || !IsRoad(x, z - 1))
                {
                    Add(x, 0, z, cRail);
                    Add(x, 1, z, cRail);
                }
            }

            float fx0 = (x + curOx) * vs, fx1 = fx0 + vs, fz0 = (z + curOz) * vs, fz1 = fz0 + vs;
            Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);
            int s = mVerts.Count;
            mVerts.Add(new Vector3(fx0, y, fz0));
            mVerts.Add(new Vector3(fx0, y, fz1));
            mVerts.Add(new Vector3(fx1, y, fz1));
            mVerts.Add(new Vector3(fx1, y, fz0));
            for (int k = 0; k < 4; k++)
            {
                mNormals.Add(Vector3.up);
                mUvs.Add(uv);
            }
            mTris.Add(s); mTris.Add(s + 1); mTris.Add(s + 2);
            mTris.Add(s); mTris.Add(s + 2); mTris.Add(s + 3);
        }
        if (mVerts.Count == 0) return;

        var mesh = new Mesh { name = "VillageFlat", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(mVerts);
        mesh.SetNormals(mNormals);
        mesh.SetUVs(0, mUvs);
        mesh.SetTriangles(mTris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject("Flat " + cur.key.x + "_" + cur.key.y + " " + tile.x + "_" + tile.y);
        go.transform.SetParent(world.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = world.RuntimeMaterial;
        rend.shadowCastingMode = ShadowCastingMode.Off;
    }

    bool IsRoad(int x, int z)
    {
        return InMap(x, z) && roadKind[I(x, z)] != 0;
    }

    // ---------- ліс-розділювач по всіх краях регіону ----------

    void BuildTileBorder(Vector2Int tile)
    {
        int x0 = tile.x * T - M, z0 = tile.y * T - M;
        int x1 = Mathf.Min(x0 + T, M), z1 = Mathf.Min(z0 + T, M);

        // швидка відсічка: плитка цілком далеко від усіх чотирьох країв регіону
        int minEdge = Mathf.Min(Mathf.Min(x0 + M, M - x1), Mathf.Min(z0 + M, M - z1));
        if (minEdge > borderBand + 6) return;

        const int step = 4;
        int sx = x0 - Mod(x0, step), sz = z0 - Mod(z0, step);
        for (int gz = sz; gz < z1; gz += step)
        for (int gx = sx; gx < x1; gx += step)
        {
            if (gx < x0 || gz < z0) continue;
            int x = gx + (Hash(gx, 1, gz) & 3) - 1;
            int z = gz + (Hash(gx, 2, gz) & 3) - 1;
            if (!InMap(x, z)) continue;

            // відстань до найближчого краю регіону (з будь-якого з чотирьох боків)
            int edge = Mathf.Min(Mathf.Min(x + M, M - 1 - x), Mathf.Min(z + M, M - 1 - z));
            float band = borderBand + (Hash01(x / 6, 3, z / 6) - 0.5f) * 8f;
            if (edge >= band) continue;
            if (Hash01(x, 9, z) > forestDensity) continue;

            int r = (Hash(x, 5, z) & 3) == 0 ? 3 : 2;
            if (!TreeClear(x, z, r)) continue;
            BuildTree(x, z, r, new System.Random(Hash(x, z, 77)), 0.45);
        }
    }

    // =====================================================================================
    // Малювання в координатах будівлі: lx вздовж фасаду, lz вглиб від фасаду (фасад біля lz = 0)
    // facing: 0 = фасад дивиться на -z, 1 = на -x, 2 = на +z, 3 = на +x
    // =====================================================================================

    void SetT(int f, int ox, int oz, int w, int d)
    {
        tF = f; tOx = ox; tOz = oz; tW = w; tD = d;
    }

    void LocalToWorld(int lx, int lz, out int x, out int z)
    {
        switch (tF)
        {
            case 0: x = tOx + lx; z = tOz + lz; break;
            case 1: x = tOx + lz; z = tOz + (tW - 1 - lx); break;
            case 2: x = tOx + (tW - 1 - lx); z = tOz + (tD - 1 - lz); break;
            default: x = tOx + (tD - 1 - lz); z = tOz + lx; break;
        }
    }

    void PH(int lx, int ly, int lz, byte id)
    {
        LocalToWorld(lx, lz, out int x, out int z);
        Add(x, ly, z, id);
    }

    void PBox(int lx, int ly, int lz, int sx, int sy, int sz, byte id)
    {
        for (int y = 0; y < sy; y++)
        for (int z = 0; z < sz; z++)
        for (int x = 0; x < sx; x++)
            PH(lx + x, ly + y, lz + z, id);
    }

    // Початок будівлі w x d всередині двору (rect) із відступом front від фасадної межі двору
    static void HouseOrigin(RectInt r, int f, int w, int d, int front, out int ox, out int oz)
    {
        switch (f)
        {
            case 0: ox = r.x + (r.width - w) / 2; oz = r.y + front; break;
            case 2: ox = r.x + (r.width - w) / 2; oz = r.y + r.height - front - d; break;
            case 1: ox = r.x + front; oz = r.y + (r.height - w) / 2; break;
            default: ox = r.x + r.width - front - d; oz = r.y + (r.height - w) / 2; break;
        }
    }

    static RectInt FootRect(int f, int ox, int oz, int w, int d)
    {
        return (f & 1) == 0 ? new RectInt(ox, oz, w, d) : new RectInt(ox, oz, d, w);
    }

    // Скільки вокселів від фасадної межі двору до точки
    static int DistFromFront(RectInt r, int f, int x, int z)
    {
        switch (f)
        {
            case 0: return z - r.y;
            case 2: return r.yMax - 1 - z;
            case 1: return x - r.x;
            default: return r.xMax - 1 - x;
        }
    }

    // Двосхилий дах. Гребінь іде вздовж lx (alongX) або вздовж lz. Склепіння порожнє, схили завтовшки 2 воксели
    // (щоб шари трималися один за одного гранями й дах не відвалювався), фронтони кольору gable.
    void GableRoof(int w, int d, int y0, bool alongX, byte gable, byte[] roofSet)
    {
        int across = alongX ? d : w;
        int along = alongX ? w : d;
        int lo = -1, hi = across;
        for (int k = 0; ; k++)
        {
            int a = lo + k, b = hi - k;
            if (a > b) break;
            for (int c = a; c <= b; c++)
            {
                bool slope = c <= a + 1 || c >= b - 1;
                for (int s = -1; s <= along; s++)
                {
                    byte id;
                    if (slope) id = Pick(roofSet, Hash(s, k, c));
                    else if (s == 0 || s == along - 1) id = gable;
                    else continue;
                    if (alongX) PH(s, y0 + k, c, id);
                    else PH(c, y0 + k, s, id);
                }
            }
        }
    }

    // ---------- двір: город, хата, двоповерхова хата або комора ----------

    void BuildYard(RectInt rect, int f, int kind)
    {
        var rng = new System.Random(Hash(seed ^ 0x1234, rect.x, rect.y));
        int lateral = (f & 1) == 0 ? rect.width : rect.height;
        int depthAvail = (f & 1) == 0 ? rect.height : rect.width;
        const int front = 6;

        RectInt hr = new RectInt(-100000, -100000, 1, 1);
        var used = new List<RectInt>();

        if (kind != 0)
        {
            int w, d;
            if (kind == 3)
            {
                w = Mathf.Min(RI(rng, 14, 16), lateral - 4);
                d = Mathf.Min(RI(rng, 10, 12), depthAvail - front - 2);
            }
            else
            {
                w = Mathf.Min(RI(rng, houseWidthMin, houseWidthMax), lateral - 4);
                d = RI(rng, houseDepthMin, houseDepthMax);
            }
            HouseOrigin(rect, f, w, d, front, out int ox, out int oz);
            SetT(f, ox, oz, w, d);

            if (kind == 3) BuildBarn(rng, w, d);
            else BuildHouseBody(rng, w, d, kind == 2 ? 2 : 1);

            // доріжка від воріт до дверей
            if (kind == 3)
            {
                int dc = w / 2;
                for (int k = 1; k <= front; k++)
                for (int dx = -2; dx <= 1; dx++)
                    PH(dc + dx, 0, -k, cPath);
            }
            else
            {
                int dl = w / 2 - 1;
                for (int k = 1; k <= front; k++)
                {
                    PH(dl, 0, -k, cPath);
                    PH(dl + 1, 0, -k, cPath);
                    // клумби вздовж доріжки
                    if (Hash01(dl, k, 51) < 0.55f) PH(dl - 2, 0, -k, Pick(flowerColors, Hash(dl, k, 52)));
                    if (Hash01(dl, k, 53) < 0.55f) PH(dl + 3, 0, -k, Pick(flowerColors, Hash(dl, k, 54)));
                }
            }
            hr = FootRect(f, ox, oz, w, d);
        }
        else
        {
            // опудало посеред городу (першим: вже поставлене не перезаписується)
            int scx = rect.x + rect.width / 2, scz = rect.y + rect.height / 2;
            Scarecrow(scx, scz);
            used.Add(new RectInt(scx - 3, scz - 3, 7, 7));
            Garden(rect, rng);
        }

        if (rng.NextDouble() < fenceChance) Fence(rect, f);

        // дрібниці: дрова, копиці, вулики, тварини, віз, будка
        if (kind != 0 && yardExtrasMax > 0)
            YardExtras(rect, f, hr, front, rng, used, rng.Next(1, yardExtrasMax + 1));

        // дерева за хатою або збоку
        int trees = kind != 0 ? rng.Next(0, treesPerYardMax + 1) : 1;
        for (int n = 0, tries = 0; n < trees && tries < 12; tries++)
        {
            int x = rng.Next(rect.x + 3, rect.xMax - 3);
            int z = rng.Next(rect.y + 3, rect.yMax - 3);
            if (DistFromFront(rect, f, x, z) < front) continue;
            if (x >= hr.x - 3 && x < hr.xMax + 3 && z >= hr.y - 3 && z < hr.yMax + 3) continue;
            RectInt tr = new RectInt(x - 3, z - 3, 7, 7);
            bool bad = false;
            foreach (var u in used) if (tr.Overlaps(u)) { bad = true; break; }
            if (bad) continue;
            BuildTree(x, z, RI(rng, 2, 3), rng, 0.15);
            n++;
        }
    }

    void Fence(RectInt r, int f)
    {
        int gateX = r.x + r.width / 2, gateZ = r.y + r.height / 2;
        for (int z = r.y; z < r.yMax; z++)
        for (int x = r.x; x < r.xMax; x++)
        {
            if (x != r.x && x != r.xMax - 1 && z != r.y && z != r.yMax - 1) continue;

            // ворота посередині фасадної межі
            bool frontSide = f == 0 ? z == r.y : f == 2 ? z == r.yMax - 1 : f == 1 ? x == r.x : x == r.xMax - 1;
            if (frontSide)
            {
                int lat = (f & 1) == 0 ? Mathf.Abs(x - gateX) : Mathf.Abs(z - gateZ);
                if (lat <= 1) continue;
            }

            if (((x + z) % 3 + 3) % 3 == 0)
            {
                for (int y = 0; y < 3; y++) Add(x, y, z, cPost);
            }
            else
            {
                Add(x, 0, z, cRail);
                Add(x, 1, z, cRail);
            }
        }
    }

    void Garden(RectInt r, System.Random rng)
    {
        for (int z = r.y + 2; z < r.yMax - 2; z += 2)
        {
            int kind = (Hash(r.x, z / 4, r.y) & 0x7fffffff) % 3;
            for (int x = r.x + 2; x < r.xMax - 2; x++)
            {
                if (kind == 0)
                {
                    Add(x, 0, z, cCabbage);
                    if (Hash01(x, z, 4) < 0.5f) Add(x, 1, z, cCabbage2);
                }
                else if (kind == 1)
                {
                    Add(x, 0, z, cLeaf);
                    if (Hash01(x, z, 5) < 0.3f) Add(x, 1, z, cStem);
                }
                else
                {
                    Add(x, 0, z, cStem);
                    Add(x, 1, z, cStem);
                    if (Hash01(x, z, 6) < 0.6f)
                    {
                        Add(x, 2, z, cStem);
                        Add(x, 3, z, cCorn);
                    }
                }
            }
        }
    }

    // ---------- дрібниці двору ----------

    // Розставляє кілька випадкових дрібниць у дворі, не заходячи на хату й на фасадну смугу
    void YardExtras(RectInt rect, int f, RectInt hr, int front, System.Random rng, List<RectInt> used, int count)
    {
        int[] kw = { 5, 7, 6, 7, 6, 6, 4 };
        int[] kd = { 3, 7, 3, 3, 4, 4, 4 };
        RectInt hrBig = new RectInt(hr.x - 2, hr.y - 2, hr.width + 4, hr.height + 4);

        for (int n = 0; n < count; n++)
        {
            int kind = rng.Next(0, 7);   // 0 дрова, 1 копиця, 2 вулики, 3 корова, 4 свиня з курами, 5 віз, 6 будка
            int sx = kw[kind], sz = kd[kind];
            bool alongX = true;
            if (kind == 3 && rng.Next(2) == 0) { alongX = false; sx = 3; sz = 7; }

            for (int tries = 0; tries < 12; tries++)
            {
                int lox = rect.x + 2, hix = rect.xMax - 2 - sx;
                int loz = rect.y + 2, hiz = rect.yMax - 2 - sz;
                if (hix < lox || hiz < loz) break;
                int x = rng.Next(lox, hix + 1), z = rng.Next(loz, hiz + 1);

                int dMin = Mathf.Min(
                    Mathf.Min(DistFromFront(rect, f, x, z), DistFromFront(rect, f, x + sx - 1, z)),
                    Mathf.Min(DistFromFront(rect, f, x, z + sz - 1), DistFromFront(rect, f, x + sx - 1, z + sz - 1)));
                if (dMin < front) continue;

                RectInt fp = new RectInt(x - 1, z - 1, sx + 2, sz + 2);
                if (fp.Overlaps(hrBig)) continue;
                bool bad = false;
                foreach (var u in used) if (fp.Overlaps(u)) { bad = true; break; }
                if (bad) continue;

                used.Add(fp);
                switch (kind)
                {
                    case 0: Woodpile(x, z); break;
                    case 1: Haystack(x + 3, z + 3); break;
                    case 2: Beehives(x, z); break;
                    case 3: Cow(x, z, alongX); break;
                    case 4: PigPen(x, z); break;
                    case 5: Cart(x, z); break;
                    default: Doghouse(x, z); break;
                }
                break;
            }
        }
    }

    // Додає воксель у координатах предмета: a вздовж, b впоперек (alongX: a іде по x)
    void AX(int ox, int oz, bool alongX, int a, int b, int y, byte id)
    {
        if (alongX) Add(ox + a, y, oz + b, id);
        else Add(ox + b, y, oz + a, id);
    }

    void Woodpile(int x, int z)
    {
        for (int xx = 0; xx < 5; xx++)
        for (int zz = 0; zz < 3; zz++)
        {
            int hgt = zz == 1 ? 3 : 2;
            for (int y = 0; y < hgt; y++)
                Add(x + xx, y, z + zz, (((xx + y) & 1) == 0) ? cLog : cLogB);
        }
    }

    void Haystack(int cx, int cz)
    {
        for (int i = 0; i < 4; i++)
            Disc(cx, cz, 3.2f * Mathf.Sqrt(1f - (i / 4f) * (i / 4f)), i, i + 1, i % 2 == 0 ? cStraw : cStraw2);
    }

    void Beehives(int x, int z)
    {
        for (int h = 0; h < 2; h++)
        {
            int bx = x + h * 3;
            for (int dx = 0; dx < 2; dx++)
            for (int dz = 0; dz < 2; dz++)
            {
                Add(bx + dx, 0, z + dz, cPost);
                Add(bx + dx, 1, z + dz, cSun);
                Add(bx + dx, 2, z + dz, cStraw2);
                Add(bx + dx, 3, z + dz, cSail);
            }
        }
    }

    void Cow(int x, int z, bool ax)
    {
        for (int a = 1; a <= 4; a++)
        for (int b = 0; b < 2; b++)
        for (int y = 2; y <= 3; y++)
        {
            byte c = Hash01(x + a * 3, y + b, z) < 0.3f ? cCowB : cCowW;
            AX(x, z, ax, a, b, y, c);
        }
        for (int b = 0; b < 2; b++)
        for (int y = 0; y < 2; y++)
        {
            AX(x, z, ax, 1, b, y, cCowB);
            AX(x, z, ax, 4, b, y, cCowB);
        }
        for (int b = 0; b < 2; b++)
        {
            AX(x, z, ax, 5, b, 3, cCowW);
            AX(x, z, ax, 5, b, 4, cCowW);
            AX(x, z, ax, 6, b, 3, cPink);
        }
        AX(x, z, ax, 5, 0, 5, cSail);
        AX(x, z, ax, 5, 1, 5, cSail);
        AX(x, z, ax, 0, 0, 3, cCowB);
        AX(x, z, ax, 0, 0, 2, cCowB);
    }

    void PigPen(int x, int z)
    {
        // свиня
        for (int a = 0; a < 3; a++)
        for (int b = 0; b < 2; b++)
        for (int y = 1; y <= 2; y++) Add(x + a, y, z + b, cPink);
        for (int b = 0; b < 2; b++)
        {
            Add(x, 0, z + b, cPigDark);
            Add(x + 2, 0, z + b, cPigDark);
            Add(x + 3, 1, z + b, cPink);
            Add(x + 3, 2, z + b, cPink);
            Add(x + 4, 1, z + b, cPigDark);
        }
        // кури
        int[] cxs = { x, x + 4 };
        foreach (int cx in cxs)
        {
            Add(cx, 0, z + 3, cSail);
            Add(cx + 1, 0, z + 3, cSail);
            Add(cx + 1, 1, z + 3, cSail);
            Add(cx + 1, 2, z + 3, cRed);
        }
    }

    void Cart(int x, int z)
    {
        // колеса
        int[] wz = { 0, 3 };
        foreach (int dz in wz)
        {
            Add(x + 1, 0, z + dz, cPost);
            Add(x + 1, 1, z + dz, cPost);
            Add(x + 1, 2, z + dz, cPost);
            Add(x, 1, z + dz, cPost);
            Add(x + 2, 1, z + dz, cPost);
        }
        // кузов із сіном
        for (int xx = 0; xx < 5; xx++)
        for (int zz = 1; zz <= 2; zz++)
        {
            Add(x + xx, 1, z + zz, plankSet[(xx & 1) == 0 ? 0 : 1]);
            Add(x + xx, 2, z + zz, cStraw);
            if (xx > 0 && xx < 4) Add(x + xx, 3, z + zz, cStraw2);
        }
        // дишель
        Add(x + 5, 1, z + 1, cPost);
        Add(x + 5, 1, z + 2, cPost);
    }

    void Doghouse(int x, int z)
    {
        // вхід (першим)
        for (int y = 0; y < 2; y++)
        for (int dx = 1; dx <= 2; dx++) Add(x + dx, y, z, cDoor);
        for (int y = 0; y < 3; y++)
        for (int zz = 0; zz < 4; zz++)
        for (int xx = 0; xx < 4; xx++)
        {
            if (xx != 0 && xx != 3 && zz != 0 && zz != 3) continue;
            Add(x + xx, y, z + zz, cPost);
        }
        for (int zz = 0; zz < 4; zz++)
        for (int xx = 0; xx < 4; xx++)
            Add(x + xx, 3, z + zz, tileSet[(xx + zz) % 3]);
        for (int zz = 0; zz < 4; zz++)
        for (int xx = 1; xx <= 2; xx++)
            Add(x + xx, 4, z + zz, tileSet[(xx + zz + 1) % 3]);
    }

    void Scarecrow(int x, int z)
    {
        byte shirt = shutterSet[2];
        Add(x, 3, z, shirt);
        Add(x, 2, z, shirt);
        for (int y = 0; y < 2; y++) Add(x, y, z, cPost);
        for (int a = 1; a <= 2; a++)
        {
            Add(x - a, 3, z, cRail);
            Add(x + a, 3, z, cRail);
        }
        Add(x - 2, 2, z, cStraw);
        Add(x + 2, 2, z, cStraw);
        Add(x, 4, z, cStraw);
        Add(x - 1, 5, z, cDoor);
        Add(x, 5, z, cDoor);
        Add(x + 1, 5, z, cDoor);
        Add(x, 6, z, cDoor);
    }

    // ---------- хата ----------

    void BuildHouseBody(System.Random rng, int w, int d, int floors)
    {
        int fh = Mathf.Max(4, wallHeight);
        int wh = fh * floors;
        bool thatched = rng.NextDouble() < thatchChance;
        byte[] roofSet = thatched ? thatchSet : (rng.NextDouble() < 0.6 ? tileSet : shingleSet);
        byte stripe = stripeSet[rng.Next(stripeSet.Length)];
        byte shut = shutterSet[rng.Next(shutterSet.Length)];

        // 1. деталі (двері, вікна, комин, балкон): вони ставляться першими, бо вже поставлене не перезаписується
        int dl = w / 2 - 1;
        for (int y = 1; y <= 3; y++)
        {
            PH(dl, y, 0, cDoor);
            PH(dl + 1, y, 0, cDoor);
        }
        int[] wxs = { 2, w - 4 };
        int sz = d / 2 - 1;
        for (int fl = 0; fl < floors; fl++)
        {
            int yb = fl * fh;
            foreach (int wx in wxs)
            for (int y = yb + 2; y <= yb + 3; y++)
            {
                PH(wx, y, 0, cGlass);
                PH(wx + 1, y, 0, cGlass);
                PH(wx - 1, y, 0, shut);
                PH(wx + 2, y, 0, shut);
            }
            for (int y = yb + 2; y <= yb + 3; y++)
            {
                PH(0, y, sz, cGlass); PH(0, y, sz + 1, cGlass);
                PH(w - 1, y, sz, cGlass); PH(w - 1, y, sz + 1, cGlass);
                PH(dl, y, d - 1, cGlass); PH(dl + 1, y, d - 1, cGlass);
            }
            // над входом на другому поверсі балконні двері
            if (fl > 0)
                for (int y = yb + 1; y <= yb + 3; y++)
                {
                    PH(dl, y, 0, cDoor);
                    PH(dl + 1, y, 0, cDoor);
                }
        }
        if (floors > 1)
        {
            // балкон: плита, перила, дві опори до землі
            for (int lx = dl - 1; lx <= dl + 2; lx++)
            for (int lz = -2; lz <= -1; lz++) PH(lx, fh, lz, cRail);
            for (int lx = dl - 1; lx <= dl + 2; lx++) PH(lx, fh + 1, -2, cRail);
            for (int y = 0; y < fh; y++)
            {
                PH(dl - 1, y, -2, cPost);
                PH(dl + 2, y, -2, cPost);
            }
            PH(dl - 1, fh + 1, -1, cRail);
            PH(dl + 2, fh + 1, -1, cRail);
        }
        if (rng.NextDouble() < chimneyChance)
        {
            int top = wh + (d + 2) / 2 + 1;
            for (int y = wh; y <= top; y++)
            for (int dx = 0; dx < 2; dx++)
            for (int dz = 0; dz < 2; dz++)
                PH(w - 4 + dx, y, d / 2 - 1 + dz, y == top ? cCap : cBrick);
        }

        // 2. стіни: знизу кольорова смуга, вище побілка (на двоповерхових ще пояс між поверхами)
        for (int y = 0; y < wh; y++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            if (lx != 0 && lx != w - 1 && lz != 0 && lz != d - 1) continue;
            bool band = floors > 1 && y == fh;
            PH(lx, y, lz, (y < 2 || band) ? stripe : Pick(wallSet, Hash(lx, y, lz)));
        }

        // 3. дах (фронтони побілені) і 4. підлога
        GableRoof(w, d, wh, true, wallSet[0], roofSet);
        PBox(0, 0, 0, w, 1, d, cFloor);
    }

    // ---------- комора ----------

    void BuildBarn(System.Random rng, int w, int d)
    {
        const int wh = 7;
        int dc = w / 2;

        // 1. деталі: хрестові розкоси, лиштва й ворота, віконце горища
        for (int i = 0; i < 4; i++)
        {
            PH(dc - 2 + i, 1 + i, 0, cSail);
            PH(dc + 1 - i, 1 + i, 0, cSail);
        }
        for (int y = 1; y <= 5; y++)
        {
            PH(dc - 3, y, 0, cSail);
            PH(dc + 2, y, 0, cSail);
        }
        for (int lx = dc - 3; lx <= dc + 2; lx++) PH(lx, 5, 0, cSail);
        for (int y = 1; y <= 4; y++)
        for (int lx = dc - 2; lx <= dc + 1; lx++) PH(lx, y, 0, cDoor);
        for (int y = wh + 1; y <= wh + 2; y++)
        {
            PH(dc - 1, y, 0, cDoor);
            PH(dc, y, 0, cDoor);
        }
        PH(dc - 1, wh + 3, 0, cSail);
        PH(dc, wh + 3, 0, cSail);
        PH(0, 3, d / 2, cGlass);
        PH(w - 1, 3, d / 2, cGlass);
        PH(0, 3, d / 2 - 3, cGlass);
        PH(w - 1, 3, d / 2 - 3, cGlass);

        // 2. стіни: кам'яна основа, червоні дошки, білі кути
        for (int y = 0; y < wh; y++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            if (lx != 0 && lx != w - 1 && lz != 0 && lz != d - 1) continue;
            bool corner = (lx == 0 || lx == w - 1) && (lz == 0 || lz == d - 1);
            byte id = y == 0 ? cStone : (corner ? cSail : Pick(barnSet, Hash(lx, y, lz)));
            PH(lx, y, lz, id);
        }

        // 3. дах (гребінь уздовж lz, фронтон до воріт), підлога, сіно всередині
        GableRoof(w, d, wh, false, barnSet[0], shingleSet);
        PBox(0, 0, 0, w, 1, d, cFloor);
        for (int y = 1; y <= 2; y++)
            PBox(1, y, 3, w - 2, 1, d - 4, y == 1 ? cStraw : cStraw2);
    }

    // ---------- церква ----------

    void BuildChurchYard(RectInt rect, int f)
    {
        var rng = new System.Random(Hash(seed ^ 0x5151, rect.x, rect.y));
        const int w = 13, d = 17, wh = 6, front = 8;
        HouseOrigin(rect, f, w, d, front, out int ox, out int oz);
        SetT(f, ox, oz, w, d);

        byte dome = domeSet[rng.Next(domeSet.Length)];

        // деталі: двері й вікна
        for (int y = 1; y <= 4; y++)
        for (int lx = 5; lx <= 7; lx++) PH(lx, y, 0, cDoor);
        foreach (int lz in new[] { 4, 5, 10, 11 })
        for (int y = 2; y <= 4; y++)
        {
            PH(0, y, lz, cGlass);
            PH(w - 1, y, lz, cGlass);
        }

        // баня: барабан із вікнами, півсфера, шпиль із хрестом
        for (int y = 10; y <= 12; y++)
        {
            PH(5, y, 4, cGlass); PH(6, y, 4, cGlass); PH(7, y, 4, cGlass);
            PH(5, y, 12, cGlass); PH(6, y, 12, cGlass); PH(7, y, 12, cGlass);
            PH(2, y, 7, cGlass); PH(2, y, 8, cGlass); PH(2, y, 9, cGlass);
            PH(10, y, 7, cGlass); PH(10, y, 8, cGlass); PH(10, y, 9, cGlass);
        }
        PBox(2, wh, 4, 9, 10, 9, cLog);
        LocalToWorld(6, 8, out int dcx, out int dcz);
        for (int i = 0; i < 7; i++)
        {
            float r = 4.6f * Mathf.Sqrt(1f - (i / 7f) * (i / 7f));
            Disc(dcx, dcz, r, wh + 10 + i, wh + 11 + i, dome);
        }
        for (int y = wh + 17; y <= wh + 22; y++) PH(6, y, 8, cGold);
        PH(5, wh + 20, 8, cGold);
        PH(7, wh + 20, 8, cGold);

        // стіни з колод
        for (int y = 0; y < wh; y++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            if (lx != 0 && lx != w - 1 && lz != 0 && lz != d - 1) continue;
            PH(lx, y, lz, y == 0 ? cStone : ((y & 1) == 0 ? cLog : cLogB));
        }

        GableRoof(w, d, wh, false, cLog, shingleSet);
        PBox(0, 0, 0, w, 1, d, cFloor);

        // доріжка від воріт до дверей
        for (int k = 1; k <= front; k++)
        {
            PH(5, 0, -k, cPath); PH(6, 0, -k, cPath); PH(7, 0, -k, cPath);
        }

        // цвинтар по боках двору: горбки з дерев'яними хрестами
        int[] gcols = { -6, -3, 15, 18 };
        for (int gz = 2; gz <= 14; gz += 3)
        foreach (int gx in gcols)
        {
            if (Hash01(gx, gz, 61) >= graveChance) continue;
            PH(gx, 0, gz, cSoil);
            PH(gx, 0, gz + 1, cSoil);
            for (int y = 0; y < 4; y++) PH(gx, y, gz - 1, cLog);
            PH(gx - 1, 2, gz - 1, cLog);
            PH(gx + 1, 2, gz - 1, cLog);
        }

        Fence(rect, f);

        RectInt hr = FootRect(f, ox, oz, w, d);
        for (int n = 0, tries = 0; n < 3 && tries < 16; tries++)
        {
            int x = rng.Next(rect.x + 3, rect.xMax - 3);
            int z = rng.Next(rect.y + 3, rect.yMax - 3);
            if (DistFromFront(rect, f, x, z) < front) continue;
            if (x >= hr.x - 3 && x < hr.xMax + 3 && z >= hr.y - 3 && z < hr.yMax + 3) continue;
            BuildTree(x, z, 3, rng, 0.1);
            n++;
        }
    }

    // ---------- криниця ----------

    void BuildWell(int cx, int cz)
    {
        Ring(cx, cz, 2.6f, 1.2f, 0, 2, cStone);
        Disc(cx, cz, 1.2f, 0, 1, cWater);
        for (int y = 0; y < 5; y++)
        {
            Add(cx - 3, y, cz, cPost);
            Add(cx + 3, y, cz, cPost);
        }
        for (int x = cx - 3; x <= cx + 3; x++) Add(x, 5, cz, cPost);
        Add(cx, 4, cz, cRail);
        Add(cx, 3, cz, cRail);
        Add(cx, 2, cz, cDoor);
    }

    // ---------- площа зі старим дубом ----------

    void BuildOakSquare(RectInt r)
    {
        var rng = new System.Random(Hash(seed ^ 0x0a0a, r.x, r.y));
        int cx = r.x + 7, cz = r.y + 7;

        // дуб першим: стовбур має перевагу над гравієм
        BuildTreeWith(cx, cz, 5, rng, false, leafSets[0], 2);

        // лавки
        Bench(cx - 1, cz - 7, true);
        Bench(cx - 1, cz + 8, true);
        Bench(cx - 7, cz - 1, false);
        Bench(cx + 8, cz - 1, false);

        // гравійний майданчик
        for (int dz = -7; dz <= 8; dz++)
        for (int dx = -7; dx <= 8; dx++)
        {
            float fx = dx - 0.5f, fz = dz - 0.5f;
            if (fx * fx + fz * fz > 58f) continue;
            int x = cx + dx, z = cz + dz;
            Add(x, 0, z, Pick(gravelSet, Hash(x, 0, z)));
        }
    }

    void Bench(int x, int z, bool alongX)
    {
        for (int i = 0; i < 3; i++)
        {
            int bx = alongX ? x + i : x;
            int bz = alongX ? z : z + i;
            Add(bx, 1, bz, plankSet[i & 1]);
            if (i != 1) Add(bx, 0, bz, cPost);
        }
    }

    // ---------- ліхтарний стовп ----------

    void BuildPole(int x)
    {
        int z = RoadZAt(x) + hw + 1;
        if (!Free(x, z)) return;
        for (int y = 0; y < 8; y++) Add(x, y, z, cPost);
        Add(x, 7, z - 1, cPost);
        Add(x, 7, z - 2, cPost);
        Add(x, 6, z - 2, cSun);
    }

    // ---------- вітряк ----------

    void BuildWindmill(int cx, int cz)
    {
        // двері на південній стороні (першими: вже поставлене не перезаписується)
        for (int y = 1; y <= 3; y++)
        {
            Add(cx, y, cz - 6, cDoor);
            Add(cx + 1, y, cz - 6, cDoor);
        }

        // вежа: від r 6.5 унизу до 3.5 угорі; нижні 4 шари кам'яні
        for (int y = 0; y < 16; y++)
        {
            float r = 6.5f - 3f * y / 15f;
            Ring(cx, cz, r, r - 1.6f, y, y + 1, y < 4 ? cMillBase : cMillWall);
        }
        Disc(cx, cz, 6.5f, 0, 1, cFloor);

        // дах-конус
        for (int i = 0; i < 6; i++) Disc(cx, cz, 4.4f * (1f - i / 6f) + 0.6f, 16 + i, 17 + i, cMillRoof);

        // вісь і лопаті (площина y-z обличчям до +x)
        for (int x = cx + 3; x <= cx + 6; x++)
        {
            Add(x, 14, cz, cPost);
            Add(x, 15, cz, cPost);
        }
        int bx = cx + 7, hy = 15, hz = cz;
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++) Add(bx, hy + dy, hz + dz, cPost);

        const int len = 11;
        for (int t = 2; t <= len; t++)
        {
            Add(bx, hy + t, hz, cPost);
            Add(bx, hy - t, hz, cPost);
            Add(bx, hy, hz + t, cPost);
            Add(bx, hy, hz - t, cPost);
            if (t < 4) continue;
            for (int s = 1; s <= 2; s++)
            {
                Add(bx, hy + t, hz + s, cSail);   // вгору: полотно праворуч
                Add(bx, hy - s, hz + t, cSail);   // праворуч: полотно знизу
                Add(bx, hy - t, hz - s, cSail);   // вниз: полотно ліворуч
                Add(bx, hy + s, hz - t, cSail);   // ліворуч: полотно зверху
            }
        }
    }

    // ---------- ділянки за селом ----------

    // type: 0 пшениця, 1 соняшники, 2 лука, 3 сад, 4 гай, 5 ставок, 6 баштан
    void BuildPlot(RectInt plot, int type)
    {
        var rng = new System.Random(Hash(seed ^ 0x777, plot.x, plot.y));
        int inner = Inner;
        int x0 = Mathf.Max(plot.x + 2, -inner), x1 = Mathf.Min(plot.xMax - 2, inner);
        int z0 = Mathf.Max(plot.y + 2, -inner), z1 = Mathf.Min(plot.yMax - 2, inner);
        if (x1 <= x0 || z1 <= z0) return;

        // опудало на полях (першим: вже поставлене не перезаписується)
        if ((type == 0 || type == 1 || type == 6) && rng.NextDouble() < 0.7)
        {
            int scx = rng.Next(x0 + 4, Mathf.Max(x0 + 5, x1 - 4));
            int scz = rng.Next(z0 + 4, Mathf.Max(z0 + 5, z1 - 4));
            if (TreeClear(scx, scz, 2)) Scarecrow(scx, scz);
        }

        switch (type)
        {
            case 0:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    byte row = wheatSet[((z / 2) & 1) == 0 ? 0 : 1];
                    Add(x, 0, z, row);
                    if (Hash01(x, 11, z) < 0.8f)
                    {
                        Add(x, 1, z, Pick(wheatSet, Hash(x, 12, z)));
                        if (Hash01(x, 13, z) < 0.15f) Add(x, 2, z, row);
                    }
                }
                // копиці сіна
                for (int n = rng.Next(0, 3); n > 0; n--)
                {
                    int hx = rng.Next(x0 + 6, Mathf.Max(x0 + 7, x1 - 6)), hz = rng.Next(z0 + 6, Mathf.Max(z0 + 7, z1 - 6));
                    if (!TreeClear(hx, hz, 3)) continue;
                    for (int i = 0; i < 4; i++) Disc(hx, hz, 3.2f * Mathf.Sqrt(1f - (i / 4f) * (i / 4f)), i + 1, i + 2, i % 2 == 0 ? cStraw : cStraw2);
                }
                break;

            case 1:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    Add(x, 0, z, ((x + z) & 1) == 0 ? cSoil : cSoilDark);
                    if (x % 3 != 0 || z % 3 != 0) continue;
                    for (int y = 1; y <= 3; y++) Add(x, y, z, cStem);
                    Add(x, 4, z, cSunCenter);
                    Add(x + 1, 4, z, cSun); Add(x - 1, 4, z, cSun);
                    Add(x, 4, z + 1, cSun); Add(x, 4, z - 1, cSun);
                    Add(x, 5, z, cSun);
                }
                break;

            case 2:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    if (Hash01(x, 21, z) < 0.045f) Add(x, 0, z, Pick(flowerColors, Hash(x, 22, z)));
                }
                break;

            case 3:
                for (int z = z0 + 3; z < z1 - 2; z += 7)
                for (int x = x0 + 3; x < x1 - 2; x += 7)
                {
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    if (!TreeClear(tx, tz, 2)) continue;
                    BuildOrchardTree(tx, tz, rng);
                }
                break;

            case 5:
            {
                // ставок: вода, кам'яний бортик, очерет, качки
                int pcx = (x0 + x1) / 2, pcz = (z0 + z1) / 2;
                float prx = Mathf.Min(12f, (x1 - x0) / 2f - 4f);
                float prz = Mathf.Min(9f, (z1 - z0) / 2f - 4f);
                if (prx < 5f || prz < 4f) break;
                int ex = Mathf.CeilToInt(prx + 3), ez = Mathf.CeilToInt(prz + 3);
                for (int dz = -ez; dz <= ez; dz++)
                for (int dx = -ex; dx <= ex; dx++)
                {
                    float nx = dx / prx, nz = dz / prz;
                    float e = nx * nx + nz * nz;
                    if (e > 1.9f) continue;
                    int x = pcx + dx, z = pcz + dz;
                    if (!Free(x, z)) continue;

                    if (e <= 1f)
                    {
                        Add(x, 0, z, WaterColor(x, z));
                        if (e < 0.5f && Hash01(x, 41, z) < 0.012f)
                        {
                            Add(x, 1, z, cSail);
                            Add(x + 1, 1, z, cSail);
                            Add(x + 1, 2, z, cSail);
                            Add(x + 2, 1, z, cSun);
                        }
                    }
                    else if (e <= 1.25f)
                    {
                        byte sc = Hash01(x, 42, z) < 0.5f ? cStone : cStone2;
                        Add(x, 0, z, sc);
                        if (Hash01(x, 43, z) < 0.7f) Add(x, 1, z, sc);
                    }
                    else
                    {
                        Add(x, 0, z, Pick(gravelSet, Hash(x, 44, z)));
                        if (Hash01(x, 45, z) < 0.1f)
                        {
                            for (int y = 1; y <= 3; y++) Add(x, y, z, cStem);
                            Add(x, 4, z, cSunCenter);
                        }
                    }
                }
                break;
            }

            case 6:
            {
                // баштан: ряди землі з гарбузами й динями
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    Add(x, 0, z, ((x + z) & 1) == 0 ? cSoil : cSoilDark);
                    if (x % 5 != 0 || z % 5 != 0) continue;
                    if (Hash01(x, 71, z) < 0.15f) continue;
                    if (!Free(x + 1, z) || !Free(x, z + 1) || !Free(x + 1, z + 1)) continue;
                    byte fruit = Hash01(x, 72, z) < 0.6f ? cPumpkin : cMelon;
                    for (int y = 1; y <= 2; y++)
                    for (int dz = 0; dz < 2; dz++)
                    for (int dx = 0; dx < 2; dx++)
                        Add(x + dx, y, z + dz, fruit);
                    Add(x, 3, z, cStem);
                }
                break;
            }

            default:
                for (int z = z0 + 2; z < z1 - 1; z += 5)
                for (int x = x0 + 2; x < x1 - 1; x += 5)
                {
                    if (rng.NextDouble() > 0.8) continue;
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    int r = rng.NextDouble() < 0.35 ? 3 : 2;
                    if (!TreeClear(tx, tz, r)) continue;
                    BuildTree(tx, tz, r, rng, 0.4);
                }
                break;
        }
    }

    // ---------- дерева ----------

    // Яблуня з червоними плодами (набір кольорів leafSets[2])
    void BuildOrchardTree(int gx, int gz, System.Random rng)
    {
        BuildTreeWith(gx, gz, 2, rng, false, leafSets[2]);
    }

    void BuildTree(int gx, int gz, int r, System.Random rng, double pineChance)
    {
        bool pine = rng.NextDouble() < pineChance;
        BuildTreeWith(gx, gz, r, rng, pine, leafSets[rng.Next(2)]);
    }

    void BuildTreeWith(int gx, int gz, int r, System.Random rng, bool pine, byte[] leaves, int extraH = 0)
    {
        int trunkH = RI(rng, 3, 5) + (r >= 3 ? 1 : 0) + extraH;
        int tw = r >= 3 ? 2 : 1;
        int crownTop = pine ? trunkH + r * 2 : trunkH + r;
        int trunkTop = pine ? crownTop - 2 : trunkH;

        for (int y = 0; y < trunkTop; y++)
        for (int dx = 0; dx < tw; dx++)
        for (int dz = 0; dz < tw; dz++)
            Add(gx + dx, y, gz + dz, ((y / 2) & 1) == 0 ? cBarkA : cBarkB);

        float cx = gx + tw * 0.5f, cz = gz + tw * 0.5f;

        if (pine)
        {
            int baseY = 2;
            for (int y = baseY; y <= crownTop; y++)
            {
                float t = (y - baseY) / (float)Mathf.Max(1, crownTop - baseY);
                float rad = (r + 0.5f) * (1f - t) + 0.4f;
                int ri = Mathf.CeilToInt(rad);
                for (int dz = -ri; dz <= ri + tw; dz++)
                for (int dx = -ri; dx <= ri + tw; dx++)
                {
                    float fx = gx + dx + 0.5f - cx, fz = gz + dz + 0.5f - cz;
                    if (fx * fx + fz * fz > rad * rad) continue;
                    int x = gx + dx, z = gz + dz;
                    Add(x, y, z, Pick(pineSet, Hash(x, y, z)));
                }
            }
            return;
        }

        float cy = trunkH + r * 0.3f;
        float ry = r * 0.9f;
        int y0 = Mathf.FloorToInt(cy - ry), y1 = Mathf.CeilToInt(cy + ry);
        for (int y = Mathf.Max(0, y0); y <= y1; y++)
        for (int dz = -r; dz <= r + tw; dz++)
        for (int dx = -r; dx <= r + tw; dx++)
        {
            int x = gx + dx, z = gz + dz;
            float nx = (x + 0.5f - cx) / (r + 0.3f);
            float ny = (y + 0.5f - cy) / ry;
            float nz = (z + 0.5f - cz) / (r + 0.3f);
            if (nx * nx + ny * ny + nz * nz > 1f - Hash01(x, y, z) * 0.35f) continue;

            int shade = ny > 0.35f ? 1 : (ny < -0.35f ? 2 : 0);
            if (Hash01(x + 7, y, z) < 0.25f) shade = (shade + 1) % 3;
            Add(x, y, z, leaves[shade]);
        }
    }

    // ---------- примітиви ----------

    void Add(int x, int y, int z, byte id)
    {
        ops.Add(new VoxelOp { pos = new Vector3Int(x + curOx, y, z + curOz), id = id });
    }

    // Суцільний диск від y0 до y1 (не включно)
    void Disc(int cx, int cz, float r, int y0, int y1, byte id)
    {
        int ri = Mathf.CeilToInt(r);
        for (int y = y0; y < y1; y++)
        for (int dz = -ri; dz <= ri; dz++)
        for (int dx = -ri; dx <= ri; dx++)
            if (dx * dx + dz * dz <= r * r)
                Add(cx + dx, y, cz + dz, id);
    }

    // Кільце між радіусами rin (не включно) і ro (включно)
    void Ring(int cx, int cz, float ro, float rin, int y0, int y1, byte id)
    {
        int ri = Mathf.CeilToInt(ro);
        for (int y = y0; y < y1; y++)
        for (int dz = -ri; dz <= ri; dz++)
        for (int dx = -ri; dx <= ri; dx++)
        {
            float d2 = dx * dx + dz * dz;
            if (d2 > rin * rin && d2 <= ro * ro)
                Add(cx + dx, y, cz + dz, id);
        }
    }

    static int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    static int RI(System.Random rng, int min, int maxInclusive)
    {
        if (maxInclusive < min) return min;
        return rng.Next(min, maxInclusive + 1);
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