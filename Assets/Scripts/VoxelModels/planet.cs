using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Рівень-планета: куляста земля, по якій котиться камінь. Повісь на порожній GameObject, VoxelWorld і камінь знайдуться самі.
// VoxelWorld має стояти на (0,0,0) з масштабом 1. FloorGenerator і VillageGenerator у цій сцені вимкни.
//
// Як це влаштовано:
//  * ОСНОВА планети (Bedrock) це гладка куля з SphereCollider і камінно-земляною текстурою. Її немає у VoxelWorld,
//    тому камінь її зламати не може. Коли вокселі зверху зламано, лишається видно каміння й землю основи.
//  * КОРА це тонкий шар справжніх вокселів на основі (до Crust Max вокселів): країни, пляжі, пагорби.
//    Камінь ламає лише її. Вокселі кори будуються по блоках лише поблизу каменя (як у VillageGenerator).
//  * МОРЕ це прозора водяна куля трохи вищої за основу: де кори нема, видно дно, а вода зверху.
//    Камінь у воді сповільнюється (Water Drag).
//  * КРАЇНИ це діаграма Вороного на кулі. Кожна країна має біом (трава, пустеля, сніг, осінь), на межах гравійна смуга.
//  * ГРАВІТАЦІЯ радіальна: до центру планети. Rigidbody каменя отримує useGravity = false і силу вбік центру.
//
// Усі розміри у ВОКСЕЛЯХ, тож планета масштабується разом із world.voxelSize.
// УВАГА: центр планети піднято так, щоб усі вокселі мали y > 0 (нижній полюс близько y = 2).
// Якщо VoxelWorld має обмеження по висоті, зменшуй Bedrock Radius (потрібна висота приблизно 2 * (Bedrock + Crust)).
public class PlanetGenerator : MonoBehaviour
{
    [Header("Зв'язки")]
    public VoxelWorld world;
    public int seed = 4004;

    [Header("Планета (у вокселях)")]
    [Tooltip("Радіус незламної основи")]
    public int bedrockRadius = 200;
    [Tooltip("Максимальна товщина вокселної кори над основою")]
    public int crustMax = 10;
    [Tooltip("Висота води над основою (у вокселях). Суша має товщину більшу за це")]
    public float seaHeight = 2.2f;
    [Tooltip("Більше = більше моря")]
    [Range(0.3f, 0.7f)] public float seaLevel = 0.46f;
    [Tooltip("Розмір материків (більше = дрібніші)")]
    public float continentFreq = 2.2f;
    [Tooltip("Частота пагорбів")]
    public float hillFreq = 9f;
    public int countryCount = 14;
    [Tooltip("Ширина смуги кордону (на одиничній кулі)")]
    public float borderWidth = 0.04f;

    [Header("Гравітація")]
    [Tooltip("Прискорення до центру планети, м/с²")]
    public float gravity = 30f;
    [Tooltip("Опір води для каменя")]
    public float waterDrag = 1.5f;
    [Range(0f, 2f)] public float friction = 0.6f;

    [Header("Стрімінг")]
    public float spawnRadius = 120f;
    public float spawnRadiusInRockRadii = 60f;
    public int blockSize = 24;
    public int voxelsPerFrame = 6000;

    [Header("Вигляд")]
    public Color waterColor = new Color(0.2f, 0.5f, 0.85f, 0.7f);
    public int textureResolution = 32;

    // ---------- стан ----------

    struct VoxelOp { public Vector3Int pos; public byte id; }
    struct Col { public bool sea, border, beach; public int biome; public float thick; }

    readonly List<VoxelOp> ops = new List<VoxelOp>();
    readonly HashSet<Vector3Int> built = new HashSet<Vector3Int>();

    float vs;
    int Rb, B;
    Vector3 C;                       // центр планети у вокселях
    Vector3 off1, off2;
    Vector3[] seeds;
    int[] biomeOf;

    RockAbsorber rock;
    Rigidbody rb;
    Vector3 rockV;                   // позиція каменя у вокселях

    byte[][] topSets, soilSets;
    byte[] stoneSet, sandSet, borderSet;

    // ---------- публічне ----------

    public Vector3 CenterWorld
    {
        get { return world != null ? world.transform.TransformPoint(C * vs) : transform.position; }
    }

    // Куди "вгору" у цій точці світу (від центру планети). Потрібно камері й керуванню каменя
    public Vector3 UpAt(Vector3 worldPos)
    {
        return (worldPos - CenterWorld).normalized;
    }

    // Північний полюс: сюди став камінь на старті (там завжди суша першої країни)
    public Vector3 StartPoint
    {
        get
        {
            if (world == null || vs <= 0f) return transform.position;
            return world.transform.TransformPoint((C + Vector3.up * (Rb + crustMax + 1)) * vs);
        }
    }

    // ---------- старт ----------

    void Start()
    {
        if (world == null) world = FindFirstObjectByType<VoxelWorld>();
        if (world == null)
        {
            Debug.LogWarning("[PlanetGenerator] Не знайдено VoxelWorld", this);
            enabled = false;
            return;
        }
        world.voxelGround = false;

        vs = world.voxelSize;
        Rb = Mathf.Max(40, bedrockRadius);
        B = Mathf.Max(8, blockSize);
        C = new Vector3(0.5f, Rb + crustMax + 2.5f, 0.5f);

        var g = new System.Random(seed);
        off1 = new Vector3((float)g.NextDouble(), (float)g.NextDouble(), (float)g.NextDouble()) * 100f;
        off2 = new Vector3((float)g.NextDouble(), (float)g.NextDouble(), (float)g.NextDouble()) * 100f;

        // країни: рівномірні точки на кулі (спіраль Фібоначчі) з легким зсувом; країна 0 біля північного полюса
        int n = Mathf.Max(3, countryCount);
        seeds = new Vector3[n];
        biomeOf = new int[n];
        for (int i = 0; i < n; i++)
        {
            float y = 1f - 2f * (i + 0.5f) / n;
            float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
            float phi = i * 2.39996f + (float)g.NextDouble() * 0.5f;
            Vector3 p = new Vector3(r * Mathf.Cos(phi), y, r * Mathf.Sin(phi));
            p += new Vector3((float)g.NextDouble() - 0.5f, (float)g.NextDouble() - 0.5f, (float)g.NextDouble() - 0.5f) * 0.25f;
            seeds[i] = p.normalized;
            biomeOf[i] = g.Next(4);
        }
        biomeOf[0] = 0;

        InitColors();
        BuildBedrock();
        BuildWater();
        StartCoroutine(Worker());
    }

    byte Cl(int r, int g, int b)
    {
        return world.GetOrAddColor(new Color32((byte)r, (byte)g, (byte)b, 255));
    }

    void InitColors()
    {
        topSets = new[]
        {
            new[] { Cl(95, 160, 70), Cl(110, 175, 80), Cl(80, 140, 62) },       // трава
            new[] { Cl(222, 196, 130), Cl(212, 184, 118), Cl(230, 206, 142) },  // пустеля
            new[] { Cl(245, 247, 250), Cl(232, 238, 244), Cl(250, 251, 252) },  // сніг
            new[] { Cl(190, 160, 60), Cl(176, 140, 52), Cl(200, 120, 50) },     // осінь
        };
        soilSets = new[]
        {
            new[] { Cl(128, 100, 70), Cl(120, 94, 66) },
            new[] { Cl(196, 164, 106), Cl(184, 152, 96) },
            new[] { Cl(120, 100, 84), Cl(110, 92, 78) },
            new[] { Cl(118, 90, 62), Cl(106, 80, 56) },
        };
        stoneSet = new[] { Cl(150, 150, 146), Cl(124, 124, 120), Cl(104, 104, 102) };
        sandSet = new[] { Cl(226, 206, 150), Cl(214, 192, 134) };
        borderSet = new[] { Cl(110, 96, 78), Cl(120, 106, 88) };
    }

    // ---------- основа й море ----------

    Mesh SphereMesh(float radius, int seg, int rings)
    {
        var verts = new Vector3[(seg + 1) * (rings + 1)];
        var norms = new Vector3[verts.Length];
        var uvs = new Vector2[verts.Length];
        for (int i = 0; i <= rings; i++)
        {
            float th = Mathf.PI * i / rings;
            for (int j = 0; j <= seg; j++)
            {
                float ph = 2f * Mathf.PI * j / seg;
                Vector3 v = new Vector3(Mathf.Sin(th) * Mathf.Cos(ph), Mathf.Cos(th), Mathf.Sin(th) * Mathf.Sin(ph));
                int k = i * (seg + 1) + j;
                verts[k] = v * radius;
                norms[k] = v;
                uvs[k] = new Vector2((float)j / seg, (float)i / rings);
            }
        }
        var tris = new int[seg * rings * 6];
        int t = 0;
        for (int i = 0; i < rings; i++)
        for (int j = 0; j < seg; j++)
        {
            int a = i * (seg + 1) + j, b = a + seg + 1;
            tris[t++] = a; tris[t++] = b; tris[t++] = a + 1;
            tris[t++] = a + 1; tris[t++] = b; tris[t++] = b + 1;
        }
        var m = new Mesh { name = "PlanetSphere" };
        m.vertices = verts;
        m.normals = norms;
        m.uv = uvs;
        m.triangles = tris;
        m.RecalculateBounds();
        return m;
    }

    void BuildBedrock()
    {
        float R = Rb * vs;
        var go = new GameObject("Bedrock");
        go.transform.SetParent(transform, false);
        go.transform.position = CenterWorld;

        go.AddComponent<MeshFilter>().sharedMesh = SphereMesh(R * 0.9995f, 128, 64);
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = BedrockMaterial();
        rend.shadowCastingMode = ShadowCastingMode.Off;

        // гладкий колайдер: ідеальна куля, камінь по ній котиться без рифлення
        var sc = go.AddComponent<SphereCollider>();
        sc.radius = R;
        sc.sharedMaterial = new PhysicsMaterial("Bedrock")
        {
            dynamicFriction = friction,
            staticFriction = friction,
            bounciness = 0f,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };
    }

    Material BedrockMaterial()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) lit = Shader.Find("Standard");
        var m = new Material(lit) { name = "BedrockMat" };

        // піксельна текстура: каміння й земля вперемішку
        int n = Mathf.Max(8, textureResolution);
        var tex = new Texture2D(n, n, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Point };
        var rng = new System.Random(seed + 5);
        var px = new Color[n * n];
        Color[] pal =
        {
            new Color32(150, 150, 146, 255), new Color32(124, 124, 120, 255), new Color32(104, 104, 102, 255),
            new Color32(128, 100, 70, 255), new Color32(110, 84, 58, 255)
        };
        for (int i = 0; i < px.Length; i++) px[i] = pal[rng.Next(pal.Length)];
        tex.SetPixels(px);
        tex.Apply(true);

        if (m.HasProperty("_BaseMap")) { m.SetTexture("_BaseMap", tex); m.SetTextureScale("_BaseMap", new Vector2(140f, 70f)); }
        if (m.HasProperty("_MainTex")) { m.SetTexture("_MainTex", tex); m.SetTextureScale("_MainTex", new Vector2(140f, 70f)); }
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
        return m;
    }

    void BuildWater()
    {
        float R = (Rb + seaHeight) * vs;
        var go = new GameObject("Sea");
        go.transform.SetParent(transform, false);
        go.transform.position = CenterWorld;
        go.AddComponent<MeshFilter>().sharedMesh = SphereMesh(R, 128, 64);
        var rend = go.AddComponent<MeshRenderer>();
        rend.sharedMaterial = WaterMaterial();
        rend.shadowCastingMode = ShadowCastingMode.Off;
        rend.receiveShadows = false;
        // колайдера немає: камінь заходить у воду
    }

    Material WaterMaterial()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        var m = new Material(lit != null ? lit : Shader.Find("Standard")) { name = "SeaMat" };
        if (lit != null)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            m.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            m.SetInt("_ZWrite", 0);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
            m.SetColor("_BaseColor", waterColor);
            m.SetFloat("_Smoothness", 0.6f);
        }
        return m;
    }

    // ---------- шум ----------

    float N3(Vector3 p)
    {
        int xi = Mathf.FloorToInt(p.x), yi = Mathf.FloorToInt(p.y), zi = Mathf.FloorToInt(p.z);
        float fx = p.x - xi, fy = p.y - yi, fz = p.z - zi;
        fx = fx * fx * (3f - 2f * fx); fy = fy * fy * (3f - 2f * fy); fz = fz * fz * (3f - 2f * fz);
        float c000 = H(xi, yi, zi), c100 = H(xi + 1, yi, zi), c010 = H(xi, yi + 1, zi), c110 = H(xi + 1, yi + 1, zi);
        float c001 = H(xi, yi, zi + 1), c101 = H(xi + 1, yi, zi + 1), c011 = H(xi, yi + 1, zi + 1), c111 = H(xi + 1, yi + 1, zi + 1);
        float x00 = Mathf.Lerp(c000, c100, fx), x10 = Mathf.Lerp(c010, c110, fx);
        float x01 = Mathf.Lerp(c001, c101, fx), x11 = Mathf.Lerp(c011, c111, fx);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, fy), Mathf.Lerp(x01, x11, fy), fz);
    }

    float H(int x, int y, int z) { return Hash01(x + seed, y, z); }

    float Fbm(Vector3 p) { return N3(p) * 0.65f + N3(p * 2.03f) * 0.35f; }

    // ---------- карта планети ----------

    // Що лежить у напрямку n (одиничний вектор від центру)
    Col Sample(Vector3 n)
    {
        var c = new Col();
        float land = Fbm(n * continentFreq + off1);
        land += Mathf.Clamp01((n.y - 0.93f) / 0.05f) * 0.6f;      // біля північного полюса завжди суша
        if (land < seaLevel) { c.sea = true; return c; }

        float d1 = 9f, d2 = 9f;
        int i1 = 0;
        for (int i = 0; i < seeds.Length; i++)
        {
            float d = (n - seeds[i]).magnitude;
            if (d < d1) { d2 = d1; d1 = d; i1 = i; }
            else if (d < d2) d2 = d;
        }
        c.biome = biomeOf[i1];
        if (n.y < -0.85f) c.biome = 2;                             // сніг на південному полюсі
        if (n.y > 0.93f) c.biome = 0;                              // стартова країна зелена
        c.border = (d2 - d1) < borderWidth;

        float coast = Mathf.Clamp01((land - seaLevel) / 0.05f);
        float hills = Fbm(n * hillFreq + off2);
        c.thick = 2.6f + coast * (1.4f + hills * (crustMax - 4.6f));
        c.beach = c.thick < 3.6f && c.biome != 2;
        return c;
    }

    // ---------- стрімінг ----------

    IEnumerator Worker()
    {
        for (int i = 0; i < 4; i++) yield return null;

        while (true)
        {
            if (!UpdateRock()) { yield return null; continue; }

            float R = Mathf.Max(spawnRadius, rock.Radius * spawnRadiusInRockRadii) / vs;
            Vector3Int rb0 = new Vector3Int(Mathf.FloorToInt(rockV.x / B), Mathf.FloorToInt(rockV.y / B), Mathf.FloorToInt(rockV.z / B));
            int k = Mathf.CeilToInt(R / B) + 1;

            bool found = false;
            Vector3Int best = default;
            float bestD = float.MaxValue;
            for (int dz = -k; dz <= k; dz++)
            for (int dy = -k; dy <= k; dy++)
            for (int dx = -k; dx <= k; dx++)
            {
                var b = new Vector3Int(rb0.x + dx, rb0.y + dy, rb0.z + dz);
                if (built.Contains(b)) continue;
                Vector3 bc = new Vector3(b.x + 0.5f, b.y + 0.5f, b.z + 0.5f) * B;
                float dr = (bc - C).magnitude;
                if (dr > Rb + crustMax + B || dr < Rb - B) continue;   // блок не зачіпає кору
                float dd = (bc - rockV).magnitude;
                if (dd > R + B) continue;
                if (dd < bestD) { bestD = dd; best = b; found = true; }
            }
            if (!found) { yield return null; continue; }

            built.Add(best);
            BuildBlock(best);

            int idx = 0;
            while (idx < ops.Count)
            {
                int end = Mathf.Min(ops.Count, idx + Mathf.Max(100, voxelsPerFrame));
                for (; idx < end; idx++)
                {
                    VoxelOp op = ops[idx];
                    if (world.GetVoxel(op.pos) == 0) world.SetVoxel(op.pos, op.id);
                }
                yield return null;
            }
            ops.Clear();
            yield return null;
        }
    }

    bool UpdateRock()
    {
        if (rock == null)
        {
            rock = FindFirstObjectByType<RockAbsorber>();
            if (rock == null) return false;
            rb = rock.GetComponentInParent<Rigidbody>();
            if (rb != null) rb.useGravity = false;
        }
        Vector3 l = world.transform.InverseTransformPoint(rock.WorldCenter);
        rockV = l / vs;
        return true;
    }

    void BuildBlock(Vector3Int b)
    {
        int x0 = b.x * B, y0 = b.y * B, z0 = b.z * B;
        for (int z = z0; z < z0 + B; z++)
        for (int y = y0; y < y0 + B; y++)
        for (int x = x0; x < x0 + B; x++)
        {
            Vector3 p = new Vector3(x + 0.5f, y + 0.5f, z + 0.5f) - C;
            float d = p.magnitude;
            float h = d - Rb;
            if (h < -0.5f || h > crustMax) continue;

            Col c = Sample(p / d);
            if (c.sea) continue;                  // океан: лише дно (основа) і вода
            if (h >= c.thick) continue;

            int depth = h < 0f ? 99 : Mathf.FloorToInt(c.thick - h);
            byte id;
            if (depth == 0)
            {
                if (c.border) id = Pick(borderSet, Hash(x, y, z));
                else if (c.beach) id = Pick(sandSet, Hash(x, y, z));
                else id = Pick(topSets[c.biome], Hash(x, y, z));
            }
            else
            {
                // під верхнім шаром: земля з камінцями (саме вони видні, коли кору зламано)
                id = Hash01(x, y, z) < 0.2f ? Pick(stoneSet, Hash(x, 1, z)) : Pick(soilSets[c.biome], Hash(x, 2, z));
            }
            ops.Add(new VoxelOp { pos = new Vector3Int(x, y, z), id = id });
        }
    }

    // ---------- фізика: радіальна гравітація ----------

    void FixedUpdate()
    {
        if (rock == null || rb == null) return;
        rb.useGravity = false;

        Vector3 to = CenterWorld - rb.worldCenterOfMass;
        float dist = to.magnitude;
        if (dist < 0.01f) return;
        rb.AddForce(to / dist * gravity, ForceMode.Acceleration);

        // у воді камінь сповільнюється
        float waterR = (Rb + seaHeight) * vs;
        if (dist < waterR + rock.Radius * 0.5f)
            rb.AddForce(-rb.linearVelocity * waterDrag, ForceMode.Acceleration);
    }

    // ---------- допоміжне ----------

    static byte Pick(byte[] set, int h)
    {
        return set[(h & 0x7fffffff) % set.Length];
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