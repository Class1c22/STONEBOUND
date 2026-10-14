using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Луг у кільці неприступних воксельних гір.
//  • Гори й дно ям це блочний меш (східці з кубів), а не гладкий Terrain. Вони не руйнуються:
//    це не частина VoxelWorld, тож камінь і шматки їх не чіпають.
//  • Біля підніжжя стоїть невидима стіна, тому на урвище не заїхати й не заскочити.
//  • Луг це воксельна земля завтовшки groundDepthVoxels, яку можна копати. Під нею основа
//    земляного кольору, що не ламається, тож глибше шару землі вирити не можна.
// Повісь на порожній GameObject, призначте World, перетягни старий plane у Old Ground, натисни Play.
// Воксельний світ має стояти в (0, 0, 0) без повороту. Верх землі = y 0, на ньому стоять споруди.
public class MountainMapGenerator : MonoBehaviour
{
    [Header("Розмір")]
    [Tooltip("Сторона квадратної карти в метрах (гори ззовні лугу)")]
    public float mapSize = 600f;
    [Tooltip("Висота найвищих гір над лугом у метрах")]
    public float mountainHeight = 160f;
    [Tooltip("Розмір блока гір у метрах: 2 = грубі блоки, 1 = дрібніші (удвічі більше блоків по кожній осі, набагато важче)")]
    public float mountainBlockSize = 2f;
    [Tooltip("Скільки стовпців блоків в одному мешi (більше = менше об'єктів, але важчий кожен)")]
    public int chunkColumns = 50;

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
    [Tooltip("Де починається сніг (частка висоти гір)")]
    [Range(0.4f, 1f)] public float snowLine = 0.72f;
    [Tooltip("До якої висоти біля підніжжя блоки зелені (частка висоти гір)")]
    [Range(0f, 0.2f)] public float grassLine = 0.06f;

    [Header("Інше")]
    [Tooltip("Невидима стіна навколо лугу, щоб камінь не міг видертись на урвище")]
    public bool invisibleCliffWall = true;
    [Tooltip("Старий plane/земля: вимкнеться, коли створиться карта")]
    public GameObject oldGround;

    const float EmbedMeters = 4f;   // воксельна земля заходить під урвище, щоб не було щілини

    // палітра блочного рельєфу: індекс = піксель текстури
    const int GrassStart = 0, GrassCount = 3;
    const int DirtStart = 3, DirtCount = 2;
    const int RockStart = 5, RockCount = 4;
    const int SnowStart = 9, SnowCount = 2;

    static readonly Color32[] Palette =
    {
        new Color32(78, 118, 46, 255), new Color32(92, 136, 54, 255), new Color32(68, 106, 42, 255),     // трава
        new Color32(112, 86, 58, 255), new Color32(96, 72, 48, 255),                                      // земля
        new Color32(110, 108, 104, 255), new Color32(124, 122, 118, 255),
        new Color32(96, 94, 92, 255), new Color32(138, 136, 132, 255),                                    // скеля
        new Color32(236, 240, 246, 255), new Color32(222, 228, 238, 255),                                 // сніг
    };
    const int PaletteWidth = 16;

    Vector3 Origin => world != null ? world.transform.position : transform.position;
    float GroundDepthMeters => world != null ? Mathf.Max(1, groundDepthVoxels) * world.voxelSize : 0f;

    void Start()
    {
        // у Start, щоб VoxelWorld уже встиг створити свій матеріал в Awake
        GenerateBlockyTerrain();
        if (world != null) FillVoxelGround();
    }

    // ---------------- гори ----------------

    void GenerateBlockyTerrain()
    {
        if (oldGround != null) oldGround.SetActive(false);

        float half = mapSize * 0.5f;
        int n = Mathf.Max(8, Mathf.RoundToInt(mapSize / Mathf.Max(0.25f, mountainBlockSize)));
        float cell = mapSize / n;   // справжній розмір блока, щоб карта вкладалась рівно
        float ox = seed * 37.17f + 1000f;
        float oy = seed * 11.93f + 1000f;

        // ---------- висоти у блоках ----------
        var level = new int[n, n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float xc = -half + (i + 0.5f) * cell;
            float zc = -half + (j + 0.5f) * cell;
            float dist = Mathf.Sqrt(xc * xc + zc * zc);

            float t = Mathf.InverseLerp(meadowRadius, meadowRadius + cliffWidth, dist);
            float m = Mathf.SmoothStep(0f, 1f, t);   // 0 на лузі, 1 у горах
            if (m <= 0f) { level[i, j] = 0; continue; }

            float noise = Ridged(xc / half * mountainNoiseScale + ox, zc / half * mountainNoiseScale + oy);
            float peak = Mathf.Lerp(minPeakHeight, 1f, noise);   // кільце ніде не опускається нижче minPeakHeight
            level[i, j] = Mathf.RoundToInt(m * peak * mountainHeight / cell);
        }

        // ---------- матеріал: палітра з кольорів, як у VoxelWorld ----------
        var tex = new Texture2D(PaletteWidth, 1, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };
        for (int p = 0; p < PaletteWidth; p++)
            tex.SetPixel(p, 0, p < Palette.Length ? (Color)Palette[p] : Color.magenta);
        tex.Apply();

        Material mat = world != null && world.RuntimeMaterial != null
            ? new Material(world.RuntimeMaterial)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", 0f);

        // ---------- меші по ділянках ----------
        Vector3 o = Origin;
        var root = new GameObject("Blocky Terrain");
        root.transform.SetParent(transform, false);
        // нуль висоти рельєфу = дно ям (нижче верху землі на її товщину)
        root.transform.position = new Vector3(o.x - half, o.y - GroundDepthMeters, o.z - half);

        int cc = Mathf.Max(8, chunkColumns);
        for (int cz = 0; cz < n; cz += cc)
        for (int cx = 0; cx < n; cx += cc)
            BuildChunk(root.transform, mat, level, n, cell, cx, cz, Mathf.Min(cx + cc, n), Mathf.Min(cz + cc, n));

        if (invisibleCliffWall) BuildCliffWall();
    }

    void BuildChunk(Transform parent, Material mat, int[,] level, int n, float cell, int i0, int j0, int i1, int j1)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        float footY = grassLine * mountainHeight;
        float snowY = snowLine * mountainHeight;

        for (int j = j0; j < j1; j++)
        for (int i = i0; i < i1; i++)
        {
            int L = level[i, j];
            float x0 = i * cell, x1 = x0 + cell;
            float z0 = j * cell, z1 = z0 + cell;
            float top = L * cell;
            int hash = Hash(i, j);

            // верх блока
            int topColor;
            if (L == 0) topColor = DirtStart + hash % DirtCount;                       // дно ям: земля
            else if (top < footY) topColor = GrassStart + hash % GrassCount;           // підніжжя: трава
            else if (top < snowY) topColor = RockStart + hash % RockCount;
            else topColor = SnowStart + hash % SnowCount;

            AddQuad(verts, normals, uvs, tris,
                new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, top, z0),
                topColor);

            // стіни до нижчих сусідів (кожну стіну будує вищий стовпець, дублів немає)
            if (i + 1 < n && level[i + 1, j] < L)
                Wall(verts, normals, uvs, tris, 0, x1, x1, z0, z1, level[i + 1, j] * cell, top, hash, footY, snowY);
            if (i - 1 >= 0 && level[i - 1, j] < L)
                Wall(verts, normals, uvs, tris, 1, x0, x0, z0, z1, level[i - 1, j] * cell, top, hash, footY, snowY);
            if (j + 1 < n && level[i, j + 1] < L)
                Wall(verts, normals, uvs, tris, 2, x0, x1, z1, z1, level[i, j + 1] * cell, top, hash, footY, snowY);
            if (j - 1 >= 0 && level[i, j - 1] < L)
                Wall(verts, normals, uvs, tris, 3, x0, x1, z0, z0, level[i, j - 1] * cell, top, hash, footY, snowY);
        }

        var mesh = new Mesh { name = $"Terrain Chunk {i0}_{j0}", indexFormat = IndexFormat.UInt32 };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.SetTriangles(tris, 0);
        mesh.RecalculateBounds();

        var go = new GameObject($"Terrain Chunk {i0}_{j0}");
        go.transform.SetParent(parent, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // Стіна між двома висотами. Ділиться на смуги за кольором: земля внизу, скеля, сніг нагорі
    void Wall(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
              int dir, float xa, float xb, float za, float zb, float yLow, float yHigh,
              int hash, float footY, float snowY)
    {
        float a = yLow;
        for (int c = 0; c < 3; c++)
        {
            float end = yHigh;
            if (c == 0 && a < footY && footY < yHigh) end = footY;
            else if (c == 0 && a >= footY) continue;
            else if (c == 1 && a < snowY && snowY < yHigh) end = snowY;
            else if (c == 1 && a >= snowY) continue;
            else if (c == 2 && a >= yHigh) break;

            float mid = (a + end) * 0.5f;
            int color = mid < footY ? DirtStart + hash % DirtCount
                      : mid < snowY ? RockStart + (hash + (int)(mid / 3f)) % RockCount
                      : SnowStart + hash % SnowCount;

            EmitWall(v, nr, uv, t, dir, xa, xb, za, zb, a, end, color);
            a = end;
            if (a >= yHigh) break;
        }
    }

    void EmitWall(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
                  int dir, float xa, float xb, float za, float zb, float y0, float y1, int color)
    {
        switch (dir)
        {
            case 0:   // +X
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, za), new Vector3(xa, y1, za), new Vector3(xa, y1, zb), new Vector3(xa, y0, zb), color);
                break;
            case 1:   // -X
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, zb), new Vector3(xa, y1, zb), new Vector3(xa, y1, za), new Vector3(xa, y0, za), color);
                break;
            case 2:   // +Z
                AddQuad(v, nr, uv, t, new Vector3(xb, y0, za), new Vector3(xb, y1, za), new Vector3(xa, y1, za), new Vector3(xa, y0, za), color);
                break;
            default:  // -Z
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, za), new Vector3(xa, y1, za), new Vector3(xb, y1, za), new Vector3(xb, y0, za), color);
                break;
        }
    }

    // Квадрат з плоским освітленням: колір береться з пікселя палітри за індексом
    static void AddQuad(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
                        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int color)
    {
        int s = v.Count;
        Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
        Vector2 uvp = new Vector2((color + 0.5f) / PaletteWidth, 0.5f);

        v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
        for (int k = 0; k < 4; k++) { nr.Add(normal); uv.Add(uvp); }
        t.Add(s); t.Add(s + 1); t.Add(s + 2);
        t.Add(s); t.Add(s + 2); t.Add(s + 3);
    }

    static int Hash(int x, int z)
    {
        unchecked { return (int)(((uint)(x * 73856093) ^ (uint)(z * 19349663)) & 0x7fffffff); }
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

        byte grassA = world.GetOrAddColor(Palette[0]);
        byte grassB = world.GetOrAddColor(Palette[1]);
        byte grassC = world.GetOrAddColor(Palette[2]);
        byte dirtA = world.GetOrAddColor(Palette[3]);
        byte dirtB = world.GetOrAddColor(Palette[4]);

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
}