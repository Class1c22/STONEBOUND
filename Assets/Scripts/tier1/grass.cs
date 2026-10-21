using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;

// Воксельна трава рівня 1: нескінченне поле навколо каменя, травинки розміром з дерева.
// - поле генерується чанками навколо каменя і підвантажується далі, куди б він не їхав (без кінця);
//   чанки позаду вивантажуються. Той самий seed = та сама трава в тому самому місці;
// - травинка = стовпчик із кубів, малюється GPU-інстансингом без кастомного шейдера (матеріали URP/Lit);
// - камінь ламає травинку дотиком, а коли затиснута E, травинки в зоні всмоктування теж розпадаються;
// - зламана травинка розсипається на фізичні кубики (Rigidbody + Absorbable), точно як уламки VoxelWorld:
//   камінь всмоктує їх по E (RockAbsorber.Suck) і росте.
public class GrassField : MonoBehaviour
{
    [Header("Масштаб")]
    [Tooltip("Камінь, від розміру якого рахується трава та уламки")]
    public RockAbsorber rock;
    public bool scaleFromRock = true;
    [Tooltip("Розмір вокселя трави = початковий радіус каменя × це число. Було 2.5, ×5 = 12.5. Висота травинки = воксель × кількість вокселів")]
    public float voxelSizeInRockRadii = 12.5f;
    [Tooltip("Розмір вокселя у метрах, якщо камінь не береться в розрахунок")]
    public float voxelSize = 6f;

    [Header("Нескінченне поле")]
    public int seed = 1;
    [Tooltip("Розмір чанка в клітинках сітки")]
    public int chunkCells = 10;
    [Tooltip("Скільки чанків навколо каменя тримати живими (радіус). Більше = далі видно, але важче")]
    public int viewChunks = 3;
    [Tooltip("Скільки нових чанків генерувати за кадр (перше заповнення йде одразу все)")]
    public int chunksPerFrame = 2;
    [Tooltip("Частка клітинок сітки, у яких росте трава (1 = суцільно)")]
    [Range(0f, 1f)] public float coverage = 0.75f;
    [Tooltip("Секунд очікування перед стартом (поки воксельний світ створить колайдери)")]
    public float startDelay = 0.5f;

    [Header("Земля")]
    [Tooltip("Шукати висоту землі променем униз. Вимкни, якщо землі без колайдера: тоді трава стоїть на висоті цього об'єкта")]
    public bool useRaycast = true;
    public LayerMask groundMask = ~0;
    [Tooltip("З якої висоти над об'єктом кидати промінь вниз. Де землі вище цього (високі гори), трава не росте")]
    public float rayHeight = 60f;
    [Tooltip("Максимальний ухил поверхні, градусів")]
    [Range(0f, 90f)] public float maxSlope = 45f;

    [Header("Травинка (у вокселях)")]
    public int minVoxels = 6;
    public int maxVoxels = 10;
    [Tooltip("Товщина стовпчика, частка вокселя")]
    [Range(0.1f, 1f)] public float thickness = 0.6f;
    [Tooltip("Товщина верхнього кубика, частка вокселя")]
    [Range(0.1f, 1f)] public float tipThickness = 0.35f;

    [Header("Матеріали")]
    [Tooltip("Свої матеріали по смугах знизу вгору (Color Bands штук). Якщо порожньо, створяться автоматично")]
    public Material[] levelMaterials;
    [Range(1, 6)] public int colorBands = 3;
    public Color baseColor = new Color(0.10f, 0.30f, 0.08f);
    public Color tipColor = new Color(0.55f, 0.82f, 0.25f);
    public bool receiveShadows = true;

    [Header("Ламання і всмоктування")]
    [Tooltip("Камінь ламає траву, якої торкається")]
    public bool breakable = true;
    [Tooltip("Радіус ламання дотиком = радіус каменя × це число")]
    public float breakRadiusFactor = 1f;
    [Tooltip("Поки затиснута E, травинки в зоні всмоктування розпадаються на кубики, які камінь втягує")]
    public bool suckBreaksGrass = true;
    [Tooltip("Яка частка радіуса всмоктування (RockAbsorber.suckRadius) руйнує траву по E")]
    [Range(0.1f, 1f)] public float suckBreakFactor = 0.8f;

    [Header("Уламки-кубики (фізичні, як у VoxelWorld)")]
    public bool debris = true;
    [Tooltip("Скільки кубиків дає одна зламана травинка")]
    public int cubesPerBlade = 30;
    [Tooltip("Ребро кубика = початковий радіус каменя × це число (кубик має бути досить малий, щоб камінь міг його всмоктати)")]
    public float debrisSizeInRockRadii = 0.45f;
    [Tooltip("Швидкість розльоту")]
    public float debrisSpread = 4f;
    [Tooltip("Розмір пулу кубиків (найстаріші використовуються повторно, як у VoxelWorld)")]
    public int maxDebris = 1500;
    [Tooltip("Стеля нових кубиків за кадр (захист від просадки FPS)")]
    public int maxDebrisPerFrame = 400;
    [Tooltip("Множник об'єму, який кубик додає каменю при всмоктуванні (1 = ребро в кубі)")]
    public float absorbVolumeFactor = 1f;

    // Скільки травинок зламано за кадр (для звуку, ефектів)
    public event System.Action<int> BladesBroken;
    public int TotalBroken { get; private set; }

    class Batch
    {
        public int variant;
        public Matrix4x4[] matrices;
        public int count;          // скільки травинок живих
    }

    class Chunk
    {
        public bool empty = true;
        public Bounds bounds;
        public List<Batch> batches = new List<Batch>();
    }

    const int MaxBatch = 1023;

    readonly Dictionary<Vector2Int, Chunk> chunks = new Dictionary<Vector2Int, Chunk>();
    readonly List<Vector2Int> pending = new List<Vector2Int>();
    readonly List<Vector2Int> removeKeys = new List<Vector2Int>();
    Vector2Int lastCenter;
    bool streamInit, initialFill, built;

    Mesh[] meshes;
    int[] variantHeights;          // висота кожного варіанта травинки у вокселях
    Material[] materials;
    float vs;                      // поточний розмір вокселя трави
    float findTimer;
    Plane[] planes = new Plane[6];

    Rigidbody[] debrisPool;
    int debrisIndex, spawnedThisFrame;
    Transform debrisRoot;
    static readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    IEnumerator Start()
    {
        if (startDelay > 0f) yield return new WaitForSecondsRealtime(startDelay);
        else yield return null;
        Physics.SyncTransforms();
        Setup();
    }

    [ContextMenu("Rebuild")]
    public void Setup()
    {
        minVoxels = Mathf.Max(1, minVoxels);
        maxVoxels = Mathf.Max(minVoxels, maxVoxels);
        chunkCells = Mathf.Max(1, chunkCells);
        vs = ResolveVoxelSize();

        PrepareMaterials();

        int variants = Mathf.Clamp(maxVoxels - minVoxels + 1, 1, 4);
        variantHeights = new int[variants];
        meshes = new Mesh[variants];
        for (int k = 0; k < variants; k++)
        {
            float t = variants == 1 ? 0f : k / (float)(variants - 1);
            variantHeights[k] = Mathf.RoundToInt(Mathf.Lerp(minVoxels, maxVoxels, t));
            meshes[k] = BuildBladeMesh(variantHeights[k], thickness, tipThickness, colorBands);
        }

        chunks.Clear();
        pending.Clear();
        streamInit = false;
        initialFill = true;
        built = true;

        Debug.Log("[GrassField] Voxel Size = " + vs.ToString("0.##") + " м, висота травинки " +
                  (minVoxels * vs).ToString("0") + "-" + (maxVoxels * vs).ToString("0") + " м.", this);
    }

    float ResolveVoxelSize()
    {
        // після переходу між сценами заглушка каменя знищується, тож шукаємо актуальний
        if (scaleFromRock && rock == null) rock = FindFirstObjectByType<RockAbsorber>();
        if (scaleFromRock && rock != null)
            return Mathf.Max(0.01f, rock.StartRadius * voxelSizeInRockRadii);
        return Mathf.Max(0.01f, voxelSize);
    }

    void PrepareMaterials()
    {
        materials = new Material[colorBands];
        Shader lit = null;

        for (int i = 0; i < colorBands; i++)
        {
            if (levelMaterials != null && i < levelMaterials.Length && levelMaterials[i] != null)
            {
                materials[i] = levelMaterials[i];
            }
            else
            {
                if (lit == null)
                {
                    lit = Shader.Find("Universal Render Pipeline/Lit");
                    if (lit == null)
                    {
                        Debug.LogError("[GrassField] Не знайдено URP/Lit. Призначте матеріали вручну в Level Materials.", this);
                        lit = Shader.Find("Standard");
                    }
                }
                var m = new Material(lit) { name = "GrassBand" + i };
                Color c = Color.Lerp(baseColor, tipColor, colorBands > 1 ? (float)i / (colorBands - 1) : 1f);
                if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
                if (m.HasProperty("_Color")) m.SetColor("_Color", c);
                if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0f);
                if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
                materials[i] = m;
            }
            materials[i].enableInstancing = true;
        }
    }

    // ---------- нескінченне підвантаження ----------

    Vector3 StreamFocus()
    {
        if (rock == null)
        {
            findTimer -= Time.deltaTime;
            if (findTimer <= 0f)
            {
                findTimer = 0.5f;
                rock = FindFirstObjectByType<RockAbsorber>();
            }
        }
        if (rock != null) return rock.WorldCenter;
        Camera cam = Camera.main;
        return cam != null ? cam.transform.position : transform.position;
    }

    void UpdateStreaming()
    {
        Vector3 focus = StreamFocus();
        float cw = chunkCells * vs;
        var center = new Vector2Int(Mathf.FloorToInt(focus.x / cw), Mathf.FloorToInt(focus.z / cw));

        if (!streamInit || center != lastCenter)
        {
            RefreshPending(center);
            lastCenter = center;
            streamInit = true;
        }

        int budget = initialFill ? int.MaxValue : Mathf.Max(1, chunksPerFrame);
        while (budget > 0 && pending.Count > 0)
        {
            Vector2Int key = pending[0];
            pending.RemoveAt(0);
            if (chunks.ContainsKey(key)) continue;
            chunks[key] = GenerateChunk(key.x, key.y);
            budget--;
        }
        if (pending.Count == 0) initialFill = false;
    }

    // Вивантажує чанки, що лишились позаду, і складає чергу нових (ближчі першими)
    void RefreshPending(Vector2Int center)
    {
        float keep = viewChunks + 1.5f;
        removeKeys.Clear();
        foreach (var kv in chunks)
        {
            float dx = kv.Key.x - center.x, dz = kv.Key.y - center.y;
            if (dx * dx + dz * dz > keep * keep) removeKeys.Add(kv.Key);
        }
        foreach (var k in removeKeys) chunks.Remove(k);

        pending.Clear();
        float want = viewChunks + 0.5f;
        for (int dz = -viewChunks - 1; dz <= viewChunks + 1; dz++)
        for (int dx = -viewChunks - 1; dx <= viewChunks + 1; dx++)
        {
            if (dx * dx + dz * dz > want * want) continue;
            var key = new Vector2Int(center.x + dx, center.y + dz);
            if (!chunks.ContainsKey(key)) pending.Add(key);
        }
        pending.Sort((a, b) =>
        {
            float da = (a.x - center.x) * (a.x - center.x) + (a.y - center.y) * (a.y - center.y);
            float db = (b.x - center.x) * (b.x - center.x) + (b.y - center.y) * (b.y - center.y);
            return da.CompareTo(db);
        });
    }

    bool SampleGround(float x, float z, float minNormalY, out float y)
    {
        y = transform.position.y;
        if (!useRaycast) return true;

        Vector3 from = new Vector3(x, transform.position.y + rayHeight, z);
        int n = Physics.RaycastNonAlloc(from, Vector3.down, hitBuffer, rayHeight * 2f, groundMask, QueryTriggerInteraction.Ignore);

        // найближче влучання, що не є рухомим тілом (уламки, камінь, шматки не рахуємо землею)
        float best = float.MaxValue, normalY = 0f;
        bool found = false;
        for (int i = 0; i < n; i++)
        {
            RaycastHit h = hitBuffer[i];
            if (h.collider.attachedRigidbody != null) continue;
            if (h.distance < best)
            {
                best = h.distance;
                y = h.point.y;
                normalY = h.normal.y;
                found = true;
            }
        }
        return found && normalY >= minNormalY;
    }

    Chunk GenerateChunk(int cx, int cz)
    {
        var chunk = new Chunk();
        int variants = meshes.Length;
        float minNormalY = Mathf.Cos(maxSlope * Mathf.Deg2Rad);

        var rng = new System.Random(seed * 73856093 ^ cx * 19349663 ^ cz * 83492791);
        var lists = new List<Matrix4x4>[variants];
        for (int k = 0; k < variants; k++) lists[k] = new List<Matrix4x4>();
        int total = 0;
        float minY = float.MaxValue, maxY = float.MinValue;

        for (int lx = 0; lx < chunkCells; lx++)
        for (int lz = 0; lz < chunkCells; lz++)
        {
            int ix = cx * chunkCells + lx;
            int iz = cz * chunkCells + lz;

            // порядок викликів rng сталий, тож результат детермінований
            double roll = rng.NextDouble();
            int variant = rng.Next(variants);
            if (roll > coverage) continue;

            float px = (ix + 0.5f) * vs;
            float pz = (iz + 0.5f) * vs;
            if (!SampleGround(px, pz, minNormalY, out float py)) continue;
            py -= vs * 0.1f;   // трохи в землю, щоб не було щілин

            lists[variant].Add(Matrix4x4.TRS(new Vector3(px, py, pz), Quaternion.identity, Vector3.one * vs));
            total++;
            minY = Mathf.Min(minY, py);
            maxY = Mathf.Max(maxY, py + variantHeights[variants - 1] * vs);
        }

        if (total == 0) return chunk;   // порожній чанк теж запам'ятовуємо, щоб не перераховувати

        float cw = chunkCells * vs;
        chunk.empty = false;
        chunk.bounds = new Bounds(
            new Vector3((cx + 0.5f) * cw, (minY + maxY) * 0.5f, (cz + 0.5f) * cw),
            new Vector3(cw + vs, maxY - minY + vs, cw + vs));

        for (int k = 0; k < variants; k++)
        {
            var list = lists[k];
            for (int s = 0; s < list.Count; s += MaxBatch)
            {
                int n = Mathf.Min(MaxBatch, list.Count - s);
                var arr = new Matrix4x4[n];
                list.CopyTo(s, arr, 0, n);
                chunk.batches.Add(new Batch { variant = k, matrices = arr, count = n });
            }
        }
        return chunk;
    }

    // ---------- ламання ----------

    // Ламає травинки, яких торкається камінь (або які в зоні всмоктування при E).
    // Зламана травинка міняється місцями з останньою в масиві і зникає (count--).
    void BreakAroundRock()
    {
        if (rock == null) return;

        Vector3 c = rock.WorldCenter;
        float r = rock.Radius * breakRadiusFactor;

        var kb = Keyboard.current;
        if (suckBreaksGrass && kb != null && kb.eKey.isPressed)
            r = Mathf.Max(r, rock.suckRadius * rock.SizeRatio * suckBreakFactor);
        float r2 = r * r;

        Vector3 rockVel = Vector3.zero;
        var rockBody = rock.GetComponent<Rigidbody>();
        if (rockBody != null) rockVel = rockBody.linearVelocity;

        int broken = 0;
        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty || chunk.bounds.SqrDistance(c) > r2) continue;

            foreach (var b in chunk.batches)
            {
                if (b.count == 0) continue;
                int heightVoxels = variantHeights[b.variant];
                var m = b.matrices;

                for (int i = b.count - 1; i >= 0; i--)
                {
                    Vector3 p = m[i].GetColumn(3);
                    float reach = r + thickness * vs * 0.5f;
                    float y = Mathf.Clamp(c.y, p.y, p.y + heightVoxels * vs);   // найближча точка стовпчика
                    float dx = c.x - p.x, dy = c.y - y, dz = c.z - p.z;
                    if (dx * dx + dy * dy + dz * dz > reach * reach) continue;

                    if (debris) SpawnCubes(p, heightVoxels, c, rockVel);

                    m[i] = m[b.count - 1];
                    b.count--;
                    broken++;
                }
            }
        }

        if (broken > 0)
        {
            TotalBroken += broken;
            BladesBroken?.Invoke(broken);
        }
    }

    // ---------- уламки-кубики (як VoxelWorld.SpawnDebris) ----------

    float DebrisEdge()
    {
        float baseRadius = rock != null ? rock.StartRadius : vs * 0.08f;
        return Mathf.Max(0.02f, baseRadius * debrisSizeInRockRadii);
    }

    void SpawnCubes(Vector3 basePos, int heightVoxels, Vector3 rockCenter, Vector3 rockVel)
    {
        for (int k = 0; k < cubesPerBlade; k++)
        {
            if (spawnedThisFrame >= maxDebrisPerFrame) return;
            spawnedThisFrame++;

            // випадкова точка всередині травинки: по всій висоті стовпчика
            int level = Random.Range(0, heightVoxels);
            float half = (level == heightVoxels - 1 && heightVoxels > 1 ? tipThickness : thickness) * vs * 0.5f;
            Vector3 pos = basePos + new Vector3(
                Random.Range(-half, half),
                (level + Random.value) * vs,
                Random.Range(-half, half));
            int band = Mathf.Min(colorBands - 1, level * colorBands / heightVoxels);

            // кубик не має з'являтись усередині каменя (інакше фізика його різко виштовхне)
            Vector3 dir = pos - rockCenter;
            float dist = dir.magnitude;
            dir = dist > 1e-4f ? dir / dist : Random.onUnitSphere;
            float minDist = rock != null ? rock.Radius * 1.1f : 0f;
            if (dist < minDist) pos = rockCenter + dir * minDist;

            Rigidbody d = GetDebris();
            d.transform.SetPositionAndRotation(pos, Random.rotation);
            d.linearVelocity = dir * Random.Range(1f, debrisSpread) + rockVel * 0.3f + Random.insideUnitSphere;
            d.angularVelocity = Random.insideUnitSphere * 5f;
            d.GetComponent<Renderer>().sharedMaterial = materials[band];
        }
    }

    Rigidbody GetDebris()
    {
        if (debrisPool == null) debrisPool = new Rigidbody[Mathf.Max(1, maxDebris)];
        if (debrisRoot == null)
        {
            var root = new GameObject("GrassDebris");
            debrisRoot = root.transform;
        }

        Rigidbody d = debrisPool[debrisIndex];
        if (d == null)
        {
            // порожній слот (ще не створювали або камінь уже всмоктав кубик): робимо новий
            float edge = DebrisEdge();
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "GrassCube";
            cube.transform.SetParent(debrisRoot, false);
            cube.transform.localScale = Vector3.one * edge;
            d = cube.AddComponent<Rigidbody>();
            d.mass = 0.05f;
            var a = cube.AddComponent<Absorbable>();
            a.volume = edge * edge * edge * absorbVolumeFactor;
            debrisPool[debrisIndex] = d;
        }
        else
        {
            var a = d.GetComponent<Absorbable>();
            if (a != null) a.attached = false;
        }
        debrisIndex = (debrisIndex + 1) % debrisPool.Length;   // найстаріший кубик використовується повторно
        return d;
    }

    // ---------- малювання ----------

    void Update()
    {
        if (!built || materials == null || meshes == null) return;
        spawnedThisFrame = 0;

        UpdateStreaming();
        if (breakable) BreakAroundRock();

        Camera cam = Camera.main;
        if (cam == null) return;
        GeometryUtility.CalculateFrustumPlanes(cam, planes);

        foreach (var chunk in chunks.Values)
        {
            if (chunk.empty) continue;
            if (!GeometryUtility.TestPlanesAABB(planes, chunk.bounds)) continue;

            foreach (var b in chunk.batches)
            {
                if (b.count == 0) continue;
                Mesh mesh = meshes[b.variant];
                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    var rp = new RenderParams(materials[sub])
                    {
                        shadowCastingMode = ShadowCastingMode.Off,
                        receiveShadows = receiveShadows,
                        layer = gameObject.layer,
                        worldBounds = chunk.bounds
                    };
                    Graphics.RenderMeshInstanced(rp, mesh, sub, b.matrices, b.count);
                }
            }
        }
    }

    // ---------- меш травинки ----------

    // Стовпчик із n кубів (висота n у вокселях). Сабмеш = смуга кольору: куби групуються по bands знизу вгору.
    static Mesh BuildBladeMesh(int n, float thick, float tipThick, int bands)
    {
        var verts = new List<Vector3>();
        var normals = new List<Vector3>();
        var tris = new List<int>[bands];
        for (int b = 0; b < bands; b++) tris[b] = new List<int>();

        for (int i = 0; i < n; i++)
        {
            float half = (i == n - 1 && n > 1 ? tipThick : thick) * 0.5f;
            int band = Mathf.Min(bands - 1, i * bands / n);
            AddCube(verts, normals, tris[band], -half, i, -half, half, i + 1, half);
        }

        var mesh = new Mesh { name = "VoxelBlade" + n };
        mesh.SetVertices(verts);
        mesh.SetNormals(normals);
        mesh.subMeshCount = bands;
        for (int b = 0; b < bands; b++) mesh.SetTriangles(tris[b], b);
        mesh.RecalculateBounds();
        return mesh;
    }

    static void AddCube(List<Vector3> v, List<Vector3> nrm, List<int> t,
                        float x0, float y0, float z0, float x1, float y1, float z1)
    {
        Face(v, nrm, t, new Vector3(1, 0, 0),
            new Vector3(x1, y0, z0), new Vector3(x1, y1, z0), new Vector3(x1, y1, z1), new Vector3(x1, y0, z1));
        Face(v, nrm, t, new Vector3(-1, 0, 0),
            new Vector3(x0, y0, z1), new Vector3(x0, y1, z1), new Vector3(x0, y1, z0), new Vector3(x0, y0, z0));
        Face(v, nrm, t, new Vector3(0, 1, 0),
            new Vector3(x0, y1, z0), new Vector3(x0, y1, z1), new Vector3(x1, y1, z1), new Vector3(x1, y1, z0));
        Face(v, nrm, t, new Vector3(0, 0, 1),
            new Vector3(x1, y0, z1), new Vector3(x1, y1, z1), new Vector3(x0, y1, z1), new Vector3(x0, y0, z1));
        Face(v, nrm, t, new Vector3(0, 0, -1),
            new Vector3(x0, y0, z0), new Vector3(x0, y1, z0), new Vector3(x1, y1, z0), new Vector3(x1, y0, z0));
    }

    static void Face(List<Vector3> v, List<Vector3> nrm, List<int> t, Vector3 normal,
                     Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        int start = v.Count;
        v.Add(a); v.Add(b); v.Add(c); v.Add(d);
        for (int i = 0; i < 4; i++) nrm.Add(normal);
        t.Add(start); t.Add(start + 1); t.Add(start + 2);
        t.Add(start); t.Add(start + 2); t.Add(start + 3);
    }
}