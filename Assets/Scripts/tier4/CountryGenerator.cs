using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Нескінченний процедурний архіпелаг для 4 рівня. Землею є МОРЕ (SeaGroundGenerator), а острови
// генеруються самі, скільки б камінь не пливув. Кожен острів має свій берег, озера й ставки, пляж,
// столицю із замком або місто, десятки сіл (колодязь чи вітряк у центрі), дороги з хуторами й деревами,
// двори, городи, поля з парканами, луки, ліси з підліском і валунами.
// Частина островів сучасні королівства (див. CountryGenerator.Modern.cs): хмарочоси, асфальт, ліхтарі.
// Навколо кожного острова є малі острівці з пальмами й маяками та об'єкти в морі (див. CountryGenerator.Sea.cs).
//
// Як це працює:
//  * Світ поділений на великі клітини (Island Spacing). У кожній клітині з імовірністю Island Chance
//    є один острів, який повністю вміщується в клітину, тож острови не перетинаються.
//    Усе виводиться із Seed: ту саму карту дає те саме значення, а Random Seed Each Run дає нову.
//  * Острів плануватиметься (суша, висоти, озера, міста, дороги) тоді, коли фокус (камінь) наближається до нього.
//    Далекі острови вивантажуються з пам'яті (Max Loaded Islands) і при поверненні плануються так само.
//  * Суша це РЕЛЬЄФНІ ВОКСЕЛЬНІ колонки від моря (y = 0) до висоти землі: пагорби, схили, скелі.
//    Озера це діри в суші, крізь які видно море. Усе це справжні вокселі, і камінь ламає їх.
//  * Міста й села стоять на рівних майданчиках, рельєф плавно наростає навколо них.
//  * Плитки будуються лише поблизу фокуса, найближчі першими, по кадрах (Voxels Per Frame).
//  * Камінь на старті переноситься на суцільну сушу острова біля початку координат.
//  * ЯКЩО КАМЕНЯ НА СЦЕНІ НЕМАЄ, через Rock Wait Seconds острови будуються навколо камери,
//    щоб карта не лишалася порожньою.
//
// Підлога (море) має бути на y = 0 (SeaGroundGenerator), VoxelWorld на (0,0,0) з масштабом 1.
// Усі розміри тут у ВОКСЕЛЯХ, тому все масштабується разом із world.voxelSize.
// ЩОБ УСЕ БУЛО ДРІБНІШИМ: зменш voxelSize у VoxelWorld і збільш Island Spacing та радіуси островів.
//
// Значення з Inspector на старті обмежуються розумними мінімумами (Sanitize), тож застарілі нулі
// більше не ламають генерацію. Усе одно корисно натиснути ⋮ біля компонента -> Reset.
public partial class CountryGenerator : MonoBehaviour
{
    [Header("Зв'язки")]
    [Tooltip("Якщо порожньо, береться VoxelWorld зі сцени")]
    public VoxelWorld world;
    public int seed = 4004;
    [Tooltip("Нова випадкова карта при кожному запуску")]
    public bool randomSeedEachRun = false;

    [Header("Діагностика")]
    [Tooltip("Писати в консоль, що відбувається (камінь, плитки, матеріал)")]
    public bool debugLog = true;
    [Tooltip("Якщо каменя немає стільки секунд, острови будуються навколо камери")]
    public float rockWaitSeconds = 2f;

    [Header("Стрімінг")]
    [Tooltip("Як далеко від каменя будується карта (метри)")]
    public float spawnRadius = 150f;
    [Tooltip("Те саме, але в радіусах каменя. Береться більше з двох")]
    public float spawnRadiusInRockRadii = 80f;
    [Tooltip("Навколо стартової позиції каменя нічого не ставиться (в радіусах каменя). Дороги лишаються")]
    public float startClearInRockRadii = 3f;
    [Tooltip("Скільки вокселів ставити за кадр")]
    public int voxelsPerFrame = 12000;
    [Tooltip("Сторона плитки, яка будується за раз")]
    public int tileSize = 48;

    [Header("Острови (у вокселях)")]
    [Tooltip("Відстань між центрами острівних клітин. Більше = більше моря між островами")]
    public int islandSpacing = 900;
    [Tooltip("Імовірність острова в клітині. Острів у клітині (0,0) є завжди: з нього стартує камінь")]
    [Range(0f, 1f)] public float islandChance = 0.7f;
    public int islandRadiusMin = 150;
    public int islandRadiusMax = 380;
    [Tooltip("Звивистість берега як частка радіуса")]
    [Range(0.05f, 0.4f)] public float coastJitterFrac = 0.22f;
    [Tooltip("Частота шуму берега (менше = плавніші затоки)")]
    public float noiseFreq = 0.008f;
    [Tooltip("Скільки вокселів від берега нічого не ставиться")]
    public int coastMargin = 5;
    public int beachWidth = 3;
    [Tooltip("Скільки островів тримається в пам'яті. Далекі вивантажуються")]
    public int maxLoadedIslands = 10;

    [Header("Озера й ставки")]
    public float lakeScale = 0.012f;
    [Tooltip("Більше = менше озер")]
    [Range(0.5f, 0.9f)] public float lakeThreshold = 0.66f;
    [Tooltip("Мінімум вокселів від берега до озера (на малих островах зменшується сам)")]
    public int lakeCoastMargin = 90;
    [Tooltip("Навколо центра острова озер і ставків немає")]
    public int lakeCenterClear = 70;
    public float pondScale = 0.045f;
    [Tooltip("Більше = менше ставків. 1 = ставків немає")]
    [Range(0.5f, 1f)] public float pondThreshold = 0.74f;

    [Header("Рельєф (у вокселях)")]
    [Tooltip("Висота рівнини над морем (вокселі). Пляж завжди 1")]
    public int baseHeight = 2;
    [Tooltip("Максимальна висота пагорбів над рівниною (вокселі). Більше = вищі гори й більше вокселів")]
    public int hillHeight = 14;
    [Tooltip("Частота шуму пагорбів (менше = ширші пагорби)")]
    public float hillFreq = 0.012f;
    [Tooltip("Поріг шуму, з якого починаються пагорби. Більше = менше пагорбів")]
    [Range(0.2f, 0.7f)] public float hillThreshold = 0.42f;
    [Tooltip("Скільки вокселів від берега рельєф наростає до повної висоти")]
    public int reliefCoastBlend = 30;
    [Tooltip("Рівний майданчик навколо міста поза його радіусом (вокселі)")]
    public int cityFlatMargin = 10;
    [Tooltip("Ширина плавного переходу від майданчика міста до пагорбів (вокселі)")]
    public int cityBlend = 24;
    [Tooltip("Скільки верхніх шарів землі (під травою) мають колір ґрунту, нижче камінь")]
    public int soilLayers = 3;
    [Tooltip("Перепад висоти до сусідньої колонки, з якого схил стає скелею")]
    public int cliffDrop = 3;

    [Header("Міста (у вокселях)")]
    [Tooltip("Одне місто на стільки вокселів площі острова")]
    public int voxelsPerTown = 60000;
    [Tooltip("Одне село на стільки вокселів площі острова")]
    public int voxelsPerVillage = 9000;
    [Tooltip("Острів із таким радіусом і більше отримує столицю із замком")]
    public int capitalMinIslandRadius = 220;
    public int cityRadiusMin = 22;
    public int cityRadiusMax = 32;
    public int capitalRadius = 40;
    [Tooltip("Мінімальна відстань між центрами міст одного острова")]
    public int citySpacing = 80;
    public int roadWidth = 2;
    [Tooltip("Як часто вулиці перетинаються поперек")]
    public int crossStreetSpacing = 26;

    [Header("Села")]
    public int villageRadiusMin = 9;
    public int villageRadiusMax = 14;
    public int villageSpacing = 40;
    [Tooltip("Шанс, що в центрі села вітряк (інакше колодязь)")]
    [Range(0f, 1f)] public float windmillChance = 0.15f;

    [Header("Хатки (у вокселях)")]
    public int houseWidthMin = 3;
    public int houseWidthMax = 4;
    public int houseDepthMin = 3;
    public int houseDepthMax = 3;
    public int houseWallHeight = 2;
    [Tooltip("Проміжок між хатками в ряду (вокселі)")]
    public int houseGap = 1;
    [Range(0f, 1f)] public float houseChance = 0.95f;
    [Range(0f, 1f)] public float tallHouseChance = 0.25f;
    [Range(0f, 1f)] public float thatchChance = 0.3f;
    [Range(0f, 1f)] public float chimneyChance = 0.5f;

    [Header("Околиці (у вокселях)")]
    [Tooltip("Розмір ділянки поля/лісу. Менше = різноманітніше")]
    public int plotSize = 32;
    [Tooltip("Густота дерев у лісах і садах")]
    [Range(0f, 1f)] public float treeDensity = 0.9f;
    [Tooltip("Шанс паркану навколо поля чи городу")]
    [Range(0f, 1f)] public float fenceChance = 0.6f;
    [Tooltip("Скільки зелені (кущі, дерева, квіти) у дворах міст і сіл")]
    [Range(0f, 1f)] public float gardenDensity = 0.8f;

    [Header("Дороги")]
    [Tooltip("Як часто вздовж доріг шанс поставити хутір (вокселі)")]
    public int roadsideSpacing = 28;
    [Range(0f, 1f)] public float roadsideHouseChance = 0.6f;
    [Tooltip("Крок дерев уздовж узбіч")]
    public int roadsideTreeSpacing = 5;

    // ---------- внутрішній стан ----------

    struct VoxelOp
    {
        public Vector3Int pos;
        public byte id;
    }

    class City
    {
        public int cx, cz, r;
        public bool capital;
        public bool village;
    }

    class Island
    {
        public Vector2Int region;
        public bool exists, isSpawn;
        public bool modern;                 // сучасне королівство
        public int cx, cz, R;
        public int ox, oz, S;               // габаритний квадрат у вокселях
        public float nox, noz, lox, loz, pox, poz, bx, bz;
        public int colorIdx;                // колір дахів і прапора
        public bool jobsAdded;

        // дані планування (null, поки острів не заплановано або вивантажено)
        public bool[] land;
        public bool[] occ;
        public byte[] road;                 // 0 = нічого, 1 = вулиця, 2 = дорога між містами
        public ushort[] dist;               // для суші: відстань до води
        public byte[] hgt;                  // для суші: висота землі у вокселях (верх колонки = hgt - 1)
        public List<City> cities;
    }

    readonly List<VoxelOp> ops = new List<VoxelOp>();
    readonly Dictionary<Vector2Int, Island> regions = new Dictionary<Vector2Int, Island>();
    readonly List<Island> loaded = new List<Island>();
    readonly List<Island> tmpNear = new List<Island>();
    readonly Dictionary<Vector2Int, List<System.Action>> jobs = new Dictionary<Vector2Int, List<System.Action>>();
    readonly HashSet<Vector2Int> built = new HashSet<Vector2Int>();

    Island cur;        // острів, який планується просто зараз
    Island lastIsl;    // кеш останнього знайденого острова

    float vs;
    int T, sp;

    // Якщо >= 0, усі Add() цього об'єкта піднімаються на цю висоту. Інакше береться висота землі під кожним вокселем
    int fixedBase = -1;

    RockAbsorber rock;
    bool haveStart;
    Vector2 rockV, startV, spawnV;     // rockV це позиція ФОКУСА (камінь або камера) у вокселях
    float startClearV;
    bool idle;
    Vector2 idlePos;

    // фокус стрімінгу
    Vector3 focusWorld;
    float focusRadius = 1f;
    bool usingFallback;
    float noRockTimer;
    float nextRockSearch;

    // діагностика
    int dbgTiles, dbgVoxels;

    // поточне перетворення для малювання будівель: facing 0..3, початок, розміри
    int tF, tOx, tOz, tW, tD;

    // кольори (індекси палітри VoxelWorld)
    byte[] wallSet, thatchSet, shutterSet, gravelSet, dirtSet, sandSet, wheatSet, pineSet, flowerColors, grassSet;
    byte[][] roofSets, leafSets;
    byte[] flagSet;
    byte cGlass, cDoor, cFloor, cBrick, cCap, cPost, cBarkA, cBarkB;
    byte cSoil, cSoilDark, cStem, cSun, cSunCenter, cStraw, cStraw2;
    byte cStone, cStone2, cCrop, cCabbage, cPumpkin, cWater;
    byte cGroundA, cGroundB, cGroundDirt;

    // ---------- публічне для інших скриптів ----------

    // Місце старту каменя на суші (світові координати)
    public Vector3 StartPoint
    {
        get
        {
            if (world == null || vs <= 0f) return transform.position;
            return world.transform.TransformPoint(new Vector3(spawnV.x * vs, 0f, spawnV.y * vs));
        }
    }
    public int TileVoxels => T;
    // Світ нескінченний; число залишено для сумісності зі старими скриптами
    public int MapRadiusVoxels => 1000000;

    // ---------- старт ----------

    void Start()
    {
        if (world == null) world = FindFirstObjectByType<VoxelWorld>();
        if (world == null)
        {
            Debug.LogWarning("[CountryGenerator] Не знайдено VoxelWorld", this);
            enabled = false;
            return;
        }

        if (randomSeedEachRun) seed = Random.Range(1, 1000000);

        vs = world.voxelSize;
        if (vs <= 0f)
        {
            Debug.LogError("[CountryGenerator] world.voxelSize <= 0, генерація зупинена", this);
            enabled = false;
            return;
        }

        Sanitize();
        T = Mathf.Max(16, tileSize);
        sp = Mathf.Max(400, islandSpacing);

        InitColors();

        // стартовий острів планується одразу, щоб знати, куди ставити камінь
        Island start = GetRegion(0, 0);
        Ensure(start);

        if (debugLog)
            Debug.Log("[CountryGenerator] Старт: seed=" + seed + ", voxelSize=" + vs + ", tile=" + T + ", spacing=" + sp +
                      ", стартовий острів R=" + start.R + " centre=(" + start.cx + "," + start.cz + ")" +
                      ", точка старту (вокселі)=" + spawnV + ", світова=" + StartPoint, this);

        StartCoroutine(Worker());
    }

    // Застарілі або нульові значення в Inspector не повинні ламати генерацію
    void Sanitize()
    {
        islandSpacing = Mathf.Max(islandSpacing, 400);
        islandRadiusMin = Mathf.Max(islandRadiusMin, 60);
        islandRadiusMax = Mathf.Max(islandRadiusMax, islandRadiusMin);
        tileSize = Mathf.Max(tileSize, 16);
        voxelsPerFrame = Mathf.Max(voxelsPerFrame, 1000);
        spawnRadius = Mathf.Max(spawnRadius, 60f);
        spawnRadiusInRockRadii = Mathf.Max(spawnRadiusInRockRadii, 20f);
        maxLoadedIslands = Mathf.Max(maxLoadedIslands, 4);
        roadWidth = Mathf.Max(roadWidth, 1);
        rockWaitSeconds = Mathf.Max(rockWaitSeconds, 0.5f);
        baseHeight = Mathf.Clamp(baseHeight, 2, 20);
        hillHeight = Mathf.Clamp(hillHeight, 0, 200);
        hillFreq = Mathf.Max(hillFreq, 0.001f);
        reliefCoastBlend = Mathf.Max(reliefCoastBlend, 1);
        cityBlend = Mathf.Max(cityBlend, 1);
        cityFlatMargin = Mathf.Max(cityFlatMargin, 0);
        soilLayers = Mathf.Max(soilLayers, 0);
        cliffDrop = Mathf.Max(cliffDrop, 1);
    }

    byte C(int r, int g, int b)
    {
        return world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));
    }

    void InitColors()
    {
        wallSet = new[] { C(244, 242, 232), C(236, 232, 220), C(247, 245, 238) };
        thatchSet = new[] { C(196, 166, 92), C(184, 152, 80), C(206, 176, 104) };
        shutterSet = new[] { C(60, 110, 170), C(70, 130, 90), C(170, 70, 60) };
        gravelSet = new[] { C(150, 135, 112), C(140, 126, 104), C(120, 110, 92) };
        dirtSet = new[] { C(128, 100, 70), C(120, 94, 66) };
        sandSet = new[] { C(232, 214, 160), C(222, 202, 148) };

        // кожен острів має свій колір дахів і прапора
        roofSets = new[]
        {
            new[] { C(168, 62, 50), C(152, 54, 44), C(180, 74, 58) },
            new[] { C(60, 100, 170), C(52, 88, 150), C(72, 114, 186) },
            new[] { C(60, 140, 84), C(52, 124, 74), C(72, 154, 96) },
            new[] { C(210, 160, 50), C(194, 144, 42), C(224, 174, 66) },
            new[] { C(130, 70, 150), C(114, 60, 134), C(146, 84, 166) },
            new[] { C(90, 90, 96), C(76, 76, 82), C(104, 104, 110) },
        };
        flagSet = new[] { C(220, 50, 50), C(50, 100, 220), C(50, 180, 90), C(240, 200, 40), C(160, 70, 190), C(240, 240, 240) };

        cGlass = C(160, 200, 225);
        cDoor = C(110, 70, 42);
        cFloor = C(180, 150, 110);
        cBrick = C(150, 74, 58);
        cCap = C(96, 48, 40);
        cPost = C(95, 62, 40);
        cBarkA = C(100, 70, 45);
        cBarkB = C(88, 60, 38);

        leafSets = new[]
        {
            new[] { C(58, 125, 52), C(72, 145, 58), C(48, 108, 46) },
            new[] { C(95, 160, 70), C(110, 175, 80), C(80, 140, 62) },
            new[] { C(190, 70, 70), C(86, 150, 62), C(70, 130, 56) },   // яблуня з плодами
        };
        pineSet = new[] { C(35, 95, 60), C(42, 110, 66), C(30, 82, 52) };
        grassSet = new[] { C(88, 160, 66), C(72, 140, 56), C(104, 176, 78) };

        cWater = C(60, 130, 200);

        cSoil = C(100, 72, 48);
        cSoilDark = C(86, 62, 40);
        cStem = C(70, 130, 56);
        cSun = C(250, 200, 40);
        cSunCenter = C(110, 70, 30);
        cStraw = C(214, 178, 76);
        cStraw2 = C(196, 160, 60);
        wheatSet = new[] { C(214, 178, 70), C(200, 160, 56), C(224, 190, 84) };
        flowerColors = new[] { C(240, 240, 235), C(250, 210, 60), C(220, 70, 80), C(150, 110, 200), C(90, 150, 230) };
        cCrop = C(90, 170, 70);
        cCabbage = C(130, 196, 108);
        cPumpkin = C(232, 130, 40);

        cStone = C(150, 150, 146);
        cStone2 = C(124, 124, 120);

        // трава й ґрунт на островах
        cGroundA = C(77, 122, 46);
        cGroundB = C(92, 140, 56);
        cGroundDirt = C(102, 77, 51);

        InitModernColors();
    }

    // ---------- допоміжне ----------

    static int FloorDiv(int a, int b)
    {
        int q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }

    // Острівна клітина (rx, rz): існує чи ні, радіус, центр, шумові зсуви. Усе з Seed
    Island GetRegion(int rx, int rz)
    {
        var key = new Vector2Int(rx, rz);
        if (regions.TryGetValue(key, out var s)) return s;

        s = new Island { region = key };
        var rng = new System.Random(Hash(seed ^ 0x1B3, rx * 7919 + 13, rz * 104729 + 7));
        bool isSpawn = rx == 0 && rz == 0;
        s.isSpawn = isSpawn;
        s.exists = isSpawn || rng.NextDouble() < islandChance;

        if (s.exists)
        {
            int half = sp / 2;
            // смуга isletBand лишається для малих острівців і об'єктів у морі навколо великого острова
            float cap = Mathf.Max(60f, (half - 16f - Mathf.Max(0, isletBand)) / (1f + coastJitterFrac));
            float rmax = Mathf.Min(islandRadiusMax, cap);
            float rmin = Mathf.Min(islandRadiusMin, rmax);
            s.R = isSpawn ? Mathf.RoundToInt(rmax) : Mathf.RoundToInt(Mathf.Lerp(rmin, rmax, (float)rng.NextDouble()));
            float ext = s.R * (1f + coastJitterFrac) + 6f + Mathf.Max(0, isletBand);
            float jit = isSpawn ? 0f : Mathf.Max(0f, half - ext - 4f);
            s.cx = rx * sp + Mathf.RoundToInt(((float)rng.NextDouble() * 2f - 1f) * jit);
            s.cz = rz * sp + Mathf.RoundToInt(((float)rng.NextDouble() * 2f - 1f) * jit);
            int h = Mathf.CeilToInt(ext);
            s.S = 2 * h;
            s.ox = s.cx - h;
            s.oz = s.cz - h;
            s.nox = 100f + (float)rng.NextDouble() * 1000f;
            s.noz = 100f + (float)rng.NextDouble() * 1000f;
            s.lox = 100f + (float)rng.NextDouble() * 1000f;
            s.loz = 100f + (float)rng.NextDouble() * 1000f;
            s.pox = 100f + (float)rng.NextDouble() * 1000f;
            s.poz = 100f + (float)rng.NextDouble() * 1000f;
            s.bx = 100f + (float)rng.NextDouble() * 1000f;
            s.bz = 100f + (float)rng.NextDouble() * 1000f;
            s.colorIdx = rng.Next(0, 6);
            s.modern = !isSpawn && rng.NextDouble() < modernChance;
        }
        regions[key] = s;
        return s;
    }

    // Острів, у габаритах якого лежить воксель (null, якщо там відкрите море)
    Island IslandAt(int x, int z)
    {
        Island l = lastIsl;
        if (l != null && x >= l.ox && x < l.ox + l.S && z >= l.oz && z < l.oz + l.S)
        {
            if (l.land == null) Ensure(l);
            return l;
        }
        int half = sp / 2;
        Island s = GetRegion(FloorDiv(x + half, sp), FloorDiv(z + half, sp));
        if (!s.exists) return null;
        if (x < s.ox || x >= s.ox + s.S || z < s.oz || z >= s.oz + s.S) return null;
        lastIsl = s;
        if (s.land == null) Ensure(s);
        return s;
    }

    void Ensure(Island s)
    {
        if (s.land == null) PlanIsland(s);
    }

    // Висота землі (у вокселях) у колонці: 0 у морі й озерах, інакше 1 і більше
    int GroundH(int x, int z)
    {
        Island s = IslandAt(x, z);
        if (s == null || s.land == null || s.hgt == null) return 0;
        int i = (x - s.ox) + s.S * (z - s.oz);
        if (!s.land[i]) return 0;
        return s.hgt[i];
    }

    bool Free(int x, int z)
    {
        Island s = IslandAt(x, z);
        if (s == null) return false;
        int i = (x - s.ox) + s.S * (z - s.oz);
        if (!s.land[i] || s.road[i] != 0 || s.occ[i]) return false;
        if (s.dist[i] < coastMargin) return false;

        // біля старту каменя нічого не ставимо (дороги лишаються)
        if (haveStart)
        {
            float dx = x - startV.x, dz = z - startV.y;
            if (dx * dx + dz * dz < startClearV * startClearV) return false;
        }
        return true;
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

    // ---- допоміжне лише для планування (працює з cur) ----

    bool InCur(int x, int z)
    {
        return x >= cur.ox && x < cur.ox + cur.S && z >= cur.oz && z < cur.oz + cur.S;
    }

    int Lc(int x, int z)
    {
        return (x - cur.ox) + cur.S * (z - cur.oz);
    }

    void MarkOcc(RectInt r)
    {
        for (int z = r.y; z < r.yMax; z++)
        for (int x = r.x; x < r.xMax; x++)
            if (InCur(x, z)) cur.occ[Lc(x, z)] = true;
    }

    void MarkRoad(int x, int z, int kind)
    {
        if (!InCur(x, z)) return;
        int i = Lc(x, z);
        if (!cur.land[i] || cur.road[i] != 0) return;
        cur.road[i] = (byte)kind;
    }

    // Вирівнює землю під прямокутником до висоти його центра (для хуторів на схилах)
    void FlattenRect(RectInt r)
    {
        if (cur.hgt == null) return;
        int cxl = r.x + r.width / 2, czl = r.y + r.height / 2;
        if (!InCur(cxl, czl)) return;
        byte h = cur.hgt[Lc(cxl, czl)];
        for (int z = r.y; z < r.yMax; z++)
        for (int x = r.x; x < r.xMax; x++)
        {
            if (!InCur(x, z)) continue;
            int i = Lc(x, z);
            if (cur.land[i]) cur.hgt[i] = h;
        }
    }

    Vector2Int TileOf(int x, int z)
    {
        return new Vector2Int(FloorDiv(x, T), FloorDiv(z, T));
    }

    void AddJob(int x, int z, System.Action a)
    {
        if (cur != null && cur.jobsAdded) return;   // повторне планування не дублює роботи
        Vector2Int tile = TileOf(x, z);
        if (!jobs.TryGetValue(tile, out var list))
        {
            list = new List<System.Action>();
            jobs[tile] = list;
        }
        list.Add(() =>
        {
            fixedBase = -1;
            a();
            fixedBase = -1;
        });
    }

    static byte Pick(byte[] set, int h)
    {
        return set[(h & 0x7fffffff) % set.Length];
    }

    // ---------- планування острова ----------

    void PlanIsland(Island s)
    {
        Island prev = cur;
        cur = s;

        int S = s.S;
        s.land = new bool[S * S];
        s.occ = new bool[S * S];
        s.road = new byte[S * S];
        s.dist = null;
        s.hgt = null;
        s.cities = new List<City>();

        var rng = new System.Random(Hash(seed ^ 0x4C1, s.region.x * 7919 + 1, s.region.y * 104729 + 2));

        // 1. суша: звивистий берег, озера, ставки
        float R = s.R;
        float jitter = R * coastJitterFrac;
        float cc = Mathf.Min(lakeCenterClear, R * 0.3f);
        float cm = Mathf.Min(lakeCoastMargin, R * 0.3f);
        float pm = Mathf.Min(40f, R * 0.2f);
        float maxD = R + jitter + 1f;
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            float dx = s.ox + lx - s.cx, dz = s.oz + lz - s.cz;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            if (d > maxD) continue;

            float n1 = Mathf.PerlinNoise(dx * noiseFreq + s.nox, dz * noiseFreq + s.noz) * 2f - 1f;
            float n2 = Mathf.PerlinNoise(dx * noiseFreq * 3.1f + s.noz, dz * noiseFreq * 3.1f + s.nox) * 2f - 1f;
            float n = n1 * 0.7f + n2 * 0.3f;
            bool land = d + n * jitter < R;

            if (land && d > cc)
            {
                if (R - d > cm && Mathf.PerlinNoise(dx * lakeScale + s.lox, dz * lakeScale + s.loz) > lakeThreshold)
                    land = false;
                else if (R - d > pm && Mathf.PerlinNoise(dx * pondScale + s.pox, dz * pondScale + s.poz) > pondThreshold)
                    land = false;
            }
            s.land[lx + S * lz] = land;
        }

        // 2. лишаємо тільки ту частину острова, що з'єднана з центром
        KeepCenterComponent(s);
        var islets = AddIslets(s);          // малі острівці (до розрахунку відстаней)
        ComputeLandDist(s);
        if (s.isSpawn) ComputeSpawn(s);
        PlanIsletsAndSea(s, islets);        // острівці, маяки, буї, човни

        // 3. міста й села
        float area = Mathf.PI * R * R;
        int towns = Mathf.Max(0, Mathf.RoundToInt(area / Mathf.Max(1000f, voxelsPerTown)));
        int villages = Mathf.Max(1, Mathf.RoundToInt(area / Mathf.Max(500f, voxelsPerVillage)));
        bool capital = R >= capitalMinIslandRadius;
        int firstR = capital ? capitalRadius
                             : Mathf.Max(12, Mathf.Min(RI(rng, cityRadiusMin, cityRadiusMax), Mathf.RoundToInt(R * 0.25f)));
        s.cities.Add(new City { cx = s.cx, cz = s.cz, r = firstR, capital = capital, village = false });
        int townCount = 1 + towns;
        int total = townCount + villages;
        for (int tries = 0; tries < 20000 && s.cities.Count < total; tries++)
        {
            bool village = s.cities.Count >= townCount;
            float ang = (float)rng.NextDouble() * 6.2832f;
            float dist = Mathf.Lerp(0.1f, 0.92f, (float)rng.NextDouble()) * R;
            int px = s.cx + Mathf.RoundToInt(Mathf.Cos(ang) * dist);
            int pz = s.cz + Mathf.RoundToInt(Mathf.Sin(ang) * dist);
            int r = village ? RI(rng, villageRadiusMin, villageRadiusMax) : RI(rng, cityRadiusMin, cityRadiusMax);
            if (!InCur(px, pz)) continue;
            int i = Lc(px, pz);
            if (!s.land[i] || s.dist[i] < r + coastMargin + 2) continue;

            int spacing = village ? villageSpacing : citySpacing;
            bool ok = true;
            foreach (var e in s.cities)
            {
                float ddx = px - e.cx, ddz = pz - e.cz;
                float need = Mathf.Max(spacing, e.r + r + 8);
                if (Mathf.Sqrt(ddx * ddx + ddz * ddz) < need) { ok = false; break; }
            }
            if (ok) s.cities.Add(new City { cx = px, cz = pz, r = r, capital = false, village = village });
        }

        // 3.5. рельєф: висоти землі (міста лишаються на рівних майданчиках)
        ComputeHeights(s);

        // 4. дороги між поселеннями, хутори й дерева вздовж узбіч
        for (int k = 1; k < s.cities.Count; k++)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int j = 0; j < k; j++)
            {
                float dx = s.cities[k].cx - s.cities[j].cx, dz = s.cities[k].cz - s.cities[j].cz;
                float d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = j; }
            }
            CarveRoad(new Vector2(s.cities[best].cx, s.cities[best].cz),
                      new Vector2(s.cities[k].cx, s.cities[k].cz), rng, s.colorIdx);
        }

        // 5. вулиці, хатки й двори
        foreach (var cy in s.cities)
            PlanCity(cy, rng);

        // 6. околиці: ліси, луки, ферми, городи (за шумом біомів)
        int P = Mathf.Max(16, plotSize);
        int px0 = FloorDiv(s.ox, P) * P, pz0 = FloorDiv(s.oz, P) * P;
        for (int pz = pz0; pz < s.oz + S; pz += P)
        for (int px = px0; px < s.ox + S; px += P)
        {
            RectInt plot = new RectInt(px, pz, P, P);
            int cx = px + P / 2, cz = pz + P / 2;
            if (!AnyLand(plot)) continue;

            bool core = false;
            foreach (var cy in s.cities)
            {
                float dx = cx - cy.cx, dz = cz - cy.cz;
                float lim = cy.r + P * 0.5f;
                if (dx * dx + dz * dz < lim * lim) { core = true; break; }
            }
            if (core) continue;

            // 0 пшениця, 1 соняшники, 2 лука, 3 сад, 4 ліс, 5 город
            float h = Hash01(px, 31, pz);
            float bn = Mathf.PerlinNoise((cx - s.cx) * 0.005f + s.bx, (cz - s.cz) * 0.005f + s.bz);
            int type;
            if (bn > 0.58f) type = h < 0.1f ? 3 : 4;                                                       // великі ліси
            else if (bn < 0.32f) type = h < 0.6f ? 2 : (h < 0.75f ? 3 : (h < 0.88f ? 4 : 5));             // луки
            else type = h < 0.28f ? 0 : h < 0.43f ? 1 : h < 0.58f ? 5 : h < 0.75f ? 2 : h < 0.9f ? 3 : 4;  // ферми
            AddJob(cx, cz, () => BuildPlot(plot, type));
        }

        s.jobsAdded = true;
        if (!loaded.Contains(s)) loaded.Add(s);
        cur = prev;

        if (debugLog)
        {
            int landCount = 0;
            for (int i = 0; i < s.land.Length; i++) if (s.land[i]) landCount++;
            Debug.Log("[CountryGenerator] Заплановано острів " + s.region + ": R=" + s.R + ", суші=" + landCount +
                      " вокселів, поселень=" + s.cities.Count + ", острівців=" + islets.Count + (s.modern ? ", сучасний" : ""));
        }

        EvictFar(s);
    }

    // Висоти землі: пляж 1, рівнина baseHeight, пагорби до baseHeight + hillHeight.
    // Міста й села (разом із запасом) рівні, рельєф плавно наростає за їхніми межами й від берега
    void ComputeHeights(Island s)
    {
        int S = s.S;
        s.hgt = new byte[S * S];
        int maxH = Mathf.Clamp(baseHeight + hillHeight, 1, 250);

        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            int i = lx + S * lz;
            if (!s.land[i]) continue;

            // малі острівці (позначені occ ще до планування міст) плоскі
            if (s.occ[i]) { s.hgt[i] = (byte)Mathf.Min(2, maxH); continue; }

            int d = s.dist[i];
            if (d <= beachWidth) { s.hgt[i] = 1; continue; }

            float wx = s.ox + lx, wz = s.oz + lz;

            // маска міст: 0 біля міста, 1 далеко
            float m = 1f;
            for (int k = 0; k < s.cities.Count; k++)
            {
                City cy = s.cities[k];
                float ddx = wx - cy.cx, ddz = wz - cy.cz;
                float dd = Mathf.Sqrt(ddx * ddx + ddz * ddz);
                float t = Mathf.Clamp01((dd - (cy.r + cityFlatMargin)) / cityBlend);
                t = t * t * (3f - 2f * t);
                if (t < m) m = t;
                if (m <= 0f) break;
            }

            // наростання від берега
            float cf = Mathf.Clamp01((d - beachWidth - 1) / (float)reliefCoastBlend);
            cf = cf * cf * (3f - 2f * cf);

            float n = Mathf.PerlinNoise(wx * hillFreq + s.pox, wz * hillFreq + s.poz) * 0.7f
                    + Mathf.PerlinNoise(wx * hillFreq * 2.7f + s.lox, wz * hillFreq * 2.7f + s.loz) * 0.3f;
            float hill = Mathf.Clamp01((n - hillThreshold) / 0.3f);
            hill = hill * hill * (3f - 2f * hill);

            int h = baseHeight + Mathf.RoundToInt(hill * hillHeight * m * cf);
            s.hgt[i] = (byte)Mathf.Clamp(h, 1, maxH);
        }
    }

    // Вивантажує з пам'яті найдальші острови (їхні роботи вже в черзі й не дублюються при поверненні)
    void EvictFar(Island keep)
    {
        int max = Mathf.Max(3, maxLoadedIslands);
        while (loaded.Count > max)
        {
            Island far = null;
            float fd = -1f;
            for (int k = 0; k < loaded.Count; k++)
            {
                Island o = loaded[k];
                if (o == keep || tmpNear.Contains(o)) continue;
                float dx = o.cx - rockV.x, dz = o.cz - rockV.y;
                float d = dx * dx + dz * dz;
                if (d > fd) { fd = d; far = o; }
            }
            if (far == null) break;
            far.land = null; far.occ = null; far.road = null; far.dist = null; far.hgt = null; far.cities = null;
            loaded.Remove(far);
        }
    }

    // Місце старту каменя: на суші неподалік від центра острова, подалі від берега
    void ComputeSpawn(Island s)
    {
        Vector2 sv = new Vector2(s.cx, s.cz);
        int want = Mathf.Max(capitalRadius + 20, 60);
        bool found = false;
        for (int dist = want; dist >= 10 && !found; dist -= 5)
        for (int a = 0; a < 24 && !found; a++)
        {
            float ang = a * (Mathf.PI * 2f / 24f);
            int px = s.cx + Mathf.RoundToInt(Mathf.Cos(ang) * dist);
            int pz = s.cz + Mathf.RoundToInt(Mathf.Sin(ang) * dist);
            if (!InCur(px, pz)) continue;
            int i = Lc(px, pz);
            if (s.land[i] && s.dist[i] > 30)
            {
                sv = new Vector2(px, pz);
                found = true;
            }
        }
        spawnV = sv;
        if (!haveStart)
        {
            haveStart = true;
            startV = sv;
            startClearV = 14f;
        }
    }

    bool AnyLand(RectInt r)
    {
        for (int k = 0; k < 16; k++)
        {
            int x = r.x + (k % 4) * r.width / 4 + r.width / 8;
            int z = r.y + (k / 4) * r.height / 4 + r.height / 8;
            if (InCur(x, z) && cur.land[Lc(x, z)]) return true;
        }
        return false;
    }

    void KeepCenterComponent(Island s)
    {
        int S = s.S;
        int si = (s.cx - s.ox) + S * (s.cz - s.oz);
        s.land[si] = true;

        bool[] visited = new bool[S * S];
        int[] queue = new int[S * S];
        int qh = 0, qt = 0;
        queue[qt++] = si;
        visited[si] = true;
        while (qh < qt)
        {
            int i = queue[qh++];
            int x = i % S, z = i / S;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= S || nz >= S) continue;
                int j = nx + S * nz;
                if (visited[j] || !s.land[j]) continue;
                visited[j] = true;
                queue[qt++] = j;
            }
        }

        for (int i = 0; i < s.land.Length; i++)
            if (s.land[i] && !visited[i])
                s.land[i] = false;
    }

    // Відстань від суші до найближчої води (море, озеро, ставок)
    void ComputeLandDist(Island s)
    {
        int S = s.S;
        s.dist = new ushort[S * S];
        int[] queue = new int[S * S];
        int qh = 0, qt = 0;

        for (int z = 0; z < S; z++)
        for (int x = 0; x < S; x++)
        {
            int i = x + S * z;
            if (!s.land[i]) continue;
            bool boundary = x == 0 || z == 0 || x == S - 1 || z == S - 1
                         || !s.land[i - 1] || !s.land[i + 1] || !s.land[i - S] || !s.land[i + S];
            if (boundary)
            {
                s.dist[i] = 1;
                queue[qt++] = i;
            }
        }

        while (qh < qt)
        {
            int i = queue[qh++];
            int x = i % S, z = i / S;
            ushort nd = (ushort)Mathf.Min(65534, s.dist[i] + 1);
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= S || nz >= S) continue;
                int j = nx + S * nz;
                if (!s.land[j] || s.dist[j] != 0) continue;
                s.dist[j] = nd;
                queue[qt++] = j;
            }
        }

        for (int i = 0; i < s.dist.Length; i++)
            if (s.land[i] && s.dist[i] == 0) s.dist[i] = 65535;
    }

    // Дорога з вигином між двома поселеннями (лише по суші). Уздовж неї стають хутори й дерева
    void CarveRoad(Vector2 a, Vector2 b, System.Random rng, int cid)
    {
        float len = Vector2.Distance(a, b);
        if (len < 1f) return;
        int steps = Mathf.CeilToInt(len);
        Vector2 dir = (b - a) / len;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        float ph = (float)rng.NextDouble() * 6.28f;
        float amp = Mathf.Min(12f, len * 0.08f);
        int rw = Mathf.Max(1, roadWidth);
        var pts = new List<Vector2Int>(steps + 1);
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            float off = amp * Mathf.Sin(t * 6.2832f + ph) * Mathf.Sin(t * Mathf.PI);
            Vector2 p = a + dir * s + perp * off;
            int px = Mathf.RoundToInt(p.x), pz = Mathf.RoundToInt(p.y);
            pts.Add(new Vector2Int(px, pz));
            for (int dz = 0; dz < rw; dz++)
            for (int dx = 0; dx < rw; dx++)
                MarkRoad(px + dx - rw / 2, pz + dz - rw / 2, 2);
        }

        // хутори вздовж дороги
        int rs = Mathf.Max(10, roadsideSpacing);
        for (int s = rs / 2; s < pts.Count; s += rs)
        {
            if (rng.NextDouble() > roadsideHouseChance) continue;
            int sd = rng.Next(0, 2) == 0 ? -1 : 1;
            PlaceRoadsideHouse(pts[s], dir, sd, rw, rng, cid);
        }

        // дерева вздовж узбіч
        int ts = Mathf.Max(3, roadsideTreeSpacing);
        for (int s = 0; s < pts.Count; s += ts)
        for (int sd = -1; sd <= 1; sd += 2)
        {
            if (rng.NextDouble() > 0.55) continue;
            float dist = rw / 2 + 2 + rng.Next(0, 2);
            int tx = pts[s].x + Mathf.RoundToInt(perp.x * sd * dist);
            int tz = pts[s].y + Mathf.RoundToInt(perp.y * sd * dist);
            AddJob(tx, tz, () => BuildRoadsideTree(tx, tz));
        }
    }

    void PlaceRoadsideHouse(Vector2Int p, Vector2 dir, int sd, int rw, System.Random rng, int cid)
    {
        if (cur.modern) return;   // на сучасних островах немає солом'яних хуторів

        int w = RI(rng, houseWidthMin, houseWidthMax);
        int d = RI(rng, houseDepthMin, houseDepthMax);
        bool alongX = Mathf.Abs(dir.x) >= Mathf.Abs(dir.y);
        int ox, oz, f;
        RectInt foot;
        if (alongX)
        {
            ox = p.x - w / 2;
            if (sd > 0) { oz = p.y + rw + 2; f = 0; }
            else { oz = p.y - rw - 2 - d; f = 2; }
            foot = new RectInt(ox - 1, oz - 1, w + 2, d + 2);
        }
        else
        {
            oz = p.y - w / 2;
            if (sd > 0) { ox = p.x + rw + 2; f = 1; }
            else { ox = p.x - rw - 2 - d; f = 3; }
            foot = new RectInt(ox - 1, oz - 1, d + 2, w + 2);
        }
        if (!RectClear(foot)) return;
        MarkOcc(foot);
        FlattenRect(foot);   // хутір стоїть на рівному майданчику
        AddJob(foot.x + foot.width / 2, foot.y + foot.height / 2, () => BuildHouse(f, ox, oz, w, d, cid));
    }

    void BuildRoadsideTree(int tx, int tz)
    {
        if (!TreeClear(tx, tz, 1)) return;
        var rng = new System.Random(Hash(seed ^ 0x55, tx, tz));
        BuildTinyTree(tx, tz, 1, rng, rng.NextDouble() < 0.3, leafSets[rng.Next(2)]);
    }

    // Вулиці міста й рядки хаток уздовж них. Столиця отримує замок, місто вежу, село колодязь чи вітряк
    void PlanCity(City cy, System.Random rng)
    {
        if (cur.modern) { PlanModernCity(cy, rng); return; }

        int cid = cur.colorIdx;
        int rw = Mathf.Max(1, roadWidth);
        int rowSp = rw + 2 * (houseDepthMax + 2);
        int rr = cy.r;

        // центральна споруда
        if (cy.capital)
        {
            RectInt cr = new RectInt(cy.cx - 12, cy.cz - 12, 24, 24);
            MarkOcc(cr);
            int ccx = cy.cx, ccz = cy.cz;
            AddJob(ccx, ccz, () => BuildCastle(ccx, ccz, cid));
        }
        else if (!cy.village)
        {
            RectInt tr = new RectInt(cy.cx - 5, cy.cz - 5, 10, 10);
            MarkOcc(tr);
            int tcx = cy.cx, tcz = cy.cz;
            AddJob(tcx, tcz, () => BuildTower(tcx, tcz, cid));
        }
        else
        {
            int vcx = cy.cx, vcz = cy.cz;
            if (rng.NextDouble() < windmillChance)
            {
                MarkOcc(new RectInt(vcx - 5, vcz - 5, 10, 10));
                AddJob(vcx, vcz, () => BuildWindmill(vcx, vcz, cid));
            }
            else
            {
                MarkOcc(new RectInt(vcx - 3, vcz - 3, 6, 6));
                AddJob(vcx, vcz, () => BuildWell(vcx, vcz, cid));
            }
        }

        int K = rr / rowSp;
        for (int k = -K; k <= K; k++)
        {
            int zs = cy.cz + k * rowSp - rw / 2;
            int dz = zs + rw / 2 - cy.cz;
            float hl2 = (float)rr * rr - (float)dz * dz;
            if (hl2 <= 0f) continue;
            int hl = Mathf.FloorToInt(Mathf.Sqrt(hl2));
            for (int x = cy.cx - hl; x <= cy.cx + hl; x++)
            for (int t = 0; t < rw; t++)
                MarkRoad(x, zs + t, 1);
        }

        int cs = Mathf.Max(8, crossStreetSpacing);
        int J = rr / cs;
        for (int j = -J; j <= J; j++)
        {
            int xs = cy.cx + j * cs - rw / 2;
            int dx = xs + rw / 2 - cy.cx;
            float vl2 = (float)rr * rr - (float)dx * dx;
            if (vl2 <= 0f) continue;
            int vl = Mathf.FloorToInt(Mathf.Sqrt(vl2));
            for (int z = cy.cz - vl; z <= cy.cz + vl; z++)
            for (int t = 0; t < rw; t++)
                MarkRoad(xs + t, z, 1);
        }

        // хатки з обох боків кожної вулиці
        for (int k = -K; k <= K; k++)
        {
            int zs = cy.cz + k * rowSp - rw / 2;
            int dz = zs + rw / 2 - cy.cz;
            float hl2 = (float)rr * rr - (float)dz * dz;
            if (hl2 <= 0f) continue;
            int hl = Mathf.FloorToInt(Mathf.Sqrt(hl2));

            for (int side = -1; side <= 1; side += 2)
            {
                int x = cy.cx - hl + rng.Next(0, 3);
                while (x < cy.cx + hl - houseWidthMin)
                {
                    int w = RI(rng, houseWidthMin, houseWidthMax);
                    int d = RI(rng, houseDepthMin, houseDepthMax);
                    int ox = x, oz, f;
                    if (side > 0) { oz = zs + rw + 1; f = 0; }
                    else { oz = zs - 1 - d; f = 2; }

                    RectInt foot = new RectInt(ox - 1, oz - 1, w + 2, d + 2);
                    if (rng.NextDouble() < houseChance && RectClear(foot))
                    {
                        MarkOcc(foot);
                        int hox = ox, hoz = oz, hf = f, hw = w, hd = d;
                        AddJob(ox + w / 2, oz + d / 2, () => BuildHouse(hf, hox, hoz, hw, hd, cid));
                    }
                    x += w + Mathf.Max(1, houseGap) + rng.Next(0, 2);
                }
            }
        }

        // двори: кущі, дерева, квіти в проміжках між хатками (перевіряється під час будування)
        if (gardenDensity > 0f)
        {
            const int B = 6;
            for (int gz = -rr; gz <= rr; gz += B)
            for (int gx = -rr; gx <= rr; gx += B)
            {
                float cdx = gx + B * 0.5f, cdz = gz + B * 0.5f;
                if (cdx * cdx + cdz * cdz > (rr + 4f) * (rr + 4f)) continue;
                int bx0 = cy.cx + gx, bz0 = cy.cz + gz;
                AddJob(bx0 + B / 2, bz0 + B / 2, () => BuildGarden(bx0, bz0, B));
            }
        }
    }

    // ---------- плитки й черга ----------

    // Острови, габарити яких перетинають прямокутник [x0,x1) x [z0,z1)
    void GatherIslands(int x0, int z0, int x1, int z1, bool ensure)
    {
        tmpNear.Clear();
        int half = sp / 2;
        int rx0 = FloorDiv(x0 + half, sp), rx1 = FloorDiv(x1 - 1 + half, sp);
        int rz0 = FloorDiv(z0 + half, sp), rz1 = FloorDiv(z1 - 1 + half, sp);
        for (int rz = rz0; rz <= rz1; rz++)
        for (int rx = rx0; rx <= rx1; rx++)
        {
            Island s = GetRegion(rx, rz);
            if (!s.exists) continue;
            if (s.ox >= x1 || s.ox + s.S <= x0 || s.oz >= z1 || s.oz + s.S <= z0) continue;
            tmpNear.Add(s);
        }
        if (ensure)
            for (int k = 0; k < tmpNear.Count; k++) Ensure(tmpNear[k]);
    }

    bool TileHasIsland(Vector2Int t)
    {
        int x0 = t.x * T, z0 = t.y * T, x1 = x0 + T, z1 = z0 + T;
        int half = sp / 2;
        int rx0 = FloorDiv(x0 + half, sp), rx1 = FloorDiv(x1 - 1 + half, sp);
        int rz0 = FloorDiv(z0 + half, sp), rz1 = FloorDiv(z1 - 1 + half, sp);
        for (int rz = rz0; rz <= rz1; rz++)
        for (int rx = rx0; rx <= rx1; rx++)
        {
            Island s = GetRegion(rx, rz);
            if (!s.exists) continue;
            if (s.ox >= x1 || s.ox + s.S <= x0 || s.oz >= z1 || s.oz + s.S <= z0) continue;
            return true;
        }
        return false;
    }

    // Планує по одному острову за кадр, поки фокус ще далеко, щоб не було підвисання при підпливанні
    bool PreplanNearby()
    {
        int half = sp / 2;
        int rx = FloorDiv(Mathf.RoundToInt(rockV.x) + half, sp);
        int rz = FloorDiv(Mathf.RoundToInt(rockV.y) + half, sp);
        float R = Mathf.Max(spawnRadius, focusRadius * spawnRadiusInRockRadii) / vs;
        float lim = R + T + 250f;
        for (int dz = -1; dz <= 1; dz++)
        for (int dx = -1; dx <= 1; dx++)
        {
            Island s = GetRegion(rx + dx, rz + dz);
            if (!s.exists || s.land != null) continue;
            float ddx = Mathf.Max(0f, Mathf.Max(s.ox - rockV.x, rockV.x - (s.ox + s.S)));
            float ddz = Mathf.Max(0f, Mathf.Max(s.oz - rockV.y, rockV.y - (s.oz + s.S)));
            if (ddx * ddx + ddz * ddz > lim * lim) continue;
            Ensure(s);
            return true;
        }
        return false;
    }

    // Найближча до фокуса ще не збудована плитка, що перетинає острів
    bool FindNearestTile(float reach, out Vector2Int best)
    {
        best = default;
        float bestD = float.MaxValue;
        bool found = false;
        int rt = Mathf.CeilToInt(reach / T) + 1;
        int cx = FloorDiv(Mathf.RoundToInt(rockV.x), T), cz = FloorDiv(Mathf.RoundToInt(rockV.y), T);
        float reach2 = reach * reach;
        for (int dz = -rt; dz <= rt; dz++)
        for (int dx = -rt; dx <= rt; dx++)
        {
            var t = new Vector2Int(cx + dx, cz + dz);
            float px = (t.x + 0.5f) * T - rockV.x, pz = (t.y + 0.5f) * T - rockV.y;
            float d = px * px + pz * pz;
            if (d > reach2 || d >= bestD) continue;
            if (built.Contains(t)) continue;
            if (!TileHasIsland(t)) continue;
            bestD = d;
            best = t;
            found = true;
        }
        return found;
    }

    IEnumerator Worker()
    {
        // даємо VoxelWorld.SettleOnStart відпрацювати раніше за нас
        for (int i = 0; i < 4; i++) yield return null;

        while (true)
        {
            if (!UpdateFocus())
            {
                yield return null;
                continue;
            }

            // нічого будувати: чекаємо, поки фокус зрушить
            if (idle && (rockV - idlePos).sqrMagnitude < (T * 0.5f) * (T * 0.5f))
            {
                yield return null;
                continue;
            }

            if (PreplanNearby())
            {
                idle = false;
                yield return null;
                continue;
            }

            float Rv = Mathf.Max(spawnRadius, focusRadius * spawnRadiusInRockRadii) / vs;   // у вокселях
            if (!FindNearestTile(Rv + T, out Vector2Int tile))
            {
                if (debugLog && !idle)
                    Debug.Log("[CountryGenerator] Поруч із фокусом " + rockV + " (вокселі) немає непобудованих плиток з островом. Збудовано плиток: " + dbgTiles);
                idle = true;
                idlePos = rockV;
                yield return null;
                continue;
            }
            idle = false;

            built.Add(tile);
            BuildTile(tile);

            int idx = 0;
            while (idx < ops.Count)
            {
                int end = Mathf.Min(ops.Count, idx + Mathf.Max(100, voxelsPerFrame));
                for (; idx < end; idx++)
                {
                    VoxelOp op = ops[idx];
                    // нічого не затираємо: деталі завжди ставляться ПЕРШИМИ, а земля потім
                    if (world.GetVoxel(op.pos) == 0)
                    {
                        world.SetVoxel(op.pos, op.id);
                        dbgVoxels++;
                    }
                }
                yield return null;
            }
            ops.Clear();
            yield return null;
        }
    }

    RockAbsorber FindRock()
    {
        RockAbsorber[] all = FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None);
        if (all.Length == 0) return null;
        if (all.Length == 1) return all[0];

        // під час переходу між сценами існує й заглушка нової сцени: беремо камінь активної сцени
        Scene active = SceneManager.GetActiveScene();
        for (int i = 0; i < all.Length; i++)
            if (all[i].gameObject.scene == active) return all[i];
        return all[0];
    }

    // Оновлює фокус стрімінгу (позиція у світових вокселях). Фокус це камінь, а якщо каменя
    // немає більше Rock Wait Seconds, то камера. false, поки фокуса ще немає
    bool UpdateFocus()
    {
        if (rock == null || Time.unscaledTime >= nextRockSearch)
        {
            nextRockSearch = Time.unscaledTime + 1f;
            RockAbsorber found = FindRock();
            if (found == null)
            {
                rock = null;
            }
            else if (found != rock)
            {
                rock = found;
                usingFallback = false;

                // швидкий камінь не повинен проскакувати крізь землю
                Rigidbody rb = rock.GetComponent<Rigidbody>();
                if (rb != null) rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

                PlaceRockOnLand();
                if (debugLog) Debug.Log("[CountryGenerator] Камінь знайдено й поставлено на " + rock.WorldCenter);
            }
        }

        if (rock != null)
        {
            focusWorld = rock.WorldCenter;
            focusRadius = Mathf.Max(0.1f, rock.Radius);
            noRockTimer = 0f;
        }
        else
        {
            noRockTimer += Time.unscaledDeltaTime;
            if (noRockTimer < rockWaitSeconds) return false;

            Camera cam = Camera.main;
            if (cam == null) cam = FindFirstObjectByType<Camera>();
            focusWorld = cam != null ? cam.transform.position : StartPoint;
            focusRadius = 1f;

            if (!usingFallback)
            {
                usingFallback = true;
                Debug.LogWarning("[CountryGenerator] RockAbsorber не знайдено на сцені. Острови будуються навколо " +
                                 (cam != null ? "камери" : "стартової точки") + ". Додай камінь на сцену, щоб він ставився на сушу.", this);
            }
        }

        Vector3 l = world.transform.InverseTransformPoint(focusWorld);
        rockV = new Vector2(l.x / vs, l.z / vs);
        return true;
    }

    // Переносить камінь на сушу стартового острова й ставить його над найвищою землею навколо
    void PlaceRockOnLand()
    {
        float r = rock.Radius;

        int cxv = Mathf.RoundToInt(spawnV.x), czv = Mathf.RoundToInt(spawnV.y);
        int rv = Mathf.CeilToInt(r / vs) + 2;
        int hmax = 1;
        for (int dz = -rv; dz <= rv; dz += 2)
        for (int dx = -rv; dx <= rv; dx += 2)
            hmax = Mathf.Max(hmax, GroundH(cxv + dx, czv + dz));

        Vector3 target = world.transform.TransformPoint(new Vector3(spawnV.x * vs, 0f, spawnV.y * vs));
        target.y = hmax * vs + r + 0.5f;           // море на y = 0
        rock.transform.position += target - rock.WorldCenter;

        Rigidbody rb = rock.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = rock.transform.position;
            rb.linearVelocity = Vector3.zero;      // у старих версіях Unity: rb.velocity
            rb.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();

        startV = spawnV;
        startClearV = Mathf.Max(14f, r * startClearInRockRadii / vs);
        haveStart = true;
        idle = false;
    }

    void BuildTile(Vector2Int tile)
    {
        int x0 = tile.x * T, z0 = tile.y * T;
        GatherIslands(x0, z0, x0 + T, z0 + T, true);

        ops.Clear();
        if (jobs.TryGetValue(tile, out var list))
        {
            for (int k = 0; k < list.Count; k++) list[k]();
            jobs.Remove(tile);
        }
        fixedBase = -1;
        BuildTileGround(tile);
        dbgTiles++;

        if (debugLog && (dbgTiles <= 3 || dbgTiles % 25 == 0))
            Debug.Log("[CountryGenerator] Плитка " + tile + " збудована: вокселів у черзі=" + ops.Count +
                      ", плиток усього=" + dbgTiles + ", вокселів поставлено=" + dbgVoxels);
    }

    // ---------- рельєфна суша: вокселі землі ----------

    byte RoadColor(int kind, int x, int z)
    {
        if (kind == 1) return Hash01(x, 0, z) < 0.2f ? gravelSet[2] : gravelSet[Hash01(x, 1, z) < 0.5f ? 0 : 1];
        return dirtSet[Hash01(x, 2, z) < 0.5f ? 0 : 1];
    }

    // Для кожної клітинки суші ставить суцільну колонку вокселів від y = 0 до висоти землі.
    // Зверху трава, пляж або дорога, під нею ґрунт, ще нижче камінь. Круті схили стають скелями.
    // Озера й море це просто відсутність колонок: крізь них видно море SeaGroundGenerator
    void BuildTileGround(Vector2Int tile)
    {
        for (int k = 0; k < tmpNear.Count; k++) Ensure(tmpNear[k]);

        int x0 = tile.x * T, z0 = tile.y * T;
        int x1 = x0 + T, z1 = z0 + T;

        for (int z = z0; z < z1; z++)
        for (int x = x0; x < x1; x++)
        {
            Island s = null;
            for (int k = 0; k < tmpNear.Count; k++)
            {
                Island c = tmpNear[k];
                if (x >= c.ox && x < c.ox + c.S && z >= c.oz && z < c.oz + c.S) { s = c; break; }
            }
            if (s == null || s.land == null || s.hgt == null) continue;
            int i = (x - s.ox) + s.S * (z - s.oz);
            if (!s.land[i]) continue;

            int h = s.hgt[i];
            byte kind = s.road[i];
            bool beach = s.dist[i] <= beachWidth;

            // верхній воксель
            byte top;
            if (kind != 0)
            {
                top = s.modern ? ModernRoadColor(kind, x, z) : RoadColor(kind, x, z);
            }
            else if (beach)
            {
                top = sandSet[Hash01(x, 4, z) < 0.5f ? 0 : 1];
            }
            else
            {
                bool cliff = false;
                if (h > baseHeight + 2)
                {
                    int lo = Mathf.Min(Mathf.Min(GroundH(x + 1, z), GroundH(x - 1, z)),
                                       Mathf.Min(GroundH(x, z + 1), GroundH(x, z - 1)));
                    cliff = h - lo >= cliffDrop;
                }
                if (cliff)
                {
                    top = Hash01(x, 7, z) < 0.5f ? cStone : cStone2;
                }
                else
                {
                    float r = Hash01(x, 5, z);
                    top = r < 0.12f ? cGroundDirt : (r < 0.55f ? cGroundA : cGroundB);
                }
            }
            AddAbs(x, h - 1, z, top);

            // нижні шари: ґрунт (або пісок на пляжі), далі камінь
            for (int y = h - 2; y >= 0; y--)
            {
                byte id;
                if (y >= h - 1 - soilLayers)
                    id = beach ? sandSet[Hash01(x, y, z) < 0.5f ? 0 : 1] : dirtSet[Hash01(x, y + 9, z) < 0.5f ? 0 : 1];
                else
                    id = Hash01(x, y + 17, z) < 0.5f ? cStone2 : cStone;
                AddAbs(x, y, z, id);
            }
        }
    }

    // =====================================================================================
    // Малювання в координатах будівлі: lx вздовж фасаду, lz вглиб від фасаду (фасад біля lz = 0)
    // facing: 0 = фасад дивиться на -z, 1 = на -x, 2 = на +z, 3 = на +x
    // =====================================================================================

    // Задає перетворення, піднімає будівлю на найвищу землю під її основою й будує фундамент у ямах
    void SetT(int f, int ox, int oz, int w, int d)
    {
        tF = f; tOx = ox; tOz = oz; tW = w; tD = d;

        int bmax = 0;
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            LocalToWorld(lx, lz, out int x, out int z);
            int gh = GroundH(x, z);
            if (gh > bmax) bmax = gh;
        }
        fixedBase = bmax;

        if (bmax <= 0) return;
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            LocalToWorld(lx, lz, out int x, out int z);
            int gh = GroundH(x, z);
            if (gh <= 0) continue;
            for (int y = gh; y < bmax; y++)
                AddAbs(x, y, z, cStone2);
        }
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

    // Двосхилий дах. Гребінь іде вздовж lx (alongX) або вздовж lz
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

    // ---------- крихітна хатка ----------

    void BuildHouse(int f, int ox, int oz, int w, int d, int cIdx)
    {
        var rng = new System.Random(Hash(seed ^ 0x2468, ox, oz));
        SetT(f, ox, oz, w, d);
        int wh = Mathf.Max(2, houseWallHeight) + (rng.NextDouble() < tallHouseChance ? 1 : 0);
        byte[] roofSet = rng.NextDouble() < thatchChance ? thatchSet : roofSets[cIdx % roofSets.Length];

        // деталі першими: двері, вікна, комин
        PH(w / 2, 1, 0, cDoor);
        if (w >= 5)
        {
            PH(1, 1, 0, cGlass);
            PH(w - 2, 1, 0, cGlass);
        }
        PH(0, 1, d / 2, cGlass);
        PH(w - 1, 1, d / 2, cGlass);
        if (wh >= 3)
        {
            PH(0, 2, d / 2, cGlass);
            PH(w - 1, 2, d / 2, cGlass);
        }
        if (rng.NextDouble() < chimneyChance)
        {
            int top = wh + (d + 2) / 2 + 1;
            for (int y = wh; y <= top; y++)
                PH(w - 2, y, d / 2, y == top ? cCap : cBrick);
        }

        // стіни
        for (int y = 0; y < wh; y++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            if (lx != 0 && lx != w - 1 && lz != 0 && lz != d - 1) continue;
            PH(lx, y, lz, Pick(wallSet, Hash(lx, y, lz)));
        }

        GableRoof(w, d, wh, true, wallSet[0], roofSet);
        PBox(0, 0, 0, w, 1, d, cFloor);
    }

    // ---------- замок у столиці ----------

    void BuildCastle(int cx, int cz, int cIdx)
    {
        const int S = 21;
        int ox = cx - S / 2, oz = cz - S / 2;
        SetT(0, ox, oz, S, S);
        byte flag = flagSet[cIdx % flagSet.Length];
        byte roof = roofSets[cIdx % roofSets.Length][0];

        // прапор на головній вежі (першим)
        for (int y = 9; y <= 17; y++) PH(10, y, 10, cPost);
        for (int y = 15; y <= 17; y++)
        for (int lx = 11; lx <= 14; lx++)
            PH(lx, y, 10, flag);

        // стіни з брамою й зубцями
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            if (lx != 0 && lx != S - 1 && lz != 0 && lz != S - 1) continue;
            for (int y = 0; y < 5; y++)
            {
                if (lz == 0 && lx >= 9 && lx <= 11 && y < 4) continue;
                PH(lx, y, lz, ((y & 1) == 0) ? cStone : cStone2);
            }
            if (((lx + lz) & 1) == 0) PH(lx, 5, lz, cStone);
        }

        // кутові вежі з дахами
        int[] tx = { 0, S - 5, 0, S - 5 };
        int[] tz = { 0, 0, S - 5, S - 5 };
        for (int t = 0; t < 4; t++)
        {
            PBox(tx[t], 0, tz[t], 5, 10, 5, cStone2);
            LocalToWorld(tx[t] + 2, tz[t] + 2, out int wx, out int wz);
            for (int i = 0; i < 4; i++)
                Disc(wx, wz, 3.4f - i * 0.9f, 10 + i, 11 + i, roof);
        }

        // головна вежа з конусним дахом
        PBox(7, 0, 7, 7, 9, 7, cStone);
        LocalToWorld(10, 10, out int kx, out int kz);
        for (int i = 0; i < 4; i++)
            Disc(kx, kz, 4.4f - i * 1.1f, 9 + i, 10 + i, roof);

        PBox(0, 0, 0, S, 1, S, cFloor);
    }

    // ---------- вежа в звичайному місті ----------

    void BuildTower(int cx, int cz, int cIdx)
    {
        const int S = 5;
        int ox = cx - S / 2, oz = cz - S / 2;
        SetT(0, ox, oz, S, S);
        byte roof = roofSets[cIdx % roofSets.Length][1];

        PH(2, 1, 0, cDoor);
        PH(2, 2, 0, cDoor);
        for (int y = 4; y <= 6; y += 2)
        {
            PH(2, y, 0, cGlass);
            PH(0, y, 2, cGlass);
            PH(S - 1, y, 2, cGlass);
            PH(2, y, S - 1, cGlass);
        }

        for (int y = 0; y < 8; y++)
        for (int lz = 0; lz < S; lz++)
        for (int lx = 0; lx < S; lx++)
        {
            if (lx != 0 && lx != S - 1 && lz != 0 && lz != S - 1) continue;
            PH(lx, y, lz, ((y & 1) == 0) ? cStone : cStone2);
        }

        LocalToWorld(2, 2, out int wx, out int wz);
        for (int i = 0; i < 5; i++)
            Disc(wx, wz, 3.6f - i * 0.8f, 8 + i, 9 + i, roof);
        PBox(0, 0, 0, S, 1, S, cFloor);
    }

    // ---------- колодязь у селі ----------

    void BuildWell(int cx, int cz, int cIdx)
    {
        SetT(0, cx - 1, cz - 1, 3, 3);
        byte roof = roofSets[cIdx % roofSets.Length][0];

        // стійки й дашок першими
        for (int y = 2; y <= 3; y++)
        {
            PH(0, y, 1, cPost);
            PH(2, y, 1, cPost);
        }
        PBox(0, 4, 0, 3, 1, 3, roof);

        // зруб із водою всередині
        for (int lz = 0; lz < 3; lz++)
        for (int lx = 0; lx < 3; lx++)
        {
            if (lx == 1 && lz == 1) { PH(1, 0, 1, cWater); continue; }
            PH(lx, 0, lz, cStone);
            PH(lx, 1, lz, cStone2);
        }
    }

    // ---------- вітряк у селі ----------

    void BuildWindmill(int cx, int cz, int cIdx)
    {
        SetT(0, cx - 2, cz - 2, 5, 5);
        byte roof = roofSets[cIdx % roofSets.Length][1];

        // лопаті на передній стороні (першими)
        PH(2, 6, -1, cDoor);
        for (int k = -4; k <= 4; k++)
        {
            if (k == 0) continue;
            PH(2 + k, 6, -1, cPost);
            PH(2, 6 + k, -1, cPost);
        }
        for (int k = -3; k <= 3; k++)
        {
            if (k == 0) continue;
            PH(2 + k, 6 + k, -1, cPost);
            PH(2 + k, 6 - k, -1, cPost);
        }

        PH(2, 1, 0, cDoor);
        PH(2, 2, 0, cDoor);
        PH(0, 3, 2, cGlass);
        PH(4, 3, 2, cGlass);

        for (int y = 0; y < 4; y++)
        for (int lz = 0; lz < 5; lz++)
        for (int lx = 0; lx < 5; lx++)
        {
            if (lx != 0 && lx != 4 && lz != 0 && lz != 4) continue;
            PH(lx, y, lz, Pick(wallSet, Hash(lx, y, lz)));
        }
        for (int y = 4; y < 6; y++)
        for (int lz = 1; lz < 4; lz++)
        for (int lx = 1; lx < 4; lx++)
        {
            if (lx != 1 && lx != 3 && lz != 1 && lz != 3) continue;
            PH(lx, y, lz, Pick(wallSet, Hash(lx, y, lz)));
        }

        LocalToWorld(2, 2, out int wx, out int wz);
        Disc(wx, wz, 2.3f, 6, 7, roof);
        Disc(wx, wz, 1.3f, 7, 8, roof);
        PBox(0, 0, 0, 5, 1, 5, cFloor);
    }

    // ---------- двір: кущі, дерева, квіти, камінці ----------

    void BuildGarden(int x0, int z0, int s)
    {
        var rng = new System.Random(Hash(seed ^ 0x999, x0, z0));
        int attempts = Mathf.CeilToInt(5f * gardenDensity);
        for (int a = 0; a < attempts; a++)
        {
            int x = x0 + rng.Next(0, s), z = z0 + rng.Next(0, s);
            double roll = rng.NextDouble();
            if (roll < 0.40) BuildBush(x, z, rng, leafSets[rng.Next(2)]);
            else if (roll < 0.65)
            {
                if (TreeClear(x, z, 1)) BuildTinyTree(x, z, 1, rng, false, leafSets[rng.Next(3)]);
            }
            else if (roll < 0.90)
            {
                if (Free(x, z)) Add(x, 0, z, Pick(flowerColors, Hash(x, 51, z)));
            }
            else
            {
                if (Free(x, z)) Add(x, 0, z, cStone);
            }
        }
    }

    // ---------- ділянки між поселеннями ----------

    // type: 0 пшениця, 1 соняшники, 2 лука, 3 сад, 4 ліс, 5 город
    void BuildPlot(RectInt plot, int type)
    {
        var rng = new System.Random(Hash(seed ^ 0x777, plot.x, plot.y));
        int x0 = plot.x + 1, x1 = plot.xMax - 1;
        int z0 = plot.y + 1, z1 = plot.yMax - 1;
        if (x1 <= x0 || z1 <= z0) return;

        // паркан навколо полів і городів (першим, щоб не затерли посіви)
        if ((type <= 1 || type == 5) && rng.NextDouble() < fenceChance)
            BuildFence(x0, z0, x1, z1);

        switch (type)
        {
            case 0:
                // вітряк посеред поля
                if (rng.NextDouble() < windmillChance * 0.5)
                {
                    int wx = (x0 + x1) / 2, wz = (z0 + z1) / 2;
                    if (TreeClear(wx, wz, 3)) BuildWindmill(wx, wz, Mathf.Abs(Hash(plot.x, 3, plot.y)));
                    fixedBase = -1;
                }
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    byte row = wheatSet[(FloorDiv(z, 2) & 1) == 0 ? 0 : 1];
                    Add(x, 0, z, row);
                    if (Hash01(x, 11, z) < 0.7f)
                        Add(x, 1, z, Pick(wheatSet, Hash(x, 12, z)));
                }
                // копиці сіна
                for (int n = rng.Next(0, 3); n > 0; n--)
                {
                    int hx = rng.Next(x0 + 4, Mathf.Max(x0 + 5, x1 - 4)), hz = rng.Next(z0 + 4, Mathf.Max(z0 + 5, z1 - 4));
                    if (!TreeClear(hx, hz, 2)) continue;
                    fixedBase = GroundH(hx, hz);
                    for (int i = 0; i < 3; i++)
                        Disc(hx, hz, 2.2f * Mathf.Sqrt(1f - (i / 3f) * (i / 3f)), i + 1, i + 2, i % 2 == 0 ? cStraw : cStraw2);
                    fixedBase = -1;
                }
                break;

            case 1:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    Add(x, 0, z, ((x + z) & 1) == 0 ? cSoil : cSoilDark);
                    if (FloorDiv(x, 3) * 3 != x || FloorDiv(z, 3) * 3 != z) continue;
                    fixedBase = GroundH(x, z);
                    for (int y = 1; y <= 2; y++) Add(x, y, z, cStem);
                    Add(x, 3, z, cSunCenter);
                    Add(x + 1, 3, z, cSun); Add(x - 1, 3, z, cSun);
                    Add(x, 3, z + 1, cSun); Add(x, 3, z - 1, cSun);
                    fixedBase = -1;
                }
                break;

            case 2:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    float r = Hash01(x, 21, z);
                    if (r < 0.07f) Add(x, 0, z, Pick(flowerColors, Hash(x, 22, z)));
                    else if (r < 0.24f)
                    {
                        Add(x, 0, z, Pick(grassSet, Hash(x, 24, z)));
                        if (Hash01(x, 23, z) < 0.3f) Add(x, 1, z, Pick(grassSet, Hash(x, 25, z)));
                    }
                }
                for (int n = rng.Next(0, 5); n > 0; n--)
                    BuildBush(rng.Next(x0, x1), rng.Next(z0, z1), rng, leafSets[rng.Next(2)]);
                for (int n = rng.Next(0, 3); n > 0; n--)
                    BuildBoulder(rng.Next(x0 + 2, Mathf.Max(x0 + 3, x1 - 2)), rng.Next(z0 + 2, Mathf.Max(z0 + 3, z1 - 2)), rng.Next(1, 3), rng);
                break;

            case 3:
                for (int z = z0 + 2; z < z1 - 1; z += 5)
                for (int x = x0 + 2; x < x1 - 1; x += 5)
                {
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    if (!TreeClear(tx, tz, 1)) continue;
                    BuildTinyTree(tx, tz, 1, rng, false, leafSets[2]);
                }
                break;

            case 5:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    bool rowSoil = (FloorDiv(z, 2) & 1) == 0;
                    Add(x, 0, z, rowSoil ? cSoil : cSoilDark);
                    if (rowSoil || Hash01(x, 41, z) > 0.55f) continue;
                    float r = Hash01(x, 42, z);
                    Add(x, 1, z, r < 0.4f ? cCrop : (r < 0.7f ? cCabbage : cPumpkin));
                }
                break;

            default:
                for (int z = z0 + 1; z < z1 - 1; z += 3)
                for (int x = x0 + 1; x < x1 - 1; x += 3)
                {
                    if (rng.NextDouble() > treeDensity) continue;
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    int r = rng.NextDouble() < 0.3 ? 2 : 1;
                    if (!TreeClear(tx, tz, r)) continue;
                    BuildTinyTree(tx, tz, r, rng, rng.NextDouble() < 0.4, leafSets[rng.Next(2)]);
                }
                // підлісок і валуни
                for (int n = rng.Next(3, 10); n > 0; n--)
                    BuildBush(rng.Next(x0, x1), rng.Next(z0, z1), rng, leafSets[rng.Next(2)]);
                for (int n = rng.Next(0, 3); n > 0; n--)
                    BuildBoulder(rng.Next(x0 + 2, Mathf.Max(x0 + 3, x1 - 2)), rng.Next(z0 + 2, Mathf.Max(z0 + 3, z1 - 2)), rng.Next(1, 3), rng);
                break;
        }
    }

    // ---------- паркан ----------

    void BuildFence(int x0, int z0, int x1, int z1)
    {
        for (int x = x0; x < x1; x++)
        {
            FencePiece(x, z0, x);
            FencePiece(x, z1 - 1, x);
        }
        for (int z = z0 + 1; z < z1 - 1; z++)
        {
            FencePiece(x0, z, z);
            FencePiece(x1 - 1, z, z);
        }
    }

    void FencePiece(int x, int z, int k)
    {
        if (FloorDiv(k, 17) * 17 == k) return;     // хвіртка
        if (!Free(x, z)) return;
        Add(x, 0, z, cPost);
        if (FloorDiv(k, 3) * 3 == k) Add(x, 1, z, cPost);
    }

    // ---------- кущі, валуни ----------

    void BuildBush(int x, int z, System.Random rng, byte[] leaves)
    {
        if (!Free(x, z)) return;
        Add(x, 0, z, leaves[0]);
        if (rng.NextDouble() < 0.7) Add(x, 1, z, leaves[1]);
        for (int d = 0; d < 4; d++)
        {
            int dx = d == 0 ? 1 : d == 1 ? -1 : 0;
            int dz = d == 2 ? 1 : d == 3 ? -1 : 0;
            if (rng.NextDouble() < 0.4 && Free(x + dx, z + dz))
                Add(x + dx, 0, z + dz, leaves[rng.Next(0, 3)]);
        }
    }

    // Валун трохи вростає в землю, щоб на схилі не висів у повітрі
    void BuildBoulder(int gx, int gz, int r, System.Random rng)
    {
        if (!TreeClear(gx, gz, r)) return;
        int ob = fixedBase;
        fixedBase = Mathf.Max(0, GroundH(gx, gz) - 1);
        for (int dy = 0; dy <= r; dy++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            float jitter = Hash01(gx + dx, dy, gz + dz);
            if (dx * dx + dz * dz + dy * dy * 2f > r * r + 0.5f - jitter * 0.8f) continue;
            Add(gx + dx, dy, gz + dz, jitter < 0.5f ? cStone : cStone2);
        }
        fixedBase = ob;
    }

    // ---------- крихітні дерева ----------

    // Дерево ставиться на висоту землі під стовбуром
    void BuildTinyTree(int gx, int gz, int r, System.Random rng, bool pine, byte[] leaves)
    {
        int ob = fixedBase;
        fixedBase = GroundH(gx, gz);
        BuildTinyTreeCore(gx, gz, r, rng, pine, leaves);
        fixedBase = ob;
    }

    void BuildTinyTreeCore(int gx, int gz, int r, System.Random rng, bool pine, byte[] leaves)
    {
        int trunkH = RI(rng, 2, 3) + (r >= 2 ? 1 : 0);
        for (int y = 0; y < trunkH; y++)
            Add(gx, y, gz, (y & 1) == 0 ? cBarkA : cBarkB);

        if (pine)
        {
            int top = trunkH + r * 2;
            for (int y = 1; y <= top; y++)
            {
                float t = (y - 1) / (float)Mathf.Max(1, top - 1);
                float rad = (r + 0.4f) * (1f - t) + 0.3f;
                Disc(gx, gz, rad, y, y + 1, Pick(pineSet, Hash(gx, y, gz)));
            }
            return;
        }

        float cy = trunkH + r * 0.3f;
        float ry = r * 0.9f + 0.5f;
        int y0 = Mathf.FloorToInt(cy - ry), y1 = Mathf.CeilToInt(cy + ry);
        for (int y = Mathf.Max(0, y0); y <= y1; y++)
        for (int dz = -r; dz <= r; dz++)
        for (int dx = -r; dx <= r; dx++)
        {
            int x = gx + dx, z = gz + dz;
            float nx = dx / (r + 0.3f);
            float ny = (y + 0.5f - cy) / ry;
            float nz = dz / (r + 0.3f);
            if (nx * nx + ny * ny + nz * nz > 1f - Hash01(x, y, z) * 0.3f) continue;

            int shade = ny > 0.35f ? 1 : (ny < -0.35f ? 2 : 0);
            if (Hash01(x + 7, y, z) < 0.25f) shade = (shade + 1) % 3;
            Add(x, y, z, leaves[shade]);
        }
    }

    // ---------- примітиви ----------

    // Абсолютна висота (вокселі від моря)
    void AddAbs(int x, int y, int z, byte id)
    {
        ops.Add(new VoxelOp { pos = new Vector3Int(x, y, z), id = id });
    }

    // Висота y рахується від землі: або від fixedBase (для цілого об'єкта), або від землі під самим вокселем
    void Add(int x, int y, int z, byte id)
    {
        int b = fixedBase >= 0 ? fixedBase : GroundH(x, z);
        AddAbs(x, y + b, z, id);
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