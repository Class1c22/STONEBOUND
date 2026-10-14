using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Луг у кільці неприступних воксельних гір.
//  • Земля й гори це один блочний меш (східці з кубів), а не гладкий Terrain. Він не частина VoxelWorld,
//    тому камінь і шматки його не руйнують: копати землю неможливо.
//  • Земля не плоска: навколо плоского майданчика зі спорудами вона плавно переходить у м'які горби й улоговини.
//  • Гори об'ємні: зламаний хребтовий шум із викривленням координат, рвана лінія підніжжя, дрібний рельєф,
//    затінення в кутах (AO), шари породи на урвищах, снігова лінія з плямами.
//  • Біля підніжжя стоїть невидима стіна, тому на урвище не заїхати й не заскочити.
// Повісь на порожній GameObject, призначте World, перетягни старий plane у Old Ground, натисни Play.
// Воксельний світ має стояти в (0, 0, 0) без повороту. Верх плоского майданчика = y 0, на ньому стоять споруди.
public class MountainMapGenerator : MonoBehaviour
{
    [Header("Розмір")]
    [Tooltip("Сторона квадратної карти в метрах (гори ззовні лугу)")]
    public float mapSize = 600f;
    [Tooltip("Висота найвищих гір над лугом у метрах")]
    public float mountainHeight = 160f;
    [Tooltip("Розмір блока в метрах: 2 = грубі блоки, 1.5 = середні, 1 = дрібні (удвічі більше блоків по кожній осі, набагато важче)")]
    public float mountainBlockSize = 1.5f;
    [Tooltip("Крок висоти в метрах. 0.25 = як воксель, плавніші схили і горби")]
    public float heightStep = 0.25f;
    [Tooltip("Скільки стовпців блоків в одному мешi (більше = менше об'єктів, але важчий кожен)")]
    public int chunkColumns = 40;

    [Header("Луг")]
    [Tooltip("Радіус лугу в метрах")]
    public float meadowRadius = 100f;
    [Tooltip("Радіус ідеально плоского майданчика навколо центру, де стоять споруди (верх = y 0). Має накривати всі стіни, будинок і ряд тестових стін")]
    public float flatRadius = 45f;
    [Tooltip("Ширина зони, де плоский майданчик поступово переходить у горби")]
    public float flatBlend = 30f;
    [Tooltip("Наскільки високі горби на лузі, у метрах (півамплітуда: від -X до +X)")]
    public float groundAmplitude = 2f;
    [Tooltip("Відстань між великими горбами в метрах")]
    public float groundWavelength = 45f;
    [Tooltip("Ширина урвища від краю лугу до повної висоти гір (менше = крутіше)")]
    public float cliffWidth = 40f;
    [Tooltip("Воксельний світ: з нього беруться матеріал і початок координат. Воксельна земля вимикається, земля це меш")]
    public VoxelWorld world;

    [Header("Гори")]
    [Tooltip("Мінімальна висота гірського кільця (частка від mountainHeight): більше = суцільніша стіна")]
    [Range(0.2f, 1f)] public float minPeakHeight = 0.6f;
    [Tooltip("Скільки хребтів на карті (більше = дрібніші й частіші гори)")]
    public float mountainNoiseScale = 3.5f;
    [Tooltip("Наскільки сильно викривлюються хребти (0 = рівні, більше = звивисті, природніші)")]
    public float ridgeWarp = 1.2f;
    [Tooltip("Дрібний рельєф на схилах у метрах: уступи, виступи, ребра")]
    public float surfaceDetail = 4f;
    [Tooltip("Наскільки нерівним є підніжжя гір (0 = ідеальне коло, 1 = дуже рване)")]
    [Range(0f, 1f)] public float footRagged = 0.5f;
    public int seed = 12345;
    [Tooltip("Де починається сніг (частка висоти гір)")]
    [Range(0.4f, 1f)] public float snowLine = 0.72f;
    [Tooltip("Наскільки снігова лінія ходить вгору-вниз, у метрах")]
    public float snowVariation = 12f;
    [Tooltip("До якої висоти біля підніжжя блоки зелені (частка висоти гір)")]
    [Range(0f, 0.2f)] public float grassLine = 0.06f;
    [Tooltip("Товщина одного шару породи на урвищах, у метрах")]
    public float strataThickness = 0.75f;

    [Header("Інше")]
    [Tooltip("Невидима стіна навколо лугу, щоб камінь не міг видертись на урвище")]
    public bool invisibleCliffWall = true;
    [Tooltip("Колайдер на меші лише біля лугу (відстань від краю лугу, м). Далекі гори без колайдера, бо недосяжні: швидший старт")]
    public float colliderMargin = 40f;
    [Tooltip("Старий plane/земля: вимкнеться, коли створиться карта")]
    public GameObject oldGround;

    // ---- палітра: базові кольори ----
    const int GrassStart = 0, GrassCount = 3;
    const int DirtStart = 3, DirtCount = 2;
    const int RockStart = 5, RockCount = 6;
    const int SnowStart = 11, SnowCount = 2;

    static readonly Color32[] BaseColors =
    {
        new Color32(78, 118, 46, 255), new Color32(92, 136, 54, 255), new Color32(68, 106, 42, 255),     // трава
        new Color32(112, 86, 58, 255), new Color32(96, 72, 48, 255),                                      // земля
        new Color32(110, 108, 104, 255), new Color32(124, 122, 118, 255), new Color32(96, 94, 92, 255),   // скеля
        new Color32(138, 136, 132, 255), new Color32(104, 100, 96, 255), new Color32(118, 112, 106, 255),
        new Color32(236, 240, 246, 255), new Color32(222, 228, 238, 255),                                 // сніг
    };
    static readonly int BaseCount = BaseColors.Length;

    // затінення верху блока, якщо навколо вищі блоки (кути й підніжжя урвищ стають темнішими)
    static readonly float[] AoMul = { 1f, 0.88f, 0.76f, 0.64f };
    const int AoLevels = 4;
    const int AoStepsHigher = 4;   // сусід вищий хоча б на стільки кроків висоти = затінює

    // шари породи: рядок текстури = відтінок, на стінах повторюється вздовж висоти. Рядок 0 = без зміни (верх блока)
    static readonly float[] StrataMul =
        { 1.00f, 0.96f, 1.03f, 0.92f, 0.98f, 1.05f, 0.94f, 0.90f, 1.01f, 0.97f, 1.04f, 0.93f, 0.99f, 0.91f, 1.02f, 0.95f };
    static int StrataRows => StrataMul.Length;

    Vector3 Origin => world != null ? world.transform.position : transform.position;

    // параметри шуму, виставляються один раз
    float ox, oy, gx, gz, nx, nz, sx, sz;

    void Start()
    {
        // у Start, щоб VoxelWorld уже встиг створити свій матеріал в Awake
        if (world != null) world.voxelGround = false;   // земля тепер меш, споруди на y = 0 тримаються самі
        GenerateTerrain();
    }

    // ---------------- рельєф ----------------

    void GenerateTerrain()
    {
        if (oldGround != null) oldGround.SetActive(false);

        float half = mapSize * 0.5f;
        int n = Mathf.Max(8, Mathf.RoundToInt(mapSize / Mathf.Max(0.25f, mountainBlockSize)));
        float cell = mapSize / n;   // справжній розмір блока, щоб карта вкладалась рівно
        float step = Mathf.Max(0.05f, heightStep);

        ox = seed * 37.17f + 1000f;  oy = seed * 11.93f + 1000f;
        gx = seed * 7.13f + 500f;    gz = seed * 3.91f + 500f;
        nx = seed * 5.31f + 300f;    nz = seed * 2.77f + 300f;
        sx = seed * 1.61f + 700f;    sz = seed * 9.07f + 700f;

        // ---------- висоти у кроках ----------
        var level = new int[n, n];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            float xc = -half + (i + 0.5f) * cell;
            float zc = -half + (j + 0.5f) * cell;
            level[i, j] = Mathf.RoundToInt(HeightAt(xc, zc, half) / step);
        }

        // ---------- матеріал: двовимірна палітра (колір x затінення, шари породи по висоті) ----------
        Material mat = world != null && world.RuntimeMaterial != null
            ? new Material(world.RuntimeMaterial)
            : new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetTexture("_BaseMap", BuildPaletteTexture());
        mat.SetColor("_BaseColor", Color.white);
        mat.SetFloat("_Smoothness", 0f);

        // ---------- меші по ділянках ----------
        Vector3 o = Origin;
        var root = new GameObject("Blocky Terrain");
        root.transform.SetParent(transform, false);
        root.transform.position = new Vector3(o.x - half, o.y, o.z - half);   // нуль рельєфу = верх плоского майданчика

        int cc = Mathf.Max(8, chunkColumns);
        for (int cz = 0; cz < n; cz += cc)
        for (int cx = 0; cx < n; cx += cc)
            BuildChunk(root.transform, mat, level, n, cell, step, half, cx, cz, Mathf.Min(cx + cc, n), Mathf.Min(cz + cc, n));

        if (invisibleCliffWall) BuildCliffWall();
    }

    // Висота рельєфу в метрах у точці (x, z) відносно центру карти
    float HeightAt(float x, float z, float half)
    {
        float dist = Mathf.Sqrt(x * x + z * z);

        // ---- луг: плоский майданчик в центрі, далі м'які горби ----
        float ground = 0f;
        float fade = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(flatRadius, flatRadius + Mathf.Max(1f, flatBlend), dist));
        if (fade > 0f)
        {
            float wl = Mathf.Max(5f, groundWavelength);
            float g = (Mathf.PerlinNoise(x / wl + gx, z / wl + gz) - 0.5f) * 2f
                    + (Mathf.PerlinNoise(x / (wl * 0.25f) + gx + 50f, z / (wl * 0.25f) + gz + 50f) - 0.5f) * 0.6f;
            ground = g * groundAmplitude * fade;
        }

        // ---- маска гір: ширина урвища ходить уздовж краю, тож підніжжя рване ----
        float edge = Mathf.PerlinNoise(x * 0.025f + sx, z * 0.025f + sz);
        float width = cliffWidth * Mathf.Lerp(1f, 0.6f + 0.8f * edge, footRagged);
        float t = Mathf.InverseLerp(meadowRadius, meadowRadius + Mathf.Max(1f, width), dist);
        float m = Mathf.SmoothStep(0f, 1f, t);   // 0 на лузі, 1 у горах
        if (m <= 0f) return ground;

        return ground * (1f - m) + m * MountainAt(x, z, half);
    }

    // Висота гір у метрах (без маски підніжжя)
    float MountainAt(float x, float z, float half)
    {
        float u = x / half * mountainNoiseScale + ox;
        float v = z / half * mountainNoiseScale + oy;

        // викривлення координат: хребти звиваються, а не тягнуться рівними смугами
        float wu = (Mathf.PerlinNoise(u * 0.6f + 17f, v * 0.6f + 3f) - 0.5f) * ridgeWarp;
        float wv = (Mathf.PerlinNoise(u * 0.6f + 91f, v * 0.6f + 55f) - 0.5f) * ridgeWarp;
        u += wu; v += wv;

        float ridge = Mathf.Clamp01(Ridged(u, v) * 1.3f);
        float broad = Mathf.PerlinNoise(u * 0.3f + 7.7f, v * 0.3f + 3.3f);   // великі масиви вищі, перевали нижчі
        float peak = Mathf.Lerp(minPeakHeight, 1f, Mathf.Clamp01(ridge * (0.75f + 0.5f * broad)));
        float h = peak * mountainHeight;

        // дрібний рельєф: чим вища гора, тим він різкіший
        float rugged = Mathf.Lerp(0.3f, 1f, peak);
        float d = (Mathf.PerlinNoise(x * 0.11f + nx, z * 0.11f + nz) - 0.5f) * 2f
                + (Mathf.PerlinNoise(x * 0.37f + nx + 40f, z * 0.37f + nz + 40f) - 0.5f) * 0.7f;
        h += d * surfaceDetail * rugged;

        return Mathf.Max(0f, h);
    }

    // Текстура-палітра: колонка = колір * затінення, рядок = відтінок шару породи
    Texture2D BuildPaletteTexture()
    {
        int w = BaseCount * AoLevels;
        int h = StrataRows;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapModeU = TextureWrapMode.Clamp,
            wrapModeV = TextureWrapMode.Repeat
        };
        var px = new Color32[w * h];
        for (int row = 0; row < h; row++)
        for (int ao = 0; ao < AoLevels; ao++)
        for (int c = 0; c < BaseCount; c++)
        {
            float k = AoMul[ao] * StrataMul[row];
            Color32 b = BaseColors[c];
            px[row * w + ao * BaseCount + c] = new Color32(
                (byte)Mathf.Clamp(Mathf.RoundToInt(b.r * k), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b.g * k), 0, 255),
                (byte)Mathf.Clamp(Mathf.RoundToInt(b.b * k), 0, 255), 255);
        }
        tex.SetPixels32(px);
        tex.Apply();
        return tex;
    }

    float PaletteU(int color, int ao) => (ao * BaseCount + color + 0.5f) / (BaseCount * AoLevels);

    void BuildChunk(Transform parent, Material mat, int[,] level, int n, float cell, float step, float half,
                    int i0, int j0, int i1, int j1)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var tris = new List<int>();

        float stripV = 1f / (Mathf.Max(0.1f, strataThickness) * StrataRows);   // v на метр висоти
        float topV = 0.5f / StrataRows;                                         // рядок 0 = чистий колір

        for (int j = j0; j < j1; j++)
        for (int i = i0; i < i1; i++)
        {
            int L = level[i, j];
            float xc = -half + (i + 0.5f) * cell;
            float zc = -half + (j + 0.5f) * cell;
            float x0 = i * cell, x1 = x0 + cell;
            float z0 = j * cell, z1 = z0 + cell;
            float top = L * step;
            int hash = Hash(i, j);

            // межі кольорів для цього місця: підніжжя і сніг ходять уздовж рельєфу
            float footY = grassLine * mountainHeight + (Mathf.PerlinNoise(xc * 0.04f + sx, zc * 0.04f + sz) - 0.5f) * 5f;
            float snowY = snowLine * mountainHeight + (Mathf.PerlinNoise(xc * 0.03f + sz, zc * 0.03f + sx) - 0.5f) * 2f * snowVariation;

            // сусіди: різниця висот для затінення і крутизни
            int hiCount = 0, maxDiff = 0;
            for (int d = 0; d < 4; d++)
            {
                int ni = i + (d == 0 ? 1 : d == 1 ? -1 : 0);
                int nj = j + (d == 2 ? 1 : d == 3 ? -1 : 0);
                if (ni < 0 || nj < 0 || ni >= n || nj >= n) continue;
                int diff = level[ni, nj] - L;
                if (diff >= AoStepsHigher) hiCount++;
                if (Mathf.Abs(diff) > maxDiff) maxDiff = Mathf.Abs(diff);
            }
            int ao = Mathf.Min(AoLevels - 1, hiCount);

            // верх блока
            int topColor;
            if (top < footY)
            {
                if (maxDiff >= AoStepsHigher) topColor = DirtStart + hash % DirtCount;   // крутий схил: голий ґрунт
                else
                {
                    float p = Mathf.PerlinNoise(xc * 0.2f + nx, zc * 0.2f + nz) * 0.75f
                            + Mathf.PerlinNoise(xc * 1.1f + nx, zc * 1.1f + nz) * 0.25f;
                    topColor = p < 0.38f ? GrassStart + 2 : (p < 0.62f ? GrassStart : GrassStart + 1);
                }
            }
            else if (top < snowY)
            {
                // плями відтінків скелі + трохи випадковості
                float p = Mathf.PerlinNoise(xc * 0.09f + nx + 10f, zc * 0.09f + nz + 10f);
                float r = (hash % 100) / 100f;
                topColor = RockStart + Mathf.Clamp(Mathf.FloorToInt((p * 0.8f + r * 0.2f) * RockCount), 0, RockCount - 1);
            }
            else topColor = SnowStart + hash % SnowCount;

            float tu = PaletteU(topColor, ao);
            var tv = new Vector2(tu, topV);
            AddQuad(verts, normals, uvs, tris,
                new Vector3(x0, top, z0), new Vector3(x0, top, z1), new Vector3(x1, top, z1), new Vector3(x1, top, z0),
                tv, tv, tv, tv);

            // стіни до нижчих сусідів (кожну стіну будує вищий стовпець, дублів немає)
            if (i + 1 < n && level[i + 1, j] < L)
                Wall(verts, normals, uvs, tris, 0, x1, x1, z0, z1, level[i + 1, j] * step, top, hash, footY, snowY, stripV);
            if (i - 1 >= 0 && level[i - 1, j] < L)
                Wall(verts, normals, uvs, tris, 1, x0, x0, z0, z1, level[i - 1, j] * step, top, hash, footY, snowY, stripV);
            if (j + 1 < n && level[i, j + 1] < L)
                Wall(verts, normals, uvs, tris, 2, x0, x1, z1, z1, level[i, j + 1] * step, top, hash, footY, snowY, stripV);
            if (j - 1 >= 0 && level[i, j - 1] < L)
                Wall(verts, normals, uvs, tris, 3, x0, x1, z0, z0, level[i, j - 1] * step, top, hash, footY, snowY, stripV);
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

        // колайдер лише там, куди можна дістатись: далекі гори його не мають
        float minX = i0 * cell - half, maxX = i1 * cell - half;
        float minZ = j0 * cell - half, maxZ = j1 * cell - half;
        float cx = Mathf.Clamp(0f, minX, maxX), cz = Mathf.Clamp(0f, minZ, maxZ);
        if (Mathf.Sqrt(cx * cx + cz * cz) < meadowRadius + colliderMargin)
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
    }

    // Стіна між двома висотами. Ділиться на смуги за кольором: земля внизу, скеля, сніг нагорі.
    // Шари породи малюються текстурою (v росте з висотою), тож додаткових граней немає
    void Wall(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
              int dir, float xa, float xb, float za, float zb, float yLow, float yHigh,
              int hash, float footY, float snowY, float stripV)
    {
        float snowWallY = snowY + 6f;   // на стінах сніг починається вище, ніж на верхах: урвища лишаються кам'яними
        float a = yLow;
        for (int c = 0; c < 3; c++)
        {
            float end = yHigh;
            if (c == 0 && a < footY && footY < yHigh) end = footY;
            else if (c == 0 && a >= footY) continue;
            else if (c == 1 && a < snowWallY && snowWallY < yHigh) end = snowWallY;
            else if (c == 1 && a >= snowWallY) continue;
            else if (c == 2 && a >= yHigh) break;

            float mid = (a + end) * 0.5f;
            int color = mid < footY ? DirtStart + hash % DirtCount
                      : mid < snowWallY ? RockStart + (hash + (int)(mid / 3f)) % RockCount
                      : SnowStart + hash % SnowCount;

            EmitWall(v, nr, uv, t, dir, xa, xb, za, zb, a, end, PaletteU(color, 0), stripV);
            a = end;
            if (a >= yHigh) break;
        }
    }

    void EmitWall(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
                  int dir, float xa, float xb, float za, float zb, float y0, float y1, float u, float stripV)
    {
        var b = new Vector2(u, y0 * stripV);   // низ стіни
        var h = new Vector2(u, y1 * stripV);   // верх стіни
        switch (dir)
        {
            case 0:   // +X
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, za), new Vector3(xa, y1, za), new Vector3(xa, y1, zb), new Vector3(xa, y0, zb), b, h, h, b);
                break;
            case 1:   // -X
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, zb), new Vector3(xa, y1, zb), new Vector3(xa, y1, za), new Vector3(xa, y0, za), b, h, h, b);
                break;
            case 2:   // +Z
                AddQuad(v, nr, uv, t, new Vector3(xb, y0, za), new Vector3(xb, y1, za), new Vector3(xa, y1, za), new Vector3(xa, y0, za), b, h, h, b);
                break;
            default:  // -Z
                AddQuad(v, nr, uv, t, new Vector3(xa, y0, za), new Vector3(xa, y1, za), new Vector3(xb, y1, za), new Vector3(xb, y0, za), b, h, h, b);
                break;
        }
    }

    // Квадрат з плоским освітленням: колір і шар породи беруться з текстури-палітри за UV
    static void AddQuad(List<Vector3> v, List<Vector3> nr, List<Vector2> uv, List<int> t,
                        Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
                        Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector2 uv3)
    {
        int s = v.Count;
        Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0).normalized;

        v.Add(p0); v.Add(p1); v.Add(p2); v.Add(p3);
        for (int k = 0; k < 4; k++) nr.Add(normal);
        uv.Add(uv0); uv.Add(uv1); uv.Add(uv2); uv.Add(uv3);
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

    // ---------------- допоміжне ----------------

    // Хребтовий шум 0..1: гострі гребені, а дрібні деталі зосереджені на самих гребенях
    static float Ridged(float x, float y)
    {
        float sum = 0f, amp = 0.5f, freq = 1f, norm = 0f, weight = 1f;
        for (int o = 0; o < 6; o++)
        {
            float p = Mathf.PerlinNoise(x * freq + o * 17.3f, y * freq + o * 9.1f);
            float r = 1f - Mathf.Abs(2f * p - 1f);
            r *= r;                                  // гостріші піки
            r *= weight;                             // деталь лише там, де вже є гребінь
            weight = Mathf.Clamp01(r * 2f);
            sum += r * amp;
            norm += amp;
            amp *= 0.5f;
            freq *= 2.1f;
        }
        return sum / norm;
    }
}