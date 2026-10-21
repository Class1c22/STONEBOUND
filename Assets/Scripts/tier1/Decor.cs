using System.Collections.Generic;
using UnityEngine;

// Нескінченна генерація декору навколо каменя: валуни, горби землі, кущі, квіти, гриби, камінці.
// Все це звичайні вокселі у VoxelWorld, тому камінь ламає їх так само, як стіну: з уламками,
// які він може всмоктати, і відламаними шматками (зламав стебло — голівка квітки падає).
// Повісь на порожній GameObject, у world перетягни VoxelWorld (rock знайдеться сам).
// Низ світу вокселів = y = 0 (там, де підлога FloorGenerator).
// Розміри задаються в радіусах стартового каменя, тому генерація працює на будь-якому масштабі.
// Клітинка генерується один раз і більше ніколи, тож зламане не відростає, коли повертаєшся.
public class WorldDecorator : MonoBehaviour
{
    [Header("Зв'язки")]
    public VoxelWorld world;
    [Tooltip("Якщо порожньо, шукається сам (після переходу між сценами теж)")]
    public RockAbsorber rock;

    [Header("Нескінченна генерація")]
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
    public float flowerPatchDensity = 1.5f;
    public float mushroomPatchDensity = 0.4f;
    public float pebbleDensity = 4f;

    [Header("Розміри (в радіусах стартового каменя)")]
    public Vector2 boulderRadius = new Vector2(0.5f, 2.0f);
    public Vector2 moundRadius = new Vector2(1.0f, 3.0f);
    public Vector2 bushRadius = new Vector2(0.4f, 0.9f);
    public Vector2 flowerHeight = new Vector2(0.5f, 1.1f);
    public Vector2 mushroomHeight = new Vector2(0.2f, 0.5f);
    public Vector2 pebbleRadius = new Vector2(0.08f, 0.22f);
    [Tooltip("Стеля розміру валуна у вокселях (захист від лагів, якщо камінь великий)")]
    public int maxBoulderVoxels = 36;
    [Range(0f, 1f)] public float mossAmount = 0.35f;

    // ---- кольори (палітра VoxelWorld має 256 місць, беремо небагато) ----
    byte[] rockShades, moss, dirtShades, dirtTop, bushShades, stem, berry, petals, flowerCore,
          mushStem, mushRed, mushBrown, mushDot;

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
        stem        = new[] { C(70, 140, 50) };
        berry       = new[] { C(190, 40, 52), C(120, 50, 150) };
        petals      = new[] { C(230, 60, 70), C(250, 215, 70), C(245, 245, 240), C(170, 90, 210), C(250, 140, 190), C(80, 140, 240) };
        flowerCore  = new[] { C(250, 190, 40) };
        mushStem    = new[] { C(236, 226, 205) };
        mushRed     = new[] { C(200, 48, 44) };
        mushBrown   = new[] { C(140, 92, 60) };
        mushDot     = new[] { C(248, 244, 236) };
        colorsReady = true;
    }

    // ---------- стрімінг ----------

    void Update()
    {
        if (world == null) return;
        if (rock == null)
        {
            findTimer -= Time.deltaTime;
            if (findTimer <= 0f) { findTimer = 0.5f; rock = FindFirstObjectByType<RockAbsorber>(); }
            return;
        }
        if (Time.time - startTime < startDelay) return;
        if (!colorsReady) BuildColors();

        Vector3 c = rock.WorldCenter;
        if (!spawnSet) { spawn = c; spawnSet = true; }

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

    // ---------- клітинка ----------

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
        for (int i = Count(rng, flowerPatchDensity * meadowMod); i > 0; i--) PlaceFlowerPatch(rng, RandCell(rng, cx, cz));
        for (int i = Count(rng, mushroomPatchDensity * Mathf.Lerp(0.5f, 1.5f, 1f - rockiness)); i > 0; i--) PlaceMushrooms(rng, RandCell(rng, cx, cz));
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

    // Скільки вокселів нависання тримає VoxelWorld (щоб капелюшки грибів і пелюстки не падали самі)
    int Overhang(int want) => world.detachFloating ? Mathf.Min(want, world.maxOverhang) : want;

    // ---------- валун ----------

    void PlaceBoulder(System.Random rng, Vector3 pos)
    {
        if (InClearZone(pos)) return;
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
        if (InClearZone(pos)) return;
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
        if (InClearZone(pos)) return;
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

    // ---------- квіти ----------

    void PlaceFlowerPatch(System.Random rng, Vector3 pos)
    {
        if (InClearZone(pos)) return;
        Vector2Int g = ToGrid(pos);
        float patchR = Mathf.Max(2f, Vox(Rand(rng, new Vector2(0.8f, 1.8f)), 2, 24));
        if (world.GetVoxel(new Vector3Int(g.x, 0, g.y)) != 0) return;

        byte main = Pick(petals, rng.Next());
        int count = rng.Next(4, 10);
        for (int i = 0; i < count; i++)
        {
            double a = rng.NextDouble() * Mathf.PI * 2.0;
            float d = Mathf.Sqrt((float)rng.NextDouble()) * patchR;
            int x = g.x + Mathf.RoundToInt(Mathf.Cos((float)a) * d);
            int z = g.y + Mathf.RoundToInt(Mathf.Sin((float)a) * d);
            byte petal = rng.NextDouble() < 0.75 ? main : Pick(petals, rng.Next());
            int h = Vox(Rand(rng, flowerHeight), 3, 14);
            PlaceFlower(rng, x, z, h, petal);
        }
    }

    void PlaceFlower(System.Random rng, int x, int z, int h, byte petal)
    {
        if (world.GetVoxel(new Vector3Int(x, 0, z)) != 0) return;
        byte st = stem[0];

        for (int y = 0; y < h - 1; y++) Put(x, y, z, st);

        // листок збоку (1 піксель нависання, VoxelWorld це тримає)
        if (h >= 4 && Overhang(1) >= 1)
        {
            int side = rng.Next(4);
            int lx = x + (side == 0 ? 1 : side == 1 ? -1 : 0), lz = z + (side == 2 ? 1 : side == 3 ? -1 : 0);
            Put(lx, h / 2, lz, st);
        }

        // голівка: серединка + хрест пелюсток (хрест тримається, якщо дозволене нависання 1)
        int hy = h - 1;
        Put(x, hy, z, flowerCore[0]);
        if (Overhang(1) >= 1)
        {
            Put(x + 1, hy, z, petal); Put(x - 1, hy, z, petal);
            Put(x, hy, z + 1, petal); Put(x, hy, z - 1, petal);
        }
        else Put(x, hy + 1, z, petal);
        if (rng.NextDouble() < 0.4) Put(x, hy + 1, z, petal);
    }

    // ---------- гриби ----------

    void PlaceMushrooms(System.Random rng, Vector3 pos)
    {
        if (InClearZone(pos)) return;
        Vector2Int g = ToGrid(pos);
        int count = rng.Next(1, 5);
        bool red = rng.NextDouble() < 0.6;
        for (int i = 0; i < count; i++)
        {
            int x = g.x + rng.Next(-5, 6), z = g.y + rng.Next(-5, 6);
            int h = Vox(Rand(rng, mushroomHeight), 2, 7);
            int cap = Overhang(h >= 4 ? 2 : 1);
            if (world.GetVoxel(new Vector3Int(x, 0, z)) != 0) continue;

            for (int y = 0; y < h; y++) Put(x, y, z, mushStem[0]);

            byte capId = red ? mushRed[0] : mushBrown[0];
            // капелюшок-ромб: |dx|+|dz| <= cap, тож кожен воксель за кроком нависання від ніжки
            for (int dz = -cap; dz <= cap; dz++)
            for (int dx = -cap; dx <= cap; dx++)
            {
                if (Mathf.Abs(dx) + Mathf.Abs(dz) > cap) continue;
                byte id = capId;
                if (red && (dx != 0 || dz != 0) && Hash(x + dx, h, z + dz, 5) % 4 == 0) id = mushDot[0];
                Put(x + dx, h, z + dz, id);
            }
            if (cap >= 2) Put(x, h + 1, z, capId);
        }
    }

    // ---------- камінці ----------

    void PlacePebble(System.Random rng, Vector3 pos)
    {
        if (InClearZone(pos)) return;
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
}