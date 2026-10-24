using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Генератор міста для 2 рівня: дороги, будинки, дерева й паркани з вокселів.
// Повісь на порожній GameObject, перетягни VoxelWorld (RockAbsorber знайдеться сам).
//
// Як це працює:
//  * Світ ділиться на квартали. Вздовж західного й південного краю кожного кварталу йде дорога,
//    тож дороги самі складаються в сітку з перехрестями.
//  * Решта кварталу ділиться на ділянки (lotsPerSide x lotsPerSide). Кожна ділянка за хешем
//    (seed + координати) стає будинком із двором, гаєм або лишається порожньою.
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
    public VoxelWorld world;
    [Tooltip("Якщо порожньо, шукається сам")]
    public RockAbsorber rock;
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

    [Header("Ділянки")]
    [Range(0f, 1f)] public float houseChance = 0.6f;
    [Tooltip("Шанс гаю (кілька дерев). Решта ділянок порожні")]
    [Range(0f, 1f)] public float treeLotChance = 0.25f;
    [Range(0f, 1f)] public float fenceChance = 0.6f;
    [Tooltip("Скільки дерев максимум у дворі будинку (якщо вміщаються)")]
    public int treesPerYardMax = 2;
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
    [Tooltip("Порожні всередині. УВАГА: дах без опори далі за VoxelWorld.maxOverhang від стін падає " +
             "при першому ударі поруч, тому за замовчуванням будинки суцільні")]
    public bool hollowHouses = false;

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

    float scanTimer;
    bool haveStart;
    Vector2 startLocal, lastRockLocal;

    // кольори (індекси палітри VoxelWorld)
    byte[] wallA, wallB, roofA, roofB;
    byte[][] leafSets;
    byte[] pineSet;
    byte cPlinth, cTrim, cGlass, cFrame, cDoor, cInterior, cBrick;
    byte cAsphaltA, cAsphaltB, cCurb, cLine;
    byte cPost, cPlank, cRail, cBarkA, cBarkB;

    // буфери для плоскої дороги
    readonly List<Vector3> mVerts = new List<Vector3>();
    readonly List<Vector3> mNormals = new List<Vector3>();
    readonly List<Vector2> mUvs = new List<Vector2>();
    readonly List<int> mTris = new List<int>();

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
    }

    // ---------- пошук кварталів навколо каменя ----------

    void Update()
    {
        scanTimer -= Time.deltaTime;
        if (scanTimer > 0f) return;
        scanTimer = Mathf.Max(0.05f, scanInterval);

        if (rock == null)
        {
            rock = FindFirstObjectByType<RockAbsorber>();
            if (rock == null) return;
        }

        ScanBlocks();
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
                    // нічого не затираємо: старі споруди й уже поставлені частини лишаються
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

    // ---------- ділянка ----------

    void BuildLot(int bx, int bz, int li, int lj, int lx0, int lz0)
    {
        var rng = new System.Random(Hash(seed ^ 0x2F6E2B1, bx * 16 + li, bz * 16 + lj));
        double roll = rng.NextDouble();

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
            ScatterTrees(rng, lx0, lz0, count, 2, null);
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

        if (fenced) BuildFence(lx0, lz0, facing);

        int yardTrees = rng.Next(0, Mathf.Max(0, treesPerYardMax) + 1);
        if (yardTrees > 0)
            ScatterTrees(rng, lx0, lz0, yardTrees, margin, footprint);
    }

    // ---------- будинок ----------

    // Будуємо в локальних координатах (lx вздовж фасаду, lz вглиб, двері на lz = 0),
    // а PlaceH повертає їх у світ залежно від того, куди дивиться фасад.
    void PlaceH(int facing, int ox, int oz, int lx, int ly, int lz, byte id)
    {
        if (facing == 0) Add(ox + lx, ly, oz + lz, id);
        else Add(ox + lz, ly, oz + lx, id);
    }

    RectInt BuildHouse(System.Random rng, int ox, int oz, int facing, int w, int d, int floors)
    {
        int fh = floorV;
        int H = floors * fh;
        int t = wallV;

        int wp = rng.Next(wallA.Length);
        byte wA = wallA[wp], wB = wallB[wp];
        int rp = rng.Next(roofA.Length);
        byte rA = roofA[rp], rB = roofB[rp];

        int doorL = w / 2 - doorHalf - 1;
        int doorR = w / 2 + doorHalf + 1;

        // тіло: стіни, а всередині суцільний заповнювач (або порожнеча, якщо hollowHouses)
        for (int ly = 0; ly < H; ly++)
        for (int lz = 0; lz < d; lz++)
        for (int lx = 0; lx < w; lx++)
        {
            bool edge = lx < t || lx >= w - t || lz < t || lz >= d - t;
            if (!edge && hollowHouses && ly > 0) continue;

            byte id;
            if (!edge)
            {
                id = cInterior;
            }
            else
            {
                id = ((lx / 4 + ly / 4) & 1) == 0 ? wA : wB;
                if (ly < 2) id = cPlinth;
                else if (floors > 1 && ly == fh) id = cTrim;

                // двері на фасаді
                if (lz < t && lx >= w / 2 - doorHalf && lx < w / 2 + doorHalf && ly < doorH)
                {
                    if (hollowHouses) continue;
                    if (lz == 0)
                        id = (lx == w / 2 - doorHalf || lx == w / 2 + doorHalf - 1 || ly == doorH - 1) ? cTrim : cDoor;
                }
                else if (ly >= 2)
                {
                    // вікна лише на зовнішньому шарі стіни
                    int u = -1, len = 0;
                    bool front = false;
                    if (lz == 0) { u = lx; len = w; front = true; }
                    else if (lz == d - 1) { u = lx; len = w; }
                    else if (lx == 0) { u = lz; len = d; }
                    else if (lx == w - 1) { u = lz; len = d; }

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

    // avoid = площа будинку, яку не можна перекривати кроною (може бути null)
    void ScatterTrees(System.Random rng, int lx0, int lz0, int count, int margin, RectInt? avoid)
    {
        var placed = new List<Vector3Int>();   // (x, радіус, z)

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
            if (avoid.HasValue)
            {
                RectInt a = avoid.Value;
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