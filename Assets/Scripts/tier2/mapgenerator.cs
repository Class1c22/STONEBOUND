using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

// Генератор міста для 2 рівня: дороги, будинки з меблями, дерева, паркани й різні цікаві ділянки з вокселів.
// Повісь на порожній GameObject, перетягни VoxelWorld. Камінь береться зі сцени сам (у Inspector його
// призначати не треба): це працює й тоді, коли камінь приходить зі сцени 1 через TierTransition.
//
// Як це працює:
//  * Світ ділиться на квартали. Вздовж західного й південного краю кожного кварталу йде дорога,
//    тож дороги самі складаються в сітку з перехрестями. Вздовж доріг стоять ліхтарі.
//  * Решта кварталу ділиться на ділянки (lotsPerSide x lotsPerSide). Кожна ділянка за хешем
//    (seed + координати) стає будинком із двором, гаєм, особливою ділянкою (фонтан, ставок,
//    парковка, ярмарок...) або лишається порожньою.
//  * Будинки порожні всередині: підлога, колони (щоб дах не падав), сходи на другий поверх
//    і меблі за темою кімнати (вітальня, спальня, кухня, кабінет, майстерня).
//  * Квартали з'являються навколо каменя, найближчі першими, і розставляються по кадрах
//    (voxelsPerFrame), щоб не було підвисань. Те саме місце завжди дає те саме місто.
//  * Підлога має бути на y = 0 (FloorGenerator), VoxelWorld на (0,0,0) з масштабом 1.
public class Tier2CityGenerator : MonoBehaviour
{
    public enum RoadMode
    {
        [Tooltip("Плоский воксельний килим без колайдера: камінь їде по підлозі й не ламає дорогу")]
        FlatMesh,
        [Tooltip("Справжні вокселі завтовшки 1 воксель: камінь може їх вибивати")]
        SolidVoxels
    }

    [Header("Зв'язки")]
    [Tooltip("Якщо порожньо, береться VoxelWorld зі сцени")]
    public VoxelWorld world;
    public int seed = 12345;

    [Header("Стрімінг")]
    [Tooltip("Як далеко від каменя з'являються квартали (метри)")]
    public float spawnRadius = 90f;
    [Tooltip("Те саме, але в радіусах каменя: коли камінь росте, місто з'являється далі. Береться більше з двох")]
    public float spawnRadiusInRockRadii = 60f;
    [Tooltip("Скільки вокселів ставити за кадр. Менше = плавніше, але будівлі з'являються довше")]
    public int voxelsPerFrame = 3000;
    public float scanInterval = 0.3f;
    [Tooltip("Навколо стартової позиції каменя будинків і дерев не буде (метри). Дороги лишаються")]
    public float startClearRadius = 12f;

    [Header("Квартали й дороги (метри)")]
    public float blockSize = 28f;
    public float roadWidth = 5f;
    [Range(1, 4)] public int lotsPerSide = 2;
    public RoadMode roadMode = RoadMode.FlatMesh;
    [Tooltip("Висота плоскої дороги над підлогою (для FlatMesh), щоб не мерехтіла разом із підлогою")]
    public float roadHeight = 0.03f;
    [Tooltip("Шанс ліхтаря в кожній з точок уздовж дороги")]
    [Range(0f, 1f)] public float streetLampChance = 0.7f;

    [Header("Ділянки")]
    [Range(0f, 1f)] public float houseChance = 0.5f;
    [Tooltip("Шанс гаю (кілька дерев)")]
    [Range(0f, 1f)] public float treeLotChance = 0.15f;
    [Tooltip("Шанс особливої ділянки: фонтан, майданчик, ставок, город, парковка, водонапірна вежа, ярмарок. Решта ділянок порожні")]
    [Range(0f, 1f)] public float specialLotChance = 0.25f;
    [Range(0f, 1f)] public float fenceChance = 0.6f;
    [Tooltip("Скільки дерев максимум у дворі будинку (якщо вміщаються)")]
    public int treesPerYardMax = 2;
    [Tooltip("Скільки дрібниць максимум у дворі: кущі, клумби, лавки, будка, мангал, машина, басейн")]
    public int propsPerYardMax = 2;
    [Range(0f, 1f)] public float mailboxChance = 0.7f;
    public int treesPerGroveMin = 2;
    public int treesPerGroveMax = 5;

    [Header("Будинки (метри)")]
    public float houseWidthMin = 6f;
    public float houseWidthMax = 9f;
    public float houseDepthMin = 5f;
    public float houseDepthMax = 8f;
    public float floorHeight = 3f;
    public float wallThickness = 0.5f;
    [Range(0f, 1f)] public float twoFloorChance = 0.35f;
    [Range(0f, 1f)] public float flatRoofChance = 0.2f;
    [Range(0f, 1f)] public float chimneyChance = 0.5f;
    [Tooltip("Порожні всередині. Дах підпирають колони з кроком pillarSpacing, тож він не падає. " +
             "Якщо вимкнути, будинки знову стануть суцільними (без меблів)")]
    public bool hollowHouses = true;
    [Tooltip("Крок колон усередині будинку у вокселях. Якщо дах усе ж падає, зменш")]
    public int pillarSpacing = 10;
    [Tooltip("Ставити меблі (працює лише з hollowHouses)")]
    public bool furnishHouses = true;

    [Header("Дерева (метри)")]
    public float trunkHeightMin = 3.5f;
    public float trunkHeightMax = 5.5f;
    public float crownRadiusMin = 1.6f;
    public float crownRadiusMax = 2.4f;
    [Range(0f, 1f)] public float pineChance = 0.3f;

    [Header("Паркани (метри)")]
    public float fenceHeight = 1.2f;
    public float gateWidth = 2f;

    // ---------- внутрішній стан ----------

    struct VoxelOp
    {
        public Vector3Int pos;
        public byte id;
    }

    readonly List<VoxelOp> ops = new List<VoxelOp>();
    readonly HashSet<Vector2Int> requested = new HashSet<Vector2Int>();
    readonly List<Vector2Int> queue = new List<Vector2Int>();

    float vs;                                  // розмір вокселя
    int blockV, roadV, lotV, floorV, wallV, fenceV, gateV;
    int doorHalf = 4, doorH = 9;

    // камінь береться зі сцени автоматично (див. FindRock), у Inspector його немає
    RockAbsorber rock;

    float scanTimer;
    bool haveStart;
    Vector2 startLocal, lastRockLocal;

    // зайняті прямокутники поточної ділянки (світові воксельні координати x/z)
    readonly List<RectInt> taken = new List<RectInt>();

    // стан поточного будинку
    bool stairsOk;
    int stairZs;
    int hFacing, hOx, hOz;
    int ix0, ix1, iz0, iz1;
    byte[,] occ;
    readonly List<Vector2Int> pil = new List<Vector2Int>();

    // поточне перетворення для малювання меблів і дрібниць
    int tFacing, tOx, tOz, tX0, tZ0, tA, tB, tS, tY0, tH;

    // кольори (індекси палітри VoxelWorld)
    byte[] wallA, wallB, roofA, roofB;
    byte[][] leafSets;
    byte[] pineSet;
    byte cPlinth, cTrim, cGlass, cFrame, cDoor, cInterior, cBrick;
    byte cAsphaltA, cAsphaltB, cCurb, cLine;
    byte cPost, cPlank, cRail, cBarkA, cBarkB;

    byte cFloorA, cFloorB, cInnerWall, cWoodDark, cWoodMid;
    byte cWhite, cBlack, cMetal, cGray, cScreen, cSheet, cPot, cLeafG, cLampShade, cCabinet, cCounter, cFire;
    byte cWater, cWaterLight, cWaterDeep, cStone, cStone2, cSand, cSoil, cSoilDark, cPath, cPathB;
    byte cRedToy, cYellowToy, cBlueToy, cSeat, cPaveA, cPaveB, cPlayfloor, cSlide;
    byte cGlassCar, cTire, cTail, cHeadLamp, cPiano;
    byte cHay, cHayB, cStraw, cShirt, cHat, cReed, cLily;
    byte cTankA, cTankB, cConcrete, cRoofRed, cLampLight, cDirt;
    byte cOrange, cCabbage, cPumpkin, cSun, cCorn;
    byte[] bookColors, blanketColors, sofaA, sofaB, rugA, rugB, carColors, flowerColors, awA, awB, goodsColors;

    // буфери для плоскої дороги
    readonly List<Vector3> mVerts = new List<Vector3>();
    readonly List<Vector3> mNormals = new List<Vector3>();
    readonly List<Vector2> mUvs = new List<Vector2>();
    readonly List<int> mTris = new List<int>();

    // меблі: 0 ліжко, 1 шафа, 2 книжкова полиця, 3 диван, 4 телевізор, 5 стіл зі стільцями, 6 письмовий стіл,
    // 7 кухня, 8 холодильник, 9 рослина, 10 торшер, 11 верстак, 12 ящики, 13 камін, 14 піаніно
    static readonly int[] DimA = { 7, 8, 9, 12, 9, 11, 9, 14, 4, 3, 3, 12, 4, 10, 10 };
    static readonly int[] DimB = { 11, 3, 2, 5, 3, 9, 7, 4, 4, 3, 3, 5, 4, 3, 7 };

    // теми кімнат: вітальня, спальня, кухня, кабінет, майстерня
    static readonly int[][] Themes =
    {
        new[] { 13, 3, 4, 2, 14, 9, 9, 10 },
        new[] { 0, 1, 6, 2, 9, 10 },
        new[] { 7, 8, 5, 9, 10, 12 },
        new[] { 2, 2, 2, 6, 3, 9, 10 },
        new[] { 11, 12, 12, 12, 1, 2, 10 },
    };
    static readonly int[] GroundThemes = { 0, 2, 3, 4, 0, 2 };
    static readonly int[] UpperThemes = { 1, 1, 3, 0 };

    // дрібниці двору: 0 живопліт, 1 клумба, 2 лавка, 3 будка, 4 мангал, 5 машина, 6 басейн
    static readonly int[] PropA = { 10, 8, 8, 5, 3, 16, 14 };
    static readonly int[] PropB = { 3, 6, 3, 5, 3, 7, 9 };

    // ---------- старт ----------

    void Start()
    {
        if (world == null) world = FindFirstObjectByType<VoxelWorld>();
        if (world == null)
        {
            Debug.LogWarning("[Tier2CityGenerator] Не знайдено VoxelWorld", this);
            enabled = false;
            return;
        }

        vs = world.voxelSize;
        blockV = Mathf.Max(24, V(blockSize));
        roadV = Mathf.Clamp(V(roadWidth), 6, blockV / 2);
        lotsPerSide = Mathf.Max(1, lotsPerSide);
        lotV = (blockV - roadV) / lotsPerSide;
        floorV = Mathf.Max(10, V(floorHeight));
        wallV = Mathf.Max(1, V(wallThickness));
        fenceV = Mathf.Max(4, V(fenceHeight));
        gateV = Mathf.Max(4, V(gateWidth));

        if (lotV < 24)
            Debug.LogWarning("[Tier2CityGenerator] Ділянка замала для будинків: збільш blockSize або зменш lotsPerSide", this);
        else if (lotV < 40)
            Debug.LogWarning("[Tier2CityGenerator] Ділянка менша за 40 вокселів: особливі ділянки (фонтан, ставок тощо) замінюються гаєм", this);

        InitColors();
        StartCoroutine(Worker());
    }

    int V(float meters)
    {
        return Mathf.Max(1, Mathf.RoundToInt(meters / world.voxelSize));
    }

    byte C(int r, int g, int b)
    {
        return world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));
    }

    void InitColors()
    {
        // стіни (пари відтінків для шахового малюнка)
        wallA = new[] { C(222, 210, 185), C(170, 195, 215), C(232, 214, 150), C(214, 150, 130), C(190, 190, 185), C(170, 80, 62) };
        wallB = new[] { C(210, 198, 172), C(158, 184, 205), C(220, 202, 140), C(202, 140, 120), C(178, 178, 174), C(155, 70, 55) };
        // дахи
        roofA = new[] { C(110, 70, 52), C(165, 60, 50), C(80, 86, 96), C(70, 110, 100) };
        roofB = new[] { C(98, 62, 46), C(150, 52, 44), C(70, 76, 86), C(62, 100, 90) };

        cPlinth = C(120, 118, 112);
        cTrim = C(240, 236, 226);
        cGlass = C(150, 195, 225);
        cFrame = C(245, 245, 240);
        cDoor = C(110, 70, 45);
        cInterior = C(150, 138, 120);
        cBrick = C(150, 70, 55);

        cAsphaltA = C(62, 64, 70);
        cAsphaltB = C(70, 72, 78);
        cCurb = C(170, 170, 165);
        cLine = C(230, 205, 85);

        cPost = C(95, 62, 40);
        cPlank = C(200, 170, 120);
        cRail = C(150, 115, 80);

        cBarkA = C(100, 70, 45);
        cBarkB = C(88, 60, 38);
        leafSets = new[]
        {
            new[] { C(58, 125, 52), C(72, 145, 58), C(48, 108, 46) },
            new[] { C(95, 160, 70), C(110, 175, 80), C(80, 140, 62) },
            new[] { C(205, 125, 45), C(190, 95, 40), C(220, 160, 60) },
        };
        pineSet = new[] { C(35, 95, 60), C(42, 110, 66), C(30, 82, 52) };

        // інтер'єр
        cFloorA = C(176, 132, 86);
        cFloorB = C(164, 122, 78);
        cInnerWall = C(236, 226, 204);
        cWoodDark = C(88, 58, 36);
        cWoodMid = C(130, 88, 56);
        cWhite = C(240, 240, 236);
        cBlack = C(34, 34, 38);
        cMetal = C(150, 155, 162);
        cGray = C(110, 112, 118);
        cScreen = C(70, 130, 190);
        cSheet = C(235, 232, 222);
        cPot = C(176, 98, 62);
        cLeafG = C(60, 140, 60);
        cLampShade = C(250, 226, 140);
        cCabinet = C(190, 150, 110);
        cCounter = C(90, 92, 98);
        cFire = C(240, 130, 40);
        cSeat = C(120, 80, 60);
        cPiano = C(30, 30, 36);
        bookColors = new[] { C(170, 60, 50), C(60, 90, 150), C(70, 130, 80), C(210, 170, 60), C(120, 70, 120), C(220, 214, 200) };
        blanketColors = new[] { C(190, 60, 60), C(70, 110, 180), C(80, 150, 100), C(210, 170, 70) };
        sofaA = new[] { C(90, 110, 150), C(150, 70, 60), C(80, 120, 90), C(120, 100, 80) };
        sofaB = new[] { C(110, 132, 172), C(172, 92, 80), C(100, 142, 112), C(142, 122, 100) };
        rugA = new[] { C(160, 50, 50), C(50, 90, 150), C(70, 120, 80), C(140, 100, 50) };
        rugB = new[] { C(220, 200, 160), C(220, 215, 200), C(225, 215, 170), C(230, 210, 160) };

        // двір, парк, вулиця
        cWater = C(60, 130, 200);
        cWaterLight = C(150, 205, 240);
        cWaterDeep = C(40, 100, 170);
        cStone = C(150, 150, 146);
        cStone2 = C(124, 124, 120);
        cSand = C(222, 200, 140);
        cSoil = C(86, 60, 40);
        cSoilDark = C(60, 40, 28);
        cPath = C(190, 176, 150);
        cPathB = C(176, 162, 136);
        cRedToy = C(210, 60, 50);
        cYellowToy = C(240, 200, 60);
        cBlueToy = C(60, 110, 210);
        cPaveA = C(196, 192, 184);
        cPaveB = C(178, 174, 166);
        cPlayfloor = C(98, 150, 104);
        cSlide = C(60, 170, 220);
        cGlassCar = C(130, 170, 200);
        cTire = C(30, 30, 32);
        cTail = C(190, 30, 30);
        cHeadLamp = C(250, 240, 180);
        cHay = C(214, 180, 80);
        cHayB = C(194, 160, 64);
        cStraw = C(224, 200, 110);
        cShirt = C(70, 100, 170);
        cHat = C(110, 76, 48);
        cReed = C(110, 150, 70);
        cLily = C(70, 160, 80);
        cTankA = C(180, 60, 50);
        cTankB = C(220, 215, 205);
        cConcrete = C(150, 150, 148);
        cRoofRed = C(165, 60, 50);
        cLampLight = C(255, 236, 160);
        cDirt = C(140, 116, 82);
        cOrange = C(230, 130, 40);
        cCabbage = C(140, 190, 100);
        cPumpkin = C(235, 125, 30);
        cSun = C(250, 200, 30);
        cCorn = C(240, 210, 80);
        carColors = new[] { C(200, 50, 45), C(50, 90, 180), C(235, 235, 230), C(40, 40, 46), C(230, 190, 50), C(60, 140, 90), C(150, 150, 156) };
        flowerColors = new[] { C(230, 70, 90), C(250, 210, 60), C(240, 240, 240), C(170, 90, 200), C(250, 140, 60) };
        awA = new[] { C(200, 50, 45), C(50, 100, 180), C(60, 150, 80), C(230, 140, 40) };
        awB = new[] { C(240, 238, 230), C(240, 238, 230), C(240, 238, 230), C(240, 238, 230) };
        goodsColors = new[] { C(200, 50, 40), C(240, 150, 40), C(110, 170, 60), C(240, 210, 60), C(130, 60, 120), C(170, 100, 50) };
    }

    // ---------- пошук кварталів навколо каменя ----------

    void Update()
    {
        scanTimer -= Time.deltaTime;
        if (scanTimer > 0f) return;
        scanTimer = Mathf.Max(0.05f, scanInterval);

        RockAbsorber found = FindRock();
        if (found == null) return;

        // камінь змінився (заглушка нової сцени → справжній камінь): стартова точка береться заново
        if (found != rock)
        {
            rock = found;
            haveStart = false;
        }

        ScanBlocks();
    }

    // Під час переходу між сценами одночасно існують заглушка нової сцени й камінь, що прийшов зі сцени 1
    // (він ще може стояти на старих координатах). Беремо заглушку: вона стоїть там, де камінь опиниться
    // після прибуття, і лише потім TierTransition її знищує. Коли камінь один, беремо його.
    RockAbsorber FindRock()
    {
        RockAbsorber[] all = FindObjectsByType<RockAbsorber>(FindObjectsSortMode.None);
        if (all.Length == 0) return null;
        if (all.Length == 1) return all[0];

        Scene active = SceneManager.GetActiveScene();
        for (int i = 0; i < all.Length; i++)
            if (all[i].gameObject.scene == active) return all[i];
        return all[0];
    }

    void ScanBlocks()
    {
        Vector3 local = world.transform.InverseTransformPoint(rock.WorldCenter);
        lastRockLocal = new Vector2(local.x, local.z);
        if (!haveStart)
        {
            startLocal = lastRockLocal;
            haveStart = true;
        }

        float bm = blockV * vs;   // сторона кварталу в метрах
        float radius = Mathf.Max(spawnRadius, rock.Radius * spawnRadiusInRockRadii);
        int cx = Mathf.FloorToInt(local.x / bm);
        int cz = Mathf.FloorToInt(local.z / bm);
        int n = Mathf.CeilToInt(radius / bm) + 1;

        for (int dz = -n; dz <= n; dz++)
        for (int dx = -n; dx <= n; dx++)
        {
            var cell = new Vector2Int(cx + dx, cz + dz);
            if (requested.Contains(cell)) continue;

            Vector2 center = new Vector2((cell.x + 0.5f) * bm, (cell.y + 0.5f) * bm);
            if (Vector2.Distance(center, lastRockLocal) > radius + bm * 0.5f) continue;

            requested.Add(cell);
            queue.Add(cell);
        }
    }

    // Будує по одному кварталу: спочатку набір вокселів, потім розставляє його по кадрах
    IEnumerator Worker()
    {
        // даємо VoxelWorld.SettleOnStart відпрацювати раніше за нас
        for (int i = 0; i < 4; i++) yield return null;

        while (true)
        {
            if (queue.Count == 0)
            {
                yield return null;
                continue;
            }

            // найближчий до каменя квартал
            float bm = blockV * vs;
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < queue.Count; i++)
            {
                Vector2 c = new Vector2((queue[i].x + 0.5f) * bm, (queue[i].y + 0.5f) * bm);
                float d = (c - lastRockLocal).sqrMagnitude;
                if (d < bestD) { bestD = d; best = i; }
            }
            Vector2Int block = queue[best];
            queue.RemoveAt(best);

            GenerateBlock(block.x, block.y);

            int idx = 0;
            while (idx < ops.Count)
            {
                int end = Mathf.Min(ops.Count, idx + Mathf.Max(100, voxelsPerFrame));
                for (; idx < end; idx++)
                {
                    VoxelOp op = ops[idx];
                    // нічого не затираємо: старі споруди й уже поставлені частини лишаються.
                    // Через це деталі в генераторі завжди ставляться ПЕРШИМИ, а заливка потім
                    if (world.GetVoxel(op.pos) == 0) world.SetVoxel(op.pos, op.id);
                }
                yield return null;
            }
            ops.Clear();
            yield return null;
        }
    }

    // ---------- квартал ----------

    void GenerateBlock(int bx, int bz)
    {
        ops.Clear();
        int ox = bx * blockV;
        int oz = bz * blockV;

        BuildRoads(bx, bz, ox, oz);
        BuildStreetLamps(bx, bz, ox, oz);

        for (int lj = 0; lj < lotsPerSide; lj++)
        for (int li = 0; li < lotsPerSide; li++)
            BuildLot(bx, bz, li, lj, ox + roadV + li * lotV, oz + roadV + lj * lotV);
    }

    // ---------- дороги ----------

    // Колір вокселя дороги в клітинці кварталу (0 = не дорога).
    // Вертикальна дорога займає lx < roadV, горизонтальна lz < roadV, їхній перетин це перехрестя.
    byte RoadColor(int lx, int lz, int gx, int gz)
    {
        bool ns = lx < roadV;
        bool ew = lz < roadV;
        if (!ns && !ew) return 0;

        bool asphaltB = Hash01(gx, 0, gz) < 0.2f;
        byte asphalt = asphaltB ? cAsphaltB : cAsphaltA;
        if (ns && ew) return asphalt;   // перехрестя без розмітки

        int mid = roadV / 2;
        if (ns)
        {
            if (lx == 0 || lx == roadV - 1) return cCurb;
            if ((lx == mid - 1 || lx == mid) && ((lz / 4) & 1) == 0) return cLine;
        }
        else
        {
            if (lz == 0 || lz == roadV - 1) return cCurb;
            if ((lz == mid - 1 || lz == mid) && ((lx / 4) & 1) == 0) return cLine;
        }
        return asphalt;
    }

    void BuildRoads(int bx, int bz, int ox, int oz)
    {
        if (roadMode == RoadMode.SolidVoxels)
        {
            for (int lz = 0; lz < blockV; lz++)
            for (int lx = 0; lx < blockV; lx++)
            {
                byte id = RoadColor(lx, lz, ox + lx, oz + lz);
                if (id != 0) Add(ox + lx, 0, oz + lz, id);
            }
            return;
        }

        // плоский воксельний килим: один квад на клітинку, колір з палітри VoxelWorld (як у чанків)
        mVerts.Clear(); mNormals.Clear(); mUvs.Clear(); mTris.Clear();
        for (int lz = 0; lz < blockV; lz++)
        for (int lx = 0; lx < blockV; lx++)
        {
            byte id = RoadColor(lx, lz, ox + lx, oz + lz);
            if (id == 0) continue;

            float x0 = (ox + lx) * vs, x1 = x0 + vs;
            float z0 = (oz + lz) * vs, z1 = z0 + vs;
            float y = roadHeight;
            Vector2 uv = new Vector2((id + 0.5f) / 256f, 0.5f);

            int s = mVerts.Count;
            mVerts.Add(new Vector3(x0, y, z0));
            mVerts.Add(new Vector3(x0, y, z1));
            mVerts.Add(new Vector3(x1, y, z1));
            mVerts.Add(new Vector3(x1, y, z0));
            for (int k = 0; k < 4; k++)
            {
                mNormals.Add(Vector3.up);
                mUvs.Add(uv);
            }
            mTris.Add(s); mTris.Add(s + 1); mTris.Add(s + 2);
            mTris.Add(s); mTris.Add(s + 2); mTris.Add(s + 3);
        }
        if (mVerts.Count == 0) return;

        var mesh = new Mesh { name = "RoadMesh", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.SetVertices(mVerts);
        mesh.SetNormals(mNormals);
        mesh.SetUVs(0, mUvs);
        mesh.SetTriangles(mTris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Road {bx}_{bz}");
        go.transform.SetParent(world.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = world.RuntimeMaterial;
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    // ---------- вуличні ліхтарі ----------

    void BuildStreetLamps(int bx, int bz, int ox, int oz)
    {
        for (int k = 0; k < lotsPerSide; k++)
        {
            // вздовж вертикальної дороги (вона ліворуч від кварталу), ліхтар тягнеться рукою над дорогою
            int zz = oz + roadV + k * lotV + 3;
            if (Hash01(bx, k + 11, bz) < streetLampChance && LampAllowed(ox + roadV, zz))
                Lamp(ox + roadV, zz, -1, 0);

            // вздовж горизонтальної дороги (вона під кварталом)
            int xx = ox + roadV + k * lotV + 3;
            if (Hash01(bx + 5, k + 23, bz) < streetLampChance && LampAllowed(xx, oz + roadV))
                Lamp(xx, oz + roadV, 0, -1);
        }
    }

    bool LampAllowed(int x, int z)
    {
        if (!haveStart) return true;
        return Vector2.Distance(new Vector2(x * vs, z * vs), startLocal) >= 3f;
    }

    // Ліхтар: стовп, рука в бік (dx, dz) і лампа, що висить на кінці руки
    void Lamp(int x, int z, int dx, int dz)
    {
        int h = Mathf.Max(14, V(4.5f));
        Box(x, 0, z, 1, h, 1, cMetal);
        Box(x - 1, 0, z - 1, 3, 2, 3, cConcrete);
        for (int i = 1; i <= 3; i++) Box(x + dx * i, h - 1, z + dz * i, 1, 1, 1, cMetal);
        Box(x + dx * 3, h - 3, z + dz * 3, 1, 2, 1, cLampLight);
    }

    // ---------- ділянка ----------

    void BuildLot(int bx, int bz, int li, int lj, int lx0, int lz0)
    {
        var rng = new System.Random(Hash(seed ^ 0x2F6E2B1, bx * 16 + li, bz * 16 + lj));
        double roll = rng.NextDouble();
        taken.Clear();

        // поруч зі стартом каменя лишаємо чисто
        Vector2 lotCenter = new Vector2((lx0 + lotV * 0.5f) * vs, (lz0 + lotV * 0.5f) * vs);
        if (haveStart && Vector2.Distance(lotCenter, startLocal) < startClearRadius) return;

        if (roll < houseChance)
        {
            BuildHouseLot(rng, li, lj, lx0, lz0);
        }
        else if (roll < houseChance + treeLotChance)
        {
            int count = RI(rng, Mathf.Max(1, treesPerGroveMin), Mathf.Max(treesPerGroveMin, treesPerGroveMax));
            ScatterTrees(rng, lx0, lz0, count, 2);
        }
        else if (roll < houseChance + treeLotChance + specialLotChance)
        {
            BuildSpecialLot(rng, lx0, lz0);
        }
        // інакше ділянка порожня
    }

    void BuildHouseLot(System.Random rng, int li, int lj, int lx0, int lz0)
    {
        bool fenced = rng.NextDouble() < fenceChance;
        int margin = fenced ? 3 : 2;

        // фасад дивиться на дорогу: -z для нижнього ряду, -x для лівого стовпчика
        int facing;
        if (lj == 0 && li != 0) facing = 0;
        else if (li == 0 && lj != 0) facing = 1;
        else facing = rng.Next(2);

        int floors = rng.NextDouble() < twoFloorChance ? 2 : 1;
        int w = RI(rng, V(houseWidthMin), Mathf.Max(V(houseWidthMin), V(houseWidthMax)));
        int d = RI(rng, V(houseDepthMin), Mathf.Max(V(houseDepthMin), V(houseDepthMax)));
        int maxSide = lotV - 2 * (margin + 1);
        w = Mathf.Min(w, maxSide);
        d = Mathf.Min(d, maxSide);
        if (w < 12 || d < 12) return;   // ділянка замала для будинку

        int setbackMin = margin + 1;
        int setbackMax = lotV - margin - 1 - d;
        int setback = RI(rng, setbackMin, Mathf.Max(setbackMin, setbackMax));
        int side = (lotV - w) / 2;   // будинок по центру вздовж фасаду, щоб двері були навпроти хвіртки

        int ox, oz;
        if (facing == 0) { ox = lx0 + side; oz = lz0 + setback; }
        else { ox = lx0 + setback; oz = lz0 + side; }

        RectInt footprint = BuildHouse(rng, ox, oz, facing, w, d, floors);
        taken.Add(footprint);

        if (fenced) BuildFence(lx0, lz0, facing);

        int yardTrees = rng.Next(0, Mathf.Max(0, treesPerYardMax) + 1);
        if (yardTrees > 0)
            ScatterTrees(rng, lx0, lz0, yardTrees, margin);

        int props = rng.Next(0, Mathf.Max(0, propsPerYardMax) + 1);
        if (props > 0)
            ScatterProps(rng, lx0, lz0, margin, props);

        // доріжка від хвіртки до дверей (ставиться після дрібниць: вони її не затруть)
        if (facing == 0)
        {
            int len = oz - (lz0 + 1);
            if (len > 0) Tiles(ox + w / 2 - 3, lz0 + 1, 6, len, 0, 2, cPath, cPathB);
        }
        else
        {
            int len = ox - (lx0 + 1);
            if (len > 0) Tiles(lx0 + 1, oz + w / 2 - 3, len, 6, 0, 2, cPath, cPathB);
        }

        if (rng.NextDouble() < mailboxChance) Mailbox(rng, lx0, lz0, facing);
    }

    void Mailbox(System.Random rng, int lx0, int lz0, int facing)
    {
        byte c = rng.Next(2) == 0 ? cBlueToy : cRedToy;
        int off = lotV / 2 + gateV / 2 + 2;
        if (facing == 0)
        {
            Box(lx0 + off - 1, 5, lz0, 3, 2, 2, c);
            Box(lx0 + off, 0, lz0, 1, 5, 1, cPost);
        }
        else
        {
            Box(lx0, 5, lz0 + off - 1, 2, 2, 3, c);
            Box(lx0, 0, lz0 + off, 1, 5, 1, cPost);
        }
    }

    // ---------- будинок ----------

    // Будуємо в локальних координатах (lx вздовж фасаду, lz вглиб, двері на lz = 0),
    // а PlaceH повертає їх у світ залежно від того, куди дивиться фасад.
    void PlaceH(int facing, int ox, int oz, int lx, int ly, int lz, byte id)
    {
        if (facing == 0) Add(ox + lx, ly, oz + lz, id);
        else Add(ox + lz, ly, oz + lx, id);
    }

    bool IsInner(int lx, int lz, int w, int d, int t)
    {
        if (lx < t - 1 || lx > w - t || lz < t - 1 || lz > d - t) return false;
        return lx == t - 1 || lx == w - t || lz == t - 1 || lz == d - t;
    }

    bool InStairHole(int lx, int lz)
    {
        return stairsOk && lx >= wallV && lx <= wallV + 2 && lz >= stairZs && lz <= stairZs + floorV - 3;
    }

    RectInt BuildHouse(System.Random rng, int ox, int oz, int facing, int w, int d, int floors)
    {
        int fh = floorV;
        int H = floors * fh;
        int t = wallV;
        bool hollow = hollowHouses;

        stairsOk = hollow && floors > 1 && (d - 2 * t) >= fh + 1;
        stairZs = t + 1;

        int wp = rng.Next(wallA.Length);
        byte wA = wallA[wp], wB = wallB[wp];
        int rp = rng.Next(roofA.Length);
        byte rA = roofA[rp], rB = roofB[rp];

        int doorL = w / 2 - doorHalf - 1;
        int doorR = w / 2 + doorHalf + 1;

        // тіло: стіни, а всередині суцільний заповнювач (або порожнеча з підлогою й перекриттям, якщо hollowHouses)
        for (int ly = 0; ly < H; ly++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            bool edge = lx < t || lx >= w - t || lz < t || lz >= d - t;
            if (!edge && hollow)
            {
                bool slab = ly == 0 || (ly % fh == fh - 1 && ly < H - 1 && !InStairHole(lx, lz));
                if (!slab) continue;
            }

            byte id;
            if (!edge)
            {
                id = hollow ? ((((lx / 3) + (lz / 6)) & 1) == 0 ? cFloorA : cFloorB) : cInterior;
            }
            else
            {
                id = ((lx / 4 + ly / 4) & 1) == 0 ? wA : wB;
                if (ly < 2) id = cPlinth;
                else if (floors > 1 && ly == fh) id = cTrim;
                else if (hollow && t >= 2 && IsInner(lx, lz, w, d, t)) id = cInnerWall;

                // двері на фасаді
                if (lz < t && lx >= w / 2 - doorHalf && lx < w / 2 + doorHalf && ly < doorH)
                {
                    if (hollow)
                    {
                        // відкритий проріз з наличником
                        if (ly == 0) id = cPlinth;
                        else if (ly == doorH - 1 || lx == w / 2 - doorHalf || lx == w / 2 + doorHalf - 1) id = cTrim;
                        else continue;
                    }
                    else if (lz == 0)
                    {
                        id = (lx == w / 2 - doorHalf || lx == w / 2 + doorHalf - 1 || ly == doorH - 1) ? cTrim : cDoor;
                    }
                }
                else if (ly >= 2)
                {
                    // вікна проходять крізь усю товщину стіни
                    int u = -1, len = 0;
                    bool front = false;
                    if (lz < t) { u = lx; len = w; front = true; }
                    else if (lz >= d - t) { u = lx; len = w; }
                    else if (lx < t) { u = lz; len = d; }
                    else if (lx >= w - t) { u = lz; len = d; }

                    if (u >= 0)
                    {
                        byte wc = WindowColor(u, len, ly, front, doorL, doorR);
                        if (wc != 0) id = wc;
                    }
                }
            }
            PlaceH(facing, ox, oz, lx, ly, lz, id);
        }

        // дах
        bool flat = rng.NextDouble() < flatRoofChance;
        int roofLayers = 0;
        bool ridgeX = w >= d;
        int LA = ridgeX ? w : d;          // довжина гребеня
        int across = ridgeX ? d : w;      // ширина схилів

        if (flat)
        {
            for (int k = 0; k < 2; k++)
            for (int lz = -1; lz <= d; lz++)
            for (int lx = -1; lx <= w; lx++)
                PlaceH(facing, ox, oz, lx, H + k, lz, ((lx / 2 + lz / 2 + k) & 1) == 0 ? rA : rB);

            // бортик по краю
            for (int lz = -1; lz <= d; lz++)
            for (int lx = -1; lx <= w; lx++)
            {
                if (lx != -1 && lx != w && lz != -1 && lz != d) continue;
                PlaceH(facing, ox, oz, lx, H + 2, lz, cPlinth);
            }
            roofLayers = 3;
        }
        else
        {
            // двосхилий дах: кожен шар вужчий на 1 воксель з кожного боку, свіс 1 воксель над стінами
            for (int k = 0; ; k++)
            {
                int c0 = k - 1, c1 = across - k;
                if (c0 > c1) break;
                roofLayers++;

                for (int a = 0; a < LA; a++)
                for (int c = c0; c <= c1; c++)
                {
                    bool gable = a < t || a >= LA - t;   // торці дахів фарбуємо кольором стін
                    byte id = gable
                        ? (((a / 4 + (H + k) / 4) & 1) == 0 ? wA : wB)
                        : (((a / 2 + k) & 1) == 0 ? rA : rB);
                    if (ridgeX) PlaceH(facing, ox, oz, a, H + k, c, id);
                    else PlaceH(facing, ox, oz, c, H + k, a, id);
                }
            }

            // димар на схилі
            if (rng.NextDouble() < chimneyChance)
            {
                int ca = LA / 4;
                int cc = across / 4 + 1;
                int top = H + roofLayers + 3;
                for (int ly = H; ly < top; ly++)
                for (int da = 0; da < 2; da++)
                for (int dc = 0; dc < 2; dc++)
                {
                    int a = ca + da, c = cc + dc;
                    if (ridgeX) PlaceH(facing, ox, oz, a, ly, c, cBrick);
                    else PlaceH(facing, ox, oz, c, ly, a, cBrick);
                }
            }
        }

        // меблі, колони й сходи
        if (hollow) BuildInterior(rng, facing, ox, oz, w, d, floors, H);

        // зайнята площа разом зі свісом (у світових координатах), щоб дерева не росли крізь будинок
        if (facing == 0) return new RectInt(ox - 1, oz - 1, w + 2, d + 2);
        return new RectInt(ox - 1, oz - 1, d + 2, w + 2);
    }

    // Колір вікна у клітинці зовнішньої стіни (0 = не вікно).
    // Вікна розставлено симетрично вздовж стіни, 5 вокселів завширшки, крок 10.
    byte WindowColor(int u, int len, int ly, bool front, int doorL, int doorR)
    {
        int fh = floorV;
        int vy = ly - (ly / fh) * fh;
        if (vy < 4 || vy >= 9) return 0;

        if (len < 13) return 0;
        int n = (len - 13) / 10 + 1;      // скільки вікон вміщається, лишаючи по 4 вокселі від кутів
        int span = n * 10 - 5;
        int start0 = (len - span) / 2;

        int rel = u - start0;
        if (rel < 0 || rel >= span) return 0;
        int wu = rel % 10;
        if (wu >= 5) return 0;

        // вікно, що торкається дверей, пропускаємо повністю
        int k = rel / 10;
        int ws = start0 + k * 10;
        if (front && ws < doorR && ws + 5 > doorL) return 0;

        int wv = vy - 4;
        bool frame = wu == 0 || wu == 4 || wv == 0 || wv == 4;
        return frame ? cFrame : cGlass;
    }

    // ---------- інтер'єр ----------

    void BuildInterior(System.Random rng, int facing, int ox, int oz, int w, int d, int floors, int H)
    {
        int fh = floorV, t = wallV;
        hFacing = facing; hOx = ox; hOz = oz;

        ix0 = t; ix1 = w - t - 1;
        iz0 = t; iz1 = d - t - 1;
        int iw = ix1 - ix0 + 1, idp = iz1 - iz0 + 1;
        if (iw < 6 || idp < 6) return;

        int doorX0 = w / 2 - doorHalf, doorX1 = w / 2 + doorHalf - 1;

        // колони: підпирають дах і перекриття, щоб вони не падали
        pil.Clear();
        int pitch = Mathf.Max(6, pillarSpacing);
        int nx = Mathf.Max(1, Mathf.RoundToInt(iw / (float)pitch));
        int nz = Mathf.Max(1, Mathf.RoundToInt(idp / (float)pitch));
        for (int a = 0; a < nx; a++)
        for (int c = 0; c < nz; c++)
        {
            int px = ix0 + (int)((a + 0.5f) * iw / nx) - 1;
            int pz = iz0 + (int)((c + 0.5f) * idp / nz) - 1;
            // не перекриваємо прохід від дверей
            if (pz < iz0 + 10 && px + 1 >= doorX0 - 1 && px <= doorX1 + 1)
                px = px < w / 2 ? doorX0 - 3 : doorX1 + 2;
            // не лізьмо в сходи
            if (stairsOk && px <= ix0 + 3 && pz + 1 >= stairZs && pz <= stairZs + fh - 2)
                px = ix0 + 4;
            px = Mathf.Clamp(px, ix0, ix1 - 1);
            pz = Mathf.Clamp(pz, iz0, iz1 - 1);
            pil.Add(new Vector2Int(px, pz));
        }
        for (int i = 0; i < pil.Count; i++)
        for (int y = 1; y < H; y++)
        for (int dx = 0; dx < 2; dx++)
        for (int dz = 0; dz < 2; dz++)
            PlaceH(facing, ox, oz, pil[i].x + dx, y, pil[i].y + dz, cPost);

        // сходи вздовж лівої стіни
        if (stairsOk)
        {
            for (int k = 0; k <= fh - 2; k++)
            for (int lx = t; lx <= t + 2; lx++)
            for (int y = 1; y <= 1 + k; y++)
                PlaceH(facing, ox, oz, lx, y, stairZs + k, (k & 1) == 0 ? cPlank : cRail);
        }

        if (!furnishHouses) return;

        occ = new byte[w, d];

        for (int f = 0; f < floors; f++)
        {
            int floorY = f == 0 ? 1 : f * fh;
            int ceilY = f == floors - 1 ? H : (f + 1) * fh - 1;
            int height = ceilY - floorY;

            System.Array.Clear(occ, 0, occ.Length);

            for (int i = 0; i < pil.Count; i++)
            for (int dx = 0; dx < 2; dx++)
            for (int dz = 0; dz < 2; dz++)
                occ[pil[i].x + dx, pil[i].y + dz] = 1;

            if (stairsOk)
            {
                for (int lx = t; lx <= t + 2; lx++)
                for (int lz = stairZs; lz <= stairZs + fh - 2 && lz <= iz1; lz++)
                    occ[lx, lz] = 1;
            }

            // перед дверима лишаємо прохід
            if (f == 0)
            {
                for (int lx = Mathf.Max(ix0, doorX0 - 1); lx <= Mathf.Min(ix1, doorX1 + 1); lx++)
                for (int lz = iz0; lz <= Mathf.Min(iz1, iz0 + 5); lz++)
                    if (occ[lx, lz] == 0) occ[lx, lz] = 2;
            }

            int theme;
            if (floors == 1) theme = rng.Next(Themes.Length);
            else if (f == 0) theme = GroundThemes[rng.Next(GroundThemes.Length)];
            else theme = UpperThemes[rng.Next(UpperThemes.Length)];

            int[] list = Themes[theme];
            for (int i = 0; i < list.Length; i++)
            {
                int kind = list[i];
                bool optional = kind == 9 || kind == 10 || kind == 12 || kind == 14;
                if (optional && rng.NextDouble() < 0.3) continue;
                TryPlace(rng, kind, floorY, height, kind == 5);
            }

            // килим посередині (меблі його не затирають: вони вже стоять)
            if (iw >= 12 && idp >= 12 && rng.NextDouble() < 0.75)
            {
                int rw = rng.Next(8, Mathf.Min(14, iw - 2) + 1);
                int rh = rng.Next(8, Mathf.Min(14, idp - 2) + 1);
                int rx = ix0 + (iw - rw) / 2, rz = iz0 + (idp - rh) / 2;
                int rc = rng.Next(rugA.Length);
                for (int z = rz; z < rz + rh; z++)
                for (int x = rx; x < rx + rw; x++)
                {
                    if (occ[x, z] == 1) continue;
                    bool border = x <= rx + 1 || x >= rx + rw - 2 || z <= rz + 1 || z >= rz + rh - 2;
                    PlaceH(facing, ox, oz, x, floorY, z, border ? rugA[rc] : rugB[rc]);
                }
            }
        }
    }

    bool RectFree(int x0, int z0, int sx, int sz, int gap)
    {
        for (int z = z0 - gap; z < z0 + sz + gap; z++)
        for (int x = x0 - gap; x < x0 + sx + gap; x++)
        {
            bool core = x >= x0 && x < x0 + sx && z >= z0 && z < z0 + sz;
            bool outside = x < ix0 || x > ix1 || z < iz0 || z > iz1;
            if (outside)
            {
                if (core) return false;
                continue;
            }
            if (occ[x, z] != 0) return false;
        }
        return true;
    }

    bool TryPlace(System.Random rng, int kind, int floorY, int height, bool free)
    {
        int a = DimA[kind], b = DimB[kind];
        int iw = ix1 - ix0 + 1, idp = iz1 - iz0 + 1;

        for (int attempt = 0; attempt < 16; attempt++)
        {
            int s = rng.Next(4);
            int sx, sz;
            Foot(a, b, s, out sx, out sz);
            if (sx > iw || sz > idp) continue;

            int x0 = 0, z0 = 0;
            if (free)
            {
                x0 = ix0 + rng.Next(iw - sx + 1);
                z0 = iz0 + rng.Next(idp - sz + 1);
            }
            else
            {
                // спиною до стіни: s = 0 низька z, 1 висока z, 2 низька x, 3 висока x
                switch (s)
                {
                    case 0: z0 = iz0; x0 = ix0 + rng.Next(iw - sx + 1); break;
                    case 1: z0 = iz1 - sz + 1; x0 = ix0 + rng.Next(iw - sx + 1); break;
                    case 2: x0 = ix0; z0 = iz0 + rng.Next(idp - sz + 1); break;
                    default: x0 = ix1 - sx + 1; z0 = iz0 + rng.Next(idp - sz + 1); break;
                }
            }

            if (!RectFree(x0, z0, sx, sz, free ? 1 : 0)) continue;

            for (int z = z0; z < z0 + sz; z++)
            for (int x = x0; x < x0 + sx; x++)
                occ[x, z] = 1;

            BeginRoom(x0, z0, a, b, s, floorY, height);
            DrawFurniture(kind, rng);
            return true;
        }
        return false;
    }

    static void Foot(int a, int b, int s, out int sx, out int sz)
    {
        if (s < 2) { sx = a; sz = b; }
        else { sx = b; sz = a; }
    }

    // Предмет малюється в своїх координатах: u вздовж стіни (0..a-1), v від стіни вглиб (0..b-1), y угору.
    void BeginRoom(int x0, int z0, int a, int b, int s, int y0, int h)
    {
        tFacing = hFacing; tOx = hOx; tOz = hOz;
        tX0 = x0; tZ0 = z0; tA = a; tB = b; tS = s; tY0 = y0; tH = h;
    }

    void BeginAbs(int x0, int z0, int a, int b, int s)
    {
        tFacing = -1; tOx = 0; tOz = 0;
        tX0 = x0; tZ0 = z0; tA = a; tB = b; tS = s; tY0 = 0; tH = 64;
    }

    void FPut(int u, int v, int y, byte c)
    {
        if (y < 0 || y >= tH) return;
        int x, z;
        switch (tS)
        {
            case 0: x = tX0 + u; z = tZ0 + v; break;
            case 1: x = tX0 + u; z = tZ0 + (tB - 1 - v); break;
            case 2: x = tX0 + v; z = tZ0 + u; break;
            default: x = tX0 + (tB - 1 - v); z = tZ0 + u; break;
        }
        if (tFacing < 0) Add(x, tY0 + y, z, c);
        else PlaceH(tFacing, tOx, tOz, x, tY0 + y, z, c);
    }

    void FBox(int u0, int v0, int y0, int su, int sv, int sy, byte c)
    {
        for (int y = 0; y < sy; y++)
        for (int v = 0; v < sv; v++)
        for (int u = 0; u < su; u++)
            FPut(u0 + u, v0 + v, y0 + y, c);
    }

    // Деталі ставимо першими: вокселі, що вже стоять, не затираються.
    void DrawFurniture(int kind, System.Random rng)
    {
        switch (kind)
        {
            case 0: // ліжко
            {
                byte bl = blanketColors[rng.Next(blanketColors.Length)];
                FBox(0, 0, 2, 7, 1, 4, cWoodDark);       // узголів'я
                FBox(1, 1, 3, 2, 2, 1, cWhite);          // подушки
                FBox(4, 1, 3, 2, 2, 1, cWhite);
                FBox(0, 4, 3, 7, 7, 1, bl);              // ковдра
                FBox(0, 0, 2, 7, 11, 1, cSheet);         // матрац
                FBox(0, 0, 0, 7, 11, 2, cWoodDark);      // рама
                break;
            }
            case 1: // шафа
                FBox(3, 2, 4, 1, 1, 3, cMetal);
                FBox(5, 2, 4, 1, 1, 3, cMetal);
                FBox(4, 2, 0, 1, 1, 11, cWoodDark);
                FBox(0, 0, 0, 8, 3, 11, cWoodMid);
                break;
            case 2: // книжкова полиця
                for (int u = 1; u < 8; u++)
                for (int k = 0; k < 6; k++)
                {
                    int y = (k / 2) * 3 + 1 + (k & 1);
                    if (Hash01(tX0 + u, tY0 + y, tZ0 + tS) < 0.8f)
                        FPut(u, 1, y, bookColors[(int)(Hash01(u + tX0, y, tZ0 + 7) * bookColors.Length) % bookColors.Length]);
                }
                for (int y = 0; y < 10; y += 3) FBox(0, 0, y, 9, 2, 1, cWoodDark);
                FBox(0, 0, 0, 1, 2, 10, cWoodDark);
                FBox(8, 0, 0, 1, 2, 10, cWoodDark);
                FBox(0, 0, 0, 9, 1, 10, cWoodDark);
                break;
            case 3: // диван
            {
                int sc = rng.Next(sofaA.Length);
                FBox(1, 1, 2, 10, 4, 1, sofaB[sc]);      // подушки
                FBox(0, 0, 2, 12, 1, 3, sofaA[sc]);      // спинка
                FBox(0, 0, 2, 1, 5, 2, sofaA[sc]);       // бильця
                FBox(11, 0, 2, 1, 5, 2, sofaA[sc]);
                FBox(0, 0, 0, 12, 5, 2, sofaA[sc]);
                break;
            }
            case 4: // телевізор на тумбі
                FBox(2, 2, 4, 5, 1, 3, cScreen);
                FBox(1, 1, 3, 7, 2, 5, cBlack);
                FBox(0, 0, 0, 9, 3, 3, cWoodMid);
                break;
            case 5: // стіл зі стільцями
                FPut(5, 4, 4, flowerColors[rng.Next(flowerColors.Length)]);
                FBox(5, 4, 4, 1, 1, 1, cLeafG);
                FBox(2, 2, 3, 7, 5, 1, cPlank);
                FBox(2, 2, 0, 1, 1, 3, cWoodDark);
                FBox(8, 2, 0, 1, 1, 3, cWoodDark);
                FBox(2, 6, 0, 1, 1, 3, cWoodDark);
                FBox(8, 6, 0, 1, 1, 3, cWoodDark);
                FBox(0, 3, 0, 2, 2, 2, cSeat);
                FBox(9, 3, 0, 2, 2, 2, cSeat);
                FBox(4, 0, 0, 2, 2, 2, cSeat);
                FBox(4, 7, 0, 2, 2, 2, cSeat);
                break;
            case 6: // письмовий стіл із монітором і кріслом
                FBox(3, 2, 4, 3, 1, 3, cScreen);
                FBox(3, 1, 4, 3, 1, 3, cBlack);
                FBox(3, 3, 4, 3, 1, 1, cWhite);
                FBox(0, 0, 3, 9, 4, 1, cPlank);
                FBox(0, 0, 0, 1, 4, 3, cWoodDark);
                FBox(8, 0, 0, 1, 4, 3, cWoodDark);
                FBox(3, 6, 2, 3, 1, 3, cSeat);
                FBox(3, 4, 1, 3, 3, 1, cSeat);
                FBox(4, 5, 0, 1, 1, 1, cMetal);
                break;
            case 7: // кухонний блок
                for (int u = 2; u < 14; u += 4) FPut(u, 3, 2, cMetal);
                FBox(3, 1, 4, 4, 2, 1, cMetal);          // мийка
                FBox(5, 0, 5, 1, 1, 2, cMetal);          // кран
                FBox(9, 1, 4, 2, 2, 1, cBlack);          // конфорки
                FBox(11, 1, 4, 2, 2, 1, cBlack);
                FBox(0, 0, 4, 14, 4, 1, cCounter);
                FBox(0, 0, 0, 14, 4, 4, cCabinet);
                break;
            case 8: // холодильник
                FBox(0, 3, 6, 1, 1, 3, cMetal);
                FBox(0, 3, 5, 4, 1, 1, cGray);
                FBox(0, 0, 0, 4, 4, 11, cWhite);
                break;
            case 9: // рослина в горщику
                FBox(0, 0, 4, 3, 3, 2, cLeafG);
                FBox(1, 1, 2, 1, 1, 2, cWoodDark);
                FBox(0, 0, 0, 3, 3, 2, cPot);
                break;
            case 10: // торшер
                FBox(0, 0, 8, 3, 3, 2, cLampShade);
                FBox(1, 1, 1, 1, 1, 7, cMetal);
                FBox(0, 0, 0, 3, 3, 1, cMetal);
                break;
            case 11: // верстак з інструментами
                FBox(2, 1, 6, 1, 1, 3, cMetal);
                FBox(5, 1, 6, 1, 1, 2, cBrick);
                FBox(8, 1, 7, 1, 1, 2, cLine);
                FBox(1, 0, 5, 10, 1, 5, cPlank);         // дошка з інструментами
                FBox(9, 3, 4, 2, 2, 2, cMetal);          // лещата
                FBox(0, 0, 3, 12, 5, 1, cPlank);
                FBox(0, 0, 0, 1, 5, 3, cWoodDark);
                FBox(11, 0, 0, 1, 5, 3, cWoodDark);
                break;
            case 12: // ящики
                FBox(0, 0, 0, 1, 1, 4, cWoodDark);
                FBox(3, 0, 0, 1, 1, 4, cWoodDark);
                FBox(0, 3, 0, 1, 1, 4, cWoodDark);
                FBox(3, 3, 0, 1, 1, 4, cWoodDark);
                if (rng.Next(2) == 0) FBox(1, 1, 4, 3, 3, 3, cPlank);
                FBox(0, 0, 0, 4, 4, 4, cPlank);
                break;
            case 13: // камін
                FBox(4, 1, 0, 2, 1, 2, cFire);
                FBox(3, 1, 0, 4, 2, 3, cBlack);
                FBox(0, 0, 6, 10, 3, 1, cWoodDark);
                FBox(3, 0, 7, 4, 2, 8, cBrick);
                FBox(0, 0, 0, 10, 3, 6, cBrick);
                break;
            case 14: // піаніно зі стільцем
                FBox(1, 4, 4, 8, 1, 1, cWhite);
                FBox(0, 4, 3, 10, 1, 1, cPiano);
                FBox(4, 5, 0, 3, 2, 2, cSeat);
                FBox(0, 0, 0, 10, 4, 6, cPiano);
                break;
        }
    }

    // ---------- дрібниці двору ----------

    bool Overlaps(int x0, int z0, int sx, int sz, int gap)
    {
        for (int i = 0; i < taken.Count; i++)
        {
            RectInt r = taken[i];
            if (x0 - gap < r.xMax && x0 + sx + gap > r.xMin && z0 - gap < r.yMax && z0 + sz + gap > r.yMin)
                return true;
        }
        return false;
    }

    void ScatterProps(System.Random rng, int lx0, int lz0, int margin, int count)
    {
        for (int n = 0; n < count; n++)
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int kind = rng.Next(PropA.Length);
            int a = PropA[kind], b = PropB[kind];
            int s = rng.Next(4);
            int sx, sz;
            Foot(a, b, s, out sx, out sz);

            int minX = lx0 + margin + 1, maxX = lx0 + lotV - margin - 1 - sx;
            int minZ = lz0 + margin + 1, maxZ = lz0 + lotV - margin - 1 - sz;
            if (maxX < minX || maxZ < minZ) continue;

            int x0 = rng.Next(minX, maxX + 1);
            int z0 = rng.Next(minZ, maxZ + 1);
            if (Overlaps(x0, z0, sx, sz, 1)) continue;

            taken.Add(new RectInt(x0, z0, sx, sz));
            BeginAbs(x0, z0, a, b, s);
            DrawProp(kind, rng);
            break;
        }
    }

    void DrawProp(int kind, System.Random rng)
    {
        switch (kind)
        {
            case 0: // живопліт із квітами
            {
                byte lA = leafSets[0][0], lB = leafSets[0][1];
                for (int u = 0; u < 10; u++)
                    if (Hash01(tX0 + u, 3, tZ0) < 0.18f)
                        FPut(u, 2, 2, flowerColors[(int)(Hash01(u, tZ0, tX0) * flowerColors.Length) % flowerColors.Length]);
                FBox(1, 0, 3, 8, 3, 1, lB);
                FBox(0, 0, 0, 10, 3, 3, lA);
                break;
            }
            case 1:
                DrawFlowerBed();
                break;
            case 2:
                DrawBench();
                break;
            case 3: // собача будка
                FBox(1, 4, 0, 3, 1, 3, cBlack);
                FBox(1, 1, 5, 3, 3, 1, cRoofRed);
                FBox(0, 0, 4, 5, 5, 1, cRoofRed);
                FBox(0, 0, 0, 5, 5, 4, cWoodMid);
                break;
            case 4: // мангал
                FBox(1, 1, 3, 1, 1, 1, cFire);
                FBox(0, 0, 0, 1, 1, 3, cMetal);
                FBox(2, 0, 0, 1, 1, 3, cMetal);
                FBox(0, 2, 0, 1, 1, 3, cMetal);
                FBox(2, 2, 0, 1, 1, 3, cMetal);
                FBox(0, 0, 3, 3, 3, 2, cBlack);
                break;
            case 5:
                DrawCar(rng);
                break;
            case 6: // басейн
                FBox(5, 3, 1, 3, 3, 1, cWhite);          // надувне коло
                for (int u = 0; u < 14; u++)
                for (int v = 0; v < 9; v++)
                    if (u == 0 || u == 13 || v == 0 || v == 8)
                    {
                        FPut(u, v, 0, cStone);
                        FPut(u, v, 1, cWhite);
                    }
                FBox(1, 1, 0, 12, 7, 1, cWater);
                break;
        }
    }

    void DrawFlowerBed() // a=8, b=6
    {
        for (int u = 1; u < 7; u++)
        for (int v = 1; v < 5; v++)
        {
            float h = Hash01(tX0 + u, tY0, tZ0 + v);
            if (h >= 0.6f) continue;
            FPut(u, v, 1, cLeafG);
            FPut(u, v, 2, flowerColors[(int)(h / 0.6f * flowerColors.Length) % flowerColors.Length]);
        }
        FBox(1, 1, 0, 6, 4, 1, cSoil);
        for (int u = 0; u < 8; u++)
        for (int v = 0; v < 6; v++)
            if (u == 0 || u == 7 || v == 0 || v == 5)
            {
                FPut(u, v, 0, cStone);
                FPut(u, v, 1, cStone);
            }
    }

    void DrawBench() // a=8, b=3, спинка біля v = 0
    {
        FBox(0, 0, 3, 8, 1, 3, cPlank);
        FBox(0, 0, 2, 8, 3, 1, cPlank);
        FBox(0, 0, 0, 1, 3, 2, cMetal);
        FBox(7, 0, 0, 1, 3, 2, cMetal);
    }

    void BenchAt(int px, int pz, int s)
    {
        int sx, sz;
        Foot(8, 3, s, out sx, out sz);
        BeginAbs(px - sx / 2, pz - sz / 2, 8, 3, s);
        DrawBench();
    }

    void DrawCar(System.Random rng) // a=16 (довжина), b=7 (ширина), перед біля u = 15
    {
        byte col = carColors[rng.Next(carColors.Length)];

        FPut(15, 1, 2, cHeadLamp);
        FPut(15, 5, 2, cHeadLamp);
        FPut(0, 1, 2, cTail);
        FPut(0, 5, 2, cTail);
        FBox(15, 2, 1, 1, 3, 1, cBlack);                 // решітка
        FBox(2, 0, 0, 3, 1, 3, cTire);                   // колеса
        FBox(11, 0, 0, 3, 1, 3, cTire);
        FBox(2, 6, 0, 3, 1, 3, cTire);
        FBox(11, 6, 0, 3, 1, 3, cTire);

        for (int u = 4; u <= 11; u++)
        for (int v = 1; v <= 5; v++)
        for (int y = 4; y <= 6; y++)
        {
            bool edge = u == 4 || u == 11 || v == 1 || v == 5;
            FPut(u, v, y, (y == 6 || !edge) ? col : cGlassCar);
        }
        FBox(0, 0, 1, 16, 7, 3, col);
    }

    void PlaceCar(System.Random rng, int x, int z, int s)
    {
        BeginAbs(x, z, 16, 7, s);
        DrawCar(rng);
    }

    // ---------- особливі ділянки ----------

    void BuildSpecialLot(System.Random rng, int lx0, int lz0)
    {
        if (lotV < 40)
        {
            ScatterTrees(rng, lx0, lz0, RI(rng, 2, 4), 2);
            return;
        }

        switch (rng.Next(7))
        {
            case 0: BuildFountain(rng, lx0, lz0); break;
            case 1: BuildPlayground(rng, lx0, lz0); break;
            case 2: BuildPond(rng, lx0, lz0); break;
            case 3: BuildFarm(rng, lx0, lz0); break;
            case 4: BuildParking(rng, lx0, lz0); break;
            case 5: BuildWaterTower(rng, lx0, lz0); break;
            default: BuildMarket(rng, lx0, lz0); break;
        }
    }

    // Площа з фонтаном, лавками, ліхтарями й клумбами
    void BuildFountain(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;
        int cx = lx0 + L / 2, cz = lz0 + L / 2;

        Disc(cx, cz, 0.9f, 7, 13, cWaterLight);           // струмінь
        Disc(cx, cz, 1.9f, 12, 13, cWaterLight);
        Disc(cx, cz, 3f, 6, 7, cWater);                   // верхня чаша
        Ring(cx, cz, 4.3f, 3f, 6, 8, cStone);
        Disc(cx, cz, 4.3f, 5, 6, cStone);
        Disc(cx, cz, 2.2f, 0, 5, cStone);                 // ніжка
        Ring(cx, cz, 9.3f, 7.5f, 0, 3, cStone);           // басейн
        Disc(cx, cz, 7.5f, 0, 2, cWater);

        BenchAt(cx, cz + 13, 1);
        BenchAt(cx, cz - 13, 0);
        BenchAt(cx + 13, cz, 3);
        BenchAt(cx - 13, cz, 2);

        Lamp(cx - 16, cz - 16, 1, 0);
        Lamp(cx + 16, cz + 16, -1, 0);

        BeginAbs(cx + 9, cz + 9, 8, 6, 0);
        DrawFlowerBed();
        BeginAbs(cx - 17, cz - 15, 8, 6, 0);
        DrawFlowerBed();

        Tiles(lx0 + 1, lz0 + 1, L - 2, L - 2, 0, 4, cPaveA, cPaveB);
    }

    // Ігровий майданчик: пісочниця, гойдалки, гірка, балансир і карусель
    void BuildPlayground(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;

        // пісочниця
        int sx = lx0 + 3, sz = lz0 + 3;
        Box(sx + 3, 1, sz + 3, 2, 2, 2, cRedToy);         // відерце
        Box(sx + 1, 0, sz + 1, 10, 1, 10, cSand);
        Frame(sx, sz, 12, 12, 0, 3, cWoodDark);

        // гойдалки
        int gx = lx0 + 20, gz = lz0 + 8;
        for (int e = 0; e < 2; e++)
        {
            int x = gx + e * 15;
            Box(x, 0, gz - 2, 1, 12, 1, cMetal);
            Box(x, 0, gz + 2, 1, 12, 1, cMetal);
            Box(x, 11, gz - 2, 1, 1, 5, cMetal);
        }
        Box(gx, 11, gz, 16, 1, 1, cMetal);
        for (int k = 0; k < 2; k++)
        {
            int x = gx + 4 + k * 7;
            Box(x - 1, 3, gz, 1, 8, 1, cMetal);
            Box(x + 1, 3, gz, 1, 8, 1, cMetal);
            Box(x - 1, 2, gz - 1, 3, 1, 3, k == 0 ? cRedToy : cBlueToy);
        }

        // гірка: вежа, драбина й схил
        int px = lx0 + 4, pz = lz0 + 22;
        Box(px, 0, pz, 1, 8, 1, cMetal);
        Box(px + 3, 0, pz, 1, 8, 1, cMetal);
        Box(px, 0, pz + 3, 1, 8, 1, cMetal);
        Box(px + 3, 0, pz + 3, 1, 8, 1, cMetal);
        Box(px, 8, pz, 4, 1, 4, cYellowToy);
        Frame(px, pz, 4, 4, 9, 11, cMetal);
        Box(px - 1, 0, pz, 1, 8, 1, cMetal);
        Box(px - 1, 0, pz + 3, 1, 8, 1, cMetal);
        for (int k = 1; k < 8; k += 2) Box(px - 1, k, pz, 1, 1, 4, cMetal);
        for (int i = 0; i < 10; i++)
        {
            int x = px + 4 + i;
            int y = 8 - (i * 8) / 10;
            Box(x, y, pz + 1, 1, 1, 2, cSlide);
            Box(x, y, pz, 1, 2, 1, cMetal);
            Box(x, y, pz + 3, 1, 2, 1, cMetal);
            if (i == 4 || i == 9) Box(x, 0, pz + 1, 1, y, 2, cMetal);
        }

        // балансир
        int ex = lx0 + 22, ez = lz0 + 26;
        Box(ex + 1, 4, ez, 1, 2, 1, cMetal);
        Box(ex + 10, 4, ez, 1, 2, 1, cMetal);
        Box(ex, 3, ez, 12, 1, 2, cYellowToy);
        Box(ex + 5, 0, ez, 2, 3, 2, cMetal);

        // карусель
        int mx = lx0 + 31, mz = lz0 + 33;
        Box(mx, 3, mz, 1, 3, 1, cMetal);
        Ring(mx, mz, 4f, 3.3f, 3, 4, cBlueToy);
        Disc(mx, mz, 4f, 2, 3, cRedToy);
        Disc(mx, mz, 1.4f, 0, 2, cMetal);

        Box(lx0 + 1, 0, lz0 + 1, L - 2, 1, L - 2, cPlayfloor);
    }

    // Ставок із каменистим берегом, пристанню, очеретом і лататтям
    void BuildPond(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;
        int cx = lx0 + L / 2, cz = lz0 + L / 2;
        float rb = Mathf.Min(L / 2 - 4, 14f);
        float noiseX = Hash01(lx0, 1, lz0) * 100f;
        float noiseZ = Hash01(lx0, 2, lz0) * 100f;

        // пристань
        int dockX = cx - (int)rb - 3;
        int dockLen = (int)rb + 1;
        for (int i = 0; i < dockLen; i += 4)
        {
            Box(dockX + i, 0, cz - 1, 1, 1, 1, cWoodDark);
            Box(dockX + i, 0, cz + 1, 1, 1, 1, cWoodDark);
        }
        Box(dockX + dockLen - 1, 2, cz - 1, 1, 3, 1, cWoodDark);
        Box(dockX + dockLen - 1, 2, cz + 1, 1, 3, 1, cWoodDark);
        Box(dockX, 1, cz - 1, dockLen, 1, 3, cPlank);

        for (int z = lz0 + 1; z < lz0 + L - 1; z++)
        for (int x = lx0 + 1; x < lx0 + L - 1; x++)
        {
            float dx = x - cx, dz = z - cz;
            float dist = Mathf.Sqrt(dx * dx + dz * dz);
            float edge = rb + (Mathf.PerlinNoise(x * 0.17f + noiseX, z * 0.17f + noiseZ) - 0.5f) * 8f;

            if (dist <= edge - 1.5f)
            {
                Add(x, 0, z, dist < edge - 5f ? cWaterDeep : cWater);
                if (dist < edge - 3f && Hash01(x, 7, z) < 0.025f) Add(x, 1, z, cLily);
            }
            else if (dist <= edge)
            {
                int h = 1 + (Hash01(x, 3, z) < 0.35f ? 1 : 0);
                byte c = Hash01(x, 5, z) < 0.5f ? cStone : cStone2;
                for (int y = 0; y < h; y++) Add(x, y, z, c);
                if (Hash01(x, 9, z) < 0.07f)
                    Box(x, h, z, 1, 3 + (int)(Hash01(x, 11, z) * 4f), 1, cReed);
            }
            else if (dist <= edge + 2f && Hash01(x, 13, z) < 0.25f)
            {
                Add(x, 0, z, cSand);
            }
        }

        // валуни по кутках
        Boulder(lx0 + 5, lz0 + 5, 3.5f);
        Boulder(lx0 + L - 7, lz0 + 6, 3f);
        Boulder(lx0 + L - 6, lz0 + L - 7, 3.8f);
    }

    void Boulder(int x, int z, float r)
    {
        Disc(x, z, r - 1.2f, 2, 3, cStone);
        Disc(x, z, r, 0, 2, cStone2);
    }

    // Город: грядки з різними культурами, колодязь, опудало й стоги
    void BuildFarm(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;

        for (int r = 0; r < 4; r++)
        {
            int bz = lz0 + 3 + r * 8;
            if (bz + 6 > lz0 + L - 1) break;
            CropBed(rng, lx0 + 3, bz, 24, rng.Next(5));
        }

        // колодязь
        int wx = lx0 + 36, wz = lz0 + 8;
        Box(wx, 7, wz, 1, 3, 1, cMetal);                  // мотузка
        Box(wx, 5, wz, 2, 2, 2, cWoodDark);               // відро
        Box(wx - 3, 4, wz, 1, 7, 1, cWoodDark);           // стійки
        Box(wx + 3, 4, wz, 1, 7, 1, cWoodDark);
        Box(wx - 3, 10, wz, 7, 1, 1, cWoodDark);          // балка
        Box(wx - 3, 12, wz - 1, 7, 1, 3, cRoofRed);       // дашок
        Box(wx - 4, 11, wz - 2, 9, 1, 5, cRoofRed);
        Disc(wx, wz, 2.4f, 0, 2, cWater);
        Ring(wx, wz, 3.6f, 2.4f, 0, 4, cStone);

        // опудало
        int sx = lx0 + 36, sz = lz0 + 24;
        Box(sx - 1, 13, sz - 1, 3, 2, 3, cHat);
        Box(sx - 2, 12, sz - 2, 5, 1, 5, cHat);
        Box(sx - 1, 9, sz - 1, 3, 3, 3, cStraw);
        Box(sx - 3, 7, sz, 7, 1, 1, cWoodDark);
        Box(sx - 1, 5, sz - 1, 3, 4, 3, cShirt);
        Box(sx, 0, sz, 1, 10, 1, cWoodDark);

        // стоги
        for (int y = 0; y < 4; y++)
        {
            byte c = (y & 1) == 0 ? cHay : cHayB;
            Box(lx0 + 34, y, lz0 + 32, 6, 1, 4, c);
            Box(lx0 + 35, 4 + y, lz0 + 32, 4, 1, 4, c);
        }

        Box(lx0 + 1, 0, lz0 + 1, L - 2, 1, L - 2, cDirt);
    }

    void CropBed(System.Random rng, int bx, int bz, int len, int crop)
    {
        int step = (crop == 1 || crop == 3 || crop == 4) ? 3 : 2;
        for (int i = 1; i < len - 1; i += step)
        {
            int x = bx + i;
            for (int r = 0; r < 2; r++)
            {
                int z = bz + 1 + r * 2;
                if (crop == 0)
                {
                    Box(x, 2, z, 1, 1, 1, cLeafG);
                    Box(x, 1, z, 1, 1, 1, cOrange);
                }
                else if (crop == 1)
                {
                    Box(x, 1, bz + r * 2, 2, 2, 2, cCabbage);
                }
                else if (crop == 2)
                {
                    Box(x, 4, z, 1, 2, 1, cCorn);
                    Box(x, 1, z, 1, 6, 1, cLeafG);
                    Box(x, 7, z, 1, 1, 1, cStraw);
                }
                else if (crop == 3)
                {
                    Box(x, 1, bz + r * 2, 2, 2, 2, cPumpkin);
                }
                else
                {
                    Box(x, 6, z, 1, 1, 1, cSoilDark);
                    Box(x, 1, z, 1, 5, 1, cLeafG);
                    Box(x - 1, 6, z - 1, 3, 1, 3, cSun);
                }
            }
        }
        Box(bx, 0, bz, len, 1, 5, cSoil);
        Frame(bx - 1, bz - 1, len + 2, 7, 0, 2, cWoodDark);
    }

    // Парковка з розміткою, машинами й ліхтарями
    void BuildParking(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;
        int spaces = Mathf.Max(2, (L - 8) / 9);
        int startX = lx0 + (L - spaces * 9) / 2;
        int zA = lz0 + 3;
        int zB = lz0 + L - 3 - 16;

        Lamp(lx0 + 2, lz0 + L / 2, 1, 0);
        Lamp(lx0 + L - 3, lz0 + L / 2, -1, 0);

        for (int i = 0; i < spaces; i++)
        {
            int x = startX + i * 9 + 1;
            if (rng.NextDouble() < 0.7) PlaceCar(rng, x, zA, 2);
            if (rng.NextDouble() < 0.7) PlaceCar(rng, x, zB, 2);
        }
        for (int i = 0; i <= spaces; i++)
        {
            Box(startX + i * 9, 0, zA, 1, 1, 18, cWhite);
            Box(startX + i * 9, 0, zB - 2, 1, 1, 18, cWhite);
        }
        Tiles(lx0 + 1, lz0 + 1, L - 2, L - 2, 0, 6, cAsphaltA, cAsphaltB);
    }

    // Водонапірна вежа з баком, драбиною й розкосами
    void BuildWaterTower(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;
        int cx = lx0 + L / 2, cz = lz0 + L / 2;
        int legH = 24;

        // драбина
        Box(cx + 7, 0, cz - 1, 1, legH + 2, 1, cMetal);
        Box(cx + 7, 0, cz + 1, 1, legH + 2, 1, cMetal);
        for (int y = 0; y < legH + 2; y += 2) Box(cx + 7, y, cz - 1, 1, 1, 3, cMetal);

        // бак
        Box(cx, legH + 22, cz, 1, 1, 1, cRedToy);         // сигнальний вогник
        Box(cx, legH + 19, cz, 1, 3, 1, cMetal);          // антена
        for (int k = 0; k <= 6; k++)
            Disc(cx, cz, 7.4f - k * 1.1f, legH + 12 + k, legH + 13 + k, cTankB);
        for (int y = legH + 1; y < legH + 12; y++)
            Ring(cx, cz, 7.4f, 6.2f, y, y + 1, (((y - legH) / 4) & 1) == 0 ? cTankA : cTankB);
        Ring(cx, cz, 8.6f, 7.4f, legH, legH + 1, cMetal);
        Disc(cx, cz, 7.4f, legH, legH + 1, cTankB);

        // ноги, пояси й розкоси
        Box(cx - 6, 0, cz - 6, 2, legH, 2, cMetal);
        Box(cx + 5, 0, cz - 6, 2, legH, 2, cMetal);
        Box(cx - 6, 0, cz + 5, 2, legH, 2, cMetal);
        Box(cx + 5, 0, cz + 5, 2, legH, 2, cMetal);
        Frame(cx - 6, cz - 6, 13, 13, 8, 9, cMetal);
        Frame(cx - 6, cz - 6, 13, 13, 16, 17, cMetal);
        for (int i = 0; i <= 8; i++)
        {
            int y = 8 + i;
            int x1 = cx - 4 + i, x2 = cx + 4 - i;
            Box(x1, y, cz - 6, 1, 1, 1, cMetal);
            Box(x2, y, cz - 6, 1, 1, 1, cMetal);
            Box(x1, y, cz + 6, 1, 1, 1, cMetal);
            Box(x2, y, cz + 6, 1, 1, 1, cMetal);
            int z1 = cz - 4 + i, z2 = cz + 4 - i;
            Box(cx - 6, y, z1, 1, 1, 1, cMetal);
            Box(cx - 6, y, z2, 1, 1, 1, cMetal);
            Box(cx + 6, y, z1, 1, 1, 1, cMetal);
            Box(cx + 6, y, z2, 1, 1, 1, cMetal);
        }

        Box(cx - 9, 0, cz - 9, 19, 1, 19, cConcrete);
    }

    // Ярмарок: чотири прилавки з тентами й товаром та ящики посередині
    void BuildMarket(System.Random rng, int lx0, int lz0)
    {
        int L = lotV;
        int xL = lx0 + 3, xR = lx0 + L - 3 - 14;
        int zA = lz0 + 3, zB = lz0 + L - 3 - 10;

        if (rng.NextDouble() < 0.85) { BeginAbs(xL, zA, 14, 10, 0); DrawStall(rng); }
        if (rng.NextDouble() < 0.85) { BeginAbs(xR, zA, 14, 10, 0); DrawStall(rng); }
        if (rng.NextDouble() < 0.85) { BeginAbs(xL, zB, 14, 10, 1); DrawStall(rng); }
        if (rng.NextDouble() < 0.85) { BeginAbs(xR, zB, 14, 10, 1); DrawStall(rng); }

        BeginAbs(lx0 + L / 2 - 8, lz0 + L / 2 - 2, 4, 4, 0);
        DrawFurniture(12, rng);
        BeginAbs(lx0 + L / 2 + 5, lz0 + L / 2 - 2, 4, 4, 0);
        DrawFurniture(12, rng);
        Lamp(lx0 + L / 2, lz0 + L / 2, 0, 1);

        Tiles(lx0 + 1, lz0 + 1, L - 2, L - 2, 0, 4, cPaveA, cPaveB);
    }

    void DrawStall(System.Random rng) // a=14, b=10, прилавок із фасадом біля v = 9
    {
        int k = rng.Next(awA.Length);
        byte aA = awA[k], aB = awB[k];

        // стійки
        FBox(0, 0, 0, 1, 1, 10, cWoodDark);
        FBox(13, 0, 0, 1, 1, 10, cWoodDark);
        FBox(0, 9, 0, 1, 1, 10, cWoodDark);
        FBox(13, 9, 0, 1, 1, 10, cWoodDark);
        FBox(6, 0, 0, 1, 1, 10, cWoodDark);
        FBox(6, 9, 0, 1, 1, 10, cWoodDark);

        // товар на прилавку й на задній полиці
        for (int u = 1; u < 13; u += 3)
        {
            FBox(u, 7, 4, 2, 2, 2, goodsColors[rng.Next(goodsColors.Length)]);
            FBox(u, 0, 3, 2, 2, 2, goodsColors[rng.Next(goodsColors.Length)]);
        }
        FBox(1, 0, 0, 12, 2, 3, cWoodDark);
        FBox(0, 7, 0, 14, 2, 4, cPlank);

        // тканина ззаду, навіс і підзор
        FBox(0, 0, 5, 14, 1, 5, aB);
        for (int u = 0; u < 14; u++)
        {
            byte c = ((u / 2) & 1) == 0 ? aA : aB;
            FPut(u, 9, 9, c);
            FPut(u, 9, 8, c);
            for (int v = 0; v < 10; v++) FPut(u, v, 10, c);
        }
    }

    // ---------- паркан ----------

    void BuildFence(int lx0, int lz0, int facing)
    {
        int f = 1;                           // відступ від краю ділянки
        int x0 = lx0 + f, x1 = lx0 + lotV - 1 - f;
        int z0 = lz0 + f, z1 = lz0 + lotV - 1 - f;
        int len = x1 - x0 + 1;
        int mid = len / 2;

        for (int i = 0; i < len; i++)
        {
            bool gap = facing == 0 && Mathf.Abs(i - mid) < gateV / 2;   // хвіртка на фасаді (-z)
            FenceCell(x0 + i, z0, i, len, gap);
            FenceCell(x0 + i, z1, i, len, false);
        }
        for (int i = 1; i < len - 1; i++)
        {
            bool gap = facing == 1 && Mathf.Abs(i - mid) < gateV / 2;   // хвіртка на фасаді (-x)
            FenceCell(x0, z0 + i, i, len, gap);
            FenceCell(x1, z0 + i, i, len, false);
        }
    }

    void FenceCell(int x, int z, int i, int len, bool gap)
    {
        if (gap) return;

        bool post = (i % 3 == 0) || i == len - 1;
        if (post)
        {
            for (int y = 0; y <= fenceV; y++) Add(x, y, z, cPost);
            return;
        }

        // рейки йдуть суцільно між стовпами, дошки через одну (рейки ставимо першими, їх дошки не затруть)
        Add(x, 1, z, cRail);
        Add(x, fenceV - 2, z, cRail);
        if ((i & 1) == 0)
            for (int y = 0; y <= fenceV - 2; y++) Add(x, y, z, cPlank);
    }

    // ---------- дерева ----------

    // Дерева не перекривають усе, що вже є в taken (будинок, дрібниці). Самі додаються в taken.
    void ScatterTrees(System.Random rng, int lx0, int lz0, int count, int margin)
    {
        var placed = new List<Vector3Int>();   // (x, радіус, z)
        int baseCount = taken.Count;

        for (int n = 0; n < count; n++)
        for (int attempt = 0; attempt < 10; attempt++)
        {
            int r = V(RF(rng, crownRadiusMin, crownRadiusMax));
            int rMax = (lotV - 2 * margin - 4) / 2;
            r = Mathf.Min(r, rMax);
            if (r < 3) return;

            int minC = margin + r;
            int maxC = lotV - margin - r - 2;
            int px = lx0 + RI(rng, minC, maxC);
            int pz = lz0 + RI(rng, minC, maxC);

            bool ok = true;
            for (int i = 0; ok && i < baseCount; i++)
            {
                RectInt a = taken[i];
                if (px + r > a.xMin && px - r < a.xMax && pz + r > a.yMin && pz - r < a.yMax) ok = false;
            }
            for (int i = 0; ok && i < placed.Count; i++)
            {
                float dx = placed[i].x - px, dz = placed[i].z - pz;
                float min = (placed[i].y + r) * 0.8f;
                if (dx * dx + dz * dz < min * min) ok = false;
            }
            if (!ok) continue;

            BuildTree(rng, px, pz, r);
            placed.Add(new Vector3Int(px, r, pz));
            taken.Add(new RectInt(px - r, pz - r, 2 * r + 2, 2 * r + 2));
            break;
        }
    }

    void BuildTree(System.Random rng, int gx, int gz, int r)
    {
        int trunkH = V(RF(rng, trunkHeightMin, trunkHeightMax));
        bool pine = rng.NextDouble() < pineChance;

        // крона: листяна куля або ялина-конус
        int crownTop = pine ? trunkH + r + r / 2 : trunkH + r;
        int trunkTop = pine ? crownTop - 3 : trunkH;

        for (int y = 0; y < trunkTop; y++)
        for (int dx = 0; dx < 2; dx++)
        for (int dz = 0; dz < 2; dz++)
            Add(gx + dx, y, gz + dz, ((y / 3) & 1) == 0 ? cBarkA : cBarkB);

        float cx = gx + 1f, cz = gz + 1f;

        if (pine)
        {
            byte[] set = pineSet;
            int baseY = Mathf.Max(3, V(1f));
            for (int y = baseY; y <= crownTop; y++)
            {
                float t = (y - baseY) / (float)Mathf.Max(1, crownTop - baseY);
                float rad = r * (1f - t) + 0.4f;
                int ri = Mathf.CeilToInt(rad);
                for (int dz = -ri; dz <= ri + 1; dz++)
                for (int dx = -ri; dx <= ri + 1; dx++)
                {
                    float fx = gx + dx + 0.5f - cx, fz = gz + dz + 0.5f - cz;
                    if (fx * fx + fz * fz > rad * rad) continue;
                    int x = gx + dx, z = gz + dz;
                    Add(x, y, z, set[(int)(Hash01(x, y, z) * set.Length) % set.Length]);
                }
            }
            return;
        }

        byte[] leaves = leafSets[rng.Next(leafSets.Length)];
        float cy = trunkH + r * 0.3f;
        float ry = r * 0.85f;
        int y0 = Mathf.FloorToInt(cy - ry), y1 = Mathf.CeilToInt(cy + ry);

        for (int y = Mathf.Max(0, y0); y <= y1; y++)
        for (int dz = -r; dz <= r + 1; dz++)
        for (int dx = -r; dx <= r + 1; dx++)
        {
            int x = gx + dx, z = gz + dz;
            float nx = (x + 0.5f - cx) / r;
            float ny = (y + 0.5f - cy) / ry;
            float nz = (z + 0.5f - cz) / r;
            float dist = nx * nx + ny * ny + nz * nz;
            if (dist > 1f - Hash01(x, y, z) * 0.35f) continue;   // нерівний край крони

            // верх світліший, низ темніший
            int shade = ny > 0.35f ? 1 : (ny < -0.35f ? 2 : 0);
            if (Hash01(x + 7, y, z) < 0.25f) shade = (shade + 1) % 3;
            Add(x, y, z, leaves[shade]);
        }
    }

    // ---------- допоміжне ----------

    void Add(int x, int y, int z, byte id)
    {
        ops.Add(new VoxelOp { pos = new Vector3Int(x, y, z), id = id });
    }

    void Box(int x, int y, int z, int sx, int sy, int sz, byte id)
    {
        for (int yy = 0; yy < sy; yy++)
        for (int zz = 0; zz < sz; zz++)
        for (int xx = 0; xx < sx; xx++)
            Add(x + xx, y + yy, z + zz, id);
    }

    // Лише периметр прямокутника, шарами від y0 до y1 (не включно)
    void Frame(int x, int z, int sx, int sz, int y0, int y1, byte id)
    {
        for (int y = y0; y < y1; y++)
        for (int zz = 0; zz < sz; zz++)
        for (int xx = 0; xx < sx; xx++)
            if (xx == 0 || xx == sx - 1 || zz == 0 || zz == sz - 1)
                Add(x + xx, y, z + zz, id);
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

    // Плоский шаховий настил один воксель завтовшки
    void Tiles(int x, int z, int sx, int sz, int y, int tile, byte a, byte b)
    {
        for (int zz = 0; zz < sz; zz++)
        for (int xx = 0; xx < sx; xx++)
            Add(x + xx, y, z + zz, (((xx / tile) + (zz / tile)) & 1) == 0 ? a : b);
    }

    static int RI(System.Random rng, int min, int maxInclusive)
    {
        if (maxInclusive < min) return min;
        return rng.Next(min, maxInclusive + 1);
    }

    static float RF(System.Random rng, float a, float b)
    {
        return a + (float)rng.NextDouble() * (b - a);
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