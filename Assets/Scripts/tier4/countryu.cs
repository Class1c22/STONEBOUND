using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

// Генератор великого континенту для 4 рівня. Країна (або кілька) оточена морем. У кожній країні столиця
// із замком, міста, багато крихітних сіл, дороги між поселеннями, поля, сади, луки, великі ліси й озера.
// Навколо моря й озер пляжі. Камінь на старті ставиться на сушу поруч із столицею.
// Повісь на порожній GameObject, VoxelWorld і камінь знайдуться самі. Підлога має бути на y = 0
// (FloorGenerator), VoxelWorld на (0,0,0) з масштабом 1.
//
// Усі розміри тут у ВОКСЕЛЯХ, тому все масштабується разом із world.voxelSize.
// Щоб усе стало дрібнішим, зменш voxelSize у VoxelWorld і збільш mapRadius / countryRadius.
//
// ВАЖЛИВО: публічні поля серіалізуються в Inspector. Якщо компонент уже висів на об'єкті,
// у ньому лишаються СТАРІ числа. Натисни ⋮ біля компонента -> Reset, щоб підхопити нові значення за замовчуванням.
//
// Як це працює:
//  * Карта скінченна: квадрат від -Map Radius до +Map Radius вокселів. Береги звивисті (два шари шуму).
//  * Озера це ділянки шуму всередині суші, подалі від моря й столиці.
//  * Море, озера, пляж, дороги й вулиці плоскі. Вода дуже низька (Water Height), щоб камінь не втоплювався.
//    Плоский меш має тонкий колайдер (Flat Collider), тож камінь їде по поверхні води, а не під нею.
//  * За бажанням (Block Water) вздовж берега стоять невидимі стіни, і камінь не може в'їхати у воду.
//  * Плитки будуються лише поблизу каменя, найближчі першими, по кадрах (Voxels Per Frame).
//  * Камінь на старті переноситься на сушу (PlaceRockOnLand). Навколо нього нічого не ставиться, крім доріг.
//  * Те саме значення Seed завжди дає ту саму карту.
public class CountryGenerator : MonoBehaviour
{
    [Header("Зв'язки")]
    [Tooltip("Якщо порожньо, береться VoxelWorld зі сцени")]
    public VoxelWorld world;
    public int seed = 4004;

    [Header("Стрімінг")]
    [Tooltip("Як далеко від каменя будується карта (метри)")]
    public float spawnRadius = 150f;
    [Tooltip("Те саме, але в радіусах каменя. Береться більше з двох")]
    public float spawnRadiusInRockRadii = 80f;
    [Tooltip("Навколо стартової позиції каменя нічого не ставиться (в радіусах каменя). Дороги лишаються")]
    public float startClearInRockRadii = 3f;
    [Tooltip("Скільки вокселів ставити за кадр")]
    public int voxelsPerFrame = 6000;

    [Header("Карта (у вокселях)")]
    [Tooltip("Півсторона всієї карти. Має бути більшою за радіус країни з запасом на море навколо")]
    public int mapRadius = 1200;
    [Tooltip("Сторона плитки, яка будується за раз")]
    public int tileSize = 48;

    [Header("Країни")]
    public int countryCount = 1;
    [Tooltip("З якої країни стартує камінь")]
    public int startCountry = 0;
    public float countryRadius = 1000f;
    [Tooltip("Ширина протоки між країнами")]
    public float channelWidth = 50f;
    [Tooltip("Наскільки звивистий берег")]
    public float coastJitter = 80f;
    public float noiseScale = 0.006f;
    [Tooltip("Мінімальна відстань між центрами країн")]
    public float minSeedDistance = 250f;
    [Tooltip("Скільки вокселів від берега нічого не ставиться")]
    public int coastMargin = 5;
    public int beachWidth = 3;

    [Header("Озера")]
    public float lakeScale = 0.012f;
    [Tooltip("Більше = менше озер")]
    [Range(0.5f, 0.9f)] public float lakeThreshold = 0.66f;
    [Tooltip("Мінімум вокселів від берега моря до озера")]
    public int lakeCoastMargin = 90;
    [Tooltip("Навколо столиці озер немає")]
    public int lakeCenterClear = 70;

    [Header("Вода й стіни")]
    [Tooltip("Висота плоского моря, озер і пляжу над підлогою (метри). Менше = камінь менше втоплюється. Дуже мале значення може мерехтіти на відстані")]
    public float waterHeight = 0.004f;
    [Tooltip("Висота плоских доріг над підлогою (метри)")]
    public float roadHeight = 0.03f;
    [Tooltip("Тонкий колайдер на плоскому меші: камінь їде по поверхні води й доріг, а не по підлозі під нею")]
    public bool flatCollider = true;
    [Tooltip("Якщо увімкнено, вздовж берега моря й озер стоять невидимі стіни (камінь не їде у воду)")]
    public bool blockWater = false;
    [Tooltip("Товщина невидимої стіни біля берега. Більше = камінь точно не проскочить")]
    public int wallThickness = 16;
    [Tooltip("Розмір клітинки, з яких зливаються колайдери стіни. Менше = точніше, але більше колайдерів")]
    public int wallCell = 4;
    [Tooltip("Висота стін (метри)")]
    public float wallHeight = 300f;

    [Header("Міста (у вокселях)")]
    public int citiesPerCountry = 12;
    public int cityRadiusMin = 22;
    public int cityRadiusMax = 32;
    public int capitalRadius = 40;
    [Tooltip("Мінімальна відстань між центрами міст однієї країни")]
    public int citySpacing = 85;
    public int roadWidth = 2;
    [Tooltip("Як часто вулиці перетинаються поперек")]
    public int crossStreetSpacing = 26;

    [Header("Села")]
    public int villagesPerCountry = 40;
    public int villageRadiusMin = 9;
    public int villageRadiusMax = 14;
    public int villageSpacing = 45;

    [Header("Хатки (у вокселях, супер маленькі)")]
    public int houseWidthMin = 3;
    public int houseWidthMax = 4;
    public int houseDepthMin = 3;
    public int houseDepthMax = 3;
    public int houseWallHeight = 2;
    [Range(0f, 1f)] public float houseChance = 0.9f;
    [Range(0f, 1f)] public float thatchChance = 0.3f;
    [Range(0f, 1f)] public float chimneyChance = 0.5f;

    [Header("Околиці (у вокселях)")]
    [Tooltip("Розмір ділянки поля/лісу")]
    public int plotSize = 48;

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

    class Country
    {
        public int id;
        public Vector2Int center;
        public List<City> cities = new List<City>();
    }

    readonly List<VoxelOp> ops = new List<VoxelOp>();
    readonly List<Country> countries = new List<Country>();

    sbyte[] country;        // -1 = море або озеро, інакше номер країни
    bool[] isLake;          // true = озеро (теж вода)
    byte[] roadKind;        // 0 = нічого, 1 = вулиця, 2 = дорога між містами
    bool[] occ;             // зайнято будівлями
    ushort[] landDist;      // відстань до берега: для суші до води, для води до суші
    readonly Dictionary<Vector2Int, List<System.Action>> jobs = new Dictionary<Vector2Int, List<System.Action>>();
    readonly List<Vector2Int> pending = new List<Vector2Int>();

    float vs;
    int M, N, T, tilesPerSide;

    RockAbsorber rock;
    bool haveStart;
    Vector2 rockV, startV, spawnV;
    float startClearV;

    // поточне перетворення для малювання будівель: facing 0..3, початок, розміри
    int tF, tOx, tOz, tW, tD;

    // буфери плоского меша
    readonly List<Vector3> mVerts = new List<Vector3>();
    readonly List<Vector3> mNormals = new List<Vector3>();
    readonly List<Vector2> mUvs = new List<Vector2>();
    readonly List<int> mTris = new List<int>();

    // кольори (індекси палітри VoxelWorld)
    byte[] wallSet, thatchSet, shutterSet, gravelSet, dirtSet, sandSet, wheatSet, pineSet, flowerColors;
    byte[][] roofSets, leafSets;
    byte[] flagSet;
    byte cGlass, cDoor, cFloor, cBrick, cCap, cPost, cBarkA, cBarkB;
    byte cWater, cWaterLight, cSoil, cSoilDark, cStem, cSun, cSunCenter, cStraw, cStraw2;
    byte cStone, cStone2;

    // ---------- публічне для інших скриптів ----------

    // Місце старту каменя на суші (світові координати)
    public Vector3 StartPoint
    {
        get
        {
            if (world == null || vs <= 0f || countries.Count == 0) return transform.position;
            return world.transform.TransformPoint(new Vector3(spawnV.x * vs, 0f, spawnV.y * vs));
        }
    }
    public int TileVoxels => T;
    public int MapRadiusVoxels => M;

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

        vs = world.voxelSize;
        M = Mathf.Max(200, mapRadius);
        N = 2 * M;
        T = Mathf.Max(16, tileSize);
        tilesPerSide = Mathf.CeilToInt(N / (float)T);

        InitColors();
        Plan();
        if (blockWater) BuildWalls();
        StartCoroutine(Worker());
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

        // кожна країна має свій колір дахів і прапора
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

        cWater = C(60, 130, 200);
        cWaterLight = C(130, 190, 235);

        cSoil = C(100, 72, 48);
        cSoilDark = C(86, 62, 40);
        cStem = C(70, 130, 56);
        cSun = C(250, 200, 40);
        cSunCenter = C(110, 70, 30);
        cStraw = C(214, 178, 76);
        cStraw2 = C(196, 160, 60);
        wheatSet = new[] { C(214, 178, 70), C(200, 160, 56), C(224, 190, 84) };
        flowerColors = new[] { C(240, 240, 235), C(250, 210, 60), C(220, 70, 80), C(150, 110, 200), C(90, 150, 230) };

        cStone = C(150, 150, 146);
        cStone2 = C(124, 124, 120);
    }

    // ---------- допоміжне ----------

    int I(int x, int z) { return (x + M) + N * (z + M); }
    bool InMap(int x, int z) { return x >= -M && x < M && z >= -M && z < M; }

    bool Free(int x, int z)
    {
        if (!InMap(x, z)) return false;
        int i = I(x, z);
        if (country[i] < 0 || roadKind[i] != 0 || occ[i]) return false;
        if (landDist[i] < coastMargin) return false;

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
        if (country[i] < 0 || roadKind[i] != 0) return;
        roadKind[i] = (byte)kind;
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

    // ---------- план карти ----------

    void Plan()
    {
        var rng = new System.Random(Hash(seed, 4, 4));

        country = new sbyte[N * N];
        isLake = new bool[N * N];
        roadKind = new byte[N * N];
        occ = new bool[N * N];
        for (int i = 0; i < country.Length; i++) country[i] = -1;

        // 1. центри країн
        var seedPos = new List<Vector2>();
        float limit = Mathf.Max(0f, M - countryRadius - wallThickness - 10f);
        float minD = minSeedDistance;
        for (int tries = 0; seedPos.Count < countryCount && tries < 5000; tries++)
        {
            if (tries > 0 && tries % 600 == 0) minD *= 0.85f;
            Vector2 p = new Vector2(((float)rng.NextDouble() * 2f - 1f) * limit,
                                    ((float)rng.NextDouble() * 2f - 1f) * limit);
            bool ok = true;
            for (int s = 0; s < seedPos.Count; s++)
                if (Vector2.Distance(p, seedPos[s]) < minD) { ok = false; break; }
            if (ok) seedPos.Add(p);
        }
        int K = seedPos.Count;

        // 2. суша: найближчий центр, відрізана смуга-протока між сусідніми країнами, звивистий берег, озера
        float nox = (float)rng.NextDouble() * 1000f;
        float noz = (float)rng.NextDouble() * 1000f;
        float lox = (float)rng.NextDouble() * 1000f;
        float loz = (float)rng.NextDouble() * 1000f;
        for (int z = -M; z < M; z++)
        for (int x = -M; x < M; x++)
        {
            float d1 = float.MaxValue, d2 = float.MaxValue;
            int i1 = -1;
            for (int s = 0; s < K; s++)
            {
                float dx = x - seedPos[s].x, dz = z - seedPos[s].y;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < d1) { d2 = d1; d1 = d; i1 = s; }
                else if (d < d2) d2 = d;
            }

            // два шари шуму: великі затоки + дрібна звивистість берега
            float n1 = Mathf.PerlinNoise((x + M) * noiseScale + nox, (z + M) * noiseScale + noz) * 2f - 1f;
            float n2 = Mathf.PerlinNoise((x + M) * noiseScale * 3.1f + noz, (z + M) * noiseScale * 3.1f + nox) * 2f - 1f;
            float n = n1 * 0.7f + n2 * 0.3f;

            bool land = d1 + n * coastJitter < countryRadius
                     && (d2 - d1) + n * coastJitter * 0.5f > channelWidth;

            bool lake = false;
            if (land && d1 > lakeCenterClear && countryRadius - d1 > lakeCoastMargin)
            {
                float ln = Mathf.PerlinNoise((x + M) * lakeScale + lox, (z + M) * lakeScale + loz);
                if (ln > lakeThreshold) { land = false; lake = true; }
            }

            country[I(x, z)] = land ? (sbyte)i1 : (sbyte)-1;
            isLake[I(x, z)] = lake;
        }

        // 3. лишаємо тільки ту частину країни, що з'єднана з її центром
        for (int s = 0; s < K; s++)
        {
            var c = new Country
            {
                id = s,
                center = new Vector2Int(Mathf.Clamp(Mathf.RoundToInt(seedPos[s].x), -M, M - 1),
                                        Mathf.Clamp(Mathf.RoundToInt(seedPos[s].y), -M, M - 1))
            };
            countries.Add(c);
            KeepSeedComponent(c);
        }

        ComputeLandDist();
        ComputeSpawn();

        // 4. міста й села
        foreach (var ct in countries)
        {
            ct.cities.Add(new City { cx = ct.center.x, cz = ct.center.y, r = Mathf.Max(20, capitalRadius), capital = true });
            int townCount = Mathf.Max(1, citiesPerCountry);
            int total = townCount + Mathf.Max(0, villagesPerCountry);
            for (int tries = 0; tries < 8000 && ct.cities.Count < total; tries++)
            {
                bool village = ct.cities.Count >= townCount;
                float ang = (float)rng.NextDouble() * 6.2832f;
                float dist = Mathf.Lerp(0.1f, 0.92f, (float)rng.NextDouble()) * countryRadius;
                int px = ct.center.x + Mathf.RoundToInt(Mathf.Cos(ang) * dist);
                int pz = ct.center.y + Mathf.RoundToInt(Mathf.Sin(ang) * dist);
                int r = village ? RI(rng, villageRadiusMin, villageRadiusMax) : RI(rng, cityRadiusMin, cityRadiusMax);
                if (!InMap(px, pz)) continue;
                int i = I(px, pz);
                if (country[i] != ct.id || landDist[i] < r + coastMargin + 2) continue;

                int spacing = village ? villageSpacing : citySpacing;
                bool ok = true;
                foreach (var e in ct.cities)
                {
                    float dx = px - e.cx, dz = pz - e.cz;
                    float need = Mathf.Max(spacing, e.r + r + 8);
                    if (Mathf.Sqrt(dx * dx + dz * dz) < need) { ok = false; break; }
                }
                if (ok) ct.cities.Add(new City { cx = px, cz = pz, r = r, capital = false, village = village });
            }
        }

        // 5. дороги між поселеннями однієї країни
        foreach (var ct in countries)
        {
            for (int k = 1; k < ct.cities.Count; k++)
            {
                int best = 0;
                float bestD = float.MaxValue;
                for (int j = 0; j < k; j++)
                {
                    float dx = ct.cities[k].cx - ct.cities[j].cx, dz = ct.cities[k].cz - ct.cities[j].cz;
                    float d = dx * dx + dz * dz;
                    if (d < bestD) { bestD = d; best = j; }
                }
                CarveRoad(new Vector2(ct.cities[best].cx, ct.cities[best].cz),
                          new Vector2(ct.cities[k].cx, ct.cities[k].cz), rng);
            }
        }

        // 6. вулиці й хатки в містах і селах
        foreach (var ct in countries)
            foreach (var cy in ct.cities)
                PlanCity(ct, cy, rng);

        // 7. околиці: ліси, луки, ферми (за шумом біомів)
        float bx = (float)rng.NextDouble() * 1000f;
        float bz = (float)rng.NextDouble() * 1000f;
        int P = Mathf.Max(24, plotSize);
        for (int pz = -M; pz < M; pz += P)
        for (int px = -M; px < M; px += P)
        {
            RectInt plot = new RectInt(px, pz, P, P);
            int cx = px + P / 2, cz = pz + P / 2;
            if (!AnyLand(plot)) continue;

            bool core = false;
            foreach (var ct in countries)
            {
                foreach (var cy in ct.cities)
                {
                    float dx = cx - cy.cx, dz = cz - cy.cz;
                    float lim = cy.r + P * 0.5f;
                    if (dx * dx + dz * dz < lim * lim) { core = true; break; }
                }
                if (core) break;
            }
            if (core) continue;

            float h = Hash01(px, 31, pz);
            float bn = Mathf.PerlinNoise(cx * 0.005f + bx, cz * 0.005f + bz);
            int type;
            if (bn > 0.58f) type = h < 0.15f ? 3 : 4;                                          // великі ліси
            else if (bn < 0.32f) type = h < 0.7f ? 2 : (h < 0.85f ? 3 : 4);                    // луки
            else type = h < 0.35f ? 0 : h < 0.55f ? 1 : h < 0.75f ? 2 : h < 0.9f ? 3 : 4;      // ферми
            AddJob(cx, cz, () => BuildPlot(plot, type));
        }

        // усі плитки в чергу (будуються лише поблизу каменя)
        for (int tz = 0; tz < tilesPerSide; tz++)
        for (int tx = 0; tx < tilesPerSide; tx++)
            pending.Add(new Vector2Int(tx, tz));
    }

    // Місце старту каменя: на суші неподалік від столиці, подалі від берега
    void ComputeSpawn()
    {
        Country c = countries[Mathf.Clamp(startCountry, 0, countries.Count - 1)];
        spawnV = new Vector2(c.center.x, c.center.y);
        int want = Mathf.Max(capitalRadius + 20, 60);
        bool found = false;
        for (int dist = want; dist >= 10 && !found; dist -= 5)
        for (int a = 0; a < 24 && !found; a++)
        {
            float ang = a * (Mathf.PI * 2f / 24f);
            int px = c.center.x + Mathf.RoundToInt(Mathf.Cos(ang) * dist);
            int pz = c.center.y + Mathf.RoundToInt(Mathf.Sin(ang) * dist);
            if (!InMap(px, pz)) continue;
            int i = I(px, pz);
            if (country[i] == c.id && landDist[i] > 30)
            {
                spawnV = new Vector2(px, pz);
                found = true;
            }
        }
        haveStart = true;
        startV = spawnV;
        startClearV = 14f;
    }

    bool AnyLand(RectInt r)
    {
        for (int k = 0; k < 16; k++)
        {
            int x = r.x + (k % 4) * r.width / 4 + r.width / 8;
            int z = r.y + (k / 4) * r.height / 4 + r.height / 8;
            if (InMap(x, z) && country[I(x, z)] >= 0) return true;
        }
        return false;
    }

    void KeepSeedComponent(Country c)
    {
        int si = I(c.center.x, c.center.y);
        country[si] = (sbyte)c.id;
        isLake[si] = false;

        bool[] visited = new bool[N * N];
        int[] queue = new int[N * N];
        int qh = 0, qt = 0;
        queue[qt++] = si;
        visited[si] = true;
        while (qh < qt)
        {
            int i = queue[qh++];
            int x = i % N, z = i / N;
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= N || nz >= N) continue;
                int j = nx + N * nz;
                if (visited[j] || country[j] != c.id) continue;
                visited[j] = true;
                queue[qt++] = j;
            }
        }

        for (int i = 0; i < country.Length; i++)
            if (country[i] == c.id && !visited[i])
                country[i] = -1;
    }

    // Відстань до берега: для суші до найближчої води (море чи озеро), для води до найближчої суші
    void ComputeLandDist()
    {
        landDist = new ushort[N * N];
        int[] queue = new int[N * N];
        int qh = 0, qt = 0;

        for (int z = 0; z < N; z++)
        for (int x = 0; x < N; x++)
        {
            int i = x + N * z;
            bool land = country[i] >= 0;
            bool boundary = false;
            if (x > 0 && (country[i - 1] >= 0) != land) boundary = true;
            else if (x < N - 1 && (country[i + 1] >= 0) != land) boundary = true;
            else if (z > 0 && (country[i - N] >= 0) != land) boundary = true;
            else if (z < N - 1 && (country[i + N] >= 0) != land) boundary = true;
            if (boundary)
            {
                landDist[i] = 1;
                queue[qt++] = i;
            }
        }

        while (qh < qt)
        {
            int i = queue[qh++];
            int x = i % N, z = i / N;
            bool land = country[i] >= 0;
            ushort nd = (ushort)Mathf.Min(65534, landDist[i] + 1);
            for (int d = 0; d < 4; d++)
            {
                int nx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nz = z + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (nx < 0 || nz < 0 || nx >= N || nz >= N) continue;
                int j = nx + N * nz;
                if (landDist[j] != 0 || (country[j] >= 0) != land) continue;
                landDist[j] = nd;
                queue[qt++] = j;
            }
        }

        for (int i = 0; i < landDist.Length; i++)
            if (landDist[i] == 0) landDist[i] = 65535;
    }

    // Дорога з вигином між двома поселеннями (лише по суші)
    void CarveRoad(Vector2 a, Vector2 b, System.Random rng)
    {
        float len = Vector2.Distance(a, b);
        if (len < 1f) return;
        int steps = Mathf.CeilToInt(len);
        Vector2 dir = (b - a) / len;
        Vector2 perp = new Vector2(-dir.y, dir.x);
        float ph = (float)rng.NextDouble() * 6.28f;
        float amp = Mathf.Min(12f, len * 0.08f);
        int rw = Mathf.Max(1, roadWidth);
        for (int s = 0; s <= steps; s++)
        {
            float t = s / (float)steps;
            float off = amp * Mathf.Sin(t * 6.2832f + ph) * Mathf.Sin(t * Mathf.PI);
            Vector2 p = a + dir * s + perp * off;
            int px = Mathf.RoundToInt(p.x), pz = Mathf.RoundToInt(p.y);
            for (int dz = 0; dz < rw; dz++)
            for (int dx = 0; dx < rw; dx++)
                MarkRoad(px + dx - rw / 2, pz + dz - rw / 2, 2);
        }
    }

    // Вулиці міста й рядки хаток уздовж них. Столиця отримує замок, місто вежу, село нічого
    void PlanCity(Country ct, City cy, System.Random rng)
    {
        int rw = Mathf.Max(1, roadWidth);
        int rowSp = rw + 2 * (houseDepthMax + 3);
        int rr = cy.r;

        // центральна споруда
        if (cy.capital)
        {
            RectInt cr = new RectInt(cy.cx - 12, cy.cz - 12, 24, 24);
            MarkOcc(cr);
            int ccx = cy.cx, ccz = cy.cz, cid = ct.id;
            AddJob(ccx, ccz, () => BuildCastle(ccx, ccz, cid));
        }
        else if (!cy.village)
        {
            RectInt tr = new RectInt(cy.cx - 5, cy.cz - 5, 10, 10);
            MarkOcc(tr);
            int tcx = cy.cx, tcz = cy.cz, tid = ct.id;
            AddJob(tcx, tcz, () => BuildTower(tcx, tcz, tid));
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
                        int hox = ox, hoz = oz, hf = f, hw = w, hd = d, hid = ct.id;
                        AddJob(ox + w / 2, oz + d / 2, () => BuildHouse(hf, hox, hoz, hw, hd, hid));
                    }
                    x += w + 3 + rng.Next(0, 2);
                }
            }
        }
    }

    // ---------- плитки й черга ----------

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

            // найближча до каменя ще не збудована плитка
            int bestIdx = -1;
            float bestD = float.MaxValue;
            for (int i = 0; i < pending.Count; i++)
            {
                Vector2 c = new Vector2((pending[i].x + 0.5f) * T - M, (pending[i].y + 0.5f) * T - M);
                float d = (c - rockV).sqrMagnitude;
                if (d < bestD) { bestD = d; bestIdx = i; }
            }
            float reach = R + T;
            if (bestIdx < 0 || bestD > reach * reach)
            {
                yield return null;
                continue;
            }

            Vector2Int tile = pending[bestIdx];
            pending.RemoveAt(bestIdx);
            BuildTile(tile);

            int idx = 0;
            while (idx < ops.Count)
            {
                int end = Mathf.Min(ops.Count, idx + Mathf.Max(100, voxelsPerFrame));
                for (; idx < end; idx++)
                {
                    VoxelOp op = ops[idx];
                    // нічого не затираємо: деталі завжди ставляться ПЕРШИМИ, а заливка потім
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

            // швидкий камінь не повинен проскакувати крізь стіни
            Rigidbody rb = rock.GetComponent<Rigidbody>();
            if (rb != null) rb.collisionDetectionMode = CollisionDetectionMode.Continuous;

            PlaceRockOnLand();
        }

        Vector3 l = world.transform.InverseTransformPoint(rock.WorldCenter);
        rockV = new Vector2(l.x / vs, l.z / vs);
        return true;
    }

    // Переносить камінь на сушу (місце з ComputeSpawn) і ставить його на підлогу
    void PlaceRockOnLand()
    {
        float r = rock.Radius;
        Vector3 target = world.transform.TransformPoint(new Vector3(spawnV.x * vs, 0f, spawnV.y * vs));
        target.y = r + 0.5f;                       // підлога на y = 0
        rock.transform.position += target - rock.WorldCenter;

        Rigidbody rb = rock.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.position = rock.transform.position;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
        Physics.SyncTransforms();

        startV = spawnV;
        startClearV = Mathf.Max(14f, r * startClearInRockRadii / vs);
        haveStart = true;
    }

    void BuildTile(Vector2Int tile)
    {
        ops.Clear();
        if (jobs.TryGetValue(tile, out var list))
            foreach (var job in list) job();
        BuildTileFlat(tile);
    }

    // ---------- невидимі стіни вздовж берега (лише якщо Block Water) ----------

    void BuildWalls()
    {
        int cs = Mathf.Max(1, wallCell);
        int cn = Mathf.CeilToInt(N / (float)cs);
        bool[] wl = new bool[cn * cn];
        for (int z = -M; z < M; z++)
        for (int x = -M; x < M; x++)
        {
            int i = I(x, z);
            if (country[i] >= 0) continue;
            if (!isLake[i] && landDist[i] > wallThickness) continue;
            wl[((x + M) / cs) + cn * ((z + M) / cs)] = true;
        }

        bool[] used = new bool[cn * cn];
        GameObject root = new GameObject("WaterBarrier");
        root.transform.SetParent(world.transform, false);

        for (int cz = 0; cz < cn; cz++)
        for (int cx = 0; cx < cn; cx++)
        {
            int idx = cx + cn * cz;
            if (!wl[idx] || used[idx]) continue;

            int w = 1;
            while (cx + w < cn && wl[idx + w] && !used[idx + w]) w++;
            int h = 1;
            bool grow = true;
            while (grow && cz + h < cn)
            {
                for (int k = 0; k < w; k++)
                {
                    int j = cx + k + cn * (cz + h);
                    if (!wl[j] || used[j]) { grow = false; break; }
                }
                if (grow) h++;
            }
            for (int dz = 0; dz < h; dz++)
            for (int dx = 0; dx < w; dx++)
                used[cx + dx + cn * (cz + dz)] = true;

            float x0 = (cx * cs - M) * vs;
            float x1 = (Mathf.Min((cx + w) * cs, N) - M) * vs;
            float z0 = (cz * cs - M) * vs;
            float z1 = (Mathf.Min((cz + h) * cs, N) - M) * vs;

            BoxCollider bc = root.AddComponent<BoxCollider>();
            bc.center = new Vector3((x0 + x1) * 0.5f, wallHeight * 0.5f - 5f, (z0 + z1) * 0.5f);
            bc.size = new Vector3(x1 - x0, wallHeight, z1 - z0);
        }
    }

    // ---------- плоске море, озера, пляж, дороги ----------

    byte RoadColor(int kind, int x, int z)
    {
        if (kind == 1) return Hash01(x, 0, z) < 0.2f ? gravelSet[2] : gravelSet[Hash01(x, 1, z) < 0.5f ? 0 : 1];
        return dirtSet[Hash01(x, 2, z) < 0.5f ? 0 : 1];
    }

    // Один квад на клітинку (колір з палітри VoxelWorld, як у чанків)
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
            else if (country[i] < 0)
            {
                id = Hash01(x, 3, z) < 0.12f ? cWaterLight : cWater;
                y = waterHeight;
            }
            else if (landDist[i] <= beachWidth)
            {
                id = sandSet[Hash01(x, 4, z) < 0.5f ? 0 : 1];
                y = waterHeight;
            }
            else continue;

            float fx0 = x * vs, fx1 = fx0 + vs, fz0 = z * vs, fz1 = fz0 + vs;
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

        var mesh = new Mesh { name = "CountryFlat", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(mVerts);
        mesh.SetNormals(mNormals);
        mesh.SetUVs(0, mUvs);
        mesh.SetTriangles(mTris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject("Flat " + tile.x + "_" + tile.y);
        go.transform.SetParent(world.transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = world.RuntimeMaterial;
        rend.shadowCastingMode = ShadowCastingMode.Off;

        // тонкий колайдер: камінь їде по поверхні води й доріг, а не по підлозі під ними
        if (flatCollider)
        {
            var mc = go.AddComponent<MeshCollider>();
            mc.sharedMesh = mesh;
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
        int wh = Mathf.Max(2, houseWallHeight);
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

    // ---------- ділянки між поселеннями ----------

    // type: 0 пшениця, 1 соняшники, 2 лука, 3 сад, 4 гай/ліс
    void BuildPlot(RectInt plot, int type)
    {
        var rng = new System.Random(Hash(seed ^ 0x777, plot.x, plot.y));
        int x0 = Mathf.Max(plot.x + 1, -M), x1 = Mathf.Min(plot.xMax - 1, M);
        int z0 = Mathf.Max(plot.y + 1, -M), z1 = Mathf.Min(plot.yMax - 1, M);
        if (x1 <= x0 || z1 <= z0) return;

        switch (type)
        {
            case 0:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    byte row = wheatSet[((z / 2) & 1) == 0 ? 0 : 1];
                    Add(x, 0, z, row);
                    if (Hash01(x, 11, z) < 0.7f)
                        Add(x, 1, z, Pick(wheatSet, Hash(x, 12, z)));
                }
                // копиці сіна
                for (int n = rng.Next(0, 3); n > 0; n--)
                {
                    int hx = rng.Next(x0 + 4, Mathf.Max(x0 + 5, x1 - 4)), hz = rng.Next(z0 + 4, Mathf.Max(z0 + 5, z1 - 4));
                    if (!TreeClear(hx, hz, 2)) continue;
                    for (int i = 0; i < 3; i++)
                        Disc(hx, hz, 2.2f * Mathf.Sqrt(1f - (i / 3f) * (i / 3f)), i + 1, i + 2, i % 2 == 0 ? cStraw : cStraw2);
                }
                break;

            case 1:
                for (int z = z0; z < z1; z++)
                for (int x = x0; x < x1; x++)
                {
                    if (!Free(x, z)) continue;
                    Add(x, 0, z, ((x + z) & 1) == 0 ? cSoil : cSoilDark);
                    if (x % 3 != 0 || z % 3 != 0) continue;
                    for (int y = 1; y <= 2; y++) Add(x, y, z, cStem);
                    Add(x, 3, z, cSunCenter);
                    Add(x + 1, 3, z, cSun); Add(x - 1, 3, z, cSun);
                    Add(x, 3, z + 1, cSun); Add(x, 3, z - 1, cSun);
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
                for (int z = z0 + 2; z < z1 - 1; z += 6)
                for (int x = x0 + 2; x < x1 - 1; x += 6)
                {
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    if (!TreeClear(tx, tz, 1)) continue;
                    BuildTinyTree(tx, tz, 1, rng, false, leafSets[2]);
                }
                break;

            default:
                for (int z = z0 + 1; z < z1 - 1; z += 3)
                for (int x = x0 + 1; x < x1 - 1; x += 3)
                {
                    if (rng.NextDouble() > 0.8) continue;
                    int tx = x + rng.Next(-1, 2), tz = z + rng.Next(-1, 2);
                    int r = rng.NextDouble() < 0.3 ? 2 : 1;
                    if (!TreeClear(tx, tz, r)) continue;
                    BuildTinyTree(tx, tz, r, rng, rng.NextDouble() < 0.4, leafSets[rng.Next(2)]);
                }
                break;
        }
    }

    // ---------- крихітні дерева ----------

    void BuildTinyTree(int gx, int gz, int r, System.Random rng, bool pine, byte[] leaves)
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

    void Add(int x, int y, int z, byte id)
    {
        ops.Add(new VoxelOp { pos = new Vector3Int(x, y, z), id = id });
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