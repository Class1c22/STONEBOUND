using UnityEngine;

// Луг у кільці неприступних гір.
//  • Гори (Unity Terrain) не руйнуються: їх не чіпають ні камінь, ні шматки, бо вони не частина VoxelWorld.
//  • Біля підніжжя стоїть невидима стіна, тому на урвище не заїхати й не заскочити.
//  • Луг це воксельна земля завтовшки groundDepthVoxels, яку можна копати. Під нею незламна основа
//    (Terrain), тож глибше за шар землі вирити не можна.
// Повісь на порожній GameObject, призначте World, перетягни старий plane у Old Ground, натисни Play.
// Воксельний світ має стояти в (0, 0, 0) без повороту. Верх землі = y 0, на ньому стоять споруди.
public class MountainMapGenerator : MonoBehaviour
{
    [Header("Розмір")]
    [Tooltip("Сторона квадратної карти в метрах (гори ззовні лугу)")]
    public float mapSize = 600f;
    [Tooltip("Висота найвищих гір над лугом у метрах")]
    public float mountainHeight = 160f;
    [Tooltip("Роздільність висот: 513 (достатньо), 1025 (детальніше, важче)")]
    public int heightmapResolution = 513;

    [Header("Луг")]
    [Tooltip("Радіус лугу в метрах. Більше = більше воксельної землі, а отже довший старт гри")]
    public float meadowRadius = 100f;
    [Tooltip("Ширина урвища від краю лугу до повної висоти гір (менше = крутіше)")]
    public float cliffWidth = 40f;
    [Tooltip("Товщина воксельної землі у вокселях (6 = 1.5 м при вокселі 0.25 м). Глибше копати неможливо")]
    public int groundDepthVoxels = 6;
    [Tooltip("Воксельний світ, у якому буде земля. Порожньо = тільки гори без ґрунту")]
    public VoxelWorld world;

    [Header("Гори")]
    [Tooltip("Мінімальна висота гірського кільця (частка від mountainHeight): більше = суцільніша стіна")]
    [Range(0.2f, 1f)] public float minPeakHeight = 0.6f;
    [Tooltip("Скільки хребтів на карті (більше = дрібніші й частіші гори)")]
    public float mountainNoiseScale = 3.5f;
    public int seed = 12345;

    [Header("Текстури")]
    [Tooltip("Де починається сніг (частка висоти гір)")]
    [Range(0.4f, 1f)] public float snowLine = 0.72f;
    public int alphamapResolution = 512;

    [Header("Інше")]
    [Tooltip("Невидима стіна навколо лугу, щоб камінь не міг видертись на урвище")]
    public bool invisibleCliffWall = true;
    [Tooltip("Старий plane/земля: вимкнеться, коли створиться карта")]
    public GameObject oldGround;

    public Terrain Terrain { get; private set; }

    const float EmbedMeters = 4f;   // воксельна земля заходить під урвище, щоб не було щілини

    void Awake()
    {
        GenerateTerrain();
    }

    void Start()
    {
        if (world != null) FillVoxelGround();
    }

    Vector3 Origin => world != null ? world.transform.position : transform.position;

    float GroundDepthMeters => world != null ? Mathf.Max(1, groundDepthVoxels) * world.voxelSize : 0f;

    // ---------------- гори ----------------

    void GenerateTerrain()
    {
        if (oldGround != null) oldGround.SetActive(false);

        int res = heightmapResolution;
        float ox = seed * 37.17f + 1000f;
        float oy = seed * 11.93f + 1000f;
        float half = mapSize * 0.5f;

        // ---------- висоти ----------
        var heights = new float[res, res];
        for (int y = 0; y < res; y++)
        for (int x = 0; x < res; x++)
        {
            float u = x / (float)(res - 1) * 2f - 1f;   // -1..1
            float v = y / (float)(res - 1) * 2f - 1f;
            float dist = Mathf.Sqrt(u * u + v * v) * half;   // метри від центру

            float t = Mathf.InverseLerp(meadowRadius, meadowRadius + cliffWidth, dist);
            float m = Mathf.SmoothStep(0f, 1f, t);           // 0 на лузі, 1 у горах
            if (m <= 0f) { heights[y, x] = 0f; continue; }

            float n = Ridged(u * mountainNoiseScale + ox, v * mountainNoiseScale + oy);
            float peak = Mathf.Lerp(minPeakHeight, 1f, n);   // кільце ніде не опускається нижче minPeakHeight
            heights[y, x] = m * peak;
        }

        var data = new TerrainData();
        data.heightmapResolution = res;
        data.size = new Vector3(mapSize, mountainHeight, mapSize);
        data.SetHeights(0, 0, heights);

        // ---------- шари текстур і розфарбовка за висотою та нахилом ----------
        var layers = new TerrainLayer[]
        {
            MakeLayer("Трава", new Color(0.30f, 0.45f, 0.18f), new Color(0.38f, 0.55f, 0.22f), 6f),
            MakeLayer("Земля", new Color(0.42f, 0.33f, 0.22f), new Color(0.52f, 0.42f, 0.28f), 8f),
            MakeLayer("Скеля", new Color(0.38f, 0.38f, 0.40f), new Color(0.55f, 0.54f, 0.52f), 10f),
            MakeLayer("Сніг",  new Color(0.88f, 0.90f, 0.94f), new Color(0.97f, 0.98f, 1.00f), 8f),
        };
        data.terrainLayers = layers;
        data.alphamapResolution = alphamapResolution;

        int ar = alphamapResolution;
        var maps = new float[ar, ar, 4];
        for (int y = 0; y < ar; y++)
        for (int x = 0; x < ar; x++)
        {
            float nx = x / (float)(ar - 1);
            float ny = y / (float)(ar - 1);
            float h01 = data.GetInterpolatedHeight(nx, ny) / mountainHeight;   // 0..1
            float slope = data.GetSteepness(nx, ny);                           // градуси

            float steep = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(28f, 46f, slope));
            float rockMask = Mathf.Max(steep, Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.32f, 0.55f, h01)));
            float dirtMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.03f, 0.22f, h01));
            float snowMask = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(snowLine, snowLine + 0.1f, h01))
                             * (1f - Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(48f, 68f, slope)));

            // сума ваг завжди дорівнює 1
            float snow = snowMask;
            float rock = (1f - snow) * rockMask;
            float dirt = (1f - snow) * (1f - rockMask) * dirtMask;
            float grass = (1f - snow) * (1f - rockMask) * (1f - dirtMask);

            // під лугом основа з ґрунту: її видно в ямах, коли землю вириють до дна
            float dx = (nx - 0.5f) * mapSize, dz = (ny - 0.5f) * mapSize;
            float meadow = 1f - Mathf.SmoothStep(meadowRadius, meadowRadius + 10f, Mathf.Sqrt(dx * dx + dz * dz));
            grass *= 1f - meadow;
            rock *= 1f - meadow;
            snow *= 1f - meadow;
            dirt = dirt * (1f - meadow) + meadow;

            maps[y, x, 0] = grass;
            maps[y, x, 1] = dirt;
            maps[y, x, 2] = rock;
            maps[y, x, 3] = snow;
        }
        data.SetAlphamaps(0, 0, maps);

        // ---------- об'єкт Terrain: центр карти в центрі світу, лужок лежить на дні шару землі ----------
        GameObject go = Terrain.CreateTerrainGameObject(data);
        go.name = "Mountain Terrain";
        go.transform.SetParent(transform, false);
        Vector3 o = Origin;
        go.transform.position = new Vector3(o.x - half, o.y - GroundDepthMeters, o.z - half);
        Terrain = go.GetComponent<Terrain>();

        if (invisibleCliffWall) BuildCliffWall();
    }

    // Кільце невидимих колайдерів по краю лугу: урвище стає недосяжним
    void BuildCliffWall()
    {
        var root = new GameObject("Cliff Wall");
        root.transform.SetParent(transform, false);

        const int segments = 96;
        const float thickness = 4f;
        float height = mountainHeight * 3f;
        float radius = meadowRadius + 1f;
        float segWidth = 2f * Mathf.PI * radius / segments * 1.2f;   // трохи внахлест, без щілин
        Vector3 o = Origin;

        for (int i = 0; i < segments; i++)
        {
            float a = i / (float)segments * Mathf.PI * 2f;
            var go = new GameObject("Wall " + i);
            go.transform.SetParent(root.transform, false);

            // локальна вісь X дивиться від центру, Z уздовж краю
            go.transform.position = o + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * (radius + thickness * 0.5f)
                                    + Vector3.up * (height * 0.4f);
            go.transform.rotation = Quaternion.Euler(0f, -a * Mathf.Rad2Deg, 0f);

            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(thickness, height, segWidth);
        }
    }

    // ---------------- воксельна земля ----------------

    void FillVoxelGround()
    {
        float vs = world.voxelSize;
        int depth = Mathf.Max(1, groundDepthVoxels);

        float radiusVox = (meadowRadius + EmbedMeters) / vs;
        int R = Mathf.CeilToInt(radiusVox);
        float R2 = radiusVox * radiusVox;

        world.voxelGround = true;

        byte grassA = world.GetOrAddColor(new Color32(78, 118, 46, 255));
        byte grassB = world.GetOrAddColor(new Color32(92, 136, 54, 255));
        byte grassC = world.GetOrAddColor(new Color32(68, 106, 42, 255));
        byte dirtA = world.GetOrAddColor(new Color32(112, 86, 58, 255));
        byte dirtB = world.GetOrAddColor(new Color32(96, 72, 48, 255));

        float nx = seed * 5.31f + 300f;
        float nz = seed * 2.77f + 300f;

        for (int z = -R; z <= R; z++)
        for (int x = -R; x <= R; x++)
        {
            float dx = x + 0.5f, dz = z + 0.5f;
            if (dx * dx + dz * dz > R2) continue;

            // верхній шар трави плямами різних відтінків
            float n = Mathf.PerlinNoise(x * 0.04f + nx, z * 0.04f + nz) * 0.75f
                    + Mathf.PerlinNoise(x * 0.3f + nx, z * 0.3f + nz) * 0.25f;
            byte top = n < 0.38f ? grassC : (n < 0.62f ? grassA : grassB);
            world.SetVoxel(new Vector3Int(x, -1, z), top);

            for (int y = 2; y <= depth; y++)
                world.SetVoxel(new Vector3Int(x, -y, z), y <= 3 ? dirtA : dirtB);
        }
    }

    // ---------------- допоміжне ----------------

    // Хребтовий шум 0..1: гострі гребені, схожі на гірський масив
    static float Ridged(float x, float y)
    {
        float sum = 0f, amp = 1f, freq = 1f, norm = 0f;
        for (int o = 0; o < 5; o++)
        {
            float p = Mathf.PerlinNoise(x * freq, y * freq);
            float r = 1f - Mathf.Abs(2f * p - 1f);
            r *= r;   // гостріші піки
            sum += r * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2f;
        }
        return sum / norm;
    }

    static TerrainLayer MakeLayer(string name, Color a, Color b, float tile)
    {
        const int size = 128;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, true)
        {
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear,
            name = name
        };

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            float n = Tileable(x, y, size, 0.09f) * 0.6f + Tileable(x, y, size, 0.4f) * 0.4f;
            tex.SetPixel(x, y, Color.Lerp(a, b, n));
        }
        tex.Apply();

        return new TerrainLayer
        {
            name = name,
            diffuseTexture = tex,
            tileSize = new Vector2(tile, tile)
        };
    }

    // Шум, що безшовно повторюється (щоб текстура на землі не мала швів)
    static float Tileable(float x, float y, float size, float scale)
    {
        float s = size;
        float a = Mathf.PerlinNoise(x * scale + 100f, y * scale + 100f);
        float b = Mathf.PerlinNoise((x - s) * scale + 100f, y * scale + 100f);
        float c = Mathf.PerlinNoise(x * scale + 100f, (y - s) * scale + 100f);
        float d = Mathf.PerlinNoise((x - s) * scale + 100f, (y - s) * scale + 100f);
        return (a * (s - x) * (s - y) + b * x * (s - y) + c * (s - x) * y + d * x * y) / (s * s);
    }
}